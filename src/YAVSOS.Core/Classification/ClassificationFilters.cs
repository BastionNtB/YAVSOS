using System;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace YAVSOS.Core.Classification;

public sealed class ClassificationFilterRule
{
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; }
    public ClassificationFilterRule(ClassificationTaxonomy taxonomy, IEnumerable<string> categories,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? filters = null)
    {
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        var selected = ClassificationValidation.Bounded(categories, 256).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var id in selected) taxonomy.Require(id);
        Categories = Array.AsReadOnly(selected);
        var owned = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in ClassificationValidation.Bounded(filters ?? Array.Empty<KeyValuePair<string, IEnumerable<string>>>(), ClassificationFilters.MaximumRuleFilters))
        {
            var choices = ClassificationValidation.Bounded(pair.Value, 64).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            ClassificationFilters.ValidateRuleFilter(pair.Key, choices);
            if (owned.ContainsKey(pair.Key)) throw new ArgumentException("Duplicate filter dimension.");
            owned.Add(pair.Key, Array.AsReadOnly(choices));
        }
        Filters = new ReadOnlyDictionary<string, IReadOnlyList<string>>(owned);
    }
}

public enum AcceptanceMatch { None, Any, Filters, Category, FilteredCategory, ExactItem, Excluded }

/// <summary>Detached acceptance only. It grants no access, inventory eligibility or transfer authority.</summary>
public sealed class ClassificationAcceptance
{
    private readonly HashSet<string> included;
    private readonly HashSet<string> excluded;
    public IReadOnlyList<ClassificationFilterRule> Rules { get; }
    public ClassificationAcceptance(IEnumerable<ClassificationFilterRule> rules, IEnumerable<string>? includes = null, IEnumerable<string>? excludes = null)
    {
        var rows = ClassificationValidation.Bounded(rules, 64);
        if (rows.Any(x => x == null)) throw new ArgumentException("Null acceptance rule.");
        Rules = Array.AsReadOnly(rows);
        included = Prefabs(includes); excluded = Prefabs(excludes);
    }
    private static HashSet<string> Prefabs(IEnumerable<string>? input)
    {
        var rows = ClassificationValidation.Bounded(input ?? Array.Empty<string>(), 2048);
        foreach (var row in rows) ClassificationValidation.Text(row, 2048);
        return new HashSet<string>(rows, StringComparer.Ordinal);
    }
    public AcceptanceMatch Match(ClassificationResult item, IEnumerable<FacetEvidence>? currentInstanceFacets = null)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (excluded.Contains(item.Prefab)) return AcceptanceMatch.Excluded;
        if (included.Contains(item.Prefab)) return AcceptanceMatch.ExactItem;
        var instance = ClassificationFilters.InstanceFacets(item, currentInstanceFacets);
        var best = AcceptanceMatch.None;
        foreach (var rule in Rules)
            if (ClassificationFilters.MatchesValidated(item, rule, instance))
            {
                var filtered = rule.Filters.Values.Any(x => x.Count != 0);
                var match = rule.Categories.Count > 0 ? filtered ? AcceptanceMatch.FilteredCategory : AcceptanceMatch.Category
                    : filtered ? AcceptanceMatch.Filters : AcceptanceMatch.Any;
                if (match > best) best = match;
            }
        return best;
    }
    public bool Accepts(ClassificationResult item, IEnumerable<FacetEvidence>? currentInstanceFacets = null)
    {
        var match = Match(item, currentInstanceFacets);
        return match != AcceptanceMatch.None && match != AcceptanceMatch.Excluded;
    }

    /// <summary>
    /// P8-18 (Q14): whether some stack of the item could be accepted, for a search that has no stack to judge: filters
    /// on what a stack is (upgrade level, condition, rarity, crafter) are set aside; exact items, never-lists, categories
    /// and item filters still decide. Not a placement answer: stashing and cleanup judge the real stack.
    /// </summary>
    public bool CouldAccept(ClassificationResult item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (excluded.Contains(item.Prefab)) return false;
        if (included.Contains(item.Prefab)) return true;
        foreach (var rule in Rules)
            if (ClassificationFilters.MatchesValidated(item, rule, Array.Empty<FacetEvidence>(), setAsideInstance: true)) return true;
        return false;
    }
}

