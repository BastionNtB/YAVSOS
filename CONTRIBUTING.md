# Contributing

Bugs, questions and ideas go to the issue forms. For code, read [DESIGN.md](docs/DESIGN.md) first: it explains how YAVSOS works and why, including the four pillars (efficiency, compatibility, extensibility and multiplayer safety) every change is weighed against. Build with [BUILDING.md](docs/BUILDING.md).

Use small changes that establish one reviewable behavior at a time. Keep planning, classification and policy logic in `src/YAVSOS.Core`, without Unity, BepInEx, game or optional-mod dependencies; game adapters, plugin lifecycle, interface and networking belong in `src/YAVSOS`. Explain why an integration or API is needed, how it fails safely when the other mod is absent or incompatible, and which saved-data or multiplayer assumptions it changes. Gate features on capability checks, not version numbers. Anything that moves items needs an authority design and conservation checks.

Keep local game references, credentials, logs, worlds and generated binaries out of commits. Add tests that verify rules and invariants rather than repeat implementation details.

A change description should state the concrete before/after behavior, performance implications, compatibility boundaries, migration needs and the validation performed. Do not describe planned features or unperformed multiplayer checks as complete.
