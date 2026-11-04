# Building and local development

## Prerequisites

- .NET SDK 10. `global.json` selects a stable 10.0 SDK; the plugin still targets `netstandard2.1`.
- Valheim and BepInExPack Valheim for plugin reference assemblies and in-game checks.
- On Arch Linux, the .NET Standard targeting pack. If it is missing, install `dotnet-sdk` and `netstandard-targeting-pack` with the package manager.

Compiling needs no Unity Editor, publicized assemblies, server connection or optional mods. The plugin references the installed BepInEx Harmony DLL and the game's networking and TextMeshPro assemblies. The build downloads ServerSync v1.20 and verifies its checksum; ILRepack 2.0.48 comes from NuGet and internalizes it. See [dependency provenance](../third_party/README.md). The first restore needs network access; later builds can use the verified cached dependencies.

## Reference paths

Copy `BuildPaths.example.props` to `BuildPaths.local.props` and set `ValheimDir` and `BepInExDir`. `BepInExDir` is the BepInEx directory that contains `core/`; with Gale it usually belongs to a profile. `ValheimManagedDir` overrides the usual `valheim_Data/Managed` layout.

The plugin also references Jotunn, a required runtime dependency since D062 (the YAVSOS panel is built on its `GUIManager`). `JotunnPath` defaults to `$(BepInExDir)/plugins/ValheimModding-Jotunn/Jotunn.dll`; set it in `BuildPaths.local.props` to use another installed copy, such as a Gale test profile's. Jotunn is only referenced, neither merged into `YAVSOS.dll` nor copied beside it, and `scripts/verify-build.py` checks both.

Git ignores the local file. Game, BepInEx and Jotunn references have `Private=false` and are not packaged with YAVSOS. A missing reference stops the build with an error that names it. MSBuild properties on the command line override the local file:

```sh
./scripts/build.sh -p:ValheimDir=/path/to/Valheim -p:BepInExDir=/path/to/profile/BepInEx
```

## Commands and artifacts

```sh
./scripts/build.sh
# Equivalent:
dotnet build YAVSOS.sln --configuration Release

# Build the detached core without game references:
dotnet build src/YAVSOS.Core/YAVSOS.Core.csproj --configuration Release

# Detached parser/lifecycle/adapter tests (no proprietary references required):
dotnet test tests/YAVSOS.Core.Tests/YAVSOS.Core.Tests.csproj --configuration Release
# Solution test discovery uses SDK 10's default VSTest command mode:
dotnet test YAVSOS.sln --configuration Release

# Assembly/runtime-output validation (requires monodis from Mono tools):
python3 scripts/verify-build.py
```

- Plugin: `artifacts/build/YAVSOS/Release/netstandard2.1/YAVSOS.dll`.
- The core library, `YAVSOS.Core.dll`, is copied beside the plugin. A manual test installation needs both.
- ServerSync and the bundled MultiUserChest are internalized into `YAVSOS.dll`; no standalone `ServerSync.dll`, ILRepack, game, BepInEx, Harmony or Jotunn DLL is distributed. Players install Jotunn themselves (D062). `YAVSOS-LICENSE.txt`, `ServerSync-LICENSE.txt` and `MultiUserChest-LICENSE.txt` accompany the DLLs.
- The core project's own output: `artifacts/build/YAVSOS.Core/Release/netstandard2.1/`.
- Player text: the English file `src/YAVSOS/Translations/English/yavsos.json` is embedded in `YAVSOS.dll`, not shipped loose; `src/YAVSOS/Translations/notes.json` holds the translator notes. Setting descriptions are messages there too; `scripts/settings_doc.py` writes `docs/SETTINGS.md` from them (`--check` reports a stale copy). To let Git merge these files key by key with `scripts/messages_merge.py`, register the merge driver once per clone:

  ```bash
  git config merge.yavsos-messages.name "YAVSOS message files"
  git config merge.yavsos-messages.driver "python3 -I scripts/messages_merge.py %O %A %B"
  ```
- Intermediate files: `artifacts/obj/<project>/`.
- Store packages: `artifacts/package/<version>/` (see [Packages and releases](#packages-and-releases)).

No command here installs a DLL, changes Gale or deploys to a server, and none publishes a release unless you pass `scripts/release.py --publish` on purpose. Runtime tests need a separate test profile, a disposable world and a deliberate deployment step.

## Future CI and packaging

The core tests run without proprietary game references. Plugin CI must get those references from a controlled runner instead of committing them. NuGet lock files with versions and content hashes are checked in beside the plugin and tests; CI can restore with `--locked-mode`. `scripts/verify-build.py` checks the build: only the allowed DLLs and licence files, ServerSync and MultiUserChest internalized, matching identity and versions, the ServerSync checksum and the ServerSync members YAVSOS couples to, and a core library with no game, Unity, BepInEx, Harmony or ServerSync reference. It doesn't open the packages; `scripts/package.py` checks those.

## Packages and releases

```sh
# Store packages from the verified Release build (nothing published):
python3 scripts/package.py [--media-url <public repository assets/media URL>] [--repo-url <public repository URL>] [--strict]

# The release action. Without --publish it is the dry run: check, build, tests,
# verify-build, package, and with --validate-online Thunderstore's validators, the Nexus
# key, mod and file, and the tag on the public repository (all read-only):
python3 scripts/release.py --validate-online --tag v1.0.1 --repo-url <url>
python3 scripts/release.py --tag v1.0.1 --media-url <url> --repo-url <url> --publish thunderstore,hexium,nexus,github
```

- `artifacts/package/<version>/`: `YAVSOS-<version>-thunderstore.zip` (Thunderstore and Hexium),
  `YAVSOS-<version>-nexus.zip`, the store `README.md` and `nexus-description.bbcode.txt`.
- The store text is the player guide with mechanical changes only (D099), the same text as
  the public repository's `README.md` (D102); its links to repository files point at the public
  repository at the release tag (`--repo-url`). The store changelog is the `## x.y.z` sections of `CHANGELOG.md`. Package name, namespace,
  dependencies and files: `packaging/package.json`.
- Publishing refuses a dirty tree, a CHANGELOG entry still marked `(unreleased)`, a media or art
  slot without its image, a link left relative, and `--skip-build`. Order: Thunderstore, Hexium, Nexus Mods (a new
  version of the existing main file through Nexus's v3 upload API, the old one archived, then the
  changelog entry), then the GitHub release with both zips (gh CLI login). The public repository
  must have the tag first.
- **Credentials** (names only; values never in files here, logs or command lines), read as
  data from `~/.config/tokens/` or from environment variables of the same names (repository
  secrets in CI):

  | File | Variables |
  | --- | --- |
  | `thunderstore.env` | `THUNDERSTORE_URL`, `THUNDERSTORE_COMMUNITY`, `THUNDERSTORE_NAMESPACE`, `THUNDERSTORE_TOKEN` |
  | `hexium.env` | `HEXIUM_URL`, `HEXIUM_COMMUNITY`, `HEXIUM_NAMESPACE`, `HEXIUM_TOKEN` |
  | `nexusmods.env` | `NEXUSMODS_GAME`, `NEXUSMODS_MOD_ID`, `NEXUSMODS_FILE_ID`, `NEXUSMODS_API_KEY` (MOD_ID: the page number; FILE_ID: the "File ID" under the file's Advanced details; the personal API key) |
