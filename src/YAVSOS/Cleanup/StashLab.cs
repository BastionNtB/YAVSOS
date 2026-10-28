using System;
using System.Linq;
using HarmonyLib;
using YAVSOS.Transfers;
using YAVSOS.Vendor.MultiUserChest;

namespace YAVSOS.Cleanup;

/// <summary>
/// Lab entry points for automated Phase 5 runtime tests (called through the test bridge's debug/call_method), in
/// disposable lab worlds only; never used by gameplay code. Chest fixtures are <see cref="CleanupLab"/>'s and
/// <see cref="RuleLab"/>'s; these act on the local player's inventory and on the stash.
/// </summary>
internal static class StashLab
{
    public static string Run(float radius) => Plugin.Instance?.Stash.Start(radius > 0 ? radius : null) ?? "not loaded";

    /// <summary>The single-item key on the first stack of the item in the player's inventory.</summary>
    public static string RunSingle(string prefab)
    {
        var item = Find(prefab);
        return item == null ? "not in inventory" : Plugin.Instance?.Stash.Start(single: item) ?? "not loaded";
    }

    public static string Status() => Plugin.Instance?.Stash is { } runner
        ? $"enabled={StashRunner.Enabled}; running={runner.Running}; progress={runner.Progress}; last={runner.LastSummary}; " +
          $"muc={(MucRoute.Available(out var source) ? source : "none")}; bundled={MucRoute.BundledStatus}; active={BundledCopy.Active}; probe={NativeInventoryProbe.Status}"
        : "not loaded";

    /// <summary>Fixture: add a stack to the player's inventory at a cell (-1, -1: the first free one), with optional metadata.</summary>
    public static string Give(string prefab, int count, int quality, string crafter, string customData, int x, int y)
    {
        var player = Player.m_localPlayer;
        var shared = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
        if (!player || shared == null) return "no player or unknown item";
        var inventory = player!.GetInventory();
        if (count < 1 || count > shared.m_maxStackSize) return "refused: one stack only";
        // "@me": crafted by the local player (their real player ID and name), for the crafter filter (D066).
        if (crafter == "@me") crafter = player!.GetPlayerName();
        var crafterId = string.IsNullOrEmpty(crafter) ? 0L
            : crafter == player!.GetPlayerName() ? player.GetPlayerID() : (long)StringExtensionMethods.GetStableHashCode(crafter);
        if (x >= 0 && (x >= inventory.GetWidth() || y < 0 || y >= inventory.GetHeight() || inventory.GetItemAt(x, y) != null)) return "refused: cell taken";
        if (!inventory.HaveEmptySlot()) return "refused: inventory full";
        // AddItem tops up any partial stack the game merges with (D061 Q11), so a partial stack of the item would take some units.
        if (inventory.GetAllItems().Any(x => x.m_shared.m_name == shared.m_name && x.m_stack < shared.m_maxStackSize))
            return "refused: a partial stack of it would merge";
        // Add to a free cell (never merged into a stack the test did not ask for), then move it to the requested cell.
        var before = inventory.GetAllItems().ToList();
        var item = inventory.AddItem(prefab, count, Math.Max(1, quality), 0, crafterId, crafter ?? "", false);
        if (item == null) return "not added";
        if (before.Contains(item)) return "refused: merged into an existing stack";
        foreach (var pair in (customData ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(new[] { '=' }, 2)))
            item.m_customData[pair[0]] = pair.Length > 1 ? pair[1] : "";
        // MoveItemToThis places a clone at the target cell and removes the original, so read the cell back.
        if (x >= 0) { inventory.MoveItemToThis(inventory, item, item.m_stack, x, y); item = inventory.GetItemAt(x, y); }
        AccessTools.Method(typeof(Inventory), "Changed")?.Invoke(inventory, new object[] { false, false });
        if (item == null) return "not moved to the requested cell";
        return $"added {item.m_stack} {prefab} at ({item.m_gridPos.x},{item.m_gridPos.y})";
    }

    /// <summary>
    /// Fixture (P8-09 M3): move the first stack of the item to cell (x, y). <paramref name="sameObject"/>: only its cell
    /// changes (as a swap or another mod may do); otherwise the game's own drag (MoveItemToThis, a new stack object).
    /// </summary>
    public static string Drag(string prefab, int x, int y, bool sameObject)
    {
        var player = Player.m_localPlayer; var item = Find(prefab);
        if (!player || item == null) return "no player or item";
        var inventory = player!.GetInventory();
        if (inventory.GetItemAt(x, y) != null) return "refused: cell taken";
        if (sameObject) item.m_gridPos = new Vector2i(x, y);
        else inventory.MoveItemToThis(inventory, item, item.m_stack, x, y);
        AccessTools.Method(typeof(Inventory), "Changed")?.Invoke(inventory, new object[] { false, false });
        var at = inventory.GetItemAt(x, y);
        return at == null ? "not moved" : $"{prefab} at ({x},{y}); same object {ReferenceEquals(at, item)}";
    }

