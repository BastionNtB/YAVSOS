using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Interface;

/// <summary>
/// S1 (audit D01): the rule being edited in the panel, as the editor shows it: one alternative of categories with its
/// filters, the Always and Never items, the priority, and what the editor keeps but can't show. Loading converts a
/// saved "upgraded" filter to upgrade levels (P6-21), so every way in gets it: the chest, and a pasted rule, which is
/// copied from a loaded state (<see cref="RuleCopy"/>, X2).
/// <see cref="Edit"/> is what a save writes. Not thread safe; the game's UI thread owns it.
/// </summary>
public sealed class RuleEditState
{
    public SortedSet<string> Categories { get; } = new SortedSet<string>(StringComparer.Ordinal);
    public SortedSet<string> Includes { get; } = new SortedSet<string>(StringComparer.Ordinal);
    public SortedSet<string> Excludes { get; } = new SortedSet<string>(StringComparer.Ordinal);
    /// <summary>Stored category IDs the current category set lacks; kept on save unless removed (D050).</summary>
    public SortedSet<string> Unknown { get; } = new SortedSet<string>(StringComparer.Ordinal);
    /// <summary>Filters of the edited alternative: values within a dimension are OR, dimensions AND (D048).</summary>
    public SortedDictionary<string, SortedSet<string>> Filters { get; } = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    public int Priority { get; set; }
    /// <summary>Stored parts of the rule the editor can't show or edit (D086 D5); kept on save, removed by Clear rule.</summary>
    public int HiddenParts { get; private set; }
    /// <summary>Whether the chest has a rule stored, as last loaded.</summary>
    public bool Stored { get; private set; }
    private IReadOnlyDictionary<string, IReadOnlyList<string>> editedFilters = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>No rule: everything empty.</summary>
    public void Clear()
    {
        Categories.Clear(); Includes.Clear(); Excludes.Clear(); Unknown.Clear(); Filters.Clear(); Priority = 0; HiddenParts = 0;
        editedFilters = new Dictionary<string, IReadOnlyList<string>>(); Stored = false;
    }

    /// <summary>Shows a stored rule: its editable alternative with filters (P6-07b), the rest kept as hidden parts.</summary>
    public void Load(ChestRules rules)
    {
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        Clear();
        Stored = true;
        var view = ChestRuleEdit.From(rules, withFilters: true);
        Categories.UnionWith(view.Categories); Includes.UnionWith(view.Includes); Excludes.UnionWith(view.Excludes);
        foreach (var pair in view.Filters) Filters.Add(pair.Key, new SortedSet<string>(pair.Value, StringComparer.Ordinal));
        // P6-21: a saved "upgraded" (yes/no) shows, and saves on the next edit, as upgrade levels (yes: 2 and up, no: 1).
        if (Filters.TryGetValue("upgraded", out var upgraded))
        {
            Filters.Remove("upgraded");
            if (!Filters.TryGetValue("quality", out var levels)) Filters.Add("quality", levels = new SortedSet<string>(StringComparer.Ordinal));
            levels.UnionWith(ClassificationFilters.LevelsFor(upgraded));
            if (levels.Count == 0) Filters.Remove("quality");
        }
        editedFilters = view.EditedFilters;
        Priority = view.Priority ?? 0; HiddenParts = view.HiddenParts;
        Unknown.UnionWith(view.UnknownCategories);
    }

    /// <summary>
    /// X2 (D116): a pasted rule replaces what the editor shows: categories, Always and Never, filters and priority. The
    /// save then replaces the alternative this chest's editor shows; parts it can't show stay, as for any edit (D086 D5).
    /// A copied category this category set no longer has is kept as unknown (D050), so the save isn't refused for it.
    /// </summary>
    public void Paste(RuleCopy copy, ClassificationTaxonomy taxonomy)
    {
        if (copy == null) throw new ArgumentNullException(nameof(copy));
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        Categories.Clear(); Includes.Clear(); Excludes.Clear(); Unknown.Clear(); Filters.Clear();
        foreach (var id in copy.Categories) (taxonomy.Categories.ContainsKey(id) ? Categories : Unknown).Add(id);
        Unknown.UnionWith(copy.Unknown.Where(x => !Categories.Contains(x)));
        Includes.UnionWith(copy.Includes); Excludes.UnionWith(copy.Excludes);
        foreach (var pair in copy.Filters) Filters.Add(pair.Key, new SortedSet<string>(pair.Value, StringComparer.Ordinal));
        Priority = copy.Priority;
    }

    public void ToggleFilter(string dimension, string value)
    {
        if (!Filters.TryGetValue(dimension, out var values)) Filters.Add(dimension, values = new SortedSet<string>(StringComparer.Ordinal));
        if (!values.Remove(value)) values.Add(value);
        if (values.Count == 0) Filters.Remove(dimension);
    }

    public void RemoveFilter(string dimension) => Filters.Remove(dimension);

    public void RemoveFilterValue(string dimension, string value)
    {
        if (Filters.TryGetValue(dimension, out var values) && values.Remove(value) && values.Count == 0) Filters.Remove(dimension);
    }

    public void RemoveUnknown(string id) => Unknown.Remove(id);

    public void ToggleCategory(ClassificationTaxonomy taxonomy, string id)
    {
        var next = CategorySelection.Toggle(taxonomy, Categories, id);
        Categories.Clear(); Categories.UnionWith(next);
    }

    /// <summary>Always (<paramref name="always"/>) or Never for an item: on if it wasn't in that list, moving it out of the other; off if it was.</summary>
    public void Choose(string prefab, bool always)
    {
        var list = always ? Includes : Excludes; var other = always ? Excludes : Includes;
        if (!list.Remove(prefab)) { list.Add(prefab); other.Remove(prefab); }
    }

    /// <summary>Filters of the edited alternative this version doesn't know (D077 B01); kept on save unless removed.</summary>
    public IReadOnlyList<string> UnknownFilters => Filters.Keys.Where(x => !ClassificationFilters.IsKnown(x)).ToArray();

    /// <summary>Values of known filters this version doesn't know (D086 D6): kept on save, match nothing, until removed.</summary>
    public IReadOnlyList<(string Dimension, string Value)> UnknownFilterValues => Filters
        .Where(x => ClassificationFilters.IsKnown(x.Key)).SelectMany(x => x.Value.Where(v => !ClassificationFilters.IsKnownRuleValue(x.Key, v)).Select(v => (x.Key, v)))
        .ToArray();

    /// <summary>
    /// What a save writes (D051: each change at once). An edit that leaves no part of the rule removes it; parts the
    /// editor can't show stay (D086 D5), so the edit, not the panel, decides that.
    /// </summary>
    public ChestRuleEdit Edit() => new ChestRuleEdit(Categories, Includes, Excludes, Unknown, Priority,
        Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray(),
        editedFilters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray());
}
