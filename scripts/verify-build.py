#!/usr/bin/env python3
"""Check built assembly metadata without loading Unity/BepInEx or deploying anything."""
import hashlib
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
# Release by default; "Lab" checks the lab build, which carries the lab fixtures (D077 B09).
CONFIGURATION = sys.argv[1] if len(sys.argv) > 1 else "Release"
if CONFIGURATION not in ("Release", "Lab"):
    raise SystemExit("Usage: verify-build.py [Release|Lab]")
OUTPUT = ROOT / f"artifacts/build/YAVSOS/{CONFIGURATION}/netstandard2.1"
LAB_TYPES = ("YAVSOS.Transfers.RuleLab", "YAVSOS.Transfers.FaultLab", "YAVSOS.Cleanup.StashLab", "YAVSOS.Cleanup.CleanupLab", "YAVSOS.Interface.UiLab")


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def metadata(path, table):
    return subprocess.run(["monodis", f"--{table}", str(path)], check=True, capture_output=True, text=True).stdout


plugin = OUTPUT / "YAVSOS.dll"
core = OUTPUT / "YAVSOS.Core.dll"
require(plugin.is_file() and core.is_file(), f"Build {CONFIGURATION} first using ./scripts/build.sh.")
require({p.relative_to(OUTPUT).as_posix() for p in OUTPUT.rglob("*.dll")} == {"YAVSOS.dll", "YAVSOS.Core.dll"},
        "Unexpected runtime DLL: never package game, BepInEx, Harmony, tests, or standalone ServerSync.")
# D082: YAVSOS is GPL-3.0; its licence ships with the plugin.
require((OUTPUT / "YAVSOS-LICENSE.txt").read_bytes() == (ROOT / "LICENSE").read_bytes(), "Missing/changed YAVSOS licence.")
require((OUTPUT / "ServerSync-LICENSE.txt").read_bytes() == (ROOT / "third_party/ServerSync/LICENSE.txt").read_bytes(),
        "Missing/changed bundled dependency license.")
require((OUTPUT / "MultiUserChest-LICENSE.txt").read_bytes() == (ROOT / "third_party/MultiUserChest/LICENSE.txt").read_bytes(),
        "Missing/changed bundled MultiUserChest license.")
# B08: ProjectIdentity.Version is the single version source; MSBuild reads the assembly version from it.
identity = (ROOT / "src/YAVSOS.Core/ProjectIdentity.cs").read_text()
found = re.search(r'const string Version = "([0-9.]+)"', identity)
require(found is not None, "ProjectIdentity.Version not found.")
version = found.group(1)
require('Guid = "com.bastionntb.yavsos"' in identity, "Plugin GUID changed.")
require(f"Version:       {version}.0" in metadata(plugin, "assembly"), "Plugin assembly version differs.")
require(f"Version:       {version}.0" in metadata(core, "assembly"), "Core assembly version differs.")
refs = metadata(plugin, "assemblyref")
require("Name=ServerSync\n" not in refs, "Plugin still depends on an external ServerSync assembly.")
require("Name=YAVSOS.MultiUserChest\n" not in refs, "Plugin depends on an external MultiUserChest assembly.")
# D062: Jotunn is a required, separately installed dependency: referenced, never merged or packaged.
require("Name=Jotunn\n" in refs, "Plugin lacks its required Jotunn reference (D062).")
core_refs = metadata(core, "assemblyref")
require(all(f"Name={name}" not in core_refs for name in ("Unity", "BepInEx", "assembly_", "0Harmony", "ServerSync")),
        "Core acquired a game/mod dependency.")
types = metadata(plugin, "typedef")
for lab in LAB_TYPES:
    present = re.search(rf"\b{re.escape(lab)}\b", types) is not None
    require(present == (CONFIGURATION == "Lab"),
            f"{lab} {'missing from the Lab build' if CONFIGURATION == 'Lab' else 'ships in the Release build (D077 B09)'}.")
require(re.search(r"^\d+: Jotunn\.", types, re.M) is None, "Jotunn types were merged into the plugin; reference Jotunn instead (D062).")
match = re.search(r"ServerSync\.ConfigSync .*flags=(0x[0-9a-f]+)", types)
require(match is not None and int(match[1], 16) & 7 == 0, "ServerSync is missing or not internalized.")
require("ServerSync.CustomSyncedValue`1" in types, "Bundled custom-policy channel is missing.")
match = re.search(r"YAVSOS\.Vendor\.MultiUserChest\.BundledCopy .*flags=(0x[0-9a-f]+)", types)
require(match is not None and int(match[1], 16) & 7 == 0, "Bundled MultiUserChest is missing or not internalized.")
# Mono lets the merged MultiUserChest code read private game members only in an unverifiable assembly.
require("SkipVerification' = bool(true)" in metadata(plugin, "declsec"), "Plugin lacks SkipVerification; bundled MultiUserChest would fail at runtime.")
cached = ROOT / "artifacts/dependencies/ServerSync/ServerSync.dll"
require(hashlib.sha256(cached.read_bytes()).hexdigest() == "e8b61f621cc396c529d9194f202e0b22c6591dd919363a4501a579f9f7252e0e",
        "Pinned ServerSync checksum differs.")
# Guard/cache adapter names are deliberately coupled to the pinned dependency's actual metadata.
methods = metadata(cached, "strings")  # Raw metadata names do not require proprietary signature resolution.
fields = metadata(cached, "fields")
for method in ("RPC_FromServerConfigSync", "RPC_FromOtherClientConfigSync", "RegisterAndCheckVersion", "CheckVersion",
               "HandleConfigSyncRPC", "Broadcast"):
    require(method in methods, f"Pinned sender hook is missing: {method}.")
require("ServerSync VersionCheck" in metadata(cached, "userstrings"),
        "Pinned version-advertisement RPC name is missing.")
for field in ("configValueCache", "cacheExpirations", "currentRpc", "LocalBaseValue", "SynchronizedConfig"):
    require(field in fields, f"Pinned cache hook is missing: {field}.")
for binary in (plugin, core):
    print(f"{binary.name}: {hashlib.sha256(binary.read_bytes()).hexdigest()}")
print("Assembly/package checks passed; this is structural evidence, not a runtime test.")
