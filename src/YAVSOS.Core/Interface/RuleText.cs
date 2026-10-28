using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>
/// Short texts about a chest rule for the interface (P6-06/07/07b): the automatic hover label (D062 Q3), filter names
/// and summaries, and whether a rule takes a searched category. Read only; pure and Unity-free.
/// </summary>
public static class RuleText
{
    public const int MaximumLabel = 56;
    public const string Separator = " · ";

    /// <summary>
    /// The filters the rule editor offers (D062 Q6), in display order. Biome is parked with its add-on. "upgraded" (yes/no)
    /// was replaced by "upgradable" and "quality" in the filter review (P6-21); the editor shows a saved one as levels.
    /// </summary>
    public static IReadOnlyList<string> EditableFilters { get; } = Array.AsReadOnly(new[]
        { "mod", "rarity", "upgradable", "quality", "damaged", "crafter", "usage", "food-stat", "eaten-by", "teleportable" });

    /// <summary>The value meaning "the base game" for the origin filter (P6-21): positive evidence, not missing evidence.</summary>
    public const string Vanilla = "vanilla";

    public static string FilterName(string dimension) => dimension switch
    {
        "rarity" => "EpicLoot rarity", "upgraded" => "Upgraded (quality 2+)", "damaged" => "Damaged", "crafter" => "Crafted by", "usage" => "Used for",
        "food-stat" => "Food's main stat", "eaten-by" => "Eaten by (tame)", "teleportable" => "Can go through portals", "mod" => "Item origin",
        "upgradable" => "Can be upgraded", "quality" => "Upgrade level", _ => dimension
    };

    /// <summary>"unknown" matches items without that evidence: for rarity, plain (non-magic) items.</summary>
    public static string ValueName(string dimension, string value) => value == "unknown"
        ? dimension == "rarity" ? "plain" : dimension == "mod" ? "origin not known" : dimension == "crafter" ? "nobody (not crafted)" : dimension == "eaten-by" ? "no tame eats it" : "unknown"
        : dimension == "mod" && value == Vanilla ? "Base game"
        : dimension == "quality" ? "level " + value
        : dimension == "crafter" && ClassificationFilters.TryParseCrafter(value, out var by, out _, out var name) ? CrafterName(by, name.Length != 0 ? name : "the player who set it")
        : value;
    /// <summary>"Bastion" / "anyone but Bastion" (Q12: the player who set the filter).</summary>
    public static string CrafterName(bool by, string who) => by ? who : "anyone but " + who;

