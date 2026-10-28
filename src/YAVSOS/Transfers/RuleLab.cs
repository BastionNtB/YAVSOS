using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;

namespace YAVSOS.Transfers;

/// <summary>
/// Lab diagnostics for automated runtime tests (called through the test bridge's debug/call_method) and support.
/// The target is the chest open in the inventory screen, else a container chosen with <see cref="Select"/>.
/// Rule edits go through the normal <see cref="ChestRuleStore"/> with its owner, access and compare-and-set
/// checks. Fixture-only members say so. Results are single-line text. Unity game thread only.
/// </summary>
internal static class RuleLab
{
    private static readonly AccessTools.FieldRef<InventoryGui, Container>? CurrentContainer = Bind();
    private static Container? selected;
    private static AccessTools.FieldRef<InventoryGui, Container>? Bind()
    {
        try { return AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer"); } catch { return null; }
    }

    internal static Container? Target()
    {
        var open = CurrentContainer != null && InventoryGui.instance && InventoryGui.IsVisible() ? CurrentContainer(InventoryGui.instance) : null;
        return open ? open : selected ? selected : null;
    }

    /// <summary>Selects the nth (0-based) nearest container within 30 m whose object name contains the text.</summary>
    public static string Select(string nameContains, int nth)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var found = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None)
            .Where(x => x && x.gameObject.name.IndexOf(nameContains ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(x => (chest: x, distance: Vector3.Distance(x.transform.position, player.transform.position)))
            .Where(x => x.distance <= 30f).OrderBy(x => x.distance).ToList();
        if (nth < 0 || nth >= found.Count) { selected = null; return $"none ({found.Count} matches)"; }
        selected = found[nth].chest;
        var p = selected.transform.position;
        return $"{selected.gameObject.name} at ({p.x:F1},{p.y:F1},{p.z:F1}) {found[nth].distance:F1} m";
    }

    /// <summary>Fixture (R-C3): selects the container nearest the world position (x, z), within 1.5 m, so each player names the same chest.</summary>
    public static string SelectAt(float x, float z)
    {
        var at = new Vector3(x, 0, z);
        var found = UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None).Where(c => c)
            .Select(c => (chest: c, distance: Vector2.Distance(new Vector2(c.transform.position.x, c.transform.position.z), new Vector2(x, z))))
            .Where(c => c.distance <= 1.5f).OrderBy(c => c.distance).FirstOrDefault();
        selected = found.chest;
        if (!selected) return "none";
        var zdo = ChestRuleStore.View(selected)?.GetZDO();
        var p = selected.transform.position;
        return $"{selected.gameObject.name} at ({p.x:F1},{p.y:F1},{p.z:F1}); owner={zdo?.GetOwner()}; me={ZDOMan.GetSessionID()}; local={ChestRuleStore.View(selected)?.IsOwner() == true}";
    }

    public static string Payload() { var chest = Target(); return chest ? ChestRuleStore.Payload(chest!) : ""; }

    public static string Read()
    {
        var chest = Target(); var plugin = Plugin.Instance;
        if (!chest || plugin == null) return "no target";
        var read = plugin.ChestRules.Read(chest!);
        var text = $"target={chest!.gameObject.name}; eligibility={ChestRuleStore.Eligibility(chest)}; owner={ChestRuleStore.View(chest)?.IsOwner() == true}; " +
            $"azu={AzuAutoStoreIntegration.State}; categoriesReady={plugin.ClassificationEvidenceReady}; rebuilding={plugin.CategoriesRebuilding}";
        var policy = read.Policy;
        if (policy == null) return text + "; " + read.Message;
        text += $"; state={policy.State}; revision={policy.Revision}; hasRule={policy.HasRule}";
        if (policy.Rules == null) return text;
        var view = ChestRuleEdit.From(policy.Rules);
        return text + $"; ruleState={policy.Rules.State}; categories={string.Join(",", view.Categories)}; includes={string.Join(",", view.Includes)}; " +
            $"excludes={string.Join(",", view.Excludes)}; unknownCategories={string.Join(",", view.UnknownCategories)}; priority={policy.Rules.Priority}; overflow={policy.Rules.Overflow}; alternatives={policy.Rules.Acceptance.Rules.Count}";
    }

    public static string Set(string categories, string includes, string excludes) => SetExpecting(Payload(), categories, includes, excludes);
    /// <summary>Fixture (D050): a stored rule naming the given category IDs exactly as written, known or not. Owner only.</summary>
    public static string WriteCategories(string categories)
    {
        var plugin = Plugin.Instance;
        if (plugin == null) return "no plugin";
        var element = "<chestRules schema=\"1\" state=\"configured\" priority=\"0\" overflow=\"false\" existingItemFallback=\"false\"><rule>" +
            string.Concat(Split(categories).OrderBy(x => x, StringComparer.Ordinal).Select(x => $"<category id=\"{x}\" />")) + "</rule></chestRules>";
        var rules = ChestRuleCodec.Parse(element, plugin.RuleTaxonomy);
        return WriteRaw(ContainerPolicyEnvelope.Serialize(1, rules));
    }
    public static string SetExpecting(string expectedPayload, string categories, string includes, string excludes) =>
        Edit(expectedPayload, new ChestRuleEdit(Split(categories), Split(includes), Split(excludes)));
    public static string Clear() => Edit(Payload(), null);

