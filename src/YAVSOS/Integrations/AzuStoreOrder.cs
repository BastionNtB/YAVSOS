using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using YAVSOS.Transfers;

namespace YAVSOS.Integrations;

/// <summary>
/// D070: AzuAutoStore stores items in YAVSOS's chest order, so the store key and "Clean up nearby" make one decision.
/// Azu's store key fills one chest at a time with everything it takes, in Azu's registration order. This runs the same
/// loop item by item instead: each stack is offered to the ruled chests whose rule takes it in <see cref="StoreOrder"/>
/// (priority, then most specific), through Azu's own per-chest store call, so Azu keeps its writes, MultiUserChest
/// routing, skips (equipped, hotbar, quick slots, favourites) and messages. Then Azu's own loop runs for the rest, with
/// ruled chests taking nothing more. Ground pickup is not a loop YAVSOS runs: there a chest is refused while a better
/// ruled chest in reach has room, and the remainder waits on the ground for Azu's next pass.
/// Each part checks Azu's code shape at startup and turns off alone, leaving Azu's order, when it differs (D058). An
/// isolated patch: remove it when AzuAutoStore offers a chest-order hook. Unity game thread only.
/// </summary>
internal static class AzuStoreOrder
{
    private const string HarmonyId = "com.bastionntb.yavsos.azustoreorder";
    private static ManualLogSource? log;
    private static Harmony? harmony;
    private static MethodInfo? storeToContainer, storeSuccess, clearPendingSends, getNearby, shouldSkip, quickSlots;
    private static Func<Container, float>? containerRange;
    private static Func<Inventory, ItemDrop.ItemData, int>? howMuchFits;
    private static FieldInfo? pingList, boxedContainer, playerRange, registry;
    private static Type? vanillaType;
    private static bool restPass;
    private static ItemDrop? drop;
    private static bool dropDecided;
    private static Container? dropBest;

    /// <summary>The store key and single-item key follow YAVSOS's chest order.</summary>
    internal static bool Active { get; private set; }
    /// <summary>Ground pickup prefers the best ruled chest.</summary>
    internal static bool PickupActive { get; private set; }

