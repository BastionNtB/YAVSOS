using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>One filter the rule editor offers, with the values that make sense for the chosen categories.</summary>
public sealed class FilterChoice
{
    public string Dimension { get; }
    /// <summary>Values in display order; "unknown" last when some item in scope has no value.</summary>
    public IReadOnlyList<string> Values { get; }
    /// <summary>False for a filter shown only because the rule already uses it: nothing in scope can have it.</summary>
    public bool Applies { get; }
    /// <summary>Saved values that nothing in scope can have; shown so the player can untick them.</summary>
    public IReadOnlyCollection<string> Stale { get; }
    internal FilterChoice(string dimension, IReadOnlyList<string> values, bool applies, IReadOnlyCollection<string> stale)
    { Dimension = dimension; Values = values; Applies = applies; Stale = stale; }
}

/// <summary>
/// P6-14 (D065): which filters, and which of their values, apply to the items a rule's categories cover, so "Wood"
/// offers no EpicLoot rarity and a filter from a mod that is not installed never shows. Saved filters are never
/// dropped: one that no longer applies stays, marked, until the player unticks it (user policy stays authoritative).
/// Read only; pure and Unity-free. Instance filters (rarity, upgraded, damaged, crafter) cannot be known from the
/// answers, so the caller says per item whether one can apply (from the game's own item data).
/// </summary>
public static class FilterChoices
{
    public const int MaximumOpenValues = 24;

    /// <summary>
    /// D098: filters whose "unknown" only means "the filter doesn't apply to this item" (not food; can't be damaged), which
    /// the categories already say. They offer their real values only; a saved "unknown" stays readable and is marked.
    /// </summary>
    public static IReadOnlyCollection<string> NoUnknown { get; } = Array.AsReadOnly(new[] { "food-stat", "damaged" });

    /// <param name="items">Every current answer; null before answers arrive, when every filter is offered in full.</param>
    /// <param name="categories">The rule's ticked categories; none means every item.</param>
    /// <param name="instanceApplies">Whether an instance filter can apply to an item, such as "damaged" to an item with durability.</param>
    /// <param name="saved">The rule's current filters.</param>
    /// <param name="crafter">The crafter values the editing player can set (Q12: "by"/"not-by" themselves); none hides the filter.</param>
    /// <param name="moot">Filters the world makes meaningless, such as "teleportable" when the world lets every item through
    /// portals; not offered, and a saved one is marked as not applying.</param>
    public static IReadOnlyList<FilterChoice> Compute(IEnumerable<ClassificationResult>? items, IReadOnlyCollection<string> categories,
        Func<ClassificationResult, string, bool> instanceApplies, IReadOnlyDictionary<string, IReadOnlyList<string>> saved,
        IReadOnlyList<string>? crafter = null, IReadOnlyCollection<string>? moot = null)
    {
        if (categories == null || instanceApplies == null || saved == null) throw new ArgumentNullException(nameof(categories));
        crafter ??= Array.Empty<string>();
        IReadOnlyList<string> Closed(string dimension) => dimension == "crafter" ? crafter : ClassificationFilters.Values(dimension) ?? Array.Empty<string>();
        var dimensions = RuleText.EditableFilters;
        var seen = dimensions.ToDictionary(x => x, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (items != null)
            foreach (var item in items)
            {
                if (item == null || categories.Count != 0 && !item.Memberships.Any(x => categories.Contains(x.Category))) continue;
                foreach (var dimension in dimensions)
                {
                    if (ClassificationFilters.IsInstanceDimension(item, dimension))
                    {
                        // P6-21: whether a recipe can upgrade it is known per item type, so both answers are real values.
                        if (dimension == "upgradable") { seen[dimension].Add(instanceApplies(item, dimension) ? "yes" : "no"); continue; }
                        // An upgrade level only for items a recipe upgrades; the rest are "Can be upgraded: no", not "unknown".
                        if (dimension == "quality" && !instanceApplies(item, dimension)) continue;
                        if (!instanceApplies(item, dimension)) { missing.Add(dimension); continue; }
                        foreach (var value in Closed(dimension)) seen[dimension].Add(value);
                        // Plain gear, found (uncrafted) items: an instance can lack the value even when the item can have it.
                        if (dimension is "rarity" or "crafter") missing.Add(dimension);
                        continue;
                    }
                    var any = false;
                    foreach (var fact in item.Facets)
                        if (fact.Dimension == dimension) { seen[dimension].Add(fact.Value); any = true; }
                    if (!any) missing.Add(dimension);
                }
            }

        var choices = new List<FilterChoice>(dimensions.Count);
        foreach (var dimension in dimensions)
        {
            saved.TryGetValue(dimension, out var ticked); ticked ??= Array.Empty<string>();
            var closed = dimension == "crafter" ? crafter : ClassificationFilters.Values(dimension);
            var found = seen[dimension];
            IEnumerable<string> values;
            var unknown = NoUnknown.Contains(dimension) ? Array.Empty<string>() : new[] { "unknown" };
            if (items == null) values = (closed ?? (IEnumerable<string>)Array.Empty<string>()).Concat(unknown);
            else
            {
                values = closed != null ? closed.Where(found.Contains)
                    : found.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).Take(MaximumOpenValues);
                if (found.Count != 0 && missing.Contains(dimension)) values = values.Concat(unknown);
            }
            var offered = values.ToList();
            // One value only (every Wood can go through portals) narrows nothing, so it is not offered (runtime 2026-10-21).
            var applies = (items == null || found.Count != 0 && offered.Count >= 2) && moot?.Contains(dimension) != true;
            // Another player's crafter choice is theirs, not stale: shown, never marked.
            var stale = ticked.Where(x => !offered.Contains(x, StringComparer.Ordinal) && !(applies && dimension == "crafter")).ToArray();
            if (dimension == "crafter") foreach (var value in ticked) if (!offered.Contains(value, StringComparer.Ordinal)) offered.Add(value);
            if (!applies && ticked.Count == 0) continue;
            // Keep every saved value visible, in the dimension's own order where it has one.
            foreach (var value in stale) offered.Add(value);
            if (closed != null) offered = offered.OrderBy(x => x == "unknown" ? int.MaxValue : Index(closed, x)).ToList();
            choices.Add(new FilterChoice(dimension, offered.AsReadOnly(), applies, Array.AsReadOnly(stale)));
        }
        return choices.AsReadOnly();
    }

    private static int Index(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++) if (values[i] == value) return i;
        return int.MaxValue - 1;
    }
}