    public static string Decide(string prefab)
    {
        var chest = Target();
        if (!chest) return "no target";
        var item = Item(prefab);
        return item == null ? "unknown item" : AzuAutoStoreIntegration.Decide(chest, item).ToString();
    }

    /// <summary>The target chest's rule decision for the item at an upgrade level (P6-21 Upgrade level filter).</summary>
    public static string DecideAt(string prefab, int quality)
    {
        var chest = Target();
        if (!chest) return "no target";
        var item = Item(prefab);
        if (item == null) return "unknown item";
        item.m_quality = quality;
        return AzuAutoStoreIntegration.Decide(chest, item).ToString();
    }

    /// <summary>Categories the current answers give an item.</summary>
    public static string Categories(string prefab)
    {
        var result = Plugin.Instance?.ResolveCategories(prefab);
        return result == null ? "none" : result.Primary + " | " + string.Join(",", result.Memberships.Select(x => x.Category).Distinct());
    }

    /// <summary>"prefab:count" for every item in the target's inventory, sorted; metadata summarized per stack.</summary>
    public static string Contents()
    {
        var chest = Target();
        return chest ? Describe(chest!.GetInventory()) : "no target";
    }

    public static string PlayerContents() => Player.m_localPlayer ? Describe(Player.m_localPlayer.GetInventory()) : "no player";

