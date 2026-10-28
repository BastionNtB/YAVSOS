using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>Detached incinerator conversion: inventory match keys (the game's shared item names) and amounts only.</summary>
public sealed class IncineratorConversionShape
{
    public int Priority { get; }
    public bool RequireOnlyOne { get; }
    public IReadOnlyList<KeyValuePair<string, int>> Requirements { get; }
    public IncineratorConversionShape(int priority, bool requireOnlyOne, IEnumerable<KeyValuePair<string, int>> requirements)
    {
        if (requirements == null) throw new ArgumentNullException(nameof(requirements));
        var rows = requirements.Take(LoadedProducerSemantics.MaxIncineratorRequirements + 1).ToArray();
        if (rows.Length > LoadedProducerSemantics.MaxIncineratorRequirements || rows.Any(x => string.IsNullOrEmpty(x.Key)))
            throw new ArgumentException("Invalid/oversized incinerator requirement shape.", nameof(requirements));
        Priority = priority; RequireOnlyOne = requireOnlyOne; Requirements = Array.AsReadOnly(rows);
    }
    // AttemptCraft divides by every visited amount, and an empty AND set crafts int.MaxValue results.
    public bool Safe => Requirements.All(x => x.Value > 0) && (RequireOnlyOne || Requirements.Count > 0);
}

/// <summary>Narrow detached interpretation of installed producer/acquisition definition fields. No game objects.</summary>
public static class LoadedProducerSemantics
{
    public const int MaxIncineratorConversions = 256, MaxIncineratorRequirements = 256;

    // SapCollector requires root level > 1 and converts it to int before draining.
    // Negative/unsupported regeneration cannot establish a stable definition witness.
    public static bool ResourceRootSupportsExtraction(float maximum, float regeneration) =>
        maximum > 1 && maximum < 2147483648f && regeneration >= 0 && !float.IsInfinity(regeneration);

    // AttemptCraft: AND needs every integer quotient >= 1; one-of needs any quotient >= 1.
    public static bool IncineratorFires(IncineratorConversionShape conversion, IReadOnlyDictionary<string, int> inventory)
    {
        if (conversion == null || inventory == null) throw new ArgumentNullException(nameof(conversion));
        if (!conversion.Safe) throw new ArgumentException("Unsafe incinerator conversion.", nameof(conversion));
        foreach (var requirement in conversion.Requirements)
        {
            var enough = inventory.TryGetValue(requirement.Key, out var count) && count / requirement.Value >= 1;
            if (conversion.RequireOnlyOne && enough) return true;
            if (!conversion.RequireOnlyOne && !enough) return false;
        }
        return !conversion.RequireOnlyOne;
    }

    // Sufficient, not necessary: the minimal inventory for one alternative must survive every conversion that may run first.
    // Awake sorts by descending priority with an unstable sort, so equal priorities may run first. Any unsafe conversion can
    // fault after earlier removals and lose items, so it disqualifies the whole machine.
    public static bool IncineratorAlternativeReachable(IReadOnlyList<IncineratorConversionShape> conversions, int conversion, int requirement)
    {
        if (conversions == null) throw new ArgumentNullException(nameof(conversions));
        if (conversions.Count > MaxIncineratorConversions || conversions.Any(x => x == null) ||
            conversions.Sum(x => x.Requirements.Count) > MaxIncineratorRequirements)
            throw new ArgumentException("Invalid/oversized incinerator definition.", nameof(conversions));
        if (conversion < 0 || conversion >= conversions.Count) throw new ArgumentOutOfRangeException(nameof(conversion));
        var target = conversions[conversion];
        if (target.RequireOnlyOne ? requirement < 0 || requirement >= target.Requirements.Count : requirement != -1)
            throw new ArgumentOutOfRangeException(nameof(requirement));
        if (conversions.Any(x => !x.Safe) || target.Requirements.Count == 0) return false;
        var inventory = new Dictionary<string, int>(StringComparer.Ordinal);
        void Need(KeyValuePair<string, int> row) =>
            inventory[row.Key] = inventory.TryGetValue(row.Key, out var count) ? Math.Max(count, row.Value) : row.Value;
        if (target.RequireOnlyOne) Need(target.Requirements[requirement]);
        else foreach (var row in target.Requirements) Need(row);
        for (var i = 0; i < conversions.Count; i++)
            if (i != conversion && conversions[i].Priority >= target.Priority && IncineratorFires(conversions[i], inventory)) return false;
        return IncineratorFires(target, inventory);
    }

    // Procreate returns while Random.value <= chance; Random.value is inclusive [0, 1], so only chance < 1 can initiate.
    public static bool ProcreationInitiates(float pregnancyChance) => !float.IsNaN(pregnancyChance) && pregnancyChance < 1;

    // IsDue compares elapsed seconds to duration. Non-finite durations are outside supported acquisition evidence.
    public static bool ProcreationCompletes(float pregnancyDuration) => !float.IsNaN(pregnancyDuration) && !float.IsInfinity(pregnancyDuration);

    // Initiation counts parent-prefab plus offspring-prefab instances (including the parent itself) against maxCreatures.
    // A same-prefab partner that must already be present is counted too; matching offspring prefabs count the same instances again.
    public static bool ProcreationCapacity(int maxCreatures, bool partnerAtInitiation, bool separatePartner,
        bool offspringIsParent, bool offspringIsPartner)
    {
        var parents = 1 + (partnerAtInitiation && !separatePartner ? 1 : 0);
        var offspring = (offspringIsParent ? parents : 0) + (partnerAtInitiation && separatePartner && offspringIsPartner ? 1 : 0);
        return parents + offspring < maxCreatures;
    }

    // StoreGui adds min(stack, maxStack) and removes the price; negative prices are not a supported purchase.
    public static int PurchaseAmount(int stack, int maxStackSize, int price) => price < 0 ? 0 : Math.Max(0, Math.Min(stack, maxStackSize));
}
