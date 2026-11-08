using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

public enum CategorySelectionState { Off, On, Implied, Partial }

public sealed class CategoryTreeRow
{
    public string Id { get; }
    public string Name { get; }
    public int Depth { get; }
    public CategorySelectionState State { get; }
    public bool HasChildren { get; }
    public bool Expanded { get; }
    /// <summary>
    /// D163: a folding heading over grouped categories (<see cref="CategoryGroup"/>), not a category: no tick, never
    /// stored, <see cref="State"/> always Off. <see cref="Id"/> is the group's.
    /// </summary>
    public bool Heading { get; }
    internal CategoryTreeRow(string id, string name, int depth, CategorySelectionState state, bool hasChildren, bool expanded, bool heading = false)
    { Id = id; Name = name; Depth = depth; State = state; HasChildren = hasChildren; Expanded = expanded; Heading = heading; }
}

/// <summary>
/// Rule picker logic for the category tree (P4-00c, D051/D054). A rule stores the highest ticked node, so a ticked
/// umbrella also accepts subcategories added later. Pure and Unity-free; the interface only draws the rows.
/// </summary>
public static class CategorySelection
{
    /// <summary>On when stored; Implied when an ancestor is stored; Partial when a descendant is stored.</summary>
    public static CategorySelectionState StateOf(ClassificationTaxonomy taxonomy, IReadOnlyCollection<string> stored, string id)
    {
        if (taxonomy == null || stored == null || id == null) throw new ArgumentNullException(nameof(taxonomy));
        if (stored.Contains(id)) return CategorySelectionState.On;
        for (var at = taxonomy.ParentOf(id); at != null; at = taxonomy.ParentOf(at))
            if (stored.Contains(at)) return CategorySelectionState.Implied;
        return stored.Any(x => x != id && taxonomy.IsAncestorOrSelf(id, x)) ? CategorySelectionState.Partial : CategorySelectionState.Off;
    }

    /// <summary>
    /// The stored set after the player clicks <paramref name="id"/>. Ticking replaces stored descendants with the
    /// node and collapses a parent whose children are all ticked; unticking an implied node stores its stored
    /// ancestor's other descendants instead. IDs the taxonomy does not know are kept unchanged (D050).
    /// </summary>
    public static SortedSet<string> Toggle(ClassificationTaxonomy taxonomy, IEnumerable<string> stored, string id)
    {
        if (taxonomy == null || stored == null || id == null) throw new ArgumentNullException(nameof(taxonomy));
        taxonomy.Require(id);
        var result = Normalize(taxonomy, stored);
        switch (StateOf(taxonomy, result, id))
        {
            case CategorySelectionState.On:
                result.Remove(id);
                break;
            case CategorySelectionState.Implied:
                var path = new List<string> { id };
                for (var at = taxonomy.ParentOf(id); !result.Contains(path[path.Count - 1]); at = taxonomy.ParentOf(at!)) path.Add(at!);
                result.Remove(path[path.Count - 1]);
                // Walk down from the stored ancestor, keeping every sibling off the path to the unticked node.
                for (var i = path.Count - 1; i > 0; i--)
                    foreach (var child in taxonomy.ChildrenOf(path[i]))
                        if (child != path[i - 1]) result.Add(child);
                break;
            default:
                result.RemoveWhere(x => taxonomy.Categories.ContainsKey(x) && taxonomy.IsAncestorOrSelf(id, x));
                result.Add(id);
                for (var parent = taxonomy.ParentOf(id); parent != null && taxonomy.ChildrenOf(parent).All(result.Contains); parent = taxonomy.ParentOf(parent))
                {
                    foreach (var child in taxonomy.ChildrenOf(parent)) result.Remove(child);
                    result.Add(parent);
                }
                break;
        }
        return result;
    }

    /// <summary>Drops stored IDs already covered by a stored ancestor; unknown IDs are kept.</summary>
    public static SortedSet<string> Normalize(ClassificationTaxonomy taxonomy, IEnumerable<string> stored)
    {
        var all = new SortedSet<string>(stored, StringComparer.Ordinal);
        all.RemoveWhere(x => taxonomy.Categories.ContainsKey(x) && Ancestors(taxonomy, x).Any(all.Contains));
        return all;
    }