    private static string Describe(Inventory? inventory)
    {
        if (inventory == null) return "no inventory";
        return string.Join(",", inventory.GetAllItems().Where(x => x?.m_dropPrefab)
            .GroupBy(x => x.m_dropPrefab.name).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}:{x.Sum(i => i.m_stack)}" + (x.Any(i => i.m_customData.Count != 0 || i.m_quality != 1 || !string.IsNullOrEmpty(i.m_crafterName))
                ? "[" + string.Join("|", x.Select(i => $"q{i.m_quality}/{i.m_crafterName}/{string.Join(";", i.m_customData.Select(d => d.Key + "=" + d.Value))}")) + "]" : "")));
    }

    /// <summary>Loose drops of the item within the radius: stacks, total, and how many the local peer owns.</summary>
    public static string Ground(string prefab, float radius)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var drops = Drops(prefab, radius, player).ToList();
        return $"stacks={drops.Count}; total={drops.Sum(x => x.m_itemData.m_stack)}; localOwned={drops.Count(x => x.GetComponent<ZNetView>()?.IsOwner() == true)}";
    }

    /// <summary>Fixture (A-Z2/H3): hand ownership of nearby drops to the server, so Azu must ask for them.</summary>
    public static string GiveDropsToServer(string prefab, float radius)
    {
        var player = Player.m_localPlayer; var server = ZNet.instance?.GetServerPeer();
        if (!player || server == null) return "no player or server peer";
        var moved = 0;
        foreach (var drop in Drops(prefab, radius, player))
        {
            var view = drop.GetComponent<ZNetView>();
            if (view == null || !view.IsOwner()) continue;
            view.GetZDO().SetOwner(server.m_uid); moved++;
        }
        return $"handed {moved} drops to server peer {server.m_uid}";
    }

    private static IEnumerable<ItemDrop> Drops(string prefab, float radius, Player player) =>
        UnityEngine.Object.FindObjectsByType<ItemDrop>(FindObjectsSortMode.None).Where(x => x && x.m_itemData?.m_dropPrefab &&
            x.m_itemData.m_dropPrefab.name == prefab && Vector3.Distance(x.transform.position, player.transform.position) <= radius);

    /// <summary>Runs AzuAutoStore's own single-item entry (the Store Single Item key) for the first stack of the item.</summary>
    public static string StoreOne(string prefab)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var item = player.GetInventory().GetAllItems().FirstOrDefault(x => x?.m_dropPrefab && x.m_dropPrefab.name == prefab);
        if (item == null) return "not carried";
        var method = AccessTools.TypeByName("AzuAutoStore.Util.Functions") is Type type
            ? AccessTools.DeclaredMethod(type, "TryStoreThisItem", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }) : null;
        if (method == null) return "AzuAutoStore entry not found";
        var before = item.m_stack;
        method.Invoke(null, new object[] { item, player.GetInventory() });
        return $"stack {before} -> {(player.GetInventory().ContainsItem(item) ? item.m_stack : 0)}";
    }

    /// <summary>Fixture (A-Z4): let H1/H2 pass so the backstop's refusal can be observed. Remember to switch off.</summary>
    public static string SkipH1(bool on) { AzuAutoStoreIntegration.DiagnosticSkipH1 = on; return "skipH1=" + on; }

    /// <summary>Fixture (A-R2/D048): rule decisions see categories as not ready. Remember to switch off.</summary>
    public static string HoldCategoriesPending(bool on) { AzuAutoStoreIntegration.DiagnosticCategoriesPending = on; return "holdCategoriesPending=" + on; }

    /// <summary>P4-00f: Azu's view of the target chest type, and its verdict for one item.</summary>
    public static string AzuExclusion(string prefab)
    {
        var chest = Target();
        if (!chest) return "no chest";
        return $"state={AzuExclusions.State}; type={AzuExclusions.ChestType(chest!)}; hasExclusions={AzuExclusions.HasExclusions(chest!)}; allows={AzuExclusions.Allows(chest!, prefab)?.ToString() ?? "unknown"}";
    }

    public static string Stats() =>
        $"decisions={AzuAutoStoreIntegration.Decisions}; decisionMs={AzuAutoStoreIntegration.DecisionTicks * 1000.0 / Stopwatch.Frequency:F3}; " +
        $"backstopRefusals={AzuAutoStoreIntegration.BackstopRefusals}; cachedRules={Plugin.Instance?.RuleEvaluator.CachedRules}; cachedDecisions={Plugin.Instance?.RuleEvaluator.CachedDecisions}";

    public static string ResetStats()
    { AzuAutoStoreIntegration.Decisions = 0; AzuAutoStoreIntegration.DecisionTicks = 0; AzuAutoStoreIntegration.BackstopRefusals = 0; return "reset"; }

    /// <summary>Times n rule decisions for the item on the target (warm cache), in microseconds per decision.</summary>
    public static string Bench(string prefab, int n)
    {
        var chest = Target(); var item = Item(prefab);
        if (!chest || item == null || n <= 0) return "no target or item";
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) AzuAutoStoreIntegration.Decide(chest, item);
        return $"{clock.Elapsed.TotalMilliseconds * 1000.0 / n:F3} us per decision over {n}";
    }

    /// <summary>Fixture (A-D1): puts the selected Ross drawer in its assigned-empty state through Ross's public API.</summary>
    public static string RossReserve(string prefab)
    {
        var drawer = Target();
        if (!drawer || !ChestRuleStore.IsRossDrawer(drawer!)) return "target is not a Ross drawer";
        var type = drawer!.GetType();
        var deposit = type.GetMethod("TryDepositExternally"); var withdraw = type.GetMethod("TryWithdrawExternally");
        if (deposit == null || withdraw == null) return "Ross API not found";
        var depositArgs = new object[] { prefab, 1, 0 };
        var deposited = (bool)deposit.Invoke(drawer, depositArgs);
        var withdrawArgs = new object[] { 1, 0 };
        var withdrawn = (bool)withdraw.Invoke(drawer, withdrawArgs);
        return $"deposit={deposited}/{depositArgs[2]}; withdraw={withdrawn}/{withdrawArgs[1]}; {DrawerState()}";
    }

    public static string DrawerState()
    {
        var drawer = Target();
        if (!drawer || !ChestRuleStore.IsRossDrawer(drawer!)) return "target is not a Ross drawer";
        var snapshot = drawer!.GetType().GetProperty("Snapshot")?.GetValue(drawer);
        return snapshot == null ? "no snapshot" : "snapshot=" + snapshot;
    }

    /// <summary>Fixture (A-R1): store an arbitrary payload, for example a future format. Owner only.</summary>
    public static string WriteRaw(string payload)
    {
        var chest = Target(); var view = chest ? ChestRuleStore.View(chest!) : null;
        if (view == null || !view.IsOwner() || ChestRuleStore.Eligibility(chest) != RuleEligibility.Eligible) return "refused";
        view.GetZDO().Set(ContainerPolicyEnvelope.ZdoKey, payload ?? "");
        return "written";
    }

    internal static string Edit(string expected, ChestRuleEdit? edit)
    {
        var chest = Target(); var plugin = Plugin.Instance;
        if (!chest || plugin == null) return "no target";
        var result = plugin.ChestRules.TryEdit(chest!, expected ?? "", edit);
        return result.Reason + ": " + result.Message;
    }

    private static ItemDrop.ItemData? Item(string prefab)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        if (!drop) return null;
        // Registered prefab data may lack m_dropPrefab; inventory and ground items always carry it.
        var item = drop!.m_itemData.Clone(); item.m_dropPrefab = drop.gameObject;
        return item;
    }

    internal static string[] Split(string? list) =>
        (list ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length != 0).ToArray();
}
