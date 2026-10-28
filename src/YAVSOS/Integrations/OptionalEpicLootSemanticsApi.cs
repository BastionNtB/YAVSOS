using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using YAVSOS.Core.Classification;

namespace YAVSOS.Integrations;

/// <summary>Public loaded shard definitions only. Never invokes item helpers, loads assets or creates components.</summary>
internal sealed class OptionalEpicLootSemanticsApi
{
    private readonly PropertyInfo definitions;
    private readonly FieldInfo rarities;
    private readonly Type shardType, rarityType;
    private readonly Func<IList, RaritySnapshot> snapshotRarities;
    private IDictionary? registry;
    private readonly List<(object Key, object Definition, IList Rarities, RaritySnapshot Snapshot)> captured = new();
    internal HashSet<string> Pairs { get; } = new(StringComparer.Ordinal);
    internal bool Faulted { get; private set; }
    private string fault = "";
    private OptionalEpicLootSemanticsApi(PropertyInfo definitions, FieldInfo rarities, Type shardType, Type rarityType)
    {
        this.definitions = definitions; this.rarities = rarities; this.shardType = shardType; this.rarityType = rarityType;
        snapshotRarities = (Func<IList, RaritySnapshot>)typeof(OptionalEpicLootSemanticsApi)
            .GetMethod(nameof(SnapshotRarities), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(rarityType).CreateDelegate(typeof(Func<IList, RaritySnapshot>));
    }

    // Close over the supported enum type once. IList enumeration/indexing boxes every enum
    // on every guard; the typed copy/comparer checks all values without that recurring churn.
    private abstract class RaritySnapshot { internal abstract bool Unchanged(); }
    private sealed class RaritySnapshot<T> : RaritySnapshot where T : struct, Enum
    {
        private readonly List<T> list;
        private readonly T[] values;
        internal RaritySnapshot(List<T> list) { this.list = list; values = list.ToArray(); }
        internal override bool Unchanged()
        {
            if (list.Count != values.Length) return false;
            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < values.Length; i++)
                if (!comparer.Equals(list[i], values[i])) return false;
            return true;
        }
    }
    private static RaritySnapshot SnapshotRarities<T>(IList list) where T : struct, Enum => new RaritySnapshot<T>((List<T>)list);