    /// <summary>
    /// Visible rows, A–Z within each level and Misc last. A category is shown when it has current members or the rule names it or one of
    /// its descendants (D054); an umbrella's rows follow it only while it is expanded. Partial nodes expand by default.
    /// </summary>
    /// <param name="names">The names the player sees (S13); rows sort by them and show them.</param>
    /// <param name="groups">D163: top-level categories folded under a heading row (the stores' categories, one heading per
    /// mod). A heading sorts among the top-level rows by its text, shows how many of its categories are shown, folds like
    /// an umbrella and opens by default while one of them is ticked; with none of them shown it is left out.</param>
    public static IReadOnlyList<CategoryTreeRow> Rows(CategoryNames names, IReadOnlyCollection<string> stored,
        Func<string, bool> hasMembers, ICollection<string> expanded, ICollection<string> collapsed, CategoryGrouping? groups = null)
    {
        if (names == null || stored == null || hasMembers == null || expanded == null || collapsed == null) throw new ArgumentNullException(nameof(names));
        groups ??= CategoryGrouping.None;
        var taxonomy = names.Taxonomy;
        var rows = new List<CategoryTreeRow>();
        bool Visible(string id) => hasMembers(id) || stored.Any(x => taxonomy.Categories.ContainsKey(x) && taxonomy.IsAncestorOrSelf(id, x));
        // A–Z within each level, Misc last (maintainer, 2026-10-21): DisplayRank picks an item's primary category, so
        // by rank the tree read as random ("Fishing bait" near the top, "Fish" in the middle). Classification keeps it.
        IEnumerable<string> Ordered(IEnumerable<string> ids) => ids.Where(Visible).OrderBy(x => x == "misc" ? 1 : 0)
            .ThenBy(names.Shown, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x, StringComparer.Ordinal);
        void Add(string id, int depth, string? name = null)
        {
            var state = StateOf(taxonomy, stored, id);
            var children = taxonomy.ChildrenOf(id).Where(Visible).ToArray();
            var open = children.Length > 0 && !collapsed.Contains(id) && (expanded.Contains(id) || state == CategorySelectionState.Partial);
            rows.Add(new CategoryTreeRow(id, name ?? names.Shown(id), depth, state, children.Length > 0, open));
            if (open) foreach (var child in Ordered(children)) Add(child, depth + 1);
        }
        // Top-level entries: each ungrouped root, and one heading per group with a shown category.
        var top = new List<(string Id, string Name, CategoryGroup? Group, string[] Members)>();
        var grouped = new Dictionary<CategoryGroup, List<string>>();
        foreach (var root in taxonomy.Categories.Keys.Where(x => taxonomy.ParentOf(x) == null && Visible(x)))
        {
            if (groups.Of(root) is not { } group) { top.Add((root, names.Shown(root), null, Array.Empty<string>())); continue; }
            if (!grouped.TryGetValue(group, out var members)) grouped[group] = members = new List<string>();
            members.Add(root);
        }
        foreach (var (group, members) in grouped)
            top.Add((group.Id, names.Format(group.Heading(members.Count)), group, members.ToArray()));
        foreach (var entry in top.OrderBy(x => x.Id == "misc" ? 1 : 0).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (entry.Group == null) { Add(entry.Id, 0, entry.Name); continue; }
            var open = !collapsed.Contains(entry.Id) && (expanded.Contains(entry.Id) || entry.Members.Any(x => StateOf(taxonomy, stored, x) != CategorySelectionState.Off));
            rows.Add(new CategoryTreeRow(entry.Id, entry.Name, 0, CategorySelectionState.Off, true, open, heading: true));
            if (!open) continue;
            foreach (var (id, name) in entry.Members.Select(x => (Id: x, Name: groups.UnderHeading(x, taxonomy.Categories[x].Name, names.Shown(x))))
                         .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal))
                Add(id, 1, name);
        }
        return rows;
    }

    /// <summary>Members per category over the current answers, closure included; used for visibility and size ties (D054).</summary>
    public static Dictionary<string, int> MemberCounts(IEnumerable<ClassificationResult> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
            foreach (var category in item.Memberships.Select(x => x.Category).Distinct(StringComparer.Ordinal))
                counts[category] = counts.TryGetValue(category, out var count) ? count + 1 : 1;
        return counts;
    }

    private static IEnumerable<string> Ancestors(ClassificationTaxonomy taxonomy, string id)
    {
        for (var at = taxonomy.ParentOf(id); at != null; at = taxonomy.ParentOf(at)) yield return at;
    }
}
