using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Integrations;

/// <summary>
/// Unity-thread optional-mod adapters. Probes read loaded plugin metadata once; capture reads loaded definitions
/// through the supported managers and the current piece/drop fields; equipment rarity reads one instance's
/// existing custom data on each call. Nothing is written, patched, cached per instance or loaded from disk.
/// </summary>
internal sealed class OptionalIntegrations
{
    private const int ItemsPerStep = 64;
    private readonly OptionalJotunnApi? jotunn;
    private readonly OptionalEpicLootSemanticsApi? semanticApi;
    internal OptionalCapability Jotunn { get; }
    internal OptionalCapability EpicLoot { get; }
    internal OptionalCapability EpicLootSemantics { get; }
    /// <summary>
    /// SF5 (D087): adapters that failed during this capture (not at the probe, which <c>LoadedSourceHost.Report</c> covers),
    /// or that skipped part of their input, with what is Unknown because of it. Filled as the capture runs.
    /// </summary>
    internal List<(OptionalCapability Capability, string Effect)> CaptureProblems { get; } = new();
    private OptionalIntegrations(OptionalCapability jotunnCapability, OptionalJotunnApi? api, OptionalCapability epicLoot,
        OptionalCapability semantics, OptionalEpicLootSemanticsApi? semanticApi)
    { Jotunn = jotunnCapability; jotunn = api; EpicLoot = epicLoot; EpicLootSemantics = semantics; this.semanticApi = semanticApi; }

    // Call after the chainloader has finished, e.g. when a world session begins.
    internal static OptionalIntegrations Probe()
    {
        FindPlugin(OptionalJotunnApi.PluginGuid, out var jotunnVersion, out var jotunnTypes);
        var jotunnCapability = OptionalJotunnApi.Probe(jotunnVersion, jotunnTypes, out var api);
        FindPlugin(OptionalEpicLootRarity.PluginGuid, out var epicVersion, out var epicTypes);
        var semantics = OptionalEpicLootSemanticsApi.Probe(epicVersion, epicTypes, out var semanticApi);
        return new OptionalIntegrations(jotunnCapability, api, OptionalEpicLootApi.Probe(epicVersion, epicTypes), semantics, semanticApi);
    }

    private static void FindPlugin(string guid, out string? version, out OptionalTypeResolver? resolve)
    {
        version = null; resolve = null;
        if (!Chainloader.PluginInfos.TryGetValue(guid, out var info) || info == null || info.Metadata == null || !info.Instance) return;
        version = info.Metadata.Version?.ToString() ?? "";
        var assembly = info.Instance.GetType().Assembly;
        resolve = name => name switch
        {
            "UnityEngine.GameObject" => typeof(GameObject), "BepInEx.BepInPlugin" => typeof(BepInPlugin),
            "ItemDrop+ItemData" => typeof(ItemDrop.ItemData), _ => Resolve(assembly, name)
        };
    }
    private static Type? Resolve(Assembly assembly, string name)
    {
        try { return assembly.GetType(name, false); }
        catch (Exception error) when (error is ArgumentException or FileLoadException or BadImageFormatException) { return null; }
    }

