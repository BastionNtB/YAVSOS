#!/usr/bin/env python3
"""Build the store packages from a verified Release build (P8-04, RELEASE-AUTOMATION R1).

Writes artifacts/package/<version>/:
  YAVSOS-<version>-thunderstore.zip  Thunderstore and Hexium (manifest, icon, README, CHANGELOG, plugins/)
  YAVSOS-<version>-nexus.zip         Nexus Mods (BepInEx/plugins/YAVSOS/)
  README.md                          the store text: docs/PLAYER-GUIDE.md with its media slots as images
  nexus-description.bbcode.txt       the same text in Nexus BBCode

The guide is the store description on every store (maintainer, 2026-10-27, D099) and the
public repository's README (D102). Only mechanical changes are made to it: comments removed,
media slots turned into images, and on the stores its links to repository files (written from
the repository root) turned into links to the public repository at the release tag. A slot
whose file is missing, or with no --media-url, stays as its text; a repository link with no
--repo-url stays relative. Both are reported as unresolved; --strict (used when publishing)
refuses them. A link to a file that doesn't exist always fails. Nothing is published here.
"""
import argparse
import json
from pathlib import Path
import re
import struct
import sys
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
import settings_doc  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
# The guide; in the public repository it is the README itself (D102), its slots already images.
GUIDE = ROOT / "docs/PLAYER-GUIDE.md" if (ROOT / "docs/PLAYER-GUIDE.md").is_file() else ROOT / "README.md"
MEDIA = ROOT / "assets/media"
ICON = ROOT / "assets/icon/icon.png"
CONFIG = ROOT / "packaging/package.json"
SLOT = re.compile(r"^> \*\*(Picture|Animation): `([^`]+)`\*\* — (.*)$")
REPO_IMAGE = re.compile(r"^!\[(.*)\]\(assets/media/([^)]+)\)$")
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")
# A fixed timestamp makes the zips byte-identical for the same inputs.
ZIP_TIME = (2026, 1, 1, 0, 0, 0)


def fail(message):
    raise SystemExit(f"package: {message}")


def project_version():
    found = re.search(r'const string Version = "([0-9.]+)"', (ROOT / "src/YAVSOS.Core/ProjectIdentity.cs").read_text())
    if found is None or not SEMVER.match(found.group(1)):
        fail("ProjectIdentity.Version not found or not x.y.z.")
    return found.group(1)


def resolve_media(markdown, media_url):
    """Turn each slot line, or an image already pointing at assets/media, into an image link under
    media_url; return the text and the slots left unresolved."""
    lines, unresolved = [], []
    for line in markdown.split("\n"):
        slot, image = SLOT.match(line), REPO_IMAGE.match(line)
        if slot is None and image is None:
            lines.append(line)
            continue
        name, caption = (slot.group(2), slot.group(3)) if slot else (image.group(2), image.group(1))
        if media_url and (MEDIA / name).is_file():
            lines.append(f"![{caption.replace(']', ')').replace('[', '(')}]({media_url.rstrip('/')}/{name})")
        else:
            lines.append(line)
            unresolved.append(name)
    return "\n".join(lines), unresolved


LINK = re.compile(r"(?<!!)\[([^\]]*)\]\(([^)]+)\)")


def repository_links(markdown):
    """The guide's links to repository files (not web addresses or in-page anchors). Each must exist."""
    paths = [link for _, link in LINK.findall(markdown) if not re.match(r"(https?://|mailto:|#)", link)]
    missing = [path for path in paths if not (ROOT / path.split("#")[0]).exists()]
    if missing:
        fail(f"the guide links files that don't exist: {', '.join(missing)}")
    return paths


def resolve_links(markdown, repo_url, ref):
    """Repository links as links to the public repository at `ref`; return the text and the links left relative."""
    paths = repository_links(markdown)
    if not repo_url:
        return markdown, paths

    def absolute(match):
        text, link = match.groups()
        if link not in paths:
            return match.group(0)
        return f"[{text}]({repo_url.rstrip('/')}/blob/{ref}/{link})"
    return LINK.sub(absolute, markdown), []


def guide_text():
    return re.sub(r"<!--.*?-->\n*", "", GUIDE.read_text(), flags=re.S)


def store_readme(media_url, repo_url=None, ref=None):
    """The store text; returns it with the media slots and the links left unresolved."""
    text, links = resolve_links(guide_text(), repo_url, ref)
    text, slots = resolve_media(text, media_url)
    return text, slots, links


def repository_readme():
    """The public repository's README.md (D102): the guide, its media slots as images from assets/media.
    Links are already written from the repository root."""
    text = guide_text()
    repository_links(text)
    text, slots = resolve_media(text, "assets/media")
    if slots:
        fail(f"media slots without their file: {', '.join(slots)}")
    return text