    internal static void Initialize(Assembly azu, ManualLogSource logger, Func<MethodBase, AzuMethodShape?> shape)
    {
        log = logger;
        try
        {
            var functions = azu.GetType("AzuAutoStore.Util.Functions", false);
            var boxes = azu.GetType("AzuAutoStore.Util.Boxes", false);
            var plugin = azu.GetType("AzuAutoStore.AzuAutoStorePlugin", false);
            var contract = azu.GetType("AzuAutoStore.Interfaces.IContainer", false);
            var move = azu.GetType("AzuAutoStore.Util.InventoryMove", false);
            vanillaType = azu.GetType("AzuAutoStore.Interfaces.VanillaContainers", false);
            var problems = new List<string>();
            T? Need<T>(T? member, string name) where T : class { if (member == null) problems.Add("Missing member: " + name); return member; }
            var store = Need(functions == null ? null : AccessTools.DeclaredMethod(functions, "TryStore", Type.EmptyTypes), "Functions.TryStore()");
            var storeThis = Need(functions == null ? null : AccessTools.DeclaredMethod(functions, "TryStoreThisItem", new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }), "Functions.TryStoreThisItem");
            storeToContainer = Need(functions == null || contract == null ? null : AccessTools.DeclaredMethod(functions, "StoreToContainer", new[] { contract, typeof(ItemDrop.ItemData), typeof(Inventory) }), "Functions.StoreToContainer");
            storeSuccess = Need(functions == null ? null : AccessTools.DeclaredMethod(functions, "StoreSuccess", new[] { typeof(int) }), "Functions.StoreSuccess");
            clearPendingSends = Need(functions == null ? null : AccessTools.DeclaredMethod(functions, "ClearPendingSends", Type.EmptyTypes), "Functions.ClearPendingSends");
            var generic = boxes == null ? null : AccessTools.GetDeclaredMethods(boxes).FirstOrDefault(x => x.Name == "GetNearbyContainers" && x.IsGenericMethodDefinition && x.GetParameters().Length == 2);
            getNearby = Need(generic?.MakeGenericMethod(typeof(Player)), "Boxes.GetNearbyContainers");
            pingList = Need(boxes == null ? null : AccessTools.DeclaredField(boxes, "ContainersToPing"), "Boxes.ContainersToPing");
            playerRange = Need(plugin == null ? null : AccessTools.DeclaredField(plugin, "PlayerRange"), "AzuAutoStorePlugin.PlayerRange");
            shouldSkip = Need(vanillaType == null ? null : AccessTools.DeclaredMethod(vanillaType, "ShouldSkip", new[] { typeof(ItemDrop.ItemData), typeof(List<ItemDrop.ItemData>) }), "VanillaContainers.ShouldSkip");
            quickSlots = Need(vanillaType == null ? null : AccessTools.DeclaredMethod(vanillaType, "QuickSlots", Type.EmptyTypes), "VanillaContainers.QuickSlots");
            boxedContainer = Need(vanillaType == null ? null : AccessTools.GetDeclaredFields(vanillaType).SingleOrDefault(x => x.FieldType == typeof(Container)), "VanillaContainers container field");
            if (getNearby != null && getNearby.ReturnType != typeof(List<>).MakeGenericType(contract!)) problems.Add("Boxes.GetNearbyContainers returns " + getNearby.ReturnType.Name + ".");
            if (playerRange != null && playerRange.FieldType != typeof(BepInEx.Configuration.ConfigEntry<float>)) problems.Add("PlayerRange is " + playerRange.FieldType.Name + ".");
            if (storeToContainer != null && storeToContainer.ReturnType != typeof(int)) problems.Add("StoreToContainer returns " + storeToContainer.ReturnType.Name + ".");
            if (shouldSkip != null && shouldSkip.ReturnType != typeof(bool)) problems.Add("ShouldSkip returns " + shouldSkip.ReturnType.Name + ".");
            problems.AddRange(AzuStoreCompatibility.CheckStoreLoop(store == null ? null : shape(store), false));
            problems.AddRange(AzuStoreCompatibility.CheckStoreLoop(storeThis == null ? null : shape(storeThis), true));
            harmony = new Harmony(HarmonyId);
            var self = typeof(AzuStoreOrder);
            HarmonyMethod Hook(string name, int priority = Priority.Normal) => new HarmonyMethod(AccessTools.DeclaredMethod(self, name)) { priority = priority };
            if (problems.Count == 0)
            {
                // Last: other mods' prefixes on Azu's store key still run first, and YAVSOS's depth marker wraps this one.
                harmony.Patch(store, prefix: Hook(nameof(StorePrefix), Priority.Last));
                harmony.Patch(storeThis, prefix: Hook(nameof(StoreThisItemPrefix), Priority.Last));
                Active = true;
                logger.LogInfo("AzuAutoStore's store key follows YAVSOS chest priority and specificity (D070).");
            }
            else logger.LogWarning("AzuAutoStore's store key keeps its own chest order: its code differs from what YAVSOS checked. " +
                                   "Clean up nearby may move items it stores. " + string.Join(" ", problems.Take(8)));

            var pickup = new List<string>();
            var pickupStore = functions == null ? null : AccessTools.DeclaredMethod(functions, "CheckItemDropInstanceAndStore", new[] { typeof(ItemDrop) });
            var range = functions == null ? null : AccessTools.DeclaredMethod(functions, "GetContainerRange", new[] { typeof(Container) });
            var fits = move == null ? null : AccessTools.DeclaredMethod(move, "HowMuchFits", new[] { typeof(Inventory), typeof(ItemDrop.ItemData) });
            registry = boxes == null ? null : AccessTools.DeclaredField(boxes, "Containers");
            if (pickupStore == null) pickup.Add("Functions.CheckItemDropInstanceAndStore");
            if (range == null || range.ReturnType != typeof(float)) pickup.Add("Functions.GetContainerRange");
            if (fits == null || fits.ReturnType != typeof(int)) pickup.Add("InventoryMove.HowMuchFits");
            if (registry == null || registry.FieldType != typeof(List<Container>)) pickup.Add("Boxes.Containers");
            if (pickup.Count == 0)
            {
                containerRange = AccessTools.MethodDelegate<Func<Container, float>>(range);
                howMuchFits = AccessTools.MethodDelegate<Func<Inventory, ItemDrop.ItemData, int>>(fits);
                harmony.Patch(pickupStore, prefix: Hook(nameof(PickupPrefix)), finalizer: Hook(nameof(PickupFinalizer)));
                PickupActive = true;
            }
            else logger.LogWarning("AzuAutoStore's ground pickup keeps its own chest order; missing: " + string.Join(", ", pickup) + ".");
        }
        catch (Exception error)
        {
            harmony?.UnpatchSelf(); harmony = null; Active = PickupActive = false;
            logger.LogWarning("AzuAutoStore keeps its own chest order: " + error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Disable()
    {
        harmony?.UnpatchSelf(); harmony = null; Active = PickupActive = false; restPass = false; drop = null;
    }

    /// <summary>
    /// For the AzuAutoStore integration's H1: while Azu's own loop stores the rest, ruled chests take nothing more (they
    /// had their turn in YAVSOS's order); during ground pickup, only the best ruled chest with room may take the item.
    /// </summary>
    internal static bool Refuses(Container chest, ItemDrop.ItemData item)
    {
        if (restPass)
        {
            // Ruled chests had their turn; an unruled one (no rule, or a rule cleared before D086 M1) is Azu's to fill.
            var payload = ChestRuleStore.Payload(chest);
            return payload.Length != 0 && Plugin.Instance is { } plugin && !plugin.RuleEvaluator.Unruled(payload, plugin.RuleTaxonomy);
        }
        if (drop == null || !drop || !ReferenceEquals(item, drop.m_itemData)) return false;
        if (!dropDecided) { dropDecided = true; dropBest = BestForPickup(drop, item); }
        return dropBest != null && !ReferenceEquals(chest, dropBest);
    }

    /// <summary>AzuAutoStore leaves this stack in the player's inventory (equipped, hotbar, quick slot, favourite); false when unknown.</summary>
    internal static bool Skips(ItemDrop.ItemData item)
    {
        if (!Active) return false;
        try { return (bool)shouldSkip!.Invoke(null, new[] { item, quickSlots!.Invoke(null, null) }); }
        catch (Exception error) { log?.LogDebug("ShouldSkip failed: " + error.GetType().Name); return false; }
    }

    /// <summary>Containers the store key would consider, from Azu's own list: player chests in its range.</summary>
    internal static List<Container> Nearby(Player player)
    {
        var result = new List<Container>();
        if (!Active) return result;
        foreach (var box in (IList)getNearby!.Invoke(null, new object[] { player, Range }))
            if (box != null && vanillaType!.IsInstanceOfType(box) && boxedContainer!.GetValue(box) is Container chest && chest) result.Add(chest);
        return result;
    }

    private static float Range => ((BepInEx.Configuration.ConfigEntry<float>)playerRange!.GetValue(null)).Value;

    // Returns false (skip Azu's loop) only when the ordered run happened.
    private static bool StorePrefix() => !Run(null, null);

    private static bool StoreThisItemPrefix(ItemDrop.ItemData itemData, Inventory m_inventory) => !Run(itemData, m_inventory);

    private static bool Run(ItemDrop.ItemData? single, Inventory? source)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        if (!Active || plugin == null || !player) return false;
        var inventory = player.GetInventory();
        if (single != null && !ReferenceEquals(source, inventory)) return false;
        var answers = plugin.CategoryAnswers;
        // Categories not ready: Azu's own loop runs and the integration's H1 skips ruled chests with its notice.
        if (answers == null || !answers.CategoriesReady) return false;
#if YAVSOS_LAB
        if (AzuAutoStoreIntegration.DiagnosticCategoriesPending) return false;
#endif
        var boxes = (IList)getNearby!.Invoke(null, new object[] { player, Range });
        var started = false;
        try
        {
            ((IList)pingList!.GetValue(null)).Clear();
            clearPendingSends!.Invoke(null, null);
            started = true;
            var total = Ordered(plugin, answers, inventory, boxes, single);
            // Azu's own loop for the rest: unruled chests, drawers, backpacks and quivers by its own settings and order.
            restPass = true;
            foreach (var box in boxes)
            {
                if (single != null && (single.m_stack <= 0 || !inventory.ContainsItem(single))) break;
                total += (int)storeToContainer!.Invoke(null, new[] { box, single, single == null ? null : inventory });
            }
            restPass = false;
            storeSuccess!.Invoke(null, new object[] { total });
            return true;
        }
        catch (Exception error)
        {
            restPass = false;
            Disable();
            var inner = error is TargetInvocationException { InnerException: { } cause } ? cause : error;
            log?.LogWarning($"AzuAutoStore's store key failed in YAVSOS's chest order ({inner.GetType().Name}: {inner.Message}); it keeps its own order from now on." +
                            (started ? " Items already stored stay stored; AzuAutoStore stores the rest now." : ""));
            return false;
        }
    }

    // Each stack, offered to the ruled chests that take it in YAVSOS's order through Azu's own per-chest store call.
    private static int Ordered(Plugin plugin, ICategoryAnswers answers, Inventory inventory, IList boxes, ItemDrop.ItemData? single)
    {
        var ruled = new Dictionary<string, (object Box, Container Chest)>(StringComparer.Ordinal);
        var payloads = new List<KeyValuePair<string, string>>();
        foreach (var box in boxes)
        {
            if (box == null || !vanillaType!.IsInstanceOfType(box) || boxedContainer!.GetValue(box) is not Container chest || !chest) continue;
            var payload = ChestRuleStore.Payload(chest);
            var view = ChestRuleStore.View(chest);
            if (payload.Length == 0 || !view || !view!.IsValid()) continue;
            var id = view.GetZDO().m_uid.ToString();
            if (ruled.ContainsKey(id)) continue;
            ruled.Add(id, (box, chest)); payloads.Add(new KeyValuePair<string, string>(id, payload));
        }
        if (ruled.Count == 0) return 0;
        var order = new StoreOrder(plugin.RuleTaxonomy, answers, payloads, plugin.CategoryMemberCount, plugin.RuleEvaluator);
        var quick = quickSlots!.Invoke(null, null);
        var items = single != null ? new List<ItemDrop.ItemData> { single } : inventory.GetAllItems().ToList();
        var total = 0;
        // Azu's loop takes the inventory from its last stack to its first; so does this one.
        for (var at = items.Count - 1; at >= 0; at--)
        {
            var item = items[at];
            if (item == null || item.m_stack <= 0 || !item.m_dropPrefab || (bool)shouldSkip!.Invoke(null, new[] { item, quick })) continue;
            var prefab = item.m_dropPrefab.name;
            foreach (var id in order.For(prefab, plugin.InstanceFacets(item, answers)))
            {
                var (box, chest) = ruled[id];
                // As cleanup does: Azu's exclusions keep a chest out of the ruled order (and its red message away). Azu's own store
                // key ignores its pause (it pauses only ground pickup), so this does too (D097).
                if (AzuExclusions.Allows(chest, prefab) == false) continue;
                total += (int)storeToContainer!.Invoke(null, new[] { box, item, inventory });
                if (item.m_stack <= 0 || !inventory.ContainsItem(item)) break;
            }
        }
        return total;
    }

    private static void PickupPrefix(ItemDrop itemDrop) { drop = itemDrop; dropDecided = false; dropBest = null; }

    private static Exception? PickupFinalizer(Exception? __exception) { drop = null; dropDecided = false; dropBest = null; return __exception; }

    // The ruled chest Azu's pickup loop would use first in YAVSOS's order: same reach, ownership, use, access, pause and
    // room checks as that loop. Null: no ruled chest takes it now, so Azu's own order applies.
    private static Container? BestForPickup(ItemDrop item, ItemDrop.ItemData data)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        var answers = plugin?.CategoryAnswers;
        if (!PickupActive || plugin == null || !player || answers == null || !answers.CategoriesReady || !data.m_dropPrefab) return null;
        try
        {
            var position = item.transform.position; var prefab = data.m_dropPrefab.name;
            var chests = new Dictionary<string, Container>(StringComparer.Ordinal);
            var payloads = new List<KeyValuePair<string, string>>();
            foreach (var chest in (List<Container>)registry!.GetValue(null))
            {
                if (!chest) continue;
                var payload = ChestRuleStore.Payload(chest);
                var view = ChestRuleStore.View(chest);
                if (payload.Length == 0 || !view || !view!.IsValid() || !view.IsOwner()) continue;
                var reach = containerRange!(chest);
                if (reach < 0 || (chest.transform.position - position).sqrMagnitude > reach * reach) continue;
                var zdo = view.GetZDO();
                if (chest.IsInUse() || zdo.GetInt(ZDOVars.s_inUse) != 0 || !ChestRuleStore.HasAccess(chest, player!)) continue;
                if (AzuExclusions.Allows(chest, prefab) == false || AzuExclusions.Paused(chest) == true) continue;
                var inventory = chest.GetInventory();
                if (inventory == null || howMuchFits!(inventory, data) <= 0) continue;
                var id = zdo.m_uid.ToString();
                if (chests.ContainsKey(id)) continue;
                chests.Add(id, chest); payloads.Add(new KeyValuePair<string, string>(id, payload));
            }
            if (chests.Count == 0) return null;
            var best = new StoreOrder(plugin.RuleTaxonomy, answers, payloads, plugin.CategoryMemberCount, plugin.RuleEvaluator)
                .For(prefab, plugin.InstanceFacets(data, answers)).FirstOrDefault();
            return best == null ? null : chests[best];
        }
        catch (Exception error)
        {
            PickupActive = false;
            log?.LogWarning("AzuAutoStore's ground pickup keeps its own chest order from now on: " + error.GetType().Name + ": " + error.Message);
            return null;
        }
    }
}
