using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Transfers;

namespace YAVSOS.Cleanup;

/// <summary>
/// Lab entry points for automated runtime tests (called through the test bridge's debug/call_method). The fixtures
/// (P4-05a) build test chests and fill them in disposable lab worlds; they are never used by gameplay code.
/// The chest they act on is <see cref="RuleLab"/>'s target (the open chest, else the last selected one).
/// </summary>
internal static class CleanupLab
{
    public static string Run(float radius) => Plugin.Instance?.Cleanup.Start(radius > 0 ? radius : null) ?? "not loaded";
    public static string Status() => Plugin.Instance?.Cleanup is { } runner
        ? $"running={runner.Running}; progress={runner.Progress}; last={runner.LastSummary}; probe={NativeInventoryProbe.Status}; " +
          $"azuExclusions={Integrations.AzuExclusions.State}; azuPause={Integrations.AzuExclusions.PauseState}; hold={CleanupRunner.DiagnosticHold}"
        : "not loaded";

    /// <summary>Fixture: hold the run's moves; <see cref="Step"/> lets that many more start. Remember to release.</summary>
    public static string Hold(bool on) { CleanupRunner.DiagnosticHold = on; CleanupRunner.DiagnosticSteps = 0; return "hold=" + on; }
    /// <summary>Fixture (A01): set this peer's AllowCleanup, for a local world where the player's own Server settings apply.</summary>
    public static string Allow(bool on)
    {
        if (Plugin.Instance is not { } plugin) return "not loaded";
        plugin.SettingsForLab.AllowCleanup.Value = on;
        return "allowCleanup=" + plugin.SettingsForLab.AllowCleanup.Value;
    }
    public static string Step(int moves) { CleanupRunner.DiagnosticSteps += Math.Max(0, moves); return "steps=" + CleanupRunner.DiagnosticSteps; }

    /// <summary>Fixture: place a piece (for example piece_chest_wood) the vanilla way, at an offset from the player.</summary>
    public static string Build(string piecePrefab, float right, float forward)
    {
        var player = Player.m_localPlayer;
        var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(piecePrefab) : null;
        var piece = prefab ? prefab!.GetComponent<Piece>() : null;
        if (!player || piece == null) return "no player or piece";
        var t = player.transform;
        var position = t.position + t.right * right + t.forward * forward;
        // Stand it on the ground; a piece left hanging in the air has no support and breaks at once.
        if (ZoneSystem.instance) position.y = ZoneSystem.instance.GetGroundHeight(position);
        player.PlacePiece(piece, position, Quaternion.Euler(0, t.eulerAngles.y + 180, 0), false);
        return $"placed {piecePrefab} at ({position.x:F1},{position.y:F1},{position.z:F1})";
    }

    /// <summary>Fixture: empty the target chest (its items are destroyed, never dropped) and remove it. Owner only.</summary>
    public static string Demolish()
    {
        var chest = RuleLab.Target(); var view = chest ? ChestRuleStore.View(chest!) : null;
        if (view == null || !view.IsOwner()) return "refused";
        chest!.GetInventory().RemoveAll();
        var wear = chest.GetComponentInParent<WearNTear>();
        if (wear) wear!.Remove(blockDrop: true); else ZNetScene.instance.Destroy(chest.gameObject);
        return "removed";
    }

    /// <summary>
    /// Fixture: add a new stack to the target chest. Optional crafter, quality and custom data ("key=value;key=value")
    /// give the stack metadata to check after a move. Owner only.
    /// </summary>
    public static string Fill(string prefab, int count, int quality, string crafter, string customData)
    {
        var chest = RuleLab.Target(); var view = chest ? ChestRuleStore.View(chest!) : null;
        if (view == null || !view.IsOwner()) return "refused";
        // Never let the game drop what does not fit: refuse instead.
        var shared = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
        if (shared == null) return "unknown item";
        var free = chest!.GetInventory().GetEmptySlots() * shared.m_maxStackSize +
            chest.GetInventory().GetAllItems().Where(x => x.m_shared.m_name == shared.m_name && x.m_quality == Math.Max(1, quality))
                .Sum(x => Math.Max(0, shared.m_maxStackSize - x.m_stack));
        if (count > free) return $"refused: room for {free}";
        var crafterId = string.IsNullOrEmpty(crafter) ? 0L : (long)StringExtensionMethods.GetStableHashCode(crafter);
        var item = chest!.GetInventory().AddItem(prefab, count, Math.Max(1, quality), 0, crafterId, crafter ?? "", false);
        if (item == null) return "not added";
        foreach (var pair in (customData ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(new[] { '=' }, 2)))
            item.m_customData[pair[0]] = pair.Length > 1 ? pair[1] : "";
        // Custom data was set after AddItem; tell the inventory so it is saved.
        AccessTools.Method(typeof(Inventory), "Changed")?.Invoke(chest.GetInventory(), new object[] { false, false });
        return $"added {item.m_stack} {prefab} at ({item.m_gridPos.x},{item.m_gridPos.y})";
    }