    /// <summary>
    /// Raw Coverage/FacetEvidence rows for the loaded-source receipt; null marks a cooperative step boundary.
    /// <paramref name="check"/> is the caller's registration freshness guard and runs before each step.
    /// </summary>
    internal IEnumerable<LoadedSourceFact?> Capture(CatalogSnapshot catalog, ObjectDB db, List<GameObject> definitions,
        Action check, OptionalDefinitionWork work)
    {
        void Fresh()
        {
            check();
            if (!db || !ReferenceEquals(ObjectDB.instance, db)) throw new InvalidDataException("Item registry changed during optional capture.");
        }
        Fresh();
        foreach (var row in OptionalEpicLootRarity.MaterialFacts(catalog, EpicLoot)) yield return row;
        yield return null;
        foreach (var row in CaptureSemantics(catalog, db, Fresh)) yield return row;
        Fresh();
        var observations = new List<OptionalDefinitionObservation>();
        var api = jotunn;
        if (api != null)
        {
            var step = 0;
            foreach (var name in catalog.Items.Keys)
            {
                if (++step % ItemsPerStep == 0) { yield return null; Fresh(); }
                var loaded = db.GetItemPrefab(name);
                if (!loaded) continue;
                var owner = api.ItemOwner(name, loaded);
                if (owner != null) observations.Add(new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, name, owner, name));
                if (api.Faulted || observations.Count > OptionalDefinitionEvidence.MaximumObservations) break;
            }
            yield return null;
            var count = definitions.Count;
            for (var i = 0; i < count && !api.Faulted && observations.Count <= OptionalDefinitionEvidence.MaximumObservations; i++)
            {
                Fresh();
                if (definitions.Count != count) throw new InvalidDataException("Loaded prefab registrations changed during optional capture.");
                var prefab = definitions[i];
                if (!prefab) throw new InvalidDataException("Null/destroyed loaded prefab registration.");
                var piece = prefab.GetComponent<Piece>();
                var drops = prefab.GetComponent<Character>() ? prefab.GetComponent<CharacterDrop>() : null;
                if (!piece && !drops)
                {
                    if ((i + 1) % ItemsPerStep == 0) yield return null;
                    continue;
                }
                var name = prefab.name;
                api.ReadDefinition(Fresh, () =>
                {
                    if (piece && api.PieceOwner(name, prefab) is { } builder) PieceResources(db, piece, name, builder, observations);
                    if (drops && api.CreatureOwner(name, prefab) is { } creature) CreatureDrops(db, drops!, name, creature, observations);
                });
                yield return null;
            }
        }
        Fresh();
        var capability = api?.CaptureCapability(Jotunn) ?? Jotunn;
        if (Jotunn.Supported && !capability.Supported)
            CaptureProblems.Add((capability, "Item origin is \"origin not known\" for items Jotunn mods add, and which mod's pieces use or creatures drop an item is unknown"));
        // A faulted adapter publishes no partial ownership; every dependent dimension stays Unknown.
        // Over-bound collections are passed through so the builder records the bound gap instead of truncating.
        var admitted = capability.Supported ? observations : new List<OptionalDefinitionObservation>();
        foreach (var row in work.Capture(catalog, capability, admitted))
        {
            Fresh();
            yield return row;
        }
        Fresh();
        // P6-21 Item origin: ItemManager registrations, then the base game's items; Jotunn's claims are never repeated.
        var claimed = new HashSet<string>(admitted.Where(x => x.Kind == OptionalDefinitionKind.ItemRegistration).Select(x => x.Item), StringComparer.Ordinal);
        var managed = new List<(string Item, string Owner)>();
        var managers = ItemOrigins.Managers(managed);
        if (!managers.Supported || managers.Diagnostic.Contains("other shape"))
            CaptureProblems.Add((managers, managers.Supported ? "Item origin is \"origin not known\" for the items of the mods listed as skipped"
                : "Item origin is \"origin not known\" for items mods add through their own ItemManager copy"));
        // D086 B3.10: one slot per ItemManager copy that lists the item, so two copies claiming one prefab make only that
        // item ambiguous (its origin Unknown) instead of failing every ItemManager origin as a duplicate slot.
        var registrations = managed.Where(x => !claimed.Contains(x.Item) && catalog.Items.ContainsKey(x.Item))
            .GroupBy(x => x.Item, StringComparer.Ordinal)
            .SelectMany(group => group.Select((x, copy) => new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, x.Item, x.Owner, x.Item, copy)))
            .ToList();
        yield return null;
        foreach (var row in work.Capture(catalog, managers, registrations)) { Fresh(); yield return row; }
        if (managers.Supported) claimed.UnionWith(registrations.Select(x => x.Item));
        Fresh();
        var baseGame = ItemOrigins.BaseGame(out var vanilla);
        if (!baseGame.Supported) CaptureProblems.Add((baseGame, "Item origin \"Base game\" matches no item; base-game items are \"origin not known\""));
        var native = vanilla.Where(x => !claimed.Contains(x) && catalog.Items.ContainsKey(x))
            .Select(x => new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, x, Core.Interface.RuleText.Vanilla, x)).ToList();
        yield return null;
        foreach (var row in work.Capture(catalog, baseGame, native)) { Fresh(); yield return row; }
        Fresh();
        // Ownership capture yields after semantic rows. Cover that tail before accepting the complete receipt.
        if (semanticApi != null && !semanticApi.Faulted) semanticApi.CheckDefinitions();
    }

    private IEnumerable<LoadedSourceFact?> CaptureSemantics(CatalogSnapshot catalog, ObjectDB db, Action fresh)
    {
        var api = semanticApi;
        var rows = new List<LoadedSourceFact>(); var bytes = 0;
        void Add(LoadedSourceFact row)
        {
            bytes = checked(bytes + row.ScalarBytes);
            if (rows.Count >= 32768 || bytes > 8 * 1024 * 1024) throw new InvalidDataException("Optional semantic evidence bound exceeded.");
            rows.Add(row);
        }
        if (api != null)
        {
            api.StartCapture(fresh);
            // Config pairs participate in receipt identity even when no item uses a changed pair.
            var pairs = new List<string>(api.Pairs); pairs.Sort(StringComparer.Ordinal);
            foreach (var pair in pairs)
            {
                api.ReadDefinition(fresh, () => Add(new LoadedSourceFact(LoadedSourceFactKind.Coverage,
                    "optional:" + OptionalEpicLootSemantics.Adapter + ":pair:" + pair, detail: pair)));
                yield return null;
            }
            var step = 0;
            foreach (var item in catalog.Items.Values)
            {
                if (api.Faulted) break;
                var readPayload = false;
                api.ReadDefinition(fresh, () =>
                {
                    api.CheckDefinitions();
                    var loaded = db.GetItemPrefab(item.Identity);
                    if (!loaded || loaded.name != item.Identity) return;
                    var drop = loaded.GetComponent<ItemDrop>();
                    var data = drop ? drop.m_itemData : null;
                    if (data?.m_shared == null) return;
                    if (data.m_shared.m_itemType.ToString() != item.Value("type") || data.m_shared.m_ammoType != item.Value("ammoType") ||
                        data.m_shared.m_name != item.Value("nameKey")) throw new InvalidDataException("Item facts changed during semantic capture.");
                    string? payload = null;
                    if (item.Value("type") == "Misc" && data.m_customData != null)
                        data.m_customData.TryGetValue(OptionalEpicLootRarity.InstanceDataKey, out payload);
                    readPayload = payload != null;
                    var observation = OptionalEpicLootSemantics.Observe(item, EpicLootSemantics, api.Pairs, payload);
                    if (observation != null) Add(observation);
                });
                if (++step % ItemsPerStep == 0 || readPayload) yield return null;
            }
            api.ReadDefinition(fresh, api.CheckDefinitions);
        }
        fresh();
        var capability = api?.CaptureCapability(EpicLootSemantics) ?? EpicLootSemantics;
        if (EpicLootSemantics.Supported && !capability.Supported)
            CaptureProblems.Add((capability, "EpicLoot's own items (dust, shards, runestones, socket tools and materials, unidentified items) aren't recognised by their role, so they get only their general categories"));
        yield return new LoadedSourceFact(LoadedSourceFactKind.Coverage,
            "optional:" + OptionalEpicLootSemantics.Adapter + ":capability", detail: capability.Witness);
        if (!capability.Supported) yield break; // Discard all partial semantic evidence on any adapter fault.
        foreach (var row in rows)
        {
            fresh(); api!.CheckDefinitions(); // Publication is part of capture; drift cannot publish a partial semantic set.
            yield return row;
        }
    }

    private static void PieceResources(ObjectDB db, Piece piece, string name, string owner, List<OptionalDefinitionObservation> output)
    {
        var resources = piece.m_resources;
        if (resources == null || resources.Length > 256) throw new InvalidDataException("Piece resource bound exceeded.");
        for (var i = 0; i < resources.Length; i++)
        {
            var resource = resources[i] ?? throw new InvalidDataException("Null piece resource.");
            output.Add(new OptionalDefinitionObservation(OptionalDefinitionKind.PieceResource, name, owner,
                Registered(db, resource.m_resItem ? resource.m_resItem.gameObject : null), i, resource.m_amount,
                piece.m_enabled && !resource.m_upgraderResource));
        }
    }

    private static void CreatureDrops(ObjectDB db, CharacterDrop drops, string name, string owner, List<OptionalDefinitionObservation> output)
    {
        var entries = drops.m_drops;
        if (entries == null || entries.Count > 256) throw new InvalidDataException("Character drop bound exceeded.");
        for (var i = 0; i < entries.Count; i++)
        {
            var drop = entries[i] ?? throw new InvalidDataException("Null character drop.");
            var chance = drop.m_chance;
            output.Add(new OptionalDefinitionObservation(OptionalDefinitionKind.CreatureDrop, name, owner, Registered(db, drop.m_prefab), i,
                drop.m_amountMax, drops.enabled && chance > 0 && !float.IsNaN(chance) && !float.IsInfinity(chance)));
        }
    }

    // Exact registration only: no clone-suffix, display-name or prefix inference.
    private static string Registered(ObjectDB db, GameObject? reference)
    {
        if (!reference) return "";
        var name = reference!.name; var registered = db.GetItemPrefab(name);
        return registered && ReferenceEquals(registered, reference) && registered.GetComponent<ItemDrop>() ? name : "";
    }

    /// <summary>Current EpicLoot rarity of one equipment instance for rule filters: keeps "no rarity" apart from "could not be read" (<see cref="OptionalEpicLootRarity.ReadInstanceForFilters"/>).</summary>
    internal IReadOnlyList<FacetEvidence> FilterEquipmentRarity(ClassificationResult item, ItemDrop.ItemData data) =>
        OptionalEpicLootRarity.ReadInstanceForFilters(EpicLoot, item, data.m_customData);
}
