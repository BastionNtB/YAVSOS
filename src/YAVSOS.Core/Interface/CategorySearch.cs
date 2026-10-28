using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>One item a search can find: its prefab, shown name and current categories (closure included).</summary>
public sealed class SearchableItem
{
    public string Prefab { get; }
    public string Name { get; }
    public IReadOnlyList<string> Categories { get; }
    public SearchableItem(string prefab, string name, IEnumerable<string> categories)
    {
        Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        Name = string.IsNullOrWhiteSpace(name) ? prefab : name;
        Categories = Array.AsReadOnly((categories ?? throw new ArgumentNullException(nameof(categories))).Distinct(StringComparer.Ordinal).ToArray());
    }
}

/// <summary>An item a search found and the smallest category that holds it.</summary>
public sealed class ItemSearchHit
{
    public string Prefab { get; }
    public string Name { get; }
    public string Category { get; }
    internal ItemSearchHit(string prefab, string name, string category) { Prefab = prefab; Name = name; Category = category; }
}

public sealed class CategorySearchResult
{
    public static readonly CategorySearchResult None = new CategorySearchResult("", Array.Empty<string>(), Array.Empty<ItemSearchHit>());
    public string Query { get; }
    /// <summary>Categories whose names match, in display order.</summary>
    public IReadOnlyList<string> Categories { get; }
    /// <summary>Items whose names match, best first, each with its smallest category.</summary>
    public IReadOnlyList<ItemSearchHit> Items { get; }
    /// <summary>Every category the tree marks: name matches and the items' smallest categories.</summary>
    public IReadOnlyCollection<string> Hits { get; }
    public bool Active => Query.Length != 0;

    /// <summary>
    /// D101 (C9): the found items whose smallest category is <paramref name="category"/>, best first, for the faint note
    /// in that category's tree row. Items never become rows of their own.
    /// </summary>
    public IReadOnlyList<string> Names(string category) =>
        Items.Where(x => StringComparer.Ordinal.Equals(x.Category, category)).Select(x => x.Name).Distinct(StringComparer.CurrentCulture).ToArray();

    internal CategorySearchResult(string query, IReadOnlyList<string> categories, IReadOnlyList<ItemSearchHit> items)
    {
        Query = query; Categories = categories; Items = items;
        Hits = new HashSet<string>(categories.Concat(items.Select(x => x.Category)), StringComparer.Ordinal);
    }
}

/// <summary>
/// P6-06 (D062 Q4): search in the category picker. A query matches category names and item names (or prefab IDs);
/// an item leads to its smallest category, so "Copper" marks the ores subcategory rather than the umbrella. Bounded
/// and read only; it never changes a rule. Pure and Unity-free.
/// </summary>
public static class CategorySearch
{
    public const int MinimumQuery = 2, MaximumQuery = 64, MaximumItems = 8, MaximumCategories = 24;

    public static CategorySearchResult Find(ClassificationTaxonomy taxonomy, string? query, IEnumerable<SearchableItem> items, Func<string, int> memberCount)
    {
        if (taxonomy == null || items == null || memberCount == null) throw new ArgumentNullException(nameof(taxonomy));
        var text = (query ?? "").Trim();
        if (text.Length > MaximumQuery) text = text.Substring(0, MaximumQuery);
        if (text.Length < MinimumQuery) return CategorySearchResult.None;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        int At(string value) => compare.IndexOf(value, text, options);
        var categories = taxonomy.Categories.Values.Where(x => At(x.Name) >= 0 && memberCount(x.Id) > 0)
            .OrderBy(x => At(x.Name) == 0 ? 0 : 1).ThenBy(x => x.DisplayRank).ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .ThenBy(x => x.Id, StringComparer.Ordinal).Take(MaximumCategories).Select(x => x.Id).ToArray();
        var found = new List<(SearchableItem Item, int Rank)>();
        foreach (var item in items)
        {
            if (item == null) continue;
            var at = At(item.Name);
            var rank = at == 0 ? 0 : at > 0 ? 1 : At(item.Prefab) >= 0 ? 2 : -1;
            if (rank >= 0) found.Add((item, rank));
        }
        var hits = new List<ItemSearchHit>();
        foreach (var (item, _) in found.OrderBy(x => x.Rank).ThenBy(x => x.Item.Name.Length).ThenBy(x => x.Item.Name, StringComparer.CurrentCulture)
                     .ThenBy(x => x.Item.Prefab, StringComparer.Ordinal))
        {
            var smallest = Smallest(taxonomy, item.Categories, memberCount);
            if (smallest == null) continue;
            hits.Add(new ItemSearchHit(item.Prefab, item.Name, smallest));
            if (hits.Count == MaximumItems) break;
        }
        return new CategorySearchResult(text, Array.AsReadOnly(categories), hits.AsReadOnly());
    }

