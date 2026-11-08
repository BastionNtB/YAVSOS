using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Templates;

/// <summary>
/// X23 (D157): the parts of a template that don't exist here, found by ID: a category the current category set lacks,
/// an Always or Never item that isn't loaded, a filter this YAVSOS version doesn't know or a value it can't check here.
/// A template with missing parts still applies: every part is set, and the missing ones stay stored but unused, as a
/// chest rule keeps an unknown category (D050). Renaming a category's display name changes nothing. Immutable.
/// </summary>
public sealed class TemplateMissing
{
    public static readonly TemplateMissing None = new TemplateMissing(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<(string, string?)>());

    public IReadOnlyList<string> Categories { get; }
    /// <summary>Always and Never items, by prefab name.</summary>
    public IReadOnlyList<string> Items { get; }
    /// <summary>A whole filter (value null) this version doesn't know, or one value of a filter it can't check here.</summary>
    public IReadOnlyList<(string Dimension, string? Value)> Filters { get; }
    /// <summary>How many parts are missing: each category, item, unknown filter and value counts once.</summary>
    public int Count => Categories.Count + Items.Count + Filters.Count;

    private TemplateMissing(IReadOnlyList<string> categories, IReadOnlyList<string> items, IReadOnlyList<(string, string?)> filters)
    { Categories = categories; Items = items; Filters = filters; }

    /// <param name="hasCategory">The current category set has this ID.</param>
    /// <param name="hasItem">The game has this item loaded. Null while the item list isn't known yet: no item counts as missing.</param>
    /// <param name="canCheck">For a known filter's known value: whether this game can check it (EpicLoot's rarity with EpicLoot
    /// absent, a mod that isn't loaded). Null: every known value can be checked.</param>
    public static TemplateMissing Find(RuleTemplate template, Func<string, bool> hasCategory, Func<string, bool>? hasItem, Func<string, string, bool>? canCheck = null)
    {
        if (template == null) throw new ArgumentNullException(nameof(template));
        if (hasCategory == null) throw new ArgumentNullException(nameof(hasCategory));
        var categories = template.Categories.Where(x => !hasCategory(x)).ToArray();
        var items = hasItem == null ? Array.Empty<string>()
            : template.Includes.Concat(template.Excludes).Where(x => !hasItem(x)).Distinct(StringComparer.Ordinal).ToArray();
        var filters = new List<(string, string?)>();
        foreach (var filter in template.Filters)
        {
            if (!ClassificationFilters.IsKnown(filter.Key)) { filters.Add((filter.Key, null)); continue; }
            foreach (var value in filter.Value)
                if (!ClassificationFilters.IsKnownRuleValue(filter.Key, value) || value != "unknown" && canCheck != null && !canCheck(filter.Key, value))
                    filters.Add((filter.Key, value));
        }
        return categories.Length + items.Length + filters.Count == 0 ? None : new TemplateMissing(categories, items, filters.ToArray());
    }
}
