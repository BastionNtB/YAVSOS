using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>Items a chest's rule asks for that the stasher's own config forbids in that chest type (P4-00f, D056).</summary>
public sealed class StasherExclusionConflicts
{
    public const int MaximumExamples = 5;
    public int Count { get; }
    /// <summary>Up to <see cref="MaximumExamples"/> conflicting items, in ordinal order.</summary>
    public IReadOnlyList<string> Examples { get; }
    /// <summary>Every conflicting item, in ordinal order (the item grid marks each one).</summary>
    public IReadOnlyList<string> Items { get; }
    private StasherExclusionConflicts(IReadOnlyList<string> items)
    { Items = items; Count = items.Count; Examples = Array.AsReadOnly(items.Take(MaximumExamples).ToArray()); }

    /// <summary>
    /// Counts the known items the rule accepts and the stasher excludes. Example: a Swords rule on a wooden chest,
    /// where AzuAutoStore's config excludes Swords from wooden chests, conflicts on every sword.
    /// </summary>
    public static StasherExclusionConflicts Find(IEnumerable<string> prefabs, Func<string, bool> ruleAccepts, Func<string, bool> stasherAllows)
    {
        if (prefabs == null) throw new ArgumentNullException(nameof(prefabs));
        if (ruleAccepts == null) throw new ArgumentNullException(nameof(ruleAccepts));
        if (stasherAllows == null) throw new ArgumentNullException(nameof(stasherAllows));
        var conflicts = prefabs.Where(x => x != null).Distinct(StringComparer.Ordinal).Where(x => ruleAccepts(x) && !stasherAllows(x))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new StasherExclusionConflicts(conflicts.AsReadOnly());
    }
}