    /// <summary>Fixture: equip the first stack of the item in the player's inventory.</summary>
    public static string Equip(string prefab)
    {
        var item = Find(prefab);
        return item == null ? "not in inventory" : "equipped=" + Player.m_localPlayer.EquipItem(item, false);
    }

    private const string BackpackData = "vapok.mods.adventurebackpacks#AdventureBackpacks.Components.BackpackComponent";

    /// <summary>
    /// Fixture (P6-16 c): put a stack into the first AdventureBackpacks backpack the player carries, through its public
    /// <c>ABAPI.GetBackpackInventory</c>, then write the contents to the item's data the way the backpack saves them.
    /// </summary>
    public static string FillBackpack(string prefab, int count)
    {
        var player = Player.m_localPlayer;
        var api = AccessTools.TypeByName("AdventureBackpacks.API.ABAPI");
        var get = api == null ? null : AccessTools.DeclaredMethod(api, "GetBackpackInventory", new[] { typeof(ItemDrop.ItemData) });
        if (!player || get == null) return "no player or AdventureBackpacks";
        var backpack = player!.GetInventory().GetAllItems().FirstOrDefault(x => x.m_customData.ContainsKey(BackpackData));
        if (backpack == null) return "no backpack";
        if (get.Invoke(null, new object[] { backpack }) is not Inventory inside) return "backpack has no inventory";
        if (inside.AddItem(prefab, count, 1, 0, 0, "", false) == null) return "not added";
        var package = new ZPackage(); inside.Save(package);
        backpack.m_customData[BackpackData] = package.GetBase64();
        return "backpack holds " + Describe(inside);
    }

    /// <summary>What every backpack in the player's inventory ("player") or the lab's target chest holds, read from its saved data.</summary>
    public static string BackpackContents(string where)
    {
        var inventory = where == "player" ? (Player.m_localPlayer ? Player.m_localPlayer.GetInventory() : null) : RuleLab.Target()?.GetInventory();
        if (inventory == null) return "no inventory";
        return string.Join("; ", inventory.GetAllItems().Where(x => x.m_customData.ContainsKey(BackpackData)).Select(x =>
        {
            var read = new Inventory("lab", null, 8, 8);
            read.Load(new ZPackage(x.m_customData[BackpackData]));
            return x.m_dropPrefab.name + "@(" + x.m_gridPos.x + "," + x.m_gridPos.y + ")=" + Describe(read);
        }));
    }

    /// <summary>Fixture: move the player's first stack of the item into the lab's target chest (MoveItemToThis, as a drag does); refuses an equipped one.</summary>
    public static string Put(string prefab)
    {
        var player = Player.m_localPlayer; var chest = RuleLab.Target();
        var item = Find(prefab);
        if (!player || !chest || item == null) return "no player, chest or item";
        // A raw move keeps m_equipped: the chest's copy would read as equipped and stay protected (lab, 2026-10-23).
        if (item.m_equipped) return "refused: equipped; unequip it first (player/unequip_item)";
        chest!.GetInventory().MoveItemToThis(player!.GetInventory(), item);
        return player.GetInventory().ContainsItem(item) ? "refused" : "moved into " + chest.name;
    }

    private static string Describe(Inventory inventory) =>
        "[" + string.Join(",", inventory.GetAllItems().Select(x => x.m_dropPrefab.name + ":" + x.m_stack)) + "]";

    /// <summary>Fixture: destroy every unequipped stack in the player's inventory (never dropped).</summary>
    public static string ClearInventory()
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var inventory = player!.GetInventory();
        var removed = 0;
        foreach (var item in inventory.GetAllItems().Where(x => !x.m_equipped).ToArray()) { inventory.RemoveItem(item); removed++; }
        return "removed " + removed;
    }

    /// <summary>Fixture (D061 Q8): overwrite the item format this peer publishes (2, 1 or 0 for unknown).</summary>
    public static string Publish(int format)
    {
        var zdo = Player.m_localPlayer ? Player.m_localPlayer.GetComponent<ZNetView>()?.GetZDO() : null;
        if (zdo == null || !zdo.IsOwner()) return "no player";
        zdo.Set(MucRoute.FormatKeyName.GetStableHashCode(), format);
        return "published " + format;
    }

    /// <summary>Diagnostics: every other player in range, their peer id and published format.</summary>
    public static string Peers() => string.Join("; ", Player.GetAllPlayers().Where(x => x && x != Player.m_localPlayer).Select(x =>
    {
        var zdo = x.GetComponent<ZNetView>()?.GetZDO();
        return zdo == null ? x.GetPlayerName() + ": no zdo"
            : $"{x.GetPlayerName()}: peer {zdo.GetOwner()}, format {zdo.GetInt(MucRoute.FormatKeyName.GetStableHashCode())}, known {PeerFormat.Known(zdo.GetOwner())}";
    }));

    private static ItemDrop.ItemData? Find(string prefab) => Player.m_localPlayer
        ? Player.m_localPlayer.GetInventory().GetAllItems().FirstOrDefault(x => x?.m_dropPrefab && x.m_dropPrefab.name == prefab) : null;
}