    /// <summary>Most names a tree row's note shows before "…" (D101 Q17); the row's tooltip lists them all.</summary>
    public const int NoteNames = 2;

    /// <summary>
    /// A tree row's note naming <paramref name="shown"/> of <paramref name="names"/> (at most <see cref="NoteNames"/>),
    /// then "…" for the rest: the panel tries fewer until the note fits the row on one line. Empty without names.
    /// </summary>
    public static string Note(IReadOnlyList<string> names, int shown)
    {
        if (names == null) throw new ArgumentNullException(nameof(names));
        if (names.Count == 0) return "";
        shown = Math.Max(0, Math.Min(Math.Min(shown, NoteNames), names.Count));
        return string.Join(", ", names.Take(shown)) + (names.Count > shown ? (shown == 0 ? "…" : ", …") : "");
    }

    /// <summary>
    /// D101 (Q16, bible B3.20): the items the highlight asks about for a category row: its members, those the search
    /// found first (<paramref name="first"/>), at most <paramref name="maximum"/>, so a chest whose Never list or filters
    /// keep the category's items out stays dark. A category of more members is judged by its first ones (as D100 Q13).
    /// </summary>
    public static IReadOnlyList<string> Members(IEnumerable<SearchableItem> items, string category, IEnumerable<string> first, int maximum)
    {
        if (items == null || category == null || first == null) throw new ArgumentNullException(nameof(items));
        var members = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = items.Where(x => x != null && x.Categories.Contains(category, StringComparer.Ordinal)).ToList();
        var inCategory = new HashSet<string>(all.Select(x => x.Prefab), StringComparer.Ordinal);
        foreach (var prefab in first.Where(inCategory.Contains).Concat(all.OrderBy(x => x.Prefab, StringComparer.Ordinal).Select(x => x.Prefab)))
        {
            if (members.Count >= maximum) break;
            if (seen.Add(prefab)) members.Add(prefab);
        }
        return members;
    }

    /// <summary>The known category with the fewest members; ties go to the deeper one, then by ID. Null when none is known.</summary>
    public static string? Smallest(ClassificationTaxonomy taxonomy, IEnumerable<string> categories, Func<string, int> memberCount) =>
        categories.Where(taxonomy.Categories.ContainsKey)
            .OrderBy(x => { var count = memberCount(x); return count > 0 ? count : int.MaxValue; })
            .ThenByDescending(taxonomy.DepthOf).ThenBy(x => x, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The picker's rows while a search is active: the marked categories, their ancestors (opened to reveal them) and
    /// what lies under a marked category. Without a search, the usual rows.
    /// </summary>
    public static IReadOnlyList<CategoryTreeRow> Rows(ClassificationTaxonomy taxonomy, IReadOnlyCollection<string> stored, Func<string, bool> hasMembers,
        ICollection<string> expanded, ICollection<string> collapsed, CategorySearchResult search)
    {
        if (search == null) throw new ArgumentNullException(nameof(search));
        if (!search.Active) return CategorySelection.Rows(taxonomy, stored, hasMembers, expanded, collapsed);
        var path = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in search.Hits)
            for (var at = taxonomy.ParentOf(hit); at != null; at = taxonomy.ParentOf(at)) path.Add(at);
        var open = new HashSet<string>(expanded.Concat(path), StringComparer.Ordinal);
        var closed = new HashSet<string>(collapsed.Where(x => !path.Contains(x)), StringComparer.Ordinal);
        bool Shown(string id)
        {
            if (path.Contains(id)) return true;
            for (string? at = id; at != null; at = taxonomy.ParentOf(at)) if (search.Hits.Contains(at)) return true;
            return false;
        }
        return CategorySelection.Rows(taxonomy, stored, x => hasMembers(x) || path.Contains(x) || search.Hits.Contains(x), open, closed)
            .Where(x => Shown(x.Id)).ToArray();
    }
}
