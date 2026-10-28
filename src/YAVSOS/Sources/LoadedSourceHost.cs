using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;

namespace YAVSOS.Sources;

/// <summary>Read-only loaded definitions for ordinary categories. No scene searches, asset loads, inventory or world mutation.</summary>
internal sealed class LoadedSourceHost : IDisposable
{
    private const int MaxRoots = 32768, MaxNestedEntries = 256;
    private readonly LoadedSourceCache cache;
    private ObjectDB? ordinaryDb;
    private readonly OptionalDefinitionWork optionalWork = new();
    private readonly ManualLogSource log;
    private readonly Func<IEnumerable<LoadedSourceFact?>?> open;
    private ZNetScene? scene;
    private List<GameObject>? prefabs;
    private StoreGui? store;
    private ItemDrop? currency;
    private int prefabCount;
    private CatalogSnapshot? catalog;
    private OptionalIntegrations? optional;
    private bool active;
    private LoadedSourceState reported;
    private int reportedRetries;
    internal ClassificationSourceSnapshot? Current => cache.Current?.Snapshot;
    internal LoadedSourceReceipt? Receipt => cache.Current;
    internal long Revision => cache.Revision;
    internal LoadedSourceHost(ManualLogSource log)
    {
        this.log = log;
        var limits = new LoadedSourceLimits();
        cache = new LoadedSourceCache(limits, (loaded, facts, token) => System.Threading.Tasks.Task.Run(
            () => LoadedSourceReceipt.Build(loaded, facts, limits, token), token), retries: 3);
        open = OpenOrdinaryCapture;
    }
    internal void BeginSession(bool isActive)
    {
        active = isActive; ordinaryDb = null; catalog = null; scene = null;
        prefabs = null; optional = null; store = null; currency = null;
        cache.BeginSession(isActive); reported = cache.State;
    }
    // In-place definition edits must call this after their complete registration transaction.
    internal void Invalidate() => cache.Invalidate();
    /// <summary>P6-14: EpicLoot is installed and its verified reader works, so its rarity filter can apply.</summary>
    internal bool EpicLootRarityOffered => optional != null && optional.EpicLoot.State == OptionalCapabilityState.Supported;
    internal OptionalCapabilityState EpicLootState => optional?.EpicLoot.State ?? OptionalCapabilityState.Absent;
    /// <summary>Loaded sources gave up for their current inputs after their retries (D086 B5.36): the reason, else null.</summary>
    internal string? GaveUp => active && cache.State == LoadedSourceState.Failed ? cache.Diagnostic : null;
    // P6-07b filters: empty means plain (EpicLoot absent, or verifiably no magic item); a reader that is not ready or
    // cannot read this instance gives an Unreadable row, so a "plain" rule never takes an item it could not check.
    internal IReadOnlyList<FacetEvidence> FilterEquipmentRarity(ClassificationResult item, ItemDrop.ItemData data)
    {
        if (!item.InstanceRarity) return Array.Empty<FacetEvidence>();
        if (optional != null && optional.EpicLoot.State == OptionalCapabilityState.Absent) return Array.Empty<FacetEvidence>();
        return cache.Current == null || optional == null ? new[] { ClassificationFilters.UnreadableRow("rarity") } : optional.FilterEquipmentRarity(item, data);
    }
    internal void Update(CatalogSnapshot? loaded)
    {
        optionalWork.ReapRetired();
        catalog = loaded; cache.SetCatalog(loaded);
        if (active)
        {
            var db = ObjectDB.instance; var net = ZNetScene.instance;
            var definitions = net ? net.m_prefabs : null;
            var nextStore = StoreGui.instance; var nextCurrency = nextStore ? nextStore.m_coinPrefab : null;
            if (!ReferenceEquals(ordinaryDb, db) || !ReferenceEquals(scene, net) || !ReferenceEquals(prefabs, definitions) ||
                prefabCount != (definitions?.Count ?? 0) || !ReferenceEquals(store, nextStore) || !ReferenceEquals(currency, nextCurrency))
            {
                ordinaryDb = db; scene = net; prefabs = definitions; prefabCount = definitions?.Count ?? 0;
                store = nextStore; currency = nextCurrency; cache.Invalidate();
            }
        }
        cache.Tick(open);
        if (cache.Retries > reportedRetries) log.LogWarning("Fresh loaded sources rejected: " + cache.Diagnostic);
        reportedRetries = cache.Retries;
        if (reported == cache.State) return;
        reported = cache.State;
        if (reported == LoadedSourceState.Ready)
        {
            var receipt = cache.Current!;
            log.LogInfo($"Fresh loaded sources: {receipt.Facts.Count} detached facts, " +
                $"{receipt.Snapshot.Routes.Count} routes, {receipt.ScalarBytes} UTF-8 scalar bytes, maximum capture step {cache.MaximumStepMilliseconds:F3} ms, " +
                $"SHA-256 {receipt.Snapshot.Fingerprint}.");
            // D087: evidence rows skipped one by one (D086 D3) are named once, for the mod that supplies them.
            foreach (var gap in receipt.Coverage)
                if (gap.EndsWith("; skipped", StringComparison.Ordinal) && malformedReported.Add(gap))
                    log.LogWarning("Loaded sources: " + gap + ". Only this evidence is missing; report it to the mod that adds the item.");
        }
        else if (reported == LoadedSourceState.Failed) log.LogWarning("Fresh loaded sources rejected: " + cache.Diagnostic);
    }
    private IEnumerable<LoadedSourceFact?>? OpenOrdinaryCapture() => catalog != null && ordinaryDb && scene && prefabs != null
        ? CaptureOrdinary(catalog, ordinaryDb, scene, prefabs) : null;
    private IEnumerable<LoadedSourceFact?> CaptureOrdinary(CatalogSnapshot loaded, ObjectDB db, ZNetScene net, List<GameObject> definitions)
    {
        var count = definitions.Count; var expectedStore = store; var expectedCurrency = currency;
        if (count > MaxRoots) throw new InvalidDataException("Ordinary definition root limit exceeded.");
        void Check()
        {
            if (!active || !ReferenceEquals(catalog, loaded) || !db || !net || !ReferenceEquals(ObjectDB.instance, db) ||
                !ReferenceEquals(ZNetScene.instance, net) || !ReferenceEquals(net.m_prefabs, definitions) || definitions.Count != count ||
                !ReferenceEquals(StoreGui.instance, expectedStore) || !ReferenceEquals(expectedStore ? expectedStore.m_coinPrefab : null, expectedCurrency))
                throw new InvalidDataException("Ordinary category registrations changed during capture.");
        }
        Check();
        yield return Coverage("ordinary:capability", "game=" + global::Version.GetVersionString() + "; ordinary-loaded-consumers");
        var paymentPrefab = expectedCurrency ? expectedCurrency.gameObject : null;
        var registeredPayment = paymentPrefab ? db.GetItemPrefab(paymentPrefab.name) : null;
        if (expectedStore && registeredPayment && registeredPayment == paymentPrefab && expectedCurrency!.m_itemData?.m_shared != null)
            yield return new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "payment:store", registeredPayment.name, route: "payment:store", method: "payment-currency");
        else yield return Coverage("payment:store", "gap: current registered payment definition unavailable");
        for (var i = 0; i < count; i++)
        {
            Check(); var prefab = definitions[i];
            if (!prefab) throw new InvalidDataException("Null/destroyed loaded prefab registration.");
            var id = "prefab:" + i + ":" + prefab.name;
            foreach (var row in Definition(prefab, id, Check)) yield return row;
            yield return null;
        }
        optional = OptionalIntegrations.Probe();
        Report(optional);
        foreach (var row in optional.Capture(loaded, db, definitions, Check, optionalWork)) yield return row;
        Check();
    }
    // D087 (P8-20): an optional feature its capability check turned off says why, once per distinct answer.
    private string reportedOff = "";
    private void Report(OptionalIntegrations found)
    {
        var off = new[] { found.Jotunn, found.EpicLoot, found.EpicLootSemantics }.Where(x => x.State == OptionalCapabilityState.Incompatible).ToList();
        var key = string.Join("|", off.Select(x => x.Witness));
        if (key == reportedOff) return;
        reportedOff = key;
        foreach (var capability in off)
            log.LogWarning($"{capability.Adapter} turned off for version {capability.ObservedVersion}: its code differs from what YAVSOS checked " +
                           $"({capability.Diagnostic}). The rest of YAVSOS keeps working.");
    }

    /// <summary>
    /// One prefab's rows, read whole so that a malformed definition (a null list or entry, a list over its bound) fails only
    /// that prefab: its evidence becomes one coverage gap naming it, and the rest of the receipt stands (D086 D3, PHILOSOPHY:
    /// "Unsupported evidence stays Unknown/fallback with a diagnostic"). Registrations changing during capture still fail the
    /// whole receipt, which is retried.
    /// </summary>
    private IReadOnlyList<LoadedSourceFact?> Definition(GameObject prefab, string id, Action check)
    {
        var rows = new List<LoadedSourceFact?>();
        try
        {
            rows.AddRange(ItemUses(prefab, id, check));
            rows.AddRange(Machine(prefab, id, check));
            var piece = prefab.GetComponent<Piece>();
            rows.AddRange(LoadedProducerReaders.Read(prefab, id, !piece || piece.m_enabled, check));
        }
        catch (MalformedDefinitionException error)
        {
            rows.Clear();
            rows.Add(Coverage(id + ":malformed", "gap: " + error.Message + " Its recipes, uses and products give no evidence."));
            if (malformedReported.Add(prefab.name))
                log.LogWarning("Loaded sources: " + prefab.name + " has a malformed definition (" + error.Message + "); only its own evidence is skipped. Report it to the mod that adds it.");
        }
        return rows;
    }
    // One warning per prefab while the game runs, not per capture (D087: enough detail to act on, without repeating).
    private readonly HashSet<string> malformedReported = new(StringComparer.Ordinal);

    private static IEnumerable<LoadedSourceFact?> ItemUses(GameObject prefab, string id, Action check)
    {
        check(); var piece = prefab.GetComponent<Piece>(); var active = !piece || piece.m_enabled;
        var plant = prefab.GetComponent<Plant>();
        var feast = prefab.GetComponent<Feast>();
        var feastFood = feast ? (feast.m_foodItem ? feast.m_foodItem : prefab.GetComponent<ItemDrop>()) : null;
        var foodFacts = feastFood ? feastFood.m_itemData?.m_shared : null;
        var feastAvailable = active && feast && feast.enabled && feast.m_eatStacks > 0 && foodFacts != null &&
            FinitePositive(foodFacts.m_foodBurnTime) && FinitePositive(foodFacts.m_food);
        if (piece)
        {
            var resources = piece.m_resources;
            if (resources == null || resources.Length > MaxNestedEntries) throw new MalformedDefinitionException("Piece resource bound exceeded.");
            for (var i = 0; i < resources.Length; i++)
            {
                check();
                if (!piece || !ReferenceEquals(piece.m_resources, resources)) throw new InvalidDataException("Piece resources changed during capture.");
                var resource = resources[i]; if (resource == null) throw new MalformedDefinitionException("Null piece resource.");
                var eligible = active && !resource.m_upgraderResource;
                yield return Use(id + ":build:" + i, resource.m_resItem, id, "building", eligible, resource.m_amount);
                if (plant) yield return Use(id + ":plant-use:" + i, resource.m_resItem, id, "planting", eligible && plant.enabled, resource.m_amount);
                if (feast) yield return Use(id + ":feast-use:" + i, resource.m_resItem, id, "feast-ingredient", eligible && feastAvailable, resource.m_amount);
            }
            check();
            if (!piece || !ReferenceEquals(piece.m_resources, resources)) throw new InvalidDataException("Piece resources changed during capture.");
        }
        var smelter = prefab.GetComponent<Smelter>();
        if (smelter && smelter.m_fuelItem)
            yield return Use(id + ":smelter-fuel", smelter.m_fuelItem, id, "fuel", active && smelter.enabled && smelter.m_maxFuel > 0 && smelter.m_fuelPerProduct > 0);
        var cooking = prefab.GetComponent<CookingStation>();
        if (cooking && cooking.m_fuelItem)
            yield return Use(id + ":cooking-fuel", cooking.m_fuelItem, id, "fuel", active && cooking.enabled && cooking.m_useFuel && cooking.m_maxFuel > 0);
        var fire = prefab.GetComponent<Fireplace>();
        if (fire && fire.m_fuelItem)
            yield return Use(id + ":fire-fuel", fire.m_fuelItem, id, "fuel", active && fire.enabled && !fire.m_infiniteFuel && fire.m_canRefill && fire.m_maxFuel > 0);
        if (fire && fire.m_fireworkItemList != null)
        {
            var entries = fire.m_fireworkItemList;
            if (entries.Length > MaxNestedEntries) throw new MalformedDefinitionException("Fireworks consumer bound exceeded.");
            var fuel = fire.m_fuelItem ? fire.m_fuelItem.m_itemData?.m_shared : null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Length; i++)
            {
                check();
                if (!fire || !ReferenceEquals(fire.m_fireworkItemList, entries)) throw new InvalidDataException("Fireworks consumers changed during capture.");
                var entry = entries[i]; var shared = entry.m_fireworkItem ? entry.m_fireworkItem.m_itemData?.m_shared : null;
                if (shared == null || string.IsNullOrEmpty(shared.m_name))
                {
                    yield return Coverage(id + ":fireworks:gap", "gap: malformed entry prevents later fireworks consumers");
                    break;
                }
                // UseItem checks the fuel branch first, then the first matching firework shared name.
                var reachable = names.Add(shared.m_name) && (fire.m_infiniteFuel || fuel?.m_name != shared.m_name);
                yield return Use(id + ":fireworks:" + i, entry.m_fireworkItem, id, "fireworks",
                    active && fire.enabled && fire.m_canRefill && fuel != null && reachable, entry.m_fireworkItemCount);
            }
            check();
            if (!fire || !ReferenceEquals(fire.m_fireworkItemList, entries)) throw new InvalidDataException("Fireworks consumers changed during capture.");
        }
        var door = prefab.GetComponent<Door>();
        if (door && door.m_keyItem) yield return Use(id + ":key", door.m_keyItem, id, "door-key", active && door.enabled);
        var tameable = prefab.GetComponent<Tameable>();
        if (tameable && tameable.m_saddleItem) yield return Use(id + ":saddle", tameable.m_saddleItem, id, "saddle", active && tameable.enabled);
        var ai = prefab.GetComponent<MonsterAI>();
        if (ai && ai.m_consumeItems != null)
        {
            // P6-15 (D065): a tameable creature's foods also carry its name token for the "Eaten by" filter. Mods that
            // make creatures tameable or change their food (Monstrum's CreatureManager) edit these same components.
            // A creature that starts tamed (a summon, such as game 1.0.20's Spirit Caller boar) is never tamed by food.
            var character = prefab.GetComponent<Character>();
            var tame = tameable && !tameable!.m_startsTamed ? character && !string.IsNullOrEmpty(character.m_name) ? character.m_name : id : "";
            var foods = ai.m_consumeItems; var count = foods.Count;
            if (count > MaxNestedEntries) throw new MalformedDefinitionException("Creature food bound exceeded.");
            for (var i = 0; i < count; i++)
            {
                check();
                if (!ai || !ReferenceEquals(ai.m_consumeItems, foods) || foods.Count != count) throw new InvalidDataException("Creature foods changed during capture.");
                yield return Use(id + ":feed:" + i, foods[i], id, "creature-food", active && ai.enabled);
                if (tame.Length != 0 && foods[i] && Item(foods[i].gameObject) is { Length: > 0 } food)
                    yield return new LoadedSourceFact(LoadedSourceFactKind.FacetEvidence, id + ":tame-food:" + i, output: food, route: id,
                        method: "eaten-by", detail: tame, amount: 1, eligible: active && ai.enabled && tameable!.enabled);
            }
            check();
            if (!ai || !ReferenceEquals(ai.m_consumeItems, foods) || foods.Count != count) throw new InvalidDataException("Creature foods changed during capture.");
        }
        var trader = prefab.GetComponent<Trader>();
        if (trader)
        {
            var entries = trader.m_useItems;
            if (entries == null || entries.Count > MaxNestedEntries) throw new MalformedDefinitionException("Trader handover bound exceeded.");
            var count = entries.Count;
            for (var i = 0; i < count; i++)
            {
                check();
                if (!trader || !ReferenceEquals(trader.m_useItems, entries) || entries.Count != count) throw new InvalidDataException("Trader handovers changed during capture.");
                var entry = entries[i]; if (entry == null) throw new MalformedDefinitionException("Null trader handover.");
                yield return Use(id + ":handover:" + i, entry.m_prefab, id, "trader-handover", active && trader.enabled);
            }
            check();
            if (!trader || !ReferenceEquals(trader.m_useItems, entries) || entries.Count != count) throw new InvalidDataException("Trader handovers changed during capture.");
        }
        var bowl = prefab.GetComponent<OfferingBowl>();
        if (bowl && bowl.m_bossItem) yield return Use(id + ":offering", bowl.m_bossItem, id, "offering", active && bowl.enabled && !bowl.m_useItemStands, bowl.m_bossItems);
        var launcher = prefab.GetComponent<Catapult>();
        if (launcher)
        {
            // Explicit shared-name overrides take precedence over all exclusion/type/attachment gates.
            // Broad allowed item types and the damage fallback are not evidence of ammunition semantics.
            var ammunition = launcher.m_includeItemsOverride;
            if (ammunition == null || ammunition.Count > MaxNestedEntries) throw new MalformedDefinitionException("Catapult include bound exceeded.");
            var count = ammunition.Count;
            for (var i = 0; i < count; i++)
            {
                check();
                if (!launcher || !ReferenceEquals(launcher.m_includeItemsOverride, ammunition) || ammunition.Count != count)
                    throw new InvalidDataException("Catapult includes changed during capture.");
                if (!ammunition[i] || ammunition[i].m_itemData?.m_shared == null)
                {
                    yield return new LoadedSourceFact(LoadedSourceFactKind.Coverage, id + ":siege:gap", detail: "gap: malformed include faults Any before later entries");
                    break;
                }
                yield return Use(id + ":siege:" + i, ammunition[i], id, "siege-ammunition", active && launcher.enabled);
            }
            check();
            if (!launcher || !ReferenceEquals(launcher.m_includeItemsOverride, ammunition) || ammunition.Count != count)
                throw new InvalidDataException("Catapult includes changed during capture.");
        }
        check();
    }
    private static LoadedSourceFact Use(string id, ItemDrop? target, string consumer, string method, bool eligible, int amount = 1)
    {
        var name = target ? Item(target.gameObject) : "";
        return name.Length == 0 ? Coverage(id, "gap: unresolved " + method + " inventory identity") :
            new LoadedSourceFact(LoadedSourceFactKind.ItemUse, id, output: name, route: consumer, method: method,
                amount: amount, eligible: eligible);
    }
    private static IEnumerable<LoadedSourceFact?> Machine(GameObject prefab, string id, Action check)
    {
        check(); var piece = prefab.GetComponent<Piece>(); var active = !piece || piece.m_enabled;
        var smelter = prefab.GetComponent<Smelter>();
        if (smelter)
        {
            if (smelter.m_conversion == null || smelter.m_conversion.Count > MaxNestedEntries) throw new MalformedDefinitionException("Smelter conversion bound exceeded.");
            var conversions = smelter.m_conversion; var count = conversions.Count;
            for (var i = 0; i < count; i++)
            {
                check();
                if (!smelter || !ReferenceEquals(smelter.m_conversion, conversions) || conversions.Count != count)
                    throw new InvalidDataException("Smelter registrations changed during capture.");
                var conversion = conversions[i]; if (conversion == null) throw new MalformedDefinitionException("Null smelter conversion.");
                yield return Conversion(id + ":smelt:" + i, conversion.m_from, conversion.m_to, "smelt", active && smelter.enabled);
            }
            check();
            if (!smelter || !ReferenceEquals(smelter.m_conversion, conversions) || conversions.Count != count)
                throw new InvalidDataException("Smelter registrations changed during capture.");
        }
        var cooking = prefab.GetComponent<CookingStation>();
        if (cooking)
        {
            if (cooking.m_conversion == null || cooking.m_conversion.Count > MaxNestedEntries) throw new MalformedDefinitionException("Cooking conversion bound exceeded.");
            var conversions = cooking.m_conversion; var count = conversions.Count;
            for (var i = 0; i < count; i++)
            {
                check();
                if (!cooking || !ReferenceEquals(cooking.m_conversion, conversions) || conversions.Count != count)
                    throw new InvalidDataException("Cooking registrations changed during capture.");
                var conversion = conversions[i]; if (conversion == null) throw new MalformedDefinitionException("Null cooking conversion.");
                yield return Conversion(id + ":cook:" + i, conversion.m_from, conversion.m_to, "cook", active && cooking.enabled);
            }
            check();
            if (!cooking || !ReferenceEquals(cooking.m_conversion, conversions) || conversions.Count != count)
                throw new InvalidDataException("Cooking registrations changed during capture.");
        }
        var fermenter = prefab.GetComponent<Fermenter>();
        if (fermenter)
        {
            if (fermenter.m_conversion == null || fermenter.m_conversion.Count > MaxNestedEntries) throw new MalformedDefinitionException("Fermenter conversion bound exceeded.");
            var conversions = fermenter.m_conversion; var count = conversions.Count;
            for (var i = 0; i < count; i++)
            {
                check();
                if (!fermenter || !ReferenceEquals(fermenter.m_conversion, conversions) || conversions.Count != count)
                    throw new InvalidDataException("Fermentation registrations changed during capture.");
                var conversion = conversions[i]; if (conversion == null) throw new MalformedDefinitionException("Null fermentation conversion.");
                yield return Conversion(id + ":ferment:" + i, conversion.m_from, conversion.m_to, "ferment", active && fermenter.enabled && conversion.m_producedItems > 0);
            }
            check();
            if (!fermenter || !ReferenceEquals(fermenter.m_conversion, conversions) || conversions.Count != count)
                throw new InvalidDataException("Fermentation registrations changed during capture.");
        }
        var plant = prefab.GetComponent<Plant>();
        if (!plant) yield break;
        if (!piece || piece.m_resources == null || piece.m_resources.Length == 0)
        { yield return Coverage(id + ":plant", "gap: missing planting materials"); yield break; }
        if (piece.m_resources.Length > MaxNestedEntries || plant.m_grownPrefabs == null || plant.m_grownPrefabs.Length > 64)
            throw new MalformedDefinitionException("Cultivation input/output bound exceeded.");
        var grownPrefabs = plant.m_grownPrefabs; var resources = piece.m_resources;
        void CheckPlant()
        {
            check();
            if (!plant || !piece || !ReferenceEquals(plant.m_grownPrefabs, grownPrefabs) || !ReferenceEquals(piece.m_resources, resources))
                throw new InvalidDataException("Cultivation registrations changed during capture.");
        }
        for (var grownIndex = 0; grownIndex < grownPrefabs.Length; grownIndex++)
        {
            CheckPlant(); var grown = grownPrefabs[grownIndex]; var pick = grown ? grown.GetComponent<Pickable>() : null;
            var route = id + ":cultivate:" + grownIndex;
            if (!pick || !pick.m_itemPrefab) { yield return Coverage(route, "gap: grown root is not a direct output Pickable"); continue; }
            var possible = active && plant.enabled && pick.m_amount > 0 &&
                (!pick.m_defaultPicked || pick.enabled && pick.m_respawnTimeMinutes > 0 && pick.m_spawnCheck == null);
            var output = Item(pick.m_itemPrefab);
            if (output.Length == 0) { yield return Coverage(route, "gap: unresolved cultivation output inventory identity"); continue; }
            for (var resourceIndex = 0; resourceIndex < resources.Length; resourceIndex++)
            {
                CheckPlant(); var resource = resources[resourceIndex];
                if (resource != null && (resource.m_upgraderResource || resource.m_amount <= 0)) { yield return null; continue; }
                var input = resource?.m_resItem ? Item(resource.m_resItem.gameObject) : "";
                if (input.Length == 0) input = Missing(route + ":" + resourceIndex);
                yield return new LoadedSourceFact(LoadedSourceFactKind.RouteInput, route + ":" + resourceIndex, output, input,
                    route, "cultivate", "natural-material-origin; planting/growth biome is not an output source", amount: resource?.m_amount ?? 1, eligible: possible);
            }
        }
        CheckPlant();
    }
    private static LoadedSourceFact Conversion(string id, ItemDrop? from, ItemDrop? to, string kind, bool eligible)
    {
        var output = to ? Item(to.gameObject) : "";
        if (output.Length == 0) return Coverage(id, "gap: missing/unresolved conversion output");
        var input = from ? Item(from.gameObject) : "";
        return new LoadedSourceFact(LoadedSourceFactKind.RouteInput, id, output, input.Length == 0 ? Missing(id) : input,
            id, kind, "machine/fuel equipment never relabels material origin", eligible: eligible);
    }
    private static string Item(GameObject prefab)
    {
        var drop = prefab.GetComponent<ItemDrop>(); if (!drop) return "";
        var configured = drop.m_itemData?.m_dropPrefab;
        var db = ObjectDB.instance; if (!db) return "";
        var registered = configured ? db.GetItemPrefab(configured.name) : null;
        if (!registered) registered = db.GetItemPrefab(prefab.name);
        if (!registered)
        {
            var cloneBase = LoadedSourceSemantics.CloneBaseName(prefab.name);
            if (cloneBase != null) registered = db.GetItemPrefab(cloneBase);
        }
        return registered && registered.GetComponent<ItemDrop>() ? registered.name : "";
    }
    private static LoadedSourceFact Coverage(string id, string detail) => new LoadedSourceFact(LoadedSourceFactKind.Coverage, id, detail: detail);
    private static string Missing(string id) => "yavsos:missing-source:" + id;
    private static bool FinitePositive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    public void Dispose() { active = false; cache.Dispose(); optionalWork.Dispose(); }
}

/// <summary>One definition's own data is malformed (D086 D3): only that definition's evidence is skipped.</summary>
internal sealed class MalformedDefinitionException : Exception
{
    internal MalformedDefinitionException(string message) : base(message) { }
}