    /// <summary>"EpicLoot rarity: magic or rare; Used for: fuel", or "" without filters.</summary>
    /// <param name="openName">Display names for open values (a mod ID, a creature's name token), by dimension and value.</param>
    public static string Filters(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, Func<string, string, string>? openName = null)
    {
        if (filters == null) throw new ArgumentNullException(nameof(filters));
        var parts = filters.Where(x => x.Value.Count != 0).OrderBy(x => Order(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => FilterName(x.Key) + ": " + Or(x.Value.Select(v => v != "unknown" && openName != null && ClassificationFilters.Values(x.Key) == null ? openName(x.Key, v) : ValueName(x.Key, v))));
        return string.Join("; ", parts);
    }

    private static int Order(string dimension) { var at = EditableFilters.ToList().IndexOf(dimension); return at < 0 ? int.MaxValue : at; }

    private static string Or(IEnumerable<string> values)
    {
        var rows = values.ToArray();
        return rows.Length <= 1 ? string.Join("", rows) : string.Join(", ", rows.Take(rows.Length - 1)) + " or " + rows[rows.Length - 1];
    }

    public const string FilterMark = " › ";

    /// <summary>
    /// The automatic chest label from its rule, such as "Food › eaten by Boar · priority 2 (Urgent)" (D062 Q3, Q8): categories in
    /// display order (then "+2"), else the exact items, then the filters in short words and a priority other than the default (D069). Null
    /// for a chest without a configured rule. At most <see cref="MaximumLabel"/> characters.
    /// </summary>
    /// <param name="openName">Display names for open filter values (mod IDs, creature tokens, crafter choices).</param>
    public static string? Label(ChestRules? rules, ClassificationTaxonomy taxonomy, Func<string, string> itemName,
        Func<string, string, string>? openName = null)
    {
        if (taxonomy == null || itemName == null) throw new ArgumentNullException(nameof(taxonomy));
        if (rules == null || rules.State != ChestRuleState.Configured) return null;
        var categories = Categories(rules).Where(taxonomy.Categories.ContainsKey).ToArray();
        categories = CategorySelection.Normalize(taxonomy, categories).OrderBy(x => taxonomy.Categories[x].DisplayRank)
            .ThenBy(x => taxonomy.Categories[x].Name, StringComparer.CurrentCulture).ToArray();
        var names = categories.Length != 0 ? categories.Select(x => taxonomy.Categories[x].Name).ToArray()
            : rules.Includes.Select(itemName).ToArray();
        var tail = "";
        var filtered = rules.Acceptance.Rules.Where(x => x.Filters.Values.Any(v => v.Count != 0)).ToArray();
        if (filtered.Length != 0)
            tail += FilterMark + Short(filtered[0].Filters, openName) + (filtered.Length > 1 ? " +" + (filtered.Length - 1) : "");
        if (rules.Priority != 0) tail += Separator + ChestPriority.Short(rules.Priority);
        if (names.Length == 0) return Bound((rules.Acceptance.Rules.Count == 0 ? "accepts nothing" : "any item") + tail);
        return Bound(Fit(names, tail, MaximumLabel));
    }

    /// <summary>
    /// As many names as fit before the tail, then "+n" for the rest, so the tail (filters, facts) is never cut for a long
    /// list: "Stamina foods, Food ingredients +3 · eaten by Boar". The first name is always shown; the caller bounds the result.
    /// </summary>
    public static string Fit(IReadOnlyList<string> names, string tail, int maximum)
    {
        if (names == null || names.Count == 0 || tail == null) throw new ArgumentException("Names and a tail are required.");
        var head = names[0]; var shown = 1;
        while (shown < names.Count && head.Length + 2 + names[shown].Length + tail.Length + 4 <= maximum) head += ", " + names[shown++];
        if (shown < names.Count) head += " +" + (names.Count - shown);
        return head + tail;
    }

    /// <summary>A rule's filters in a few words: "eaten by Boar", "magic or rare, not teleportable".</summary>
    public static string Short(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, Func<string, string, string>? openName = null)
    {
        if (filters == null) throw new ArgumentNullException(nameof(filters));
        return string.Join(", ", filters.Where(x => x.Value.Count != 0).OrderBy(x => Order(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Short(x.Key, x.Value, openName)));
    }

    private static string Short(string dimension, IReadOnlyList<string> values, Func<string, string, string>? openName)
    {
        string Name(string v) => openName != null && ClassificationFilters.Values(dimension) == null ? openName(dimension, v) : ValueName(dimension, v);
        var closed = ClassificationFilters.Values(dimension);
        int Rank(string v) { var at = closed == null ? -1 : closed.ToList().IndexOf(v); return at < 0 ? int.MaxValue : at; }
        var known = values.Where(x => x != "unknown").OrderBy(Rank).ToArray();
        var parts = new List<string>();
        if (known.Length != 0)
        {
            string Each(string v) => (dimension, v) switch
            {
                ("upgraded", "yes") => "upgraded", ("upgraded", "no") => "not upgraded",
                ("upgradable", "yes") => "upgradable", ("upgradable", "no") => "not upgradable",
                ("mod", Vanilla) => "the base game",
                ("damaged", "yes") => "damaged", ("damaged", "no") => "undamaged",
                ("teleportable", "yes") => "teleportable", ("teleportable", "no") => "not teleportable",
                _ => dimension is "food-stat" or "usage" or "quality" ? v : Name(v)
            };
            // A rarity and every one above it reads as "rare or better" (runtime 2026-10-21: six names did not fit a label).
            var joined = dimension == "rarity" && known.Length >= 2 && closed != null && Rank(known[0]) + known.Length == closed.Count &&
                known.Select(Rank).SequenceEqual(Enumerable.Range(Rank(known[0]), known.Length))
                ? Each(known[0]) + " or better" : Or(known.Select(Each));
            parts.Add(dimension switch
            {
                "food-stat" => joined + " food", "eaten-by" => "eaten by " + joined, "usage" => "used for " + joined,
                "mod" => known.Length == 1 && known[0] == Vanilla ? "from the base game" : "from " + joined, "crafter" => "crafted by " + joined,
                "quality" => "upgrade level " + joined, _ => joined
            });
        }
        if (values.Contains("unknown"))
            parts.Add(dimension switch
            {
                "crafter" => "not crafted", "food-stat" => "no food stat", "eaten-by" => "eaten by no tame", "usage" => "unused",
                "mod" => "origin not known", "rarity" => "plain", _ => "unknown " + FilterName(dimension).ToLowerInvariant()
            });
        return string.Join(" or ", parts);
    }

    public const int MaximumItemLine = 80;

    /// <summary>
    /// The line under an item's tooltip (Q8): its smallest categories in display order, then facts worth knowing:
    /// the tames that eat it and "not teleportable", such as "Raw food, Animal feed · eaten by Boar, Hen". Umbrellas
    /// over a listed category are left out. Empty when the item has no category. At most <see cref="MaximumItemLine"/>.
    /// </summary>
    /// <param name="portals">False when the world lets every item through portals, so "not teleportable" is left out (as the game does).</param>
    public static string ItemLine(ClassificationResult? item, ClassificationTaxonomy taxonomy, Func<string, string, string>? openName = null,
        bool portals = true)
    {
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        if (item == null) return "";
        var ids = item.Memberships.Select(x => x.Category).Where(taxonomy.Categories.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        var smallest = ids.Where(id => !ids.Any(other => other != id && taxonomy.IsAncestorOrSelf(id, other)))
            .OrderBy(x => taxonomy.Categories[x].DisplayRank).ThenBy(x => taxonomy.Categories[x].Name, StringComparer.CurrentCulture)
            .Select(x => taxonomy.Categories[x].Name).ToArray();
        if (smallest.Length == 0) return "";
        var facts = new List<string>();
        var tames = item.Facets.Where(x => x.Dimension == "eaten-by").Select(x => openName?.Invoke("eaten-by", x.Value) ?? x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCulture).OrderBy(x => x, StringComparer.CurrentCulture).ToArray();
        if (tames.Length != 0) facts.Add("eaten by " + string.Join(", ", tames));
        if (portals && item.Facets.Any(x => x.Dimension == "teleportable" && x.Value == "no")) facts.Add("not teleportable");
        var text = Fit(smallest, facts.Count == 0 ? "" : Separator + string.Join(Separator, facts), MaximumItemLine);
        return text.Length <= MaximumItemLine ? text : text.Substring(0, MaximumItemLine - 1) + "…";
    }

    private static string Bound(string text) => text.Length <= MaximumLabel ? text : text.Substring(0, MaximumLabel - 1) + "…";

    /// <summary>Categories named by the rule's alternatives, preserved ones included.</summary>
    public static IEnumerable<string> Categories(ChestRules rules) =>
        rules.FullRules.SelectMany(x => x.Categories).Concat(rules.Preserved.SelectMany(x => x.Effective?.Categories ?? (IEnumerable<string>)Array.Empty<string>()))
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Whether a configured rule takes part of <paramref name="category"/> (P6-06 highlight): it names the category,
    /// an umbrella over it, or a subcategory of it. Filters are not applied; the highlight shows where it may go.
    /// </summary>
    public static bool TakesCategory(ChestRules? rules, ClassificationTaxonomy taxonomy, string category)
    {
        if (taxonomy == null || category == null) throw new ArgumentNullException(nameof(taxonomy));
        if (rules == null || rules.State != ChestRuleState.Configured) return false;
        foreach (var id in Categories(rules))
            if (taxonomy.IsAncestorOrSelf(id, category) || taxonomy.IsAncestorOrSelf(category, id)) return true;
        return false;
    }
}