    /// <summary>Fixture: set or clear "Don't clean up this container" on the target, through the panel's own path.</summary>
    public static string OptOut(bool on)
    {
        var chest = RuleLab.Target(); var plugin = Plugin.Instance;
        return chest && plugin != null ? plugin.ChestRules.TrySetCleanupOptOut(chest!, on).Message : "no target";
    }

    /// <summary>Fixture (D059): ask AzuAutoStore, through its own RPC, to set or clear "storing paused" on the target.</summary>
    public static string AzuPause(bool on)
    {
        var chest = RuleLab.Target(); var view = chest ? ChestRuleStore.View(chest!) : null;
        if (view == null || !view.IsValid()) return "no target";
        view.InvokeRPC("RequestPause", on);
        return $"requested; paused={Integrations.AzuExclusions.Paused(chest!)?.ToString() ?? "unknown"}";
    }

    /// <summary>Fixture: open the target chest's inventory screen (for the panel and the cursor check), or close it.</summary>
    public static string Open(bool on)
    {
        if (!InventoryGui.instance) return "no inventory screen";
        if (!on) { InventoryGui.instance.Hide(); return "closed"; }
        var chest = RuleLab.Target();
        if (!chest) return "no target";
        InventoryGui.instance.Show(chest!);
        return "open=" + InventoryGui.IsVisible();
    }

    /// <summary>Fixture (D057 O8): pick up the first stack of the item in the open chest onto the cursor; empty prefab drops it back.</summary>
    public static string Cursor(string prefab)
    {
        var gui = InventoryGui.instance; var chest = RuleLab.Target();
        var setup = AccessTools.Method(typeof(InventoryGui), "SetupDragItem");
        if (!gui || !chest || setup == null) return "unavailable";
        if (string.IsNullOrEmpty(prefab)) { setup.Invoke(gui, new object?[] { null, null, 1 }); return "released"; }
        var item = chest!.GetInventory().GetAllItems().FirstOrDefault(x => x?.m_dropPrefab && x.m_dropPrefab.name == prefab);
        if (item == null) return "not in chest";
        setup.Invoke(gui, new object[] { item, chest.GetInventory(), item.m_stack });
        return $"holding {item.m_stack} {prefab}";
    }

    /// <summary>Fixture (R-C3): stand the player on the ground at (x, z), facing north, without a loading screen.</summary>
    public static string Goto(float x, float z)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var position = new Vector3(x, ZoneSystem.instance ? ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0, z)) : player.transform.position.y, z);
        player.TeleportTo(position, Quaternion.identity, false);
        return $"at ({position.x:F1},{position.y:F1},{position.z:F1})";
    }

    /// <summary>Fixture (R-C3): open the target the way a player does (vanilla Interact, so MultiUserChest and ownership apply).</summary>
    public static string Interact()
    {
        var chest = RuleLab.Target(); var player = Player.m_localPlayer;
        if (!chest || !player) return "no target or player";
        return "interact=" + chest!.Interact(player, false, false);
    }

    /// <summary>
    /// Fixture (R-C3, "B takes items mid-run"): move up to count of the item from the target chest into the player's
    /// inventory the way the game does. A chest owned by someone else goes through MultiUserChest's intercept, as in the UI.
    /// </summary>
    public static string Take(string prefab, int count)
    {
        var chest = RuleLab.Target(); var player = Player.m_localPlayer; var view = chest ? ChestRuleStore.View(chest!) : null;
        if (!chest || !player || view == null) return "no target or player";
        var item = chest!.GetInventory().GetAllItems().FirstOrDefault(x => x?.m_dropPrefab && x.m_dropPrefab.name == prefab);
        if (item == null) return "not in chest";
        var units = Math.Min(Math.Max(1, count), item.m_stack);
        var x = -1; var y = -1;
        if (view.IsOwner())
        {
            var find = AccessTools.Method(typeof(Inventory), "FindEmptySlot");
            if (find == null) return "unavailable";
            var cell = (Vector2i)find.Invoke(player!.GetInventory(), new object[] { false });
            if (cell.x < 0) return "player inventory full";
            x = cell.x; y = cell.y;
        }
        player!.GetInventory().MoveItemToThis(chest.GetInventory(), item, units, x, y);
        return $"requested {units} {prefab} (owner={view.IsOwner()})";
    }

    /// <summary>Fixture: set the target's rule with a priority (D057 O3).</summary>
    public static string SetWithPriority(string categories, string includes, string excludes, int priority) =>
        RuleLab.Edit(RuleLab.Payload(), new Core.Transfers.ChestRuleEdit(RuleLab.Split(categories), RuleLab.Split(includes), RuleLab.Split(excludes), priority: priority));
}
