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
    internal CategoryTreeRow(string id, string name, int depth, CategorySelectionState state, bool hasChildren, bool expanded)
    { Id = id; Name = name; Depth = depth; State = state; HasChildren = hasChildren; Expanded = expanded; }
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
    public static IReadOnlyList<CategoryTreeRow> Rows(ClassificationTaxonomy taxonomy, IReadOnlyCollection<string> stored,
        Func<string, bool> hasMembers, ICollection<string> expanded, ICollection<string> collapsed)
    {
        if (taxonomy == null || stored == null || hasMembers == null || expanded == null || collapsed == null) throw new ArgumentNullException(nameof(taxonomy));
        var rows = new List<CategoryTreeRow>();
        bool Visible(string id) => hasMembers(id) || stored.Any(x => taxonomy.Categories.ContainsKey(x) && taxonomy.IsAncestorOrSelf(id, x));
        // A–Z within each level, Misc last (maintainer, 2026-10-21): DisplayRank picks an item's primary category, so
        // by rank the tree read as random ("Fishing bait" near the top, "Fish" in the middle). Classification keeps it.
        IEnumerable<string> Ordered(IEnumerable<string> ids) => ids.Where(Visible).OrderBy(x => x == "misc" ? 1 : 0)
            .ThenBy(x => taxonomy.Categories[x].Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x, StringComparer.Ordinal);
        void Add(string id, int depth)
        {
            var state = StateOf(taxonomy, stored, id);
            var children = taxonomy.ChildrenOf(id).Where(Visible).ToArray();
            var open = children.Length > 0 && !collapsed.Contains(id) && (expanded.Contains(id) || state == CategorySelectionState.Partial);
            rows.Add(new CategoryTreeRow(id, taxonomy.Categories[id].Name, depth, state, children.Length > 0, open));
            if (open) foreach (var child in Ordered(children)) Add(child, depth + 1);
        }
        foreach (var root in Ordered(taxonomy.Categories.Keys.Where(x => taxonomy.ParentOf(x) == null))) Add(root, 0);
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
