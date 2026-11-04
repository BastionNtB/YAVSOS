using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// Short texts about a chest rule for the interface (P6-06/07/07b): the automatic chest label (D062 Q3), filter names
/// and values, the filters in a few words, the item tooltip's category line, and whether a rule accepts a searched
/// category. P10-04: everything a player reads is a message (inventory S9); stored filter tokens ("magic", "yes")
/// stay as they are in rules, and only what the panel shows has a key (case 6). Read only; pure and Unity-free.
/// </summary>
/// <remarks>
/// A shown value is a string (a name already in the player's language: a tame, a mod, a player, a raw token this
/// version can't name) or a <see cref="Message"/>, as <see cref="MessageList"/> items are.
/// </remarks>
public static class RuleText
{
    public const int MaximumLabel = 56;

    /// <summary>
    /// The filters the rule editor offers (D062 Q6), in display order. Biome is parked with its add-on. "upgraded" (yes/no)
    /// was replaced by "upgradable" and "quality" in the filter review (P6-21); the editor shows a saved one as levels.
    /// </summary>
    public static IReadOnlyList<string> EditableFilters { get; } = Array.AsReadOnly(new[]
        { "mod", "rarity", "upgradable", "quality", "damaged", "crafter", "usage", "food-stat", "eaten-by", "teleportable" });

    /// <summary>The value meaning "the base game" for the origin filter (P6-21): positive evidence, not missing evidence.</summary>
    public const string Vanilla = "vanilla";

    /// <summary>A filter's name on the Filters tab ("EpicLoot rarity"); null for a dimension the editor never names.</summary>
    public static Message? FilterName(string dimension)
    {
        string? key = dimension switch
        {
            "rarity" => "yavsos_filter_name_rarity", "upgraded" => "yavsos_filter_name_upgraded", "damaged" => "yavsos_filter_name_damaged",
            "crafter" => "yavsos_filter_name_crafter", "usage" => "yavsos_filter_name_usage", "food-stat" => "yavsos_filter_name_food_stat",
            "eaten-by" => "yavsos_filter_name_eaten_by", "teleportable" => "yavsos_filter_name_teleportable", "mod" => "yavsos_filter_name_mod",
            "upgradable" => "yavsos_filter_name_upgradable", "quality" => "yavsos_filter_name_quality", _ => null
        };
        return key == null ? null : Message.Of(key);
    }

    /// <summary>
    /// A value as a filter tick box shows it: a closed token's display name (case 6), "plain" for no rarity, "level 2"
    /// for an upgrade level, the crafter's name. A string for a name shown as is, or a token this version has no name for.
    /// </summary>
    public static object ValueName(string dimension, string value)
    {
        if (value == "unknown")
            return Message.Of(dimension switch
            {
                "rarity" => "yavsos_filter_value_plain", "mod" => "yavsos_filter_value_origin_unknown", "crafter" => "yavsos_filter_value_not_crafted",
                "eaten-by" => "yavsos_filter_value_no_tame", _ => "yavsos_filter_value_unknown"
            });
        if (dimension == "mod" && value == Vanilla) return Message.Of("yavsos_filter_value_base_game");
        if (dimension == "quality") return Message.Of("yavsos_filter_value_level").With("level", value);
        if (dimension == "crafter" && ClassificationFilters.TryParseCrafter(value, out var by, out _, out var name)) return CrafterName(by, name);
        return ClosedValue(dimension, value) is { } key ? Message.Of(key) : value;
    }

