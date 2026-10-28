using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>Definition roles from supported EpicLoot metadata, independent of prefab spelling and rarity facets.</summary>
public static class OptionalEpicLootSemantics
{
    public const string Adapter = "epicloot-semantics";
    public const string Version = "yavsos-epicloot-semantics-1";
    public const int MaximumShardPairs = 1024;

    /// <summary>The host supplies only pairs from the current supported definition registry and a read-only definition flag.</summary>
    public static LoadedSourceFact? Observe(CatalogFact item, OptionalCapability capability, ISet<string> shardPairs,
        string? savedDefinitionData = null)
    {
        if (item == null || capability == null || shardPairs == null) throw new ArgumentNullException(nameof(item));
        if (capability.Adapter != Adapter) throw new ArgumentException("EpicLoot semantics capability required.");
        if (!capability.Supported || item.Kind != CatalogFactKind.Item) return null;
        if (shardPairs.Count > MaximumShardPairs) throw new ArgumentException("Shard definition bound exceeded.");
        var type = item.Value("type"); var parts = item.Value("ammoType").Split('|');
        string? method = null;
        if (type == "Misc" && DefinitionIsUnidentified(savedDefinitionData)) method = "epicloot-unidentified";
        else if (type == "Misc" && parts.Length == 2 && Rarity(parts[0]) && parts[1] == "ShardSlotChisel")
            method = "epicloot-socket-tools";
        else if (type == "Material" && parts.Length == 3 && parts[0].Length != 0 && Rarity(parts[1]) &&
            parts[2] == "ShardStone" && shardPairs.Contains(parts[0] + "|" + parts[1], StringComparer.Ordinal))
            method = "epicloot-socket-materials";
        else if (type == "Material" && parts.Length == 2 && Rarity(parts[0]))
        {
            if (parts[1] == "Runestone") method = "epicloot-stones";
            else if (parts[1] == "MagicCraftingMaterial")
            {
                // These are untranslated definition keys, never translated display-name guesses.
                var prefix = "$mod_epicloot_" + parts[0] + " $mod_epicloot_assets_";
                var key = item.Value("nameKey");
                if (key == prefix + "dust") method = "epicloot-dust";
                else if (key == prefix + "shard") method = "epicloot-shards";
                else if (key == prefix + "reagent") method = "epicloot-reagents";
                else if (key == prefix + "essence") method = "epicloot-essence";
            }
        }
        if (method == null) return null;
        var identity = "optional:" + Adapter + ":" + item.Identity;
        if (!OptionalDefinitionEvidence.Scalar(identity)) return null;
        return new LoadedSourceFact(LoadedSourceFactKind.ItemUse, identity, output: item.Identity,
            method: method, route: Version + ": supported loaded definition metadata");
    }

    /// <summary>Reads saved definition data directly. No mod API, lazy component creation, cache or write is involved.</summary>
    public static bool DefinitionIsUnidentified(string? payload)
    {
        if (payload == null || payload.Length == 0 || payload.Length > OptionalEpicLootRarity.MaximumInstancePayload) return false;
        var reader = new OptionalEpicLootRarity.JsonScan(payload, identification: true);
        return reader.TopLevel(out var members) && members != null &&
            OptionalEpicLootRarity.StoredRarityIndex(members) >= 0 &&
            members.TryGetValue("IsUnidentified", out var flag) && flag.Kind == OptionalEpicLootRarity.JsonKind.Boolean && flag.Integer == 1;
    }

    private static bool Rarity(string value) => OptionalEpicLootRarity.RarityNames.Contains(value, StringComparer.Ordinal);
}
