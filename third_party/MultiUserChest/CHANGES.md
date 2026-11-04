# MultiUserChest: vendored copy and local changes

- Upstream: https://github.com/MSchmoecker/No-Chest-Block (published as MultiUserChest on Thunderstore and Nexus).
- Pinned commit: `e85141286f755652827934b58d8052861ad89c34`, "Update to 0.6.2" (2026-09-12).
- License: MIT, Copyright (c) 2022 Maximilian Schmöcker ([LICENSE.txt](LICENSE.txt), copied unaltered). The notice ships next to `YAVSOS.dll` as `MultiUserChest-LICENSE.txt`.
- Location: `src/YAVSOS.MultiUserChest/Upstream/` is upstream's `MultiUserChest/` source folder without `Plugin.cs` and `Properties/`. `src/YAVSOS.MultiUserChest/Bundle/` is YAVSOS's own code. The project is merged and internalized into `YAVSOS.dll`; it has no plugin GUID of its own.
- Why: D057 and D061. YAVSOS's copy runs only when none of these is installed: standalone MultiUserChest, AzuAutoStore, or a mod both of those step aside for (`quick_stack`, `SimpleSort`, `QuickStore`).

## Local changes

Every change in `Upstream/` is marked `YAVSOS-LOCAL`. [local-changes.diff](local-changes.diff) is the full diff against upstream after the namespace rename.

1. **Namespace** `MultiUserChest` → `YAVSOS.Vendor.MultiUserChest` in every file, so the types never collide with a standalone copy.
2. **No `Plugin` class.** `Bundle/BundledCopy.cs` applies the same patches under YAVSOS's Harmony ID `com.bastionntb.yavsos.multiuserchest`: every annotated patch class (upstream's `PatchAll`), then `ItemDrawerCompat` when `mkz.itemdrawers` is loaded (upstream's `ApplyModPatches`). Upstream's Jotunn `NetworkCompatibility` attribute does not apply; YAVSOS's own version check covers YAVSOS.
3. *Reverted 2026-10-27 (D090).* This change made `InventoryHelper.ClampStackSize` read the prefab from `ObjectDB` instead of Jotunn's `PrefabManager.Cache`, to avoid a Jotunn dependency. Jotunn is required since D062, so the copy uses upstream's code again, and the project references Jotunn (not merged). The numbering stays so that D090's "item 4" still points at the right change.
4. **Per-peer item format** (`Bundle/PeerFormat.cs`, D061 Q8). AzuAutoStore 3.1.7's copy writes items without `m_cheated`; 0.6.2 writes it. This copy:
   - writes each request in the receiving owner's format (`GamePatches.InvokeRPC`);
   - reads each request, and writes its response, in the sender's format (`ContainerRPCHandler`);
   - reads each response in the responder's format (`InventoryHandler`);
   - refuses a move, consume or drop when the other owner's format is unknown, instead of sending a request the owner might misread (`InventoryPatch.InterceptAddItem`, `InventoryGuiPatch`);
   - **guards answers to a sender whose format is unknown** (D116, 1.0.2; `Bundle/UnknownFormatGuard.cs`, `ContainerRPCHandler`). Such a sender is a player with AzuAutoStore but not YAVSOS, on a server with neither mod. The copy answers in format 2, which AzuAutoStore 3.1.7's copy reads correctly only for a plain item (no cheated flag, no custom data). A remove, consume or drop of any other item is refused before anything leaves the chest, with upstream's failure answer: no item, or, for a swap, the sender's own switch item, as upstream's refusal of a swap does. A request that can't be read (every add and every swap from AzuAutoStore 3.1.7's copy) is dropped unanswered, with nothing added or removed, as upstream's exception did; AzuAutoStore's copy gives its item back after about 30 seconds. Each cause is logged once per player instead of as an exception per request. Without the guard, an item with custom data taken from such a chest was lost (X19 research §5.1).
   - **measures the format of a player who publishes none** (D116, 1.1; `Bundle/FormatProbe.cs`, `Bundle/PeerProbe.cs`, `InventoryHandler`). When such a player's character appears, this copy sends their peer a `MUC_RequestItemRemove` for container `ZDOID.None`, carrying one plain Wood written in format 2. Every known copy takes its "no such chest" path, touches no inventory and sends the Wood back in its own format. After `m_pickedUp`, exactly 4 zero bytes mean format 1 and exactly 5 mean format 2; any other answer, or none, leaves the format unknown, so the refusals and the guard above stay in force. An answer counts only when its request ID, prefab, stack and fallback fields match. A peer gets at most 3 probes per session (10 s timeout, retries 30 s and 90 s apart); the result is kept by peer ID and dropped when the player leaves or the world closes. A published key always wins, and a player who publishes 0 is not probed. Every answer for container `ZDOID.None` belongs to a probe: each response handler in `InventoryHandler` returns before reading it, so a probe answer is never applied. Probes go out only while this copy is active, since AzuAutoStore's handler would add the echoed Wood to the inventory.

   YAVSOS publishes every peer's format on its player object (`YAVSOS.Cleanup.MucRoute`), or 0 when its copy can't take requests (D086 D13).

   **Removal condition** (D090, extended by the X19 research §7; covers the guard and the probe): MultiUserChest itself handles both formats with a format or version marker in each message, or every copy players still use writes the 0.6.2 format **and** YAVSOS has another way to tell whether a peer has a working copy at all. An AzuAutoStore release with the 0.6.2 format is not enough on its own while older AzuAutoStore builds stay in use.

## Updating

Copy the source again from a new upstream commit, reapply the changes above, regenerate `local-changes.diff`, and before release rerun the Phase 5 MultiUserChest checks (two clients, including one with AzuAutoStore).
