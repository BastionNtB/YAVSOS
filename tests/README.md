# Tests

`YAVSOS.Core.Tests` is the detached suite: xUnit 2 on .NET 10, pinned in the project's NuGet
lock file. It references only `YAVSOS.Core` (plus a few dependency-free adapter files linked
from the plugin), so it runs without Unity, Valheim, BepInEx, game profiles or a network.
Tests use in-memory streams and isolated temporary directories.

```sh
dotnet test tests/YAVSOS.Core.Tests/YAVSOS.Core.Tests.csproj --configuration Release
# or the whole solution:
dotnet test YAVSOS.sln --configuration Release
```

The suite covers the parts that decide things: category inference and the categories file,
chest rules and their saved format, filters, ranking and destination choice, stash and cleanup
planning, move results and the MultiUserChest observer, the client/server policy and category
exchange (formats, versions, stale or malformed data), capability checks for optional mods,
and the panel's text and layout rules.

Passing tests prove that behaviour, not runtime compatibility. Game hooks, other mods,
ServerSync, and anything that moves items between players also need runtime checks in the
game; a change to those paths should say which checks it ran (see `CONTRIBUTING.md`).