    // The display key of a closed token (case 6); null for a token this version doesn't have.
    private static string? ClosedValue(string dimension, string value) => (dimension, value) switch
    {
        ("rarity", "magic") => "yavsos_filter_value_rarity_magic", ("rarity", "rare") => "yavsos_filter_value_rarity_rare",
        ("rarity", "epic") => "yavsos_filter_value_rarity_epic", ("rarity", "legendary") => "yavsos_filter_value_rarity_legendary",
        ("rarity", "mythic") => "yavsos_filter_value_rarity_mythic", ("rarity", "ancient") => "yavsos_filter_value_rarity_ancient",
        ("usage", "crafting") => "yavsos_filter_value_usage_crafting", ("usage", "building") => "yavsos_filter_value_usage_building",
        ("usage", "fuel") => "yavsos_filter_value_usage_fuel", ("usage", "processing") => "yavsos_filter_value_usage_processing",
        ("usage", "food") => "yavsos_filter_value_usage_food", ("usage", "brewing") => "yavsos_filter_value_usage_brewing",
        ("usage", "farming") => "yavsos_filter_value_usage_farming", ("usage", "feed") => "yavsos_filter_value_usage_feed",
        ("usage", "equipment") => "yavsos_filter_value_usage_equipment",
        ("food-stat", "health") => "yavsos_filter_value_food_health", ("food-stat", "stamina") => "yavsos_filter_value_food_stamina",
        ("food-stat", "eitr") => "yavsos_filter_value_food_eitr", ("food-stat", "balanced") => "yavsos_filter_value_food_balanced",
        ("teleportable" or "upgraded" or "damaged" or "upgradable", "yes") => "yavsos_filter_value_yes",
        ("teleportable" or "upgraded" or "damaged" or "upgradable", "no") => "yavsos_filter_value_no",
        _ => null
    };

    /// <summary>
    /// A crafter filter's value (Q12: the player who set the filter): their name, or "anyone but Bastion". <paramref name="me"/>
    /// names the local player "me"; an empty name is "the player who set it".
    /// </summary>
    public static object CrafterName(bool by, string? who, bool me = false)
    {
        object person = me ? Message.Of("yavsos_filter_value_crafter_me")
            : string.IsNullOrEmpty(who) ? Message.Of("yavsos_filter_value_crafter_setter") : who!;
        return by ? person : With(Message.Of("yavsos_filter_value_crafter_not_by"), "who", person);
    }

    private static int Order(string dimension) { var at = EditableFilters.ToList().IndexOf(dimension); return at < 0 ? int.MaxValue : at; }

    /// <summary>
    /// The automatic chest label from its rule, such as "Food › eaten by Boar · priority 2 (Urgent)" (D062 Q3, Q8):
    /// categories in display order, else the exact items, then the filters in short words and a priority other than the
    /// default (D069), as the parts of one line. As many names as fit in <see cref="MaximumLabel"/> characters, then
    /// "+2" for the rest, measured with <paramref name="measure"/> (the formatted text in the player's language); the
    /// caller cuts what is still too long (<see cref="Cut"/>). Null for a chest without a configured rule.
    /// </summary>
    /// <param name="openName">Display names for open filter values (mod IDs, creature tokens, crafter choices).</param>
    /// <param name="names">The category names the player sees (S13).</param>
    public static MessageList? Label(ChestRules? rules, CategoryNames names, Func<string, string> itemName, Func<MessageList, string> measure,
        Func<string, string, string>? openName = null)
    {
        if (names == null || itemName == null || measure == null) throw new ArgumentNullException(nameof(names));
        var taxonomy = names.Taxonomy;
        if (rules == null || rules.State != ChestRuleState.Configured) return null;
        var ticked = Categories(rules).Where(taxonomy.Categories.ContainsKey).ToArray();
        ticked = CategorySelection.Normalize(taxonomy, ticked).OrderBy(x => taxonomy.Categories[x].DisplayRank)
            .ThenBy(names.Shown, StringComparer.CurrentCulture).ToArray();
        var shown = ticked.Length != 0 ? ticked.Select(names.Shown).ToArray()
            : rules.Includes.Select(itemName).ToArray();
        object? filters = null;
        var filtered = rules.Acceptance.Rules.Where(x => x.Filters.Values.Any(v => v.Count != 0)).ToArray();
        if (filtered.Length != 0 && Short(filtered[0].Filters, openName) is { } first)
            filters = filtered.Length > 1 ? Message.Of("yavsos_label_filters_more").With("filters", first).Count(filtered.Length - 1) : first;
        var priority = ChestPriority.Phrase(rules.Priority);
        MessageList Build(object head)
        {
            var parts = new List<object> { filters == null ? head : With(With(Message.Of("yavsos_label_filtered"), "names", head), "filters", filters) };
            if (priority != null) parts.Add(priority);
            return MessageList.Of(ListKind.Parts, parts);
        }
        if (shown.Length == 0) return Build(Message.Of(rules.Acceptance.Rules.Count == 0 ? "yavsos_label_nothing" : "yavsos_label_anything"));
        return Fit(shown, Build, measure, MaximumLabel);
    }