def inline_bbcode(text):
    """Inline Markdown to Nexus BBCode. Code spans are set aside first, so their content is left
    alone while emphasis around them still pairs up."""
    spans = []

    def hold(match):
        spans.append(match.group(1))
        return f"\x00{len(spans) - 1}\x00"

    text = re.sub(r"`([^`]+)`", hold, text)
    text = re.sub(r"!\[[^\]]*\]\(([^)]+)\)", r"[img]\1[/img]", text)
    text = re.sub(r"\[([^\]]+)\]\(#[^)]*\)", r"\1", text)  # in-page anchors don't work on Nexus
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"[url=\2]\1[/url]", text)
    text = re.sub(r"\*\*(.+?)\*\*", r"[b]\1[/b]", text)
    text = re.sub(r"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])", r"[i]\1[/i]", text)
    return re.sub(r"\x00(\d+)\x00", lambda m: f"[font=Courier New]{spans[int(m.group(1))]}[/font]", text)


def table_bbcode(rows):
    """Nexus has no tables: each row becomes a list item, the first cell bold, the others named by their header."""
    cells = [[cell.strip() for cell in row.strip().strip("|").split("|")] for row in rows]
    header, body = cells[0], cells[2:]
    items = []
    for row in body:
        rest = "; ".join(f"{header[i]}: {row[i]}" if header[i] else row[i] for i in range(1, len(row)))
        first = inline_bbcode(row[0])
        if "[b]" not in first:
            first = f"[b]{first}[/b]"
        items.append(f"[*]{first} — {inline_bbcode(rest)}")
    return "[list]\n" + "\n".join(items) + "\n[/list]"


def list_bbcode(lines, numbered=False):
    """A list with one level of nesting ("  - "); other indented lines continue the item above."""
    items = []  # (item text, nested item texts)
    for line in lines:
        start = re.match(r"(- |\d+\. )", line)
        if start:
            items.append([line[start.end():].strip(), []])
        elif line.startswith("  - "):
            items[-1][1].append(line[4:].strip())
        elif items[-1][1]:
            items[-1][1][-1] += " " + line.strip()
        else:
            items[-1][0] += " " + line.strip()
    out = ["[list=1]" if numbered else "[list]"]
    for text, nested in items:
        out.append(f"[*]{inline_bbcode(text)}")
        if nested:
            out.append("[list]\n" + "\n".join(f"[*]{inline_bbcode(item)}" for item in nested) + "\n[/list]")
    return "\n".join(out + ["[/list]"])


def nexus_bbcode(markdown):
    """The guide's Markdown subset (headings, paragraphs, flat lists, tables, quotes) in Nexus BBCode.
    Nexus keeps line breaks, so wrapped lines are joined. The Contents list is dropped: its
    in-page links don't work there."""
    markdown = re.sub(r"\n## Contents\n.*?(?=\n## )", "\n", markdown, flags=re.S)
    # A heading, or a list right after a paragraph line, may have no blank line before the next block.
    markdown = re.sub(r"^(#{1,3} .*)\n(?=\S)", r"\1\n\n", markdown, flags=re.M)
    markdown = re.sub(r"^((?![-|>]|\d+\. )\S.*)\n(?=(- |\d+\. ))", r"\1\n\n", markdown, flags=re.M)
    blocks, out = re.split(r"\n\s*\n", markdown.strip()), []
    for block in blocks:
        lines = block.split("\n")
        heading = re.match(r"^(#{1,3}) (.*)$", lines[0])
        if heading and len(lines) == 1:
            size = {1: 6, 2: 5, 3: 4}[len(heading.group(1))]
            out.append(f"[size={size}][b]{inline_bbcode(heading.group(2))}[/b][/size]")
        elif lines[0].startswith("|"):
            out.append(table_bbcode(lines))
        elif lines[0].startswith("- "):
            out.append(list_bbcode(lines))
        elif re.match(r"\d+\. ", lines[0]):
            out.append(list_bbcode(lines, numbered=True))
        elif lines[0].startswith(">"):
            out.append(f"[quote]{inline_bbcode(' '.join(line.lstrip('>').strip() for line in lines))}[/quote]")
        elif lines[0].startswith("!["):
            out.append(inline_bbcode(" ".join(lines)))
        else:
            out.append(inline_bbcode(" ".join(line.strip() for line in lines)))
    return "\n\n".join(out) + "\n"


def changelog(version):
    """Only released-version sections (`## x.y.z`) go into the package; the development history above them stays here."""
    text = (ROOT / "CHANGELOG.md").read_text()
    sections = re.findall(r"^## (\d+\.\d+\.\d+)(.*?)$\n(.*?)(?=^## |\Z)", text, flags=re.M | re.S)
    if not any(found == version for found, _, _ in sections):
        fail(f"CHANGELOG.md has no '## {version}' section.")
    body = "".join(f"## {v}{suffix}\n{content.rstrip()}\n\n" for v, suffix, content in sections)
    unreleased = [v for v, suffix, _ in sections if "unreleased" in suffix.lower()]
    return "# Changelog\n\n" + body.rstrip() + "\n", unreleased


