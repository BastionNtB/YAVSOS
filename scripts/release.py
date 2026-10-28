#!/usr/bin/env python3
"""One release action for Thunderstore, Hexium, Nexus Mods and the GitHub release (P8-08,
RELEASE-AUTOMATION R2 and R6).

Without --publish this is the dry run: check, build, test, verify, package, and (with
--validate-online) ask Thunderstore to validate the manifest, README and icon, check that the
Nexus key, mod and file resolve, and that the public repository has the tag. Nothing is
published unless --publish names the targets (thunderstore, hexium, nexus, github; that order).
Publishing is a separately requested act by the maintainer; a target that fails is reported and
retried alone, and the others stay published.

Credentials come from ~/.config/tokens/<service>.env, read as data (never executed), or from
environment variables with the same names. Values are never printed or put on a command line.

  thunderstore.env  THUNDERSTORE_URL THUNDERSTORE_COMMUNITY THUNDERSTORE_NAMESPACE THUNDERSTORE_TOKEN
  hexium.env        HEXIUM_URL HEXIUM_COMMUNITY HEXIUM_NAMESPACE HEXIUM_TOKEN
  nexusmods.env     NEXUSMODS_GAME NEXUSMODS_MOD_ID NEXUSMODS_FILE_ID NEXUSMODS_API_KEY
                    (MOD_ID: the page's number; FILE_ID: the API "File ID" under the file's
                    Advanced details, which takes each new version)

Nexus uses its v3 upload API the way Nexus-Mods/upload-action v1.0.0-beta.11 does. The GitHub
release (on the --repo-url repository, at the tag, with both zips) uses the gh CLI's own login.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
import package  # noqa: E402

ROOT = package.ROOT
TOKENS = Path.home() / ".config/tokens"
STORES = {"thunderstore": "THUNDERSTORE", "hexium": "HEXIUM"}
TARGETS = ["thunderstore", "hexium", "nexus", "github"]
NEXUS_API = "https://api.nexusmods.com/v3"
TIMEOUT = 60


def step(text):
    print(f"\n== {text}", flush=True)


def fail(message):
    raise SystemExit(f"release: {message}")


def load_values(service, names):
    """Settings from a token file, overridden by the environment. Only names are ever reported."""
    values = {}
    path = TOKENS / f"{service}.env"
    if path.is_file():
        for line in path.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip("'\"")
    values.update({name: os.environ[name] for name in names if os.environ.get(name)})
    missing = [name for name in names if not values.get(name)]
    if missing:
        fail(f"{service}: missing {', '.join(missing)} (in {path} or the environment).")
    return [values[name] for name in names]


def load_store(store):
    prefix = STORES[store]
    url, community, namespace, token = load_values(
        store, [f"{prefix}_URL", f"{prefix}_COMMUNITY", f"{prefix}_NAMESPACE", f"{prefix}_TOKEN"])
    return {"url": url.rstrip("/"), "community": community, "namespace": namespace, "token": token,
            "auth": {"Authorization": f"Bearer {token}"}}


def load_nexus():
    game, mod_id, file_id, key = load_values(
        "nexusmods", ["NEXUSMODS_GAME", "NEXUSMODS_MOD_ID", "NEXUSMODS_FILE_ID", "NEXUSMODS_API_KEY"])
    return {"url": NEXUS_API, "game": game, "mod_id": mod_id, "file_id": file_id, "token": key,
            "auth": {"apikey": key}}


def request(store, method, path, body=None, raw=None, url=None, headers=None):
    """One bounded HTTP call. Errors show the status and the response text, never the token."""
    target = url or f"{store['url']}{path}"
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    sent = {"Accept": "application/json", "User-Agent": "YAVSOS-release"}
    if url is None:
        sent.update(store["auth"])
    if body is not None:
        sent["Content-Type"] = "application/json"
    sent.update(headers or {})
    call = urllib.request.Request(target, data=data, method=method, headers=sent)
    try:
        with urllib.request.urlopen(call, timeout=TIMEOUT) as response:
            text = response.read()
            return response.status, response.headers, (json.loads(text) if text[:1] in (b"{", b"[") else text)
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:500].replace(store["token"], "<token>")
        return error.code, error.headers, detail


def run(command, log_name):
    """Run a build or test command; its output goes to artifacts/release-logs/."""
    logs = ROOT / "artifacts/release-logs"
    logs.mkdir(parents=True, exist_ok=True)
    log = logs / f"{log_name}.log"
    with log.open("w") as handle:
        result = subprocess.run(command, cwd=ROOT, stdout=handle, stderr=subprocess.STDOUT)
    tail = log.read_text().strip().splitlines()[-3:]
    print("\n".join(f"   {line}" for line in tail))
    if result.returncode != 0:
        fail(f"{' '.join(command)} failed; see {log.relative_to(ROOT)}.")


def check(version, tag, publishing):
    step("Check")
    if tag is not None and tag != f"v{version}":
        fail(f"tag {tag} doesn't match ProjectIdentity.Version {version} (expected v{version}).")
    _, unreleased = package.changelog(version)
    if publishing:
        dirty = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True).stdout
        if dirty.strip():
            fail("the working tree has uncommitted changes; publish from a clean commit.")
        if version in unreleased:
            fail(f"CHANGELOG.md still marks {version} as unreleased.")
    head = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    print(f"   version {version}, commit {head}, CHANGELOG entry present"
          f"{' (marked unreleased)' if version in unreleased else ''}")


def build_and_test():
    step("Build (Release) and detached tests")
    run(["./scripts/build.sh"], "build-release")
    run(["dotnet", "test", "tests/YAVSOS.Core.Tests/YAVSOS.Core.Tests.csproj", "--configuration", "Release", "--no-build"],
        "tests-release")
    step("Verify the build")
    run([sys.executable, "scripts/verify-build.py", "Release"], "verify-build")


def validate_online(result, publishing):
    """Thunderstore's validators read the package parts without publishing anything. In a dry run, a
    version that is already published is a notice; publishing it again is refused."""
    step("Thunderstore validation (nothing is published)")
    store = load_store("thunderstore")
    manifest = json.dumps(result["manifest"], indent=2).encode()
    checks = [
        ("manifest", "/api/experimental/submission/validate/manifest-v1/",
         {"namespace": store["namespace"], "manifest_data": base64.b64encode(manifest).decode()}),
        ("README", "/api/experimental/submission/validate/readme/",
         {"readme_data": base64.b64encode(result["readme"].encode()).decode()}),
        ("icon", "/api/experimental/submission/validate/icon/",
         {"icon_data": base64.b64encode(result["icon"]).decode()}),
    ]
    failed = []
    for name, path, body in checks:
        status, _, answer = request(store, "POST", path, body)
        ok = status == 200 and isinstance(answer, dict) and answer.get("success") is True
        if not ok and not publishing and name == "manifest" and "already exists" in str(answer):
            print(f"   {name}: this version is already published (fine for a dry run; publishing it again is refused)")
            continue
        if not ok and status >= 500:
            # The README validator answered 500 even for "# Hello" on 2026-10-27: a store fault, not
            # a refusal. A minimal sample tells the two apart; the upload itself still validates.
            sample = {key: base64.b64encode(b"# Hello\n\nText.\n").decode() for key in body if key.endswith("_data")}
            if name == "README" and request(store, "POST", path, sample)[0] >= 500:
                print(f"   {name}: validator unavailable (server error for a minimal sample too); checked at upload")
                continue
        print(f"   {name}: {'valid' if ok else f'refused ({status}): {answer}'}")
        if not ok:
            failed.append(name)
    print("   Hexium has no validation endpoint; its first upload is the proof.")
    if failed:
        fail(f"Thunderstore refused: {', '.join(failed)}.")


def publish(store_name, zip_path):
    """Thunderstore's upload flow (Hexium mirrors it): initiate, upload the parts, finish, submit."""
    step(f"Publish to {store_name}")
    store = load_store(store_name)
    data = zip_path.read_bytes()
    status, _, started = request(store, "POST", "/api/experimental/usermedia/initiate-upload/",
                                 {"filename": zip_path.name, "file_size_bytes": len(data)})
    if status != 201:
        fail(f"{store_name}: initiate-upload refused ({status}): {started}")
    uuid = started["user_media"]["uuid"]
    parts = []
    for part in started["upload_urls"]:
        chunk = data[part["offset"]:part["offset"] + part["length"]]
        status, headers, answer = request(store, "PUT", "", raw=chunk, url=part["url"])
        if status not in (200, 201) or not headers.get("ETag"):
            request(store, "POST", f"/api/experimental/usermedia/{uuid}/abort-upload/", {})
            fail(f"{store_name}: part {part['part_number']} failed ({status}): {answer}")
        parts.append({"ETag": headers["ETag"], "PartNumber": part["part_number"]})
    status, _, answer = request(store, "POST", f"/api/experimental/usermedia/{uuid}/finish-upload/", {"parts": parts})
    if status != 200:
        fail(f"{store_name}: finish-upload refused ({status}): {answer}")
    status, _, answer = request(store, "POST", "/api/experimental/submission/submit/",
                                {"author_name": store["namespace"], "communities": [store["community"]],
                                 "has_nsfw_content": False, "upload_uuid": uuid})
    if status != 200:
        fail(f"{store_name}: submit refused ({status}): {answer}")
    version = answer.get("package_version", {})
    print(f"   published {version.get('full_name')} ({version.get('download_url', 'no download URL given')})")


