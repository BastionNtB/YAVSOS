using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;

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

/// <summary>X59 (D149): the buttons on a grid section's header. Each click is one rule edit, so undo puts it back.</summary>
public enum GridButton { NeverAll, Clear }

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
    /// <summary>
    /// X59 (D149): in <see cref="GridSection.Rule"/>, the ticked category whose section the icon sits in, the first of
    /// them in the panel's order that the item belongs to; null in the other sections, or when the rule takes it only by
    /// its filters.
    /// </summary>
    public string? Group { get; }
    internal GridEntry(string prefab, string name, GridSection section, GridMark mark, bool taken, IReadOnlyList<string>? copies = null, string? group = null)
    { Prefab = prefab; Name = name; Section = section; Mark = mark; Taken = taken; Copies = copies ?? new[] { prefab }; Group = group; }
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
    /// <summary>#105 (D171): the catalog's item limit (the assignments the server sends carry at most this many), so every loaded item can show.</summary>
    public const int MaximumItems = AuthoritativeAssignmentsCodec.MaximumItems;

    public static IReadOnlyList<GridEntry> Build(IEnumerable<GridSource> catalog, IReadOnlyCollection<string> categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters, IReadOnlyCollection<string> includes, IReadOnlyCollection<string> excludes,
        IReadOnlyCollection<string> here, string? query = null, Func<ClassificationResult, string, bool>? canHave = null,
        IReadOnlyList<string>? groups = null)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        // X59 (D149): with groups (the ticked categories in the panel's order), "Accepted by this rule" is one section per
        // category; an item in several shows under the first. Null or empty: one section, as before.
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups ?? Array.Empty<string>()) if (group != null && !order.ContainsKey(group)) order[group] = order.Count;
        string? GroupOf(ClassificationResult item)
        {
            string? best = null; var rank = int.MaxValue;
            foreach (var membership in item.Memberships)
                if (order.TryGetValue(membership.Category, out var at) && at < rank) { rank = at; best = membership.Category; }
            return best;
        }
        int Rank(GridEntry row) => row.Group != null && order.TryGetValue(row.Group, out var at) ? at : int.MaxValue;
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
            rows.Add(new GridEntry(source.Prefab, source.Name, section, mark, mark == GridMark.Always || mark == GridMark.None && byCategory, null,
                section == GridSection.Rule && order.Count != 0 ? GroupOf(source.Item!) : null));
        }
        // Prefabs a mark or the chest names that the catalog no longer has (a removed mod) still show, so they can be cleared.
        foreach (var prefab in always.Concat(never).Concat(present))
        {
            if (!seen.Add(prefab) || text.Length != 0 && prefab.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var mark = always.Contains(prefab) ? GridMark.Always : never.Contains(prefab) ? GridMark.Never : GridMark.None;
            rows.Add(new GridEntry(prefab, prefab, present.Contains(prefab) ? GridSection.Here : GridSection.Marked, mark, mark == GridMark.Always));
        }
        return Merge(rows, looks, Rank).OrderBy(x => x.Section).ThenBy(Rank).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Prefab, StringComparer.Ordinal).ToArray();
    }

    // Look-alikes (same shown name and icon) become one entry: the earliest section and any mark or take among them; the
    // copy shown is the one here, else one a recipe makes, else the shortest prefab name. Rows without a look stay apart.
    // The merged icon sits in the earliest section, and there in the earliest group (X59).
    private static IEnumerable<GridEntry> Merge(List<GridEntry> rows, Dictionary<string, GridSource> sources, Func<GridEntry, int> rank)
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
            var section = copies.Min(x => x.Section);
            var category = copies.Where(x => x.Section == section).OrderBy(rank).First().Group;
            yield return new GridEntry(first.Prefab, first.Name, section, mark, copies.Any(x => x.Taken), copies.Select(x => x.Prefab).ToArray(), category);
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

    /// <summary>
    /// The mark a right-click gives, the other way round: none → Never → Always → none (X61, D143). Each click steps
    /// from the mark the icon has now, so a player can switch buttons at any point of the cycle.
    /// </summary>
    public static GridMark Previous(GridMark mark) => mark switch { GridMark.None => GridMark.Never, GridMark.Never => GridMark.Always, _ => GridMark.None };

    /// <summary>The mark a click on an icon gives: <see cref="Next"/>, or <see cref="Previous"/> for a right-click.</summary>
    public static GridMark Step(GridMark mark, bool backwards) => backwards ? Previous(mark) : Next(mark);

    /// <summary>
    /// What a mark means, for the icon's tooltip (P10-04: a message; the click hint is <see cref="ClickHint"/>, its own
    /// sentence). The mark names come from their own keys, so the sentences can't drift from them.
    /// </summary>
    public static Message MarkText(GridMark mark, bool byCategory) => (mark switch
    {
        GridMark.Always => Message.Of(byCategory ? "yavsos_grid_mark_always" : "yavsos_grid_mark_always_only"),
        GridMark.Never => Message.Of("yavsos_grid_mark_never"),
        _ => Message.Of(byCategory ? "yavsos_grid_mark_accepted" : "yavsos_grid_mark_not_accepted")
    }).With("always", Always).With("never", Never);

    /// <summary>"Click: Always → Never → clear." under every icon's mark sentence.</summary>
    public static Message ClickHint => Message.Of("yavsos_grid_mark_click").With("always", Always).With("never", Never);

    /// <summary>"Right-click: Never → Always → clear." under <see cref="ClickHint"/> (X61).</summary>
    public static Message RightClickHint => Message.Of("yavsos_grid_mark_right_click").With("always", Always).With("never", Never);

    /// <summary>The item marks' names, as the panel shows them; other messages quote them through these.</summary>
    public static Message Always => Message.Of("yavsos_grid_always");
    public static Message Never => Message.Of("yavsos_grid_never");

    /// <summary>
    /// A section's title with how many icons it has: "Accepted by this rule (12)", or for one ticked category's section
    /// (X59) the category's name as the panel shows it: "Metals (12)".
    /// </summary>
    public static Message Title(GridSection section, string? category, int count) =>
        section == GridSection.Rule && category != null ? Message.Of("yavsos_grid_category").With("category", category).Count(count) : Title(section, count);

    /// <summary>
    /// X59 (D149): the buttons a section's header has. "Always or Never, outside the categories" has only Clear, since
    /// Never all there would mark items the categories don't take anyway. No section has Always all.
    /// </summary>
    public static IReadOnlyList<GridButton> Buttons(GridSection section) =>
        section == GridSection.Marked ? new[] { GridButton.Clear } : new[] { GridButton.NeverAll, GridButton.Clear };

    /// <summary>
    /// X59 (D149): what a section button does to the rule's item lists, for the icons the section shows (with a search,
    /// only the hits): Never all gives each of them Never, Clear takes their Always and Never marks off. Applied to every
    /// look-alike copy an icon stands for. Returns how many icons change mark; 0 leaves the lists as they were.
    /// </summary>
    public static int MarkSection(IEnumerable<GridEntry> section, GridButton button, ISet<string> includes, ISet<string> excludes)
    {
        if (section == null) throw new ArgumentNullException(nameof(section));
        if (includes == null) throw new ArgumentNullException(nameof(includes));
        if (excludes == null) throw new ArgumentNullException(nameof(excludes));
        var target = Target(button);
        var changing = section.Where(x => x != null && x.Mark != target).ToList();
        // D158: Never all stops above the limit; the caller greys the button out and says why, and this refuses as well.
        if (button == GridButton.NeverAll && changing.Count > MaximumNeverAll) return 0;
        foreach (var entry in changing)
            foreach (var prefab in entry.Copies)
            {
                includes.Remove(prefab); excludes.Remove(prefab);
                if (target == GridMark.Never) excludes.Add(prefab);
            }
        return changing.Count;
    }

    /// <summary>
    /// D158: the most icons one Never all click marks, in either section view. Above it the button is greyed out, and its
    /// tooltip says to search or untick the category. Marks added one at a time aren't limited; the rule's 64 KiB limit
    /// still refuses an edit that doesn't fit. 512 marks are about 22 KiB of rule.
    /// </summary>
    public const int MaximumNeverAll = 512;

    private static GridMark Target(GridButton button) => button == GridButton.NeverAll ? GridMark.Never : GridMark.None;

    /// <summary>D158: how many of a section's icons a button would change: those not already at its mark.</summary>
    public static int ToMark(IEnumerable<GridEntry> section, GridButton button)
    {
        if (section == null) throw new ArgumentNullException(nameof(section));
        var target = Target(button);
        return section.Count(x => x != null && x.Mark != target);
    }

    /// <summary>
    /// D158: <see cref="ToMark"/> for the section that is <paramref name="count"/> rows of <paramref name="rows"/> from
    /// <paramref name="first"/>, without a copy (the grid view counts each section once per rebuild).
    /// </summary>
    public static int ToMark(IReadOnlyList<GridEntry> rows, int first, int count, GridButton button)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var target = Target(button); var changing = 0;
        for (var i = Math.Max(0, first); i < first + count && i < rows.Count; i++) if (rows[i] != null && rows[i].Mark != target) changing++;
        return changing;
    }

    /// <summary>D158: whether a section's button works with <paramref name="toMark"/> icons to change: Never all only up to the limit.</summary>
    public static bool Usable(GridButton button, int toMark) => button != GridButton.NeverAll || toMark <= MaximumNeverAll;

    /// <summary>X59: a section button's name, as its tooltip's heading ("Never all", "Clear").</summary>
    public static Message ButtonName(GridButton button) =>
        button == GridButton.NeverAll ? Message.Of("yavsos_grid_never_all").With("never", Never) : Message.Of("yavsos_grid_clear");

    /// <summary>X59: the text drawn on a section button, beside the drawn ✕ for Never all ("All", "Clear").</summary>
    public static Message ButtonLabel(GridButton button) =>
        button == GridButton.NeverAll ? Message.Of("yavsos_grid_never_all_label") : Message.Of("yavsos_grid_clear");

    /// <summary>
    /// X59: a section button's tooltip, for the <paramref name="count"/> icons the section shows now. D158: a Never all
    /// greyed out by the limit (<paramref name="toMark"/> above <see cref="MaximumNeverAll"/>) says why and what to do
    /// instead; a negative <paramref name="toMark"/> means not counted, as before.
    /// </summary>
    public static Tooltip ButtonTip(GridButton button, int count, bool searching, int toMark = -1)
    {
        if (toMark >= 0 && !Usable(button, toMark))
            return Tooltip.Of(ButtonName(button), Message.Of("yavsos_grid_never_all_limit").With("never", Never)
                .With("limit", (long)MaximumNeverAll).Count(toMark));
        var lines = new List<Message>
        {
            (button == GridButton.NeverAll ? Message.Of("yavsos_grid_never_all_tip").With("never", Never)
                : Message.Of("yavsos_grid_clear_tip").With("always", Always).With("never", Never)).Count(count)
        };
        if (searching) lines.Add(Message.Of("yavsos_grid_section_search_tip"));
        lines.Add(Message.Of("yavsos_grid_section_undo_tip"));
        return Tooltip.Of(ButtonName(button), MessageList.Of(ListKind.Lines, lines));
    }

    /// <summary>
    /// X59 (D149): what the strip says after a section button: how many icons changed and that undo puts them back, or
    /// that nothing in the section needed it.
    /// </summary>
    public static Message ButtonDone(GridButton button, int changed) => changed == 0
        ? Message.Of("yavsos_grid_section_unchanged")
        : (button == GridButton.NeverAll ? Message.Of("yavsos_grid_never_all_done").With("never", Never) : Message.Of("yavsos_grid_clear_done")).Count(changed);

    /// <summary>
    /// #62 (D158): the section view button's tooltip. <paramref name="perCategory"/> is the view shown now (the button is
    /// lit); the text says what a click switches to.
    /// </summary>
    public static Tooltip ViewTip(bool perCategory) => Tooltip.Of(Message.Of("yavsos_grid_view"),
        Message.Of(perCategory ? "yavsos_grid_view_per_category" : "yavsos_grid_view_single"));

    /// <summary>
    /// #62 (D158): the ticked categories as section groups for <see cref="Build"/> in the view the player chose: one
    /// section per ticked category, or (null) one "Accepted by this rule" section.
    /// </summary>
    public static IReadOnlyList<string>? Groups(bool perCategory, IReadOnlyList<string> ticked) => perCategory ? ticked : null;

    /// <summary>A grid section's title with how many icons it has: "Accepted by this rule (12)".</summary>
    public static Message Title(GridSection section, int count) => (section switch
    {
        GridSection.Here => Message.Of("yavsos_grid_here"),
        GridSection.Marked => Message.Of("yavsos_grid_marked").With("always", Always).With("never", Never),
        GridSection.Rule => Message.Of("yavsos_grid_rule"),
        _ => Message.Of("yavsos_grid_found")
    }).Count(count);

    private static bool Matches(GridSource source, string text) =>
        CultureInfo.CurrentCulture.CompareInfo.IndexOf(source.Name, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0 ||
        source.Prefab.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
}