def png_size(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        fail("icon is not a PNG.")
    return struct.unpack(">II", data[16:24])


def manifest(config, version, description):
    names = re.compile(r"^[A-Za-z0-9_]+$")
    dependency = re.compile(r"^[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+$")
    if not names.match(config["name"]) or not names.match(config["namespace"]):
        fail("name and namespace may hold only letters, digits and _.")
    if len(description) > 250:
        fail(f"description is {len(description)} characters; Thunderstore allows 250.")
    bad = [d for d in config["dependencies"] if not dependency.match(d)]
    if bad:
        fail(f"malformed dependency strings: {bad}")
    website = config["website_url"]
    if website and not re.match(r"^https?://", website):
        fail("website_url must be empty or an http(s) URL.")
    return {"name": config["name"], "version_number": version, "website_url": website,
            "description": description, "dependencies": config["dependencies"]}


def write_zip(path, entries):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in sorted(entries):
            info = zipfile.ZipInfo(name, ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, entries[name])


def check_zip(path, expected):
    with zipfile.ZipFile(path) as archive:
        if archive.testzip() is not None:
            fail(f"{path.name} is damaged.")
        names = set(archive.namelist())
        if names != set(expected):
            fail(f"{path.name} holds {sorted(names ^ set(expected))} unexpectedly.")
        for name, data in expected.items():
            if archive.read(name) != data:
                fail(f"{path.name}: {name} differs from its source.")


def build(media_url=None, strict=False, configuration="Release", repo_url=None):
    """Build both packages; return a summary dict. The build must have passed verify-build.py."""
    config = json.loads(CONFIG.read_text())
    version = project_version()
    if not settings_doc.is_current():
        fail("docs/SETTINGS.md is out of date; run scripts/settings_doc.py.")
    output = ROOT / f"artifacts/build/YAVSOS/{configuration}/netstandard2.1"
    missing = [name for name in config["files"] if not (output / name).is_file()]
    if missing:
        fail(f"build output lacks {missing}; build {configuration} and run verify-build.py first.")
    plugin_files = {name: (output / name).read_bytes() for name in config["files"]}

    readme, unresolved, relative = store_readme(media_url, repo_url, f"v{version}")
    changes, unreleased = changelog(version)
    if strict and unresolved:
        fail(f"media slots unresolved: {', '.join(unresolved)}")
    if strict and relative:
        fail(f"links left relative (no --repo-url): {', '.join(relative)}")
    if strict and version in unreleased:
        fail(f"CHANGELOG.md marks {version} as unreleased.")
    icon = ICON.read_bytes()
    if png_size(icon) != (256, 256):
        fail(f"icon is {png_size(icon)}; Thunderstore needs 256×256.")
    description = config["description"]
    manifest_data = manifest(config, version, description)
    manifest_bytes = (json.dumps(manifest_data, indent=2, ensure_ascii=False) + "\n").encode()

    target = ROOT / "artifacts/package" / version
    target.mkdir(parents=True, exist_ok=True)
    thunderstore = {"manifest.json": manifest_bytes, "icon.png": icon, "README.md": readme.encode(),
                    "CHANGELOG.md": changes.encode()}
    thunderstore.update({f"plugins/{name}": data for name, data in plugin_files.items()})
    nexus = {f"BepInEx/plugins/YAVSOS/{name}": data for name, data in plugin_files.items()}
    ts_zip = target / f"YAVSOS-{version}-thunderstore.zip"
    nexus_zip = target / f"YAVSOS-{version}-nexus.zip"
    write_zip(ts_zip, thunderstore)
    write_zip(nexus_zip, nexus)
    check_zip(ts_zip, thunderstore)
    check_zip(nexus_zip, nexus)
    (target / "README.md").write_text(readme)
    (target / "nexus-description.bbcode.txt").write_text(nexus_bbcode(readme))
    return {"version": version, "directory": target, "thunderstore": ts_zip, "nexus": nexus_zip,
            "manifest": manifest_data, "readme": readme, "icon": icon, "unresolved": unresolved, "relative": relative,
            "unreleased": unreleased}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--media-url", help="URL of the folder holding the media files (the public repository's assets/media at the release tag)")
    parser.add_argument("--repo-url", help="the public repository (https://github.com/<owner>/<name>); the guide's links point into it at tag v<version>")
    parser.add_argument("--strict", action="store_true", help="refuse unresolved media slots, relative links and an unreleased CHANGELOG entry")
    args = parser.parse_args()
    result = build(args.media_url, args.strict, repo_url=args.repo_url)
    for key in ("thunderstore", "nexus"):
        print(f"{result[key].relative_to(ROOT)}: {result[key].stat().st_size:,} bytes")
    print(f"description: {result['manifest']['description']}")
    if result["unresolved"]:
        print(f"unresolved media slots ({len(result['unresolved'])}): {', '.join(result['unresolved'])}", file=sys.stderr)
    if result["relative"]:
        print(f"links left relative, no --repo-url ({len(result['relative'])}): {', '.join(result['relative'])}", file=sys.stderr)
    if result["unreleased"]:
        print(f"CHANGELOG marks as unreleased: {', '.join(result['unreleased'])}", file=sys.stderr)
    print("Packages built and checked locally; nothing published.")


if __name__ == "__main__":
    main()