def changelog_entry(version):
    """This version's CHANGELOG text, without its heading."""
    text = (ROOT / "CHANGELOG.md").read_text()
    found = re.search(rf"^## {re.escape(version)}\b[^\n]*\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    if found is None:
        fail(f"CHANGELOG.md has no '## {version}' section.")
    return found.group(1).strip()


def nexus_resolve(nexus):
    """Read-only: the key works, the page number gives the mod's v3 id, and the file exists."""
    status, _, mod = request(nexus, "GET", f"/games/{nexus['game']}/mods/{nexus['mod_id']}")
    if status != 200:
        fail(f"nexus: mod {nexus['game']}/{nexus['mod_id']} not readable ({status}): {mod}")
    status, _, mod_file = request(nexus, "GET", f"/mod-files/{nexus['file_id']}")
    if status != 200:
        fail(f"nexus: file {nexus['file_id']} not readable ({status}): {mod_file}")
    return mod["data"], mod_file["data"]


def nexus_check():
    step("Nexus Mods check (nothing is uploaded)")
    nexus = load_nexus()
    mod, mod_file = nexus_resolve(nexus)
    print(f"   key valid; mod {nexus['mod_id']} = '{mod.get('name')}' (v3 id {mod.get('id')}, version {mod.get('version')}); "
          f"file {nexus['file_id']} = '{mod_file.get('name')}'")


def publish_nexus(zip_path, version):
    """Nexus's v3 upload, as Nexus-Mods/upload-action does it: a multipart upload, finalise, wait until
    it is available, add it as the newest version of the existing file, then this version's changelog."""
    step("Publish to nexus")
    nexus = load_nexus()
    mod, mod_file = nexus_resolve(nexus)
    data = zip_path.read_bytes()
    status, _, started = request(nexus, "POST", "/uploads/multipart",
                                 {"filename": zip_path.name, "size_bytes": str(len(data))})
    if status != 201:
        fail(f"nexus: multipart upload refused ({status}): {started}")
    upload, size = started["data"], int(started["data"]["part_size_bytes"])
    parts = []
    for number, part_url in enumerate(upload["part_presigned_urls"], 1):
        chunk = data[(number - 1) * size:number * size]
        status, headers, answer = request(nexus, "PUT", "", raw=chunk, url=part_url,
                                          headers={"Content-Type": "application/octet-stream"})
        if status not in (200, 201) or not headers.get("ETag"):
            fail(f"nexus: part {number} failed ({status}): {answer}")
        parts.append((number, headers["ETag"].replace('"', "")))
    xml = "<CompleteMultipartUpload>\n" + "\n".join(
        f"  <Part>\n    <PartNumber>{n}</PartNumber>\n    <ETag>{etag}</ETag>\n  </Part>" for n, etag in parts
    ) + "\n</CompleteMultipartUpload>"
    status, _, answer = request(nexus, "POST", "", raw=xml.encode(), url=upload["complete_presigned_url"],
                                headers={"Content-Type": "application/xml"})
    if status not in (200, 201):
        fail(f"nexus: completing the upload failed ({status}): {answer}")
    status, _, answer = request(nexus, "POST", f"/uploads/{upload['id']}/finalise", {})
    if status not in (200, 201):
        fail(f"nexus: finalise refused ({status}): {answer}")
    deadline, delay = time.monotonic() + 300, 2.0
    while True:
        status, _, answer = request(nexus, "GET", f"/uploads/{upload['id']}")
        if status != 200:
            fail(f"nexus: upload state unreadable ({status}): {answer}")
        if answer["data"]["state"] == "available":
            break
        if time.monotonic() > deadline:
            fail(f"nexus: upload {upload['id']} still '{answer['data']['state']}' after 5 minutes; nothing was published.")
        time.sleep(delay)
        delay = min(delay * 1.5, 30.0)
    status, _, answer = request(nexus, "POST", f"/mod-files/{nexus['file_id']}/versions", {
        "upload_id": upload["id"], "name": mod_file["name"], "version": version, "file_category": "main",
        "archive_existing_file": True, "update_mod_version": True})
    if status != 201:
        fail(f"nexus: new file version refused ({status}): {answer}")
    print(f"   published {mod_file['name']} {version} (version id {answer['data']['version']['id']})")
    status, _, answer = request(nexus, "POST", f"/mods/{mod['id']}/changelogs",
                                {"version": version, "changelog": changelog_entry(version)})
    if status != 201:
        print(f"   changelog not added ({status}): {answer}; add it on the page by hand")
    print(f"   https://www.nexusmods.com/{nexus['game']}/mods/{nexus['mod_id']}")


def github_repo(repo_url):
    found = re.match(r"https://github\.com/([^/]+/[^/]+?)/?$", repo_url or "")
    if found is None:
        fail("the GitHub release needs --repo-url https://github.com/<owner>/<name>.")
    return found.group(1)


def github_check(repo_url, tag):
    step("GitHub release check (nothing is created)")
    repo = github_repo(repo_url)
    if tag is None:
        fail("the GitHub release needs --tag.")
    found = subprocess.run(["gh", "api", f"repos/{repo}/git/ref/tags/{tag}", "--jq", ".ref"],
                           capture_output=True, text=True, timeout=TIMEOUT)
    if found.returncode != 0:
        fail(f"github: {repo} has no tag {tag}; push the public repository's tag first.")
    print(f"   {repo} has {tag}")


def publish_github(repo_url, tag, version, result):
    step("Publish the GitHub release")
    repo = github_repo(repo_url)
    ts, nexus_env = load_store("thunderstore"), load_values("nexusmods", ["NEXUSMODS_GAME", "NEXUSMODS_MOD_ID"])
    name = result["manifest"]["name"]
    notes = (f"{changelog_entry(version)}\n\n**Install:** [Thunderstore]({ts['url']}/c/{ts['community']}/p/{ts['namespace']}/{name}/) "
             f"(Gale, r2modman), [Nexus Mods](https://www.nexusmods.com/{nexus_env[0]}/mods/{nexus_env[1]}), or Hexium. "
             "Requires BepInExPack Valheim and Jotunn; AzuAutoStore is optional. See the README for setup.\n\n"
             f"**Files:** `{result['thunderstore'].name}` is the Thunderstore/Hexium package; `{result['nexus'].name}` "
             "unpacks into your game or profile folder (`BepInEx/plugins/YAVSOS/`).\n")
    with tempfile.NamedTemporaryFile("w", suffix=".md", delete=False) as handle:
        handle.write(notes)
    made = subprocess.run(["gh", "release", "create", tag, "-R", repo, "--title", f"{name} {version}",
                           "--notes-file", handle.name, "--verify-tag", str(result["thunderstore"]), str(result["nexus"])],
                          capture_output=True, text=True, timeout=300)
    os.unlink(handle.name)
    if made.returncode != 0:
        fail(f"github: release refused: {made.stderr.strip()}")
    print(f"   {made.stdout.strip()}")


def package_uuid(store_name, namespace, name):
    """The package's uuid4 from the store's community listing (large; read once per release)."""
    store = load_store(store_name)
    status, _, listing = request(store, "GET", f"/c/{store['community']}/api/v1/package/")
    if status != 200 or not isinstance(listing, list):
        return None
    return next((p.get("uuid4") for p in listing if p.get("full_name") == f"{namespace}-{name}"), None)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--tag", help="release tag; must be v<ProjectIdentity.Version>")
    parser.add_argument("--media-url", help="URL of the public repository's assets/media at the release tag")
    parser.add_argument("--repo-url", help="the public repository (https://github.com/<owner>/<name>); the guide's links point into it at the tag")
    parser.add_argument("--skip-build", action="store_true", help="package the existing Release build (dry runs only)")
    parser.add_argument("--validate-online", action="store_true", help="ask Thunderstore to validate the parts (publishes nothing)")
    parser.add_argument("--publish", help="comma-separated targets: thunderstore,hexium,nexus,github")
    args = parser.parse_args()

    targets = [s.strip() for s in args.publish.split(",")] if args.publish else []
    unknown = [s for s in targets if s not in TARGETS]
    if unknown:
        fail(f"unknown target {unknown}; choose from {', '.join(TARGETS)}.")
    if targets and args.skip_build:
        fail("publishing always builds and tests; drop --skip-build.")
    # Thunderstore first: Hexium usually takes the Thunderstore package UUID. GitHub last, so its
    # release links stores that already have the version.
    targets.sort(key=TARGETS.index)
    stores = [s for s in targets if s in STORES]

    version = package.project_version()
    check(version, args.tag, bool(stores))
    if not args.skip_build:
        build_and_test()
    step("Package")
    result = package.build(args.media_url, strict=bool(stores), repo_url=args.repo_url)
    for key in ("thunderstore", "nexus"):
        print(f"   {result[key].relative_to(ROOT)}: {result[key].stat().st_size:,} bytes")
    if result["unresolved"]:
        print(f"   media slots unresolved ({len(result['unresolved'])}); they stay as text: {', '.join(result['unresolved'])}")
    if result["relative"]:
        print(f"   links left relative ({len(result['relative'])}, no --repo-url): {', '.join(result['relative'])}")
    if args.validate_online or stores:
        validate_online(result, bool(stores))
    if args.validate_online or "nexus" in targets:
        nexus_check()
    if "github" in targets or (args.validate_online and args.repo_url):
        github_check(args.repo_url, args.tag)
    for store in stores:
        publish(store, result["thunderstore"])
    if "nexus" in targets:
        publish_nexus(result["nexus"], version)
    if "github" in targets:
        publish_github(args.repo_url, args.tag, version, result)
    if set(stores) == set(STORES):
        step("Package UUIDs (Gale shows one entry only when they match)")
        config = result["manifest"]
        ids = {s: package_uuid(s, load_store(s)["namespace"], config["name"]) for s in STORES}
        print(f"   {ids}; {'same' if len(set(ids.values())) == 1 and None not in ids.values() else 'DIFFERENT or not listed yet: ask Hexium how to link them'}")
    step("Done")
    print("   Published: " + (", ".join(targets) if targets else "nothing (dry run)"))


if __name__ == "__main__":
    main()
