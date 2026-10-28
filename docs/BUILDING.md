# Building and local development

## Prerequisites

- .NET SDK 10. `global.json` selects a stable 10.0 SDK; the plugin runtime target remains `netstandard2.1`.
- Valheim and BepInExPack Valheim for plugin reference assemblies and in-game checks.
- On Arch Linux, the installed .NET Standard targeting pack. If missing, install `dotnet-sdk` and `netstandard-targeting-pack` through the package manager.

Unity Editor, publicized assemblies, a server connection, and optional mods are not needed to compile. The plugin references the installed BepInEx Harmony DLL and game's networking/TextMeshPro assemblies. ServerSync v1.20 is downloaded and checksum-verified by the build; ILRepack 2.0.48 comes from NuGet and internalizes it. See [dependency provenance](../third_party/README.md). Initial dependency restore needs network access; subsequent builds can use verified cached dependencies.

## Reference paths

Copy `BuildPaths.example.props` to `BuildPaths.local.props` and set `ValheimDir` and `BepInExDir`. The latter is the BepInEx directory containing `core/`; for Gale it usually belongs to a profile. `ValheimManagedDir` can override the usual `valheim_Data/Managed` layout.

The plugin also references Jotunn, a required runtime dependency since D062 (the rule panel is built on its `GUIManager`). `JotunnPath` defaults to `$(BepInExDir)/plugins/ValheimModding-Jotunn/Jotunn.dll`; set it in `BuildPaths.local.props` to use another installed copy, for example a Gale test profile's. Jotunn is referenced only: it is not merged into `YAVSOS.dll` or copied beside it, and `scripts/verify-build.py` checks both.

The local file is ignored. Game/BepInEx/Jotunn references have `Private=false` and are not packaged with YAVSOS. Missing references produce a targeted build error. Command-line MSBuild properties can override the local file:

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
- Runtime core dependency is copied beside the plugin: `YAVSOS.Core.dll`. Both must accompany an eventual manual test installation.
- ServerSync is internalized into `YAVSOS.dll`; no standalone `ServerSync.dll`, ILRepack, game, BepInEx, Harmony or Jotunn DLL is distributed. Players install Jotunn themselves (D062). `ServerSync-LICENSE.txt` accompanies the DLLs.
- Core project's own output: `artifacts/build/YAVSOS.Core/Release/netstandard2.1/`.
- Intermediate files: `artifacts/obj/<project>/`.
- Store packages: `artifacts/package/<version>/` (see [Packages and releases](#packages-and-releases)).

No command here installs a DLL, changes Gale or deploys to a server, and none publishes a release unless `scripts/release.py --publish` is given on purpose. Runtime tests need a separate test profile, a disposable world and an explicit deployment step.

## Future CI and packaging

Pure core tests run without proprietary game references. Plugin CI must obtain those references from a controlled runner rather than commit them. NuGet content/version locks are checked in beside the plugin and tests; CI can restore with `--locked-mode`. `scripts/verify-build.py` checks runtime DLL allowlisting, internalized ServerSync, identity/version alignment, dependency checksum, coupling metadata names, and detached core references. It inspects build output only; `scripts/package.py` checks the packages.

## Packages and releases

```sh
# Store packages from the verified Release build (nothing published):
python3 scripts/package.py [--media-url <public repository assets/media URL>] [--repo-url <public repository URL>] [--strict]

# The release action. Without --publish it is the dry run: check, build, tests,
# verify-build, package, and with --validate-online Thunderstore's validators:
python3 scripts/release.py --validate-online
python3 scripts/release.py --tag v1.0.0 --media-url <url> --repo-url <url> --publish thunderstore,hexium
```

- `artifacts/package/<version>/`: `YAVSOS-<version>-thunderstore.zip` (Thunderstore and Hexium),
  `YAVSOS-<version>-nexus.zip`, the store `README.md` and `nexus-description.bbcode.txt`.
- The store text is the player guide with mechanical changes only (D099), the same text as
  the public repository's `README.md` (D102); its links to repository files point at the public
  repository at the release tag (`--repo-url`). The store changelog is the `## x.y.z` sections of `CHANGELOG.md`. Package name, namespace,
  dependencies and files: `packaging/package.json`.
- Publishing refuses a dirty tree, a CHANGELOG entry still marked `(unreleased)`, a media slot
  without its image, a link left relative, and `--skip-build`. Thunderstore goes before Hexium. Nexus Mods is
  uploaded by the public repository's workflow; this script builds its zip.
- **Credentials** (names only; values never in files here, logs or command lines), read as
  data from `~/.config/tokens/` or from environment variables of the same names (repository
  secrets in CI):

  | File | Variables |
  | --- | --- |
  | `thunderstore.env` | `THUNDERSTORE_URL`, `THUNDERSTORE_COMMUNITY`, `THUNDERSTORE_NAMESPACE`, `THUNDERSTORE_TOKEN` |
  | `hexium.env` | `HEXIUM_URL`, `HEXIUM_COMMUNITY`, `HEXIUM_NAMESPACE`, `HEXIUM_TOKEN` |
  | `nexusmods.env` | `NEXUSMODS_GAME`, `NEXUSMODS_MOD_ID`, `NEXUSMODS_FILE_ID`, `NEXUSMODS_API_KEY` (for the workflow) |
