# MultiUserChest: vendored copy and local changes

- Upstream: https://github.com/MSchmoecker/No-Chest-Block (published as MultiUserChest on Thunderstore and Nexus).
- Pinned commit: `e85141286f755652827934b58d8052861ad89c34`, "Update to 0.6.2" (2026-09-12).
- License: MIT, Copyright (c) 2022 Maximilian Schmöcker ([LICENSE.txt](LICENSE.txt), copied unaltered). The notice ships next to `YAVSOS.dll` as `MultiUserChest-LICENSE.txt`.
- Location: `src/YAVSOS.MultiUserChest/Upstream/` is upstream's `MultiUserChest/` source folder without `Plugin.cs` and `Properties/`. `src/YAVSOS.MultiUserChest/Bundle/` is YAVSOS's own code. The project is merged and internalized into `YAVSOS.dll`; it has no plugin GUID of its own.
- Why: D057 and D061. YAVSOS's copy runs only when neither standalone MultiUserChest, AzuAutoStore nor a mod both of those step aside for (`quick_stack`, `SimpleSort`, `QuickStore`) is installed.

## Local changes

Every change in `Upstream/` is marked `YAVSOS-LOCAL`. [local-changes.diff](local-changes.diff) is the full diff against upstream after the namespace rename.

1. **Namespace** `MultiUserChest` → `YAVSOS.Vendor.MultiUserChest` in every file, so the types never collide with a standalone copy.
2. **No `Plugin` class.** `Bundle/BundledCopy.cs` applies the same patches under YAVSOS's Harmony id `com.bastionntb.yavsos.multiuserchest`: every annotated patch class (upstream's `PatchAll`), then `ItemDrawerCompat` when `mkz.itemdrawers` is loaded (upstream's `ApplyModPatches`). Upstream's Jotunn `NetworkCompatibility` attribute does not apply; YAVSOS's own version check covers YAVSOS.
3. *Reverted 2026-10-27 (D090).* This used to read the prefab in `InventoryHelper.ClampStackSize` from `ObjectDB` instead of Jotunn's `PrefabManager.Cache`, to avoid a Jotunn dependency. Jotunn is required since D062, so the copy uses upstream's code again; the project references Jotunn (not merged). Numbering kept so D090's "item 4" still points at the right change.
4. **Per-peer item format** (`Bundle/PeerFormat.cs`, D061 Q8). AzuAutoStore 3.1.7's copy writes items without `m_cheated`; 0.6.2 writes it. This copy:
   - writes each request in the receiving owner's format (`GamePatches.InvokeRPC`);
   - reads each request, and writes its response, in the sender's format (`ContainerRPCHandler`);
   - reads each response in the responder's format (`InventoryHandler`);
   - refuses a move, consume or drop when the other owner's format is unknown, instead of sending a request it might misread (`InventoryPatch.InterceptAddItem`, `InventoryGuiPatch`).
   YAVSOS publishes every peer's format on its player object (`YAVSOS.Cleanup.MucRoute`).

## Updating

Re-vendor from a new upstream commit, reapply the changes above, regenerate `local-changes.diff`, and rerun the Phase 5 MultiUserChest checks (two clients, including one with AzuAutoStore) before release.