    /// <summary>
    /// As many <paramref name="names"/> as fit before the rest of the line, then "+n" for the others, so the filters and
    /// facts are never cut for a long list: "Stamina foods, Food ingredients +3 · eaten by Boar". The first name is
    /// always shown; the caller cuts the formatted result if it is still too long.
    /// </summary>
    public static MessageList Fit(IReadOnlyList<string> names, Func<object, MessageList> line, Func<MessageList, string> measure, int maximum)
    {
        if (names == null || names.Count == 0 || line == null || measure == null) throw new ArgumentException("Names, a line and a measure are required.");
        object Head(int shown) => shown >= names.Count ? MessageList.Of(ListKind.Comma, names)
            : Message.Of("yavsos_label_more").With("names", MessageList.Of(ListKind.Comma, names.Take(shown))).Count(names.Count - shown);
        var all = line(Head(names.Count));
        if (measure(all).Length <= maximum) return all;
        var fits = 1;
        while (fits + 1 < names.Count && measure(line(Head(fits + 1))).Length <= maximum) fits++;
        return line(Head(fits));
    }

    /// <summary>
    /// A rule's filters in a few words, one phrase per filter: "eaten by Boar", "magic or rare, not teleportable"; null
    /// without filters. Word order and conjunctions are the messages' (inventory case 5).
    /// </summary>
    public static MessageList? Short(IReadOnlyDictionary<string, IReadOnlyList<string>> filters, Func<string, string, string>? openName = null)
    {
        if (filters == null) throw new ArgumentNullException(nameof(filters));
        var parts = filters.Where(x => x.Value.Count != 0).OrderBy(x => Order(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Short(x.Key, x.Value, openName)).ToList();
        return parts.Count == 0 ? null : MessageList.Of(ListKind.Comma, parts);
    }

    private static object Short(string dimension, IReadOnlyList<string> values, Func<string, string, string>? openName)
    {
        var closed = ClassificationFilters.Values(dimension);
        object Name(string v) => openName != null && closed == null ? openName(dimension, v) : ValueName(dimension, v);
        int Rank(string v) { var at = closed == null ? -1 : closed.ToList().IndexOf(v); return at < 0 ? int.MaxValue : at; }
        var known = values.Where(x => x != "unknown").OrderBy(Rank).ToArray();
        var parts = new List<object>();
        if (known.Length != 0)
        {
            object Each(string v) => (dimension, v) switch
            {
                ("upgraded", "yes") => Message.Of("yavsos_label_upgraded"), ("upgraded", "no") => Message.Of("yavsos_label_not_upgraded"),
                ("upgradable", "yes") => Message.Of("yavsos_label_upgradable"), ("upgradable", "no") => Message.Of("yavsos_label_not_upgradable"),
                ("mod", Vanilla) => Message.Of("yavsos_label_base_game"),
                ("damaged", "yes") => Message.Of("yavsos_label_damaged"), ("damaged", "no") => Message.Of("yavsos_label_undamaged"),
                ("teleportable", "yes") => Message.Of("yavsos_label_teleportable"), ("teleportable", "no") => Message.Of("yavsos_label_not_teleportable"),
                _ => dimension == "quality" ? v : Name(v)
            };
            // A rarity and every one above it reads as "rare or better" (runtime 2026-10-21: six names did not fit a label).
            var better = dimension == "rarity" && known.Length >= 2 && closed != null && Rank(known[0]) + known.Length == closed.Count &&
                known.Select(Rank).SequenceEqual(Enumerable.Range(Rank(known[0]), known.Length));
            var each = known.Select(Each).ToList();
            object joined = better ? With(Message.Of("yavsos_label_rarity_better"), "rarity", each[0]) : each.Count == 1 ? each[0] : MessageList.Of(ListKind.Or, each);
            string? phrase = dimension switch
            {
                "food-stat" => "yavsos_label_food", "eaten-by" => "yavsos_label_eaten_by", "usage" => "yavsos_label_used_for",
                "mod" => "yavsos_label_from", "crafter" => "yavsos_label_crafted_by", "quality" => "yavsos_label_upgrade_level", _ => null
            };
            parts.Add(phrase == null ? joined : With(Message.Of(phrase), "values", joined));
        }
        if (values.Contains("unknown"))
            parts.Add(dimension switch
            {
                "crafter" => Message.Of("yavsos_label_not_crafted"), "food-stat" => Message.Of("yavsos_label_no_food_stat"),
                "eaten-by" => Message.Of("yavsos_label_no_tame"), "usage" => Message.Of("yavsos_label_unused"),
                "mod" => Message.Of("yavsos_filter_value_origin_unknown"), "rarity" => Message.Of("yavsos_filter_value_plain"),
                "upgraded" => Message.Of("yavsos_label_upgraded_unknown"), "upgradable" => Message.Of("yavsos_label_upgradable_unknown"),
                "quality" => Message.Of("yavsos_label_level_unknown"), "damaged" => Message.Of("yavsos_label_condition_unknown"),
                "teleportable" => Message.Of("yavsos_label_portals_unknown"),
                // Case 4: no lower-cased name; a dimension the editor doesn't name shows its ID.
                _ => Message.Of("yavsos_label_unknown").With("filter", dimension)
            });
        return parts.Count == 1 ? parts[0] : MessageList.Of(ListKind.Or, parts);
    }

    /// <summary>A shown value (a string, a <see cref="Message"/> or a <see cref="MessageList"/>) as a message value.</summary>
    internal static Message With(Message message, string name, object value) => value switch
    {
        Message inner => message.With(name, inner),
        MessageList list => message.With(name, list),
        _ => message.With(name, value as string ?? value?.ToString() ?? "")
    };

    public const int MaximumItemLine = 80;

    /// <summary>
    /// The line under an item's tooltip (Q8): its smallest categories in display order, then facts worth knowing, the
    /// tames that eat it and "not teleportable", as the parts of one line: "Food ingredients, Animal feed · eaten by Boar, Hen".
    /// Umbrellas over a listed category are left out. Null when the item has no category. As many categories as fit in
    /// <see cref="MaximumItemLine"/> characters (<see cref="Fit"/>); the caller cuts what is still too long.
    /// </summary>
    /// <param name="portals">False when the world lets every item through portals, so "not teleportable" is left out (as the game does).</param>
    /// <param name="names">The category names the player sees (S13).</param>
    public static MessageList? ItemLine(ClassificationResult? item, CategoryNames names, Func<MessageList, string> measure,
        Func<string, string, string>? openName = null, bool portals = true)
    {
        if (names == null || measure == null) throw new ArgumentNullException(nameof(names));
        var taxonomy = names.Taxonomy;
        if (item == null) return null;
        var ids = item.Memberships.Select(x => x.Category).Where(taxonomy.Categories.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
        var smallest = ids.Where(id => !ids.Any(other => other != id && taxonomy.IsAncestorOrSelf(id, other)))
            .OrderBy(x => taxonomy.Categories[x].DisplayRank).ThenBy(names.Shown, StringComparer.CurrentCulture)
            .Select(names.Shown).ToArray();
        if (smallest.Length == 0) return null;
        var facts = new List<object>();
        var tames = item.Facets.Where(x => x.Dimension == "eaten-by").Select(x => openName?.Invoke("eaten-by", x.Value) ?? x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCulture).OrderBy(x => x, StringComparer.CurrentCulture).ToArray();
        // The label's phrases, as the facts say the same thing (one key per phrase); every tame eats it, so no "or".
        if (tames.Length != 0) facts.Add(Message.Of("yavsos_label_eaten_by").With("values", MessageList.Of(ListKind.Comma, tames)));
        if (portals && item.Facets.Any(x => x.Dimension == "teleportable" && x.Value == "no")) facts.Add(Message.Of("yavsos_label_not_teleportable"));
        return Fit(smallest, head => MessageList.Of(ListKind.Parts, new[] { head }.Concat(facts)), measure, MaximumItemLine);
    }

    /// <summary>Formatted text cut to <paramref name="maximum"/> characters with "…", never inside a surrogate pair (case 9).</summary>
    public static string Cut(string text, int maximum) => ResultSummary.Bound(text, maximum);

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
