# Contributing

Bugs, questions and ideas go to the issue forms. Before writing code, read [DESIGN.md](docs/DESIGN.md): it explains how YAVSOS works and why, including the four pillars every change is weighed against (efficiency, compatibility, extensibility and multiplayer safety). To build, follow [BUILDING.md](docs/BUILDING.md).

Keep each change small enough to review as one behavior. Planning, classification and policy logic belong in `src/YAVSOS.Core`, with no Unity, BepInEx, game or optional-mod dependencies; game adapters, plugin lifecycle, interface and networking belong in `src/YAVSOS`. For an integration or a new API, explain why it is needed, how it fails safely when the other mod is missing or incompatible, and which saved-data or multiplayer assumptions it changes. Gate features on capability checks, not version numbers. Anything that moves items needs an authority design and conservation checks.

Text a player sees goes in the English message file `src/YAVSOS/Translations/English/yavsos.json`, with a note in `src/YAVSOS/Translations/notes.json`; code uses the message key, never English. The detached tests check that every key the code uses exists and has a note. Log lines stay in English.

Keep local game references, credentials, logs, worlds and generated binaries out of commits. Write tests that check rules and invariants, not implementation details.

A change description states the behavior before and after, the performance cost, where compatibility ends, any migration, and the checks you ran. Don't describe planned features or multiplayer checks you haven't run as done.
