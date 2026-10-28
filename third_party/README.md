# Dependency provenance

YAVSOS itself is GPL-3.0-or-later ([LICENSE](../LICENSE)). The code below keeps its own licence; these notices describe it.

## blaxxun ServerSync

- Upstream: https://github.com/blaxxun-boop/ServerSync
- Release: **v1.20**, release asset `ServerSync.dll` (asset ID `553027276`, observed 2026-10-10).
- URL: https://github.com/blaxxun-boop/ServerSync/releases/download/v1.20/ServerSync.dll
- SHA-256: `e8b61f621cc396c529d9194f202e0b22c6591dd919363a4501a579f9f7252e0e`.
- Tag's source revision: `c57c2aa54e07cdcc7630d6068699ea781622323e`. The release describes a recompile for Valheim 1.0; the downloaded binary hash is the build's authoritative pin.
- License: [unaltered upstream notice](ServerSync/LICENSE.txt), obtained from that source revision's `LICENSE.txt`.
- Build: verified download under ignored `artifacts/dependencies/`; merged/internalized into `YAVSOS.dll`. No dependency binary/source is tracked. Its notice is copied into plugin output as `ServerSync-LICENSE.txt`.
- Checked: Linux build, checksum, assembly metadata and packaging; client, dedicated-server and hosted runs, including alongside another mod's bundled ServerSync (policy checks P01–P10, 2026-10-12, and every later test session).

## MultiUserChest (vendored source)

- Upstream: https://github.com/MSchmoecker/No-Chest-Block at `e85141286f755652827934b58d8052861ad89c34` (0.6.2).
- License: MIT, Copyright (c) 2022 Maximilian Schmöcker ([unaltered notice](MultiUserChest/LICENSE.txt)), copied into plugin output as `MultiUserChest-LICENSE.txt`.
- Source is tracked under `src/YAVSOS.MultiUserChest/`, merged/internalized into `YAVSOS.dll`, and active only when no other MultiUserChest copy is (D057, D061). Local changes: [CHANGES.md](MultiUserChest/CHANGES.md).

## Keeping the bundled copies current (D090)

ServerSync and MultiUserChest are pinned in [upstream-pins.json](upstream-pins.json). `python3 scripts/bundled-deps/bundled_deps.py` compares each pin with its GitHub repository: newer releases, commits after the pinned commit, and open issues updated since `issuesReviewedThrough` (marked "copied code" when they name copied types or methods). It runs in the pre-session check and before each release. A finding is a question for the maintainer; nothing updates automatically.

After reading new issues, record each one in `trackedIssues` with its effect on YAVSOS and move `issuesReviewedThrough` forward. When a bundled copy has a bug that upstream already tracks, wait for upstream's fix and take it with the next pin update instead of patching the copy (D090).

**Updating ServerSync:**

1. Read the release notes and the diff between the pinned and new tag, looking for wire-format, `ConfigSync`/`VersionCheck`, RPC registration and save-path changes. YAVSOS couples to its private publication delegate (D011) and to the `VersionCheck` wire order (D010).
2. Change the release URL, SHA-256 and error text in `src/YAVSOS/ServerSync.targets`, delete the cached `artifacts/dependencies/ServerSync/ServerSync.dll`, update this file and the tag and commit in `upstream-pins.json`, and copy the release's unaltered `LICENSE.txt` if it changed.
3. Build, and run the detached tests and `scripts/verify-build.py`.
4. Runtime: the policy checks P01, P02, P06–P10: managed join, lock and admin propagation, version refusal, rejoin and unmanaged servers, and P09 (another mod's bundled ServerSync alongside). Raise YAVSOS's minimum managed version if the wire changed.
5. Record the new pin here and in the project's decision and validation records (D009).

**Updating MultiUserChest:** see [CHANGES.md](MultiUserChest/CHANGES.md#updating), and update `upstream-pins.json`.

## Krafs.Publicizer

- Build-only package **2.3.0** (MIT), content hash in `src/YAVSOS.MultiUserChest/packages.lock.json`. It lets the vendored MultiUserChest source compile against a publicized `assembly_valheim`, as upstream does. Not distributed.

## ILRepack

- Build-only package: `ILRepack.Lib.MSBuild.Task` **2.0.48**, with NuGet content hash recorded in `src/YAVSOS/packages.lock.json`.
- Upstream: https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task ; merger: https://github.com/gluck/il-repack.
- Task package license: MIT, as declared by NuGet; the underlying ILRepack merger uses Apache-2.0. This build tool is not distributed as a plugin dependency.

Test-only xUnit/VSTest packages are pinned in the test project and its NuGet lock file. They are excluded from plugin output.
