using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>Where an icon sits in the item grid (P6-18), in display order.</summary>
public enum GridSection
{
    /// <summary>In this chest or the player's inventory, whatever the rule says.</summary>
    Here,
    /// <summary>Always or Never items the categories do not take.</summary>
    Marked,
    /// <summary>Everything else the rule's categories and filters take.</summary>
    Rule,
    /// <summary>Search hits from the rest of the game's items.</summary>
    Found
}

public enum GridMark { None, Always, Never }

public sealed class GridEntry
{
    public string Prefab { get; }
    public string Name { get; }
    public GridSection Section { get; }
    public GridMark Mark { get; }
    /// <summary>The rule takes the item (Never items and items it does not take are shown dimmed).</summary>
    public bool Taken { get; }
    /// <summary>
    /// Every prefab this icon stands for, <see cref="Prefab"/> first: look-alike copies (same shown name and icon, such
    /// as the game's Fallen Warrior and Shadow Person NPC capes) are one icon, and a mark applies to all of them.
    /// </summary>
    public IReadOnlyList<string> Copies { get; }
    internal GridEntry(string prefab, string name, GridSection section, GridMark mark, bool taken, IReadOnlyList<string>? copies = null)
    { Prefab = prefab; Name = name; Section = section; Mark = mark; Taken = taken; Copies = copies ?? new[] { prefab }; }
}

/// <summary>One catalog item for the grid: identity, the name shown, and its current categories and facts (null: unknown).</summary>
public sealed class GridSource
{
    public string Prefab { get; }
    public string Name { get; }
    public ClassificationResult? Item { get; }
    /// <summary>A player-facing item (it has an icon); creature attacks and other hidden entries show only when here or marked.</summary>
    public bool Listed { get; }
    /// <summary>The icon's identity, or null: items with the same shown name and look are merged into one icon.</summary>
    public string? Look { get; }
    /// <summary>Shown for its look-alikes when none is here: an item a recipe makes (the player's own, not an NPC's copy).</summary>
    public bool Preferred { get; }
    public GridSource(string prefab, string name, ClassificationResult? item, bool listed = true, string? look = null, bool preferred = false)
    {
        Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab)); Name = string.IsNullOrEmpty(name) ? prefab : name; Item = item; Listed = listed;
        Look = look; Preferred = preferred;
    }
}

/// <summary>
/// P6-18 (maintainer, 2026-10-21 and 2026-10-23): the item grid of the rule panel's Items tab. Every item the rule's
/// categories and definition filters take, with items in this chest or the player's inventory first, then Always/Never
/// items the categories do not take, then the rest; a search covers the whole catalog. A click cycles an icon's mark:
/// none → Always → Never → none. Filters read from each stack (rarity, upgraded, damaged, crafter) cannot be shown per
/// icon: an item counts when it can have the asked fact at all; the rule's "only …" chip names them (D101).
/// Pure; built once per rule change, search or answers generation, never per frame.
/// </summary>
public static class ItemGrid
{
    public const int MaximumItems = 8192;