public static class ClassificationFilters
{
    public static IReadOnlyList<string> Dimensions { get; } = Array.AsReadOnly(new[]
        { "mod", "related-mod", "construction-mod", "rarity", "usage", "acquisition",
          "teleportable", "food-stat", "upgraded", "damaged", "crafter", "eaten-by", "upgradable", "quality" });
    /// <summary>
    /// Facts of one item instance that change as it is used (P6-07b): always fresh caller-supplied evidence, never a
    /// prefab fact. EpicLoot <c>rarity</c> is per instance only for equipment types (<see cref="ClassificationResult.InstanceRarity"/>).
    /// <c>upgradable</c> (a recipe can upgrade the item) and <c>quality</c> (the stack's upgrade level, P6-21) are read from
    /// the game's own item and recipe data on each peer, like the item's condition (maintainer, 2026-10-23).
    /// </summary>
    public static IReadOnlyList<string> InstanceDimensions { get; } = Array.AsReadOnly(new[] { "rarity", "upgraded", "damaged", "crafter", "upgradable", "quality" });
    /// <summary>Upgrade levels a filter can name (P6-21): 1 to 4, and 5+ for the Forge of Potential and similar.</summary>
    public static IReadOnlyList<string> Qualities { get; } = Array.AsReadOnly(new[] { "1", "2", "3", "4", "5+" });
    /// <summary>The level value of a stack's quality: "1" to "4", or "5+".</summary>
    public static string QualityValue(int quality) => quality >= 5 ? "5+" : Math.Max(1, quality).ToString(CultureInfo.InvariantCulture);
    /// <summary>
    /// Legacy "upgraded" filter values (before P6-21) as upgrade levels, for the editor to show and save: yes is level 2
    /// and above, no is level 1. A rule not edited since keeps evaluating "upgraded" as before.
    /// </summary>
    public static IReadOnlyList<string> LevelsFor(IEnumerable<string> upgraded)
    {
        var values = new HashSet<string>(upgraded ?? Array.Empty<string>(), StringComparer.Ordinal);
        return Qualities.Where(x => x == "1" ? values.Contains("no") : values.Contains("yes")).ToArray();
    }
    public static IReadOnlyList<string> Rarities { get; } = Array.AsReadOnly(new[] { "magic", "rare", "epic", "legendary", "mythic", "ancient" });
    public static IReadOnlyList<string> Uses { get; } = Array.AsReadOnly(new[]
        { "crafting", "building", "fuel", "processing", "food", "brewing", "farming", "feed", "equipment" });
    /// <summary>
    /// The food.* category rule: the game's fork-icon colour (<see cref="FoodStat"/>, D064). ZenUI's "simple majority"
    /// colour option can show a different colour for foods without a stat over twice the others.
    /// </summary>
    public static IReadOnlyList<string> FoodStats { get; } = Array.AsReadOnly(new[] { "health", "stamina", "eitr", "balanced" });
    public static IReadOnlyList<string> YesNo { get; } = Array.AsReadOnly(new[] { "yes", "no" });
    /// <summary>
    /// D064/Q12: a crafter filter names the player who set it: "by:&lt;player ID&gt;:&lt;name&gt;" (crafted by them) or
    /// "not-by:…" (crafted by anyone else). The name is for display only. An item nobody crafted has no row (unknown).
    /// </summary>
    public static string CrafterValue(bool by, long player, string name)
    {
        if (player == 0) throw new ArgumentOutOfRangeException(nameof(player));
        var shown = new string((name ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray());
        return (by ? "by:" : "not-by:") + player.ToString(CultureInfo.InvariantCulture) + ":" + shown;
    }
    public static bool TryParseCrafter(string value, out bool by, out long player, out string name)
    {
        by = false; player = 0; name = "";
        if (value == null) return false;
        var rest = value.StartsWith("by:", StringComparison.Ordinal) ? value.Substring(3)
            : value.StartsWith("not-by:", StringComparison.Ordinal) ? value.Substring(7) : null;
        if (rest == null) return false;
        by = value[0] == 'b';
        var colon = rest.IndexOf(':');
        if (colon <= 0 || !long.TryParse(rest.Substring(0, colon), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out player) || player == 0)
            return false;
        name = rest.Substring(colon + 1);
        return true;
    }
    /// <summary>
    /// Instance evidence that could not be read now (reader off, not ready, failed or an unsupported payload). It is
    /// observed, so it never falls back to "unknown" (plain), and no rule can name it, so any filter on that dimension
    /// rejects the item: missing evidence never broadens a filtered rule (P6-07b).
    /// </summary>
    public const string Unreadable = "unreadable";
    /// <summary>The closed value set of a dimension, for pickers; null when values are open (mod names, creature names).</summary>
    public static IReadOnlyList<string>? Values(string dimension) => dimension switch
    {
        "rarity" => Rarities, "usage" => Uses, "food-stat" => FoodStats,
        "teleportable" or "upgraded" or "damaged" or "upgradable" => YesNo, "quality" => Qualities, _ => null
    };
    /// <summary>Whether this dimension is read from the instance's current evidence instead of the item's facets.</summary>
    public static bool IsInstanceDimension(ClassificationResult item, string dimension) =>
        dimension == "rarity" ? item.InstanceRarity : dimension is "upgraded" or "damaged" or "crafter" or "upgradable" or "quality";
    internal static void ValidateDimension(string dimension)
    {
        if (!IsKnown(dimension)) throw new ArgumentException("Unknown filter dimension: " + dimension);
    }
    public static bool IsKnown(string dimension) => Dimensions.Contains(dimension, StringComparer.Ordinal);
    /// <summary>A facet value this version can name in a rule: a known dimension and, for a closed one, a value in its set (D081).</summary>
    internal static bool IsKnownValue(string dimension, string value)
    {
        if (!IsKnown(dimension)) return false;
        try { ValidateValue(dimension, value, false); return true; }
        catch (ArgumentException) { return false; }
    }
    /// <summary>A value this version can show in a rule filter: a known dimension's own value, "unknown" included (D086 D6).</summary>
    public static bool IsKnownRuleValue(string dimension, string value)
    {
        if (!IsKnown(dimension)) return false;
        try { ValidateValue(dimension, value, true); return true; }
        catch (ArgumentException) { return false; }
    }
    /// <summary>Filters one rule alternative can store: the known dimensions and room for ones a later version or an add-on adds (D077 B01).</summary>
    public const int MaximumRuleFilters = 32;
    /// <summary>
    /// A filter a stored rule may name (D077 B01, extending D050 to dimensions). A known dimension takes its own values. A
    /// dimension this version doesn't know (a later version's, or an add-on's that isn't installed, D079 B03) is kept as
    /// stored and matches nothing, so its alternative accepts less, never more; its values are only checked as bounded text.
    /// A value a known dimension doesn't have (a later version's) is kept the same way and matches nothing (D086 D6); the
    /// dimension's other values still match.
    /// </summary>
    internal static void ValidateRuleFilter(string dimension, IEnumerable<string> values)
    {
        if (IsKnown(dimension))
        {
            foreach (var value in values)
                if (!IsKnownRuleValue(dimension, value))
                {
                    ClassificationValidation.Text(value, 128);
                    if (value == Unreadable) throw new ArgumentException("Unreadable is current instance evidence only; a rule cannot name it.");
                }
            return;
        }
        ClassificationValidation.Category(dimension);
        foreach (var value in values)
        {
            ClassificationValidation.Text(value, 128);
            if (value == Unreadable) throw new ArgumentException("Unreadable is current instance evidence only; a rule cannot name it.");
        }
    }
    internal static void ValidateValue(string dimension, string value, bool allowUnknown)
    {
        ValidateDimension(dimension); ClassificationValidation.Text(value, 128);
        if (value == Unreadable)
        {
            if (allowUnknown || !InstanceDimensions.Contains(dimension, StringComparer.Ordinal))
                throw new ArgumentException("Unreadable is current instance evidence only; a rule cannot name it.");
            return;
        }
        if (value == "unknown")
        {
            if (!allowUnknown) throw new ArgumentException("Unknown is represented by missing evidence, not a known facet.");
            return;
        }
        if (dimension == "crafter")
        {
            // A rule names the setter ("by:…"/"not-by:…"); current evidence is a crafter's player ID.
            if (allowUnknown ? !TryParseCrafter(value, out _, out _, out _) : !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                throw new ArgumentException("Unsupported crafter value.");
            return;
        }
        var closed = Values(dimension);
        if (closed != null && !closed.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unsupported facet value.");
    }
    public static bool Matches(ClassificationResult item, ClassificationFilterRule rule, IEnumerable<FacetEvidence>? currentInstanceFacets = null)
    {
        if (item == null || rule == null) throw new ArgumentNullException(nameof(item));
        return MatchesValidated(item, rule, InstanceFacets(item, currentInstanceFacets));
    }
    /// <summary>Owns one item's current instance evidence: instance dimensions only, one value each, rarity only on an instance-rarity item.</summary>
    internal static FacetEvidence[] InstanceFacets(ClassificationResult item, IEnumerable<FacetEvidence>? input)
    {
        var rows = ClassificationValidation.Bounded(input ?? Array.Empty<FacetEvidence>(), InstanceDimensions.Count);
        if (rows.Any(x => x == null || !IsInstanceDimension(item, x.Dimension)) ||
            rows.Select(x => x.Dimension).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new ArgumentException("Only one current value per instance dimension can supplement an item; rarity only an instance-rarity item.");
        return rows;
    }
    internal static bool MatchesValidated(ClassificationResult item, ClassificationFilterRule rule, FacetEvidence[] instance, bool setAsideInstance = false)
    {
        if (rule.Categories.Count > 0 && !item.Memberships.Any(x => rule.Categories.Contains(x.Category, StringComparer.Ordinal))) return false;
        foreach (var filter in rule.Filters)
        {
            if (filter.Value.Count == 0) continue;
            if (!IsKnown(filter.Key)) return false; // D077 B01: kept, matches nothing.
            if (setAsideInstance && IsInstanceDimension(item, filter.Key)) continue;
            var facts = IsInstanceDimension(item, filter.Key) ? instance : item.Facets;
            var observed = false; var matched = false;
            foreach (var fact in facts)
                if (fact.Dimension == filter.Key)
                {
                    observed = true;
                    if (filter.Key == "crafter" ? CrafterMatches(filter.Value, fact.Value) : filter.Value.Contains(fact.Value, StringComparer.Ordinal)) matched = true;
                }
            if (!observed ? !filter.Value.Contains("unknown", StringComparer.Ordinal) : !matched) return false;
        }
        return true;
    }

    /// <summary>
    /// Current condition of one item instance (P6-07b), shaped for the matcher. Upgraded: quality 2 or more. Damaged:
    /// durability below the instance's maximum. An item without durability gets no damaged row (Unknown), so a
    /// "damaged: no" filter never collects materials. The adapter reads the numbers from the live item.
    /// </summary>
    /// <param name="upgradable">A recipe can upgrade the item (P6-21); only then does the stack get an upgrade level, so a
    /// level filter never collects materials or eggs, whose quality a recipe does not set.</param>
    public static IReadOnlyList<FacetEvidence> Condition(int quality, bool usesDurability, float durability, float maximumDurability, bool upgradable = false)
    {
        if (quality < 1) throw new ArgumentOutOfRangeException(nameof(quality));
        var rows = new List<FacetEvidence> { new FacetEvidence("upgraded", quality >= 2 ? "yes" : "no", "instance", "Item quality."),
            new FacetEvidence("upgradable", upgradable ? "yes" : "no", "instance", "A recipe upgrades the item.") };
        if (upgradable) rows.Add(new FacetEvidence("quality", QualityValue(quality), "instance", "Upgrade level."));
        if (usesDurability && Finite(durability) && Finite(maximumDurability) && maximumDurability > 0)
            rows.Add(new FacetEvidence("damaged", durability < maximumDurability ? "yes" : "no", "instance", "Durability against its maximum."));
        return rows.AsReadOnly();
    }
    /// <summary>
    /// Who crafted one item instance (D064): the crafter's player ID, compared with the ID stored in the filter by the
    /// player who set it (Q12). A stack nobody crafted (crafter ID 0: materials, loot) has no row, so it is unknown.
    /// </summary>
    public static FacetEvidence? Crafter(long crafter) =>
        crafter == 0 ? null : new FacetEvidence("crafter", crafter.ToString(CultureInfo.InvariantCulture), "instance", "Crafter's player ID.");
    private static bool CrafterMatches(IReadOnlyList<string> values, string crafter)
    {
        if (crafter == Unreadable) return false;
        foreach (var value in values)
            if (TryParseCrafter(value, out var by, out var player, out _) &&
                by == (crafter == player.ToString(CultureInfo.InvariantCulture))) return true;
        return false;
    }
    /// <summary>
    /// Instance evidence for an item whose instance could not be read: every instance dimension that applies to it is
    /// <see cref="Unreadable"/>, so its instance filters reject it instead of matching "unknown".
    /// </summary>
    public static IReadOnlyList<FacetEvidence> UnreadableInstance(ClassificationResult item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        var rows = new List<FacetEvidence>(InstanceDimensions.Count);
        foreach (var dimension in InstanceDimensions)
            if (IsInstanceDimension(item, dimension)) rows.Add(UnreadableRow(dimension));
        return rows.AsReadOnly();
    }
    public static FacetEvidence UnreadableRow(string dimension) => new FacetEvidence(dimension, Unreadable, "instance", "Could not be read now.");
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