    internal static OptionalCapability Probe(string? version, OptionalTypeResolver? resolve, out OptionalEpicLootSemanticsApi? api)
    {
        api = null;
        var storage = OptionalEpicLootApi.Probe(version, resolve);
        const string adapter = OptionalEpicLootSemantics.Adapter;
        if (!storage.Supported) return new OptionalCapability(adapter, storage.State, storage.ObservedVersion, storage.Diagnostic);
        try
        {
            var magic = OptionalJotunnApi.Require(resolve!, "EpicLoot.MagicItem");
            var flag = magic.GetField("IsUnidentified", BindingFlags.Public | BindingFlags.Instance);
            if (flag == null || flag.FieldType != typeof(bool) || flag.IsInitOnly ||
                System.Linq.Enumerable.Any(flag.GetCustomAttributesData(), a => a.AttributeType.Name.Contains("Json")))
                throw new MissingMemberException("MagicItem.IsUnidentified");
            var type = OptionalJotunnApi.Require(resolve!, "EpicLoot.ShardStones.ShardType");
            var rarity = OptionalJotunnApi.Require(resolve!, "EpicLoot.ItemRarity");
            var definition = OptionalJotunnApi.Require(resolve!, "EpicLoot.ShardStones.ShardDefinition");
            var owner = OptionalJotunnApi.Require(resolve!, "EpicLoot.ShardStones.Shards+ShardDefinitions");
            var registry = owner.GetProperty("ShardEffects", BindingFlags.Public | BindingFlags.Static);
            var values = definition.GetField("Rarities", BindingFlags.Public | BindingFlags.Instance);
            if (!type.IsEnum || Enum.GetUnderlyingType(type) != typeof(int) || !rarity.IsEnum ||
                Enum.GetUnderlyingType(rarity) != typeof(int) || registry?.GetGetMethod() == null ||
                registry.PropertyType != typeof(Dictionary<,>).MakeGenericType(type, definition) ||
                values == null || values.FieldType != typeof(List<>).MakeGenericType(rarity))
                throw new MissingMemberException("ShardDefinitions.ShardEffects / ShardDefinition.Rarities");
            api = new OptionalEpicLootSemanticsApi(registry, values, type, rarity);
            return new OptionalCapability(adapter, OptionalCapabilityState.Supported, version!,
                "public active shard pairs; raw saved IsUnidentified definition flag; " + OptionalEpicLootSemantics.Version);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { return OptionalCapability.Incompatible(adapter, version!, "API shape: " + error.GetType().Name); }
    }

    internal void StartCapture(Action freshness)
    {
        freshness();
        Faulted = false; fault = ""; registry = null; captured.Clear(); Pairs.Clear();
        ReadDefinition(freshness, BeginCapture);
    }

    internal void BeginCapture()
    {
        registry = definitions.GetValue(null) as IDictionary ?? throw new InvalidOperationException("Missing shard registry.");
        if (registry.Count > 128) throw new InvalidOperationException("Shard family bound exceeded.");
        captured.Clear(); Pairs.Clear();
        foreach (DictionaryEntry entry in registry)
        {
            if (entry.Key.GetType() != shardType || !Enum.IsDefined(shardType, entry.Key))
                throw new InvalidOperationException("Unsupported shard family.");
            var family = Enum.GetName(shardType, entry.Key)!;
            if (family.Length == 0 || family.Length > 128 || family.IndexOf('|') >= 0)
                throw new InvalidOperationException("Malformed shard family.");
            if (entry.Value == null) throw new InvalidOperationException("Missing shard definition.");
            var list = rarities.GetValue(entry.Value) as IList ?? throw new InvalidOperationException("Missing shard rarities.");
            if (list.Count > 64) throw new InvalidOperationException("Shard rarity bound exceeded.");
            var snapshot = snapshotRarities(list);
            captured.Add((entry.Key, entry.Value, list, snapshot));
            if (family == "None") continue;
            foreach (var value in list)
            {
                if (value == null || value.GetType() != rarityType || !Enum.IsDefined(rarityType, value))
                    throw new InvalidOperationException("Unsupported shard rarity.");
                Pairs.Add(family + "|" + Enum.GetName(rarityType, value));
                if (Pairs.Count > OptionalEpicLootSemantics.MaximumShardPairs) throw new InvalidOperationException("Shard pair bound exceeded.");
            }
        }
    }

    internal void CheckDefinitions()
    {
        if (registry == null || !ReferenceEquals(registry, definitions.GetValue(null)) || registry.Count != captured.Count)
            throw new InvalidOperationException("Shard registry changed during capture.");
        foreach (var entry in captured)
            if (!ReferenceEquals(registry[entry.Key], entry.Definition) ||
                !ReferenceEquals(rarities.GetValue(entry.Definition), entry.Rarities) || !entry.Snapshot.Unchanged())
                throw new InvalidOperationException("Shard definitions changed during capture.");
    }

    internal void ReadDefinition(Action freshness, Action read)
    {
        freshness(); // Session/catalog failures belong to the whole capture, outside optional fault isolation.
        if (Faulted) return;
        try { read(); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { Faulted = true; fault = (error is TargetInvocationException { InnerException: { } inner } ? inner : error).GetType().Name; }
    }

    internal OptionalCapability CaptureCapability(OptionalCapability initial) => Faulted
        ? OptionalCapability.Incompatible(OptionalEpicLootSemantics.Adapter, initial.ObservedVersion, "faulted during capture: " + fault)
        : initial;
}
