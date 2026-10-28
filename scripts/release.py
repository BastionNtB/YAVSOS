#!/usr/bin/env python3
"""One release action for Thunderstore, Hexium and Nexus Mods (P8-08, RELEASE-AUTOMATION R2).

Without --publish this is the dry run: check, build, test, verify, package, and (with
--validate-online) ask Thunderstore to validate the manifest, README and icon. Nothing is
published unless --publish names the stores. Publishing is a separately requested act
by the maintainer; a store that fails is reported and retried alone, and the others stay published.

Credentials come from ~/.config/tokens/<service>.env, read as data (never executed), or from
environment variables with the same names (the public repository's secrets). Values are
never printed or put on a command line.

  thunderstore.env  THUNDERSTORE_URL THUNDERSTORE_COMMUNITY THUNDERSTORE_NAMESPACE THUNDERSTORE_TOKEN
  hexium.env        HEXIUM_URL HEXIUM_COMMUNITY HEXIUM_NAMESPACE HEXIUM_TOKEN

Nexus Mods is uploaded by Nexus-Mods/upload-action in the public repository's workflow
(R6); this script builds its zip only.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import urllib.error
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
import package  # noqa: E402

ROOT = package.ROOT
TOKENS = Path.home() / ".config/tokens"
STORES = {"thunderstore": "THUNDERSTORE", "hexium": "HEXIUM"}
TIMEOUT = 60


def step(text):
    print(f"\n== {text}", flush=True)


def fail(message):
    raise SystemExit(f"release: {message}")


def load_store(store):
    """Store settings from its token file, overridden by the environment. Only names are ever reported."""
    prefix = STORES[store]
    names = [f"{prefix}_URL", f"{prefix}_COMMUNITY", f"{prefix}_NAMESPACE", f"{prefix}_TOKEN"]
    values = {}
    path = TOKENS / f"{store}.env"
    if path.is_file():
        for line in path.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                values[key.strip()] = value.strip().strip("'\"")
    values.update({name: os.environ[name] for name in names if os.environ.get(name)})
    missing = [name for name in names if not values.get(name)]
    if missing:
        fail(f"{store}: missing {', '.join(missing)} (in {path} or the environment).")
    return {"url": values[names[0]].rstrip("/"), "community": values[names[1]],
            "namespace": values[names[2]], "token": values[names[3]]}


def request(store, method, path, body=None, raw=None, url=None, headers=None):
    """One bounded HTTP call. Errors show the status and the response text, never the token."""
    target = url or f"{store['url']}{path}"
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    sent = {"Accept": "application/json", "User-Agent": "YAVSOS-release"}
    if url is None:
        sent["Authorization"] = f"Bearer {store['token']}"
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


def validate_online(result):
    """Thunderstore's validators read the package parts without publishing anything."""
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
    parser.add_argument("--publish", help="comma-separated stores to publish to: thunderstore,hexium")
    args = parser.parse_args()

    stores = [s.strip() for s in args.publish.split(",")] if args.publish else []
    unknown = [s for s in stores if s not in STORES]
    if unknown:
        fail(f"unknown store {unknown}; choose from {', '.join(STORES)}.")
    if stores and args.skip_build:
        fail("publishing always builds and tests; drop --skip-build.")
    # Thunderstore first: Hexium usually takes the Thunderstore package UUID.
    stores.sort(key=list(STORES).index)

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
        validate_online(result)
    for store in stores:
        publish(store, result["thunderstore"])
    if set(stores) == set(STORES):
        step("Package UUIDs (Gale shows one entry only when they match)")
        config = result["manifest"]
        ids = {s: package_uuid(s, load_store(s)["namespace"], config["name"]) for s in STORES}
        print(f"   {ids}; {'same' if len(set(ids.values())) == 1 and None not in ids.values() else 'DIFFERENT or not listed yet: ask Hexium how to link them'}")
    step("Done")
    print(f"   Nexus Mods zip: {result['nexus'].relative_to(ROOT)} (uploaded by the public repository's workflow, R6)")
    print("   Published: " + (", ".join(stores) if stores else "nothing (dry run)"))


if __name__ == "__main__":
    main()