    public static IReadOnlyList<GridEntry> Build(IEnumerable<GridSource> catalog, IReadOnlyCollection<string> categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters, IReadOnlyCollection<string> includes, IReadOnlyCollection<string> excludes,
        IReadOnlyCollection<string> here, string? query = null, Func<ClassificationResult, string, bool>? canHave = null)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        var always = new HashSet<string>(includes ?? Array.Empty<string>(), StringComparer.Ordinal);
        var never = new HashSet<string>(excludes ?? Array.Empty<string>(), StringComparer.Ordinal);
        var present = new HashSet<string>(here ?? Array.Empty<string>(), StringComparer.Ordinal);
        var text = (query ?? "").Trim();
        var rows = new List<GridEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var looks = new Dictionary<string, GridSource>(StringComparer.Ordinal);
        foreach (var source in ClassificationValidation.Bounded(catalog, MaximumItems))
        {
            if (source == null || !seen.Add(source.Prefab)) continue;
            looks[source.Prefab] = source;
            var byCategory = source.Item != null && Takes(source.Item, categories, filters, canHave);
            var mark = always.Contains(source.Prefab) ? GridMark.Always : never.Contains(source.Prefab) ? GridMark.Never : GridMark.None;
            var section = present.Contains(source.Prefab) ? GridSection.Here
                : mark != GridMark.None && !byCategory ? GridSection.Marked
                : byCategory ? GridSection.Rule : GridSection.Found;
            if (text.Length != 0 ? !Matches(source, text) : section == GridSection.Found) continue;
            if (!source.Listed && section is GridSection.Rule or GridSection.Found) continue;
            rows.Add(new GridEntry(source.Prefab, source.Name, section, mark, mark == GridMark.Always || mark == GridMark.None && byCategory));
        }
        // Prefabs a mark or the chest names that the catalog no longer has (a removed mod) still show, so they can be cleared.
        foreach (var prefab in always.Concat(never).Concat(present))
        {
            if (!seen.Add(prefab) || text.Length != 0 && prefab.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var mark = always.Contains(prefab) ? GridMark.Always : never.Contains(prefab) ? GridMark.Never : GridMark.None;
            rows.Add(new GridEntry(prefab, prefab, present.Contains(prefab) ? GridSection.Here : GridSection.Marked, mark, mark == GridMark.Always));
        }
        return Merge(rows, looks).OrderBy(x => x.Section).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Prefab, StringComparer.Ordinal).ToArray();
    }

    // Look-alikes (same shown name and icon) become one entry: the earliest section and any mark or take among them; the
    // copy shown is the one here, else one a recipe makes, else the shortest prefab name. Rows without a look stay apart.
    private static IEnumerable<GridEntry> Merge(List<GridEntry> rows, Dictionary<string, GridSource> sources)
    {
        string? Key(GridEntry row) => sources.TryGetValue(row.Prefab, out var source) && source.Look != null ? row.Name + "\n" + source.Look : null;
        foreach (var group in rows.GroupBy(x => Key(x) ?? "\0" + x.Prefab, StringComparer.Ordinal))
        {
            var copies = group.OrderBy(x => x.Section == GridSection.Here ? 0 : 1)
                .ThenBy(x => sources.TryGetValue(x.Prefab, out var source) && source.Preferred ? 0 : 1)
                .ThenBy(x => x.Prefab.Length).ThenBy(x => x.Prefab, StringComparer.Ordinal).ToList();
            var first = copies[0];
            if (copies.Count == 1) { yield return first; continue; }
            var marks = copies.Select(x => x.Mark).Distinct().ToList();
            var mark = marks.Count == 1 ? marks[0] : marks.Contains(GridMark.Always) ? GridMark.Always : GridMark.Never;
            yield return new GridEntry(first.Prefab, first.Name, copies.Min(x => x.Section), mark, copies.Any(x => x.Taken), copies.Select(x => x.Prefab).ToArray());
        }
    }

    /// <summary>
    /// Whether the rule's categories and filters take the item as a kind of item. No category and no filter takes
    /// nothing; filters alone take every item that passes them. A filter read from each stack passes when the item can
    /// have that fact (<paramref name="canHave"/>; without it, when the classifier says the dimension is per instance), and
    /// an item that can't passes only when "unknown" is ticked, as sorting does (D110, F11).
    /// </summary>
    public static bool Takes(ClassificationResult item, IReadOnlyCollection<string> categories, IReadOnlyDictionary<string, IReadOnlyList<string>> filters,
        Func<ClassificationResult, string, bool>? canHave = null)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        var active = (filters ?? new Dictionary<string, IReadOnlyList<string>>()).Where(x => x.Value.Count != 0).ToList();
        if ((categories?.Count ?? 0) == 0 && active.Count == 0) return false;
        if (categories!.Count != 0 && !item.Memberships.Any(x => categories.Contains(x.Category))) return false;
        foreach (var filter in active)
        {
            if (filter.Key == "upgradable")
            {
                // Known per item type (P6-21): the item passes when its answer is a ticked value.
                if (!filter.Value.Contains(canHave?.Invoke(item, filter.Key) == true ? "yes" : "no", StringComparer.Ordinal)) return false;
                continue;
            }
            if (ClassificationFilters.InstanceDimensions.Contains(filter.Key, StringComparer.Ordinal))
            {
                // D110 (F11): an item that can have the fact passes (some stack may match); one that can't passes only when
                // "unknown" is ticked, as sorting does ("damaged = no" never shows Wood; "not crafted" shows it).
                if (!(canHave?.Invoke(item, filter.Key) ?? ClassificationFilters.IsInstanceDimension(item, filter.Key)) &&
                    !filter.Value.Contains("unknown", StringComparer.Ordinal)) return false;
                continue;
            }
            var observed = false; var matched = false;
            foreach (var fact in item.Facets)
                if (fact.Dimension == filter.Key) { observed = true; matched |= filter.Value.Contains(fact.Value, StringComparer.Ordinal); }
            if (!observed ? !filter.Value.Contains("unknown", StringComparer.Ordinal) : !matched) return false;
        }
        return true;
    }

    /// <summary>The mark a click gives: none → Always → Never → none.</summary>
    public static GridMark Next(GridMark mark) => mark switch { GridMark.None => GridMark.Always, GridMark.Always => GridMark.Never, _ => GridMark.None };

    /// <summary>What a mark means, for the icon's tooltip.</summary>
    public static string MarkText(GridMark mark, bool byCategory) => mark switch
    {
        GridMark.Always => byCategory
            ? "Always here: ignores this rule's filters and wins over chests that take it only by category, at the same priority."
            : "Always here, although no ticked category takes it.",
        GridMark.Never => "Never here, whatever the categories say.",
        _ => byCategory ? "Taken by this rule's categories." : "Not taken by this rule."
    } + " Click: Always → Never → clear.";

    private static bool Matches(GridSource source, string text) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(source.Name, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
        source.Prefab.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
}
