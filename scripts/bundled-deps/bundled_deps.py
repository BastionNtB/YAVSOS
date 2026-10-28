#!/usr/bin/env python3
"""YAVSOS bundled dependency check (D090).

Compares the upstream code bundled into YAVSOS.dll (ServerSync, MultiUserChest) with its
upstream repository. Pins live in third_party/upstream-pins.json. Read-only: a finding is a
question for the maintainer, never an automatic update.

For each dependency it reports:
  - a newer GitHub release than the pinned tag;
  - commits on the default branch after the pinned commit;
  - open issues updated after `issuesReviewedThrough`, marked "copied code" when the title or body
    names a type or method from the copied source (for ServerSync the whole library is copied, so
    every issue counts), and tracked issues with their notes;
  - pull requests listed in `openPullRequests` that were merged or closed.

  python3 scripts/bundled-deps/bundled_deps.py [--all-issues] [--json FILE]

Uses the unauthenticated GitHub API (60 requests an hour; one run needs about eight).
Exit codes: 0 = pins current and no new issues, 1 = something to review, 2 = input or network error.
"""

import argparse
import datetime
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PINS = ROOT / "third_party/upstream-pins.json"
API = "https://api.github.com/repos/"


def get(path):
    request = urllib.request.Request(API + path, headers={"Accept": "application/vnd.github+json",
                                                         "User-Agent": "YAVSOS-bundled-deps"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def copied_names(folder):
    """Type and method names declared in the copied source, used to spot issues about that code."""
    names = set()
    for f in (ROOT / folder).rglob("*.cs"):
        text = f.read_text(encoding="utf-8-sig")
        names |= set(re.findall(r"\b(?:class|struct|interface|enum)\s+([A-Z]\w{3,})", text))
        names |= set(re.findall(r"\b(?:public|private|internal|protected)\s+(?:static\s+)?[\w<>\[\],]+\s+([A-Z]\w{3,})\s*\(",
                                text))
    # Unity and Harmony entry-point names say nothing about which code an issue is about.
    return names - {"Awake", "Start", "Update", "OnDestroy", "Prefix", "Postfix", "Transpiler", "Patch", "Init"}


def check(dep, all_issues):
    repo, out = dep["repository"], {"name": dep["name"], "findings": []}
    info = get(repo)
    branch = info["default_branch"]

    releases = get(f"{repo}/releases?per_page=5")
    if releases:
        newest = releases[0]
        out["latestRelease"] = newest["tag_name"]
        if dep["kind"] == "release" and newest["tag_name"] != dep["pinnedTag"]:
            out["findings"].append(f"newer release {newest['tag_name']} ({newest['published_at'][:10]}); "
                                   f"pinned {dep['pinnedTag']}")

    compare = get(f"{repo}/compare/{dep['pinnedCommit']}...{branch}")
    out["commitsAhead"] = compare["ahead_by"]
    for c in compare.get("commits", [])[-10:]:
        out["findings"].append(f"commit {c['sha'][:10]} {c['commit']['committer']['date'][:10]}: "
                               f"{c['commit']['message'].splitlines()[0][:90]}")

    names = copied_names(dep["copiedNamesFrom"]) if dep["copiedNamesFrom"] else None
    since = dep["issuesReviewedThrough"]
    issues = [i for i in get(f"{repo}/issues?state=open&per_page=100&sort=updated")
              if "pull_request" not in i]
    out["openIssues"] = len(issues)
    for i in issues:
        number, tracked = str(i["number"]), dep["trackedIssues"].get(str(i["number"]))
        new = i["updated_at"][:10] > since
        if not (new or all_issues or tracked):
            continue
        text = f"{i['title']}\n{i.get('body') or ''}"
        hits = sorted(n for n in names if re.search(rf"\b{re.escape(n)}\b", text)) if names is not None else None
        scope = "copied code" if names is None or hits else "other"
        line = f"issue #{number} ({'updated ' + i['updated_at'][:10] if new else 'unchanged'}, {scope}"
        line += f": {', '.join(hits[:4])}" if hits else ""
        line += f"): {i['title'][:90]}"
        if tracked:
            line += f"\n      tracked: {tracked}"
        if new or all_issues:
            out["findings"].append(line)
        else:
            out.setdefault("tracked", []).append(line)
    # Pull requests we wait on (D090): a merge or close is something to act on.
    for number, note in dep.get("openPullRequests", {}).items():
        pr = get(f"{repo}/pulls/{number}")
        state = "merged" if pr.get("merged_at") else pr["state"]
        line = f"pull request #{number} ({state}): {pr['title'][:90]}\n      tracked: {note}"
        if state == "open":
            out.setdefault("tracked", []).append(line)
        else:
            out["findings"].append(line)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--all-issues", action="store_true", help="list every open issue, not only new ones")
    ap.add_argument("--json", metavar="FILE", help="also write the report as JSON")
    args = ap.parse_args()
    pins = json.loads(PINS.read_text())
    report, review = {"time": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
                      "dependencies": []}, False
    for dep in pins["dependencies"]:
        try:
            result = check(dep, args.all_issues)
        except (urllib.error.URLError, TimeoutError, KeyError) as e:
            print(f"{dep['name']}: could not check {dep['repository']} ({e})")
            return 2
        report["dependencies"].append(result)
        pin = dep["pinnedTag"] + ("" if dep["kind"] == "release" else f" @ {dep['pinnedCommit'][:10]}")
        state = "current" if not result["findings"] else "review"
        print(f"{dep['name']} (pinned {pin}; latest release {result.get('latestRelease', 'none')}; "
              f"{result['commitsAhead']} commits after the pin; {result['openIssues']} open issues): {state}")
        for line in result["findings"]:
            print(f"  {line}")
        for line in result.get("tracked", []):
            print(f"  {line}")
        review |= bool(result["findings"])
    if args.json:
        Path(args.json).write_text(json.dumps(report, indent=2) + "\n")
    return 1 if review else 0


if __name__ == "__main__":
    sys.exit(main())
