using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Interface;

/// <summary>Caution (D101): a note the player must see, such as a rule that takes nothing; drawn amber, never folded away.</summary>
public enum ChipKind { Category, Filter, Always, Never, Note, Caution }

/// <summary>One short piece of the panel's Rule section (D071): a category, the filters, or an Always or Never item.</summary>
public sealed class RuleChip
{
    public string Text { get; }
    public ChipKind Kind { get; }
    /// <summary>The chip's tooltip (D101): its full text and what it means.</summary>
    public string Detail { get; }
    public RuleChip(string text, ChipKind kind, string detail = "")
    {
        Text = text ?? throw new ArgumentNullException(nameof(text)); Kind = kind; Detail = detail ?? "";
    }
}

/// <summary>What a chest without a rule does with stashing (D101): only items it already holds, anything, or not known.</summary>
public enum NoRuleStash { ExistingOnly, Anything, Unknown }

/// <summary>
/// D071: what a chest's rule takes, as chips for the Rule section: categories, then the filters in short words ("only
/// eaten by Fox"), then Always and Never items. D101: the chips area is at most <see cref="Rows"/> rows, then one "+N
/// more" chip whose tooltip lists the rest (<see cref="Fit"/>); a chest without a rule, or a rule that takes nothing,
/// says so as a chip in the same area. Pure and Unity-free.
/// </summary>
public static class RuleChips
{
    public const int MaximumText = 40, Rows = 2, MaximumListed = 20;

    /// <param name="filters">The filters in short words (<see cref="RuleText.Short"/>), or empty.</param>
    /// <param name="ignored">Categories the rule names that this game does not have (D050).</param>
    /// <param name="stasher">What does the stashing ("stashing" or "AzuAutoStore"), for the takes-nothing caution.</param>
    public static IReadOnlyList<RuleChip> Build(IEnumerable<string> categories, string filters, IEnumerable<string> always, IEnumerable<string> never, int ignored = 0,
        string stasher = "stashing")
    {
        if (categories == null || filters == null || always == null || never == null) throw new ArgumentNullException(nameof(categories));
        // Look-alike copies (the game's NPC capes) share a name: one chip each (P6-24, 2026-10-23).
        static List<string> Names(IEnumerable<string> names) => names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCulture).ToList();
        var categoryNames = Names(categories); var alwaysNames = Names(always); var neverNames = Names(never);
        var chips = new List<RuleChip>();
        // A must-say note comes first, so the two-row limit can never fold it into "+N more" (D101: no warning only in a tooltip).
        var note = Note(categoryNames.Count, alwaysNames.Count, neverNames.Count, filters.Length != 0, ignored, stasher);
        if (note != null) chips.Add(note);
        chips.AddRange(categoryNames.Select(x => new RuleChip(Bound(x), ChipKind.Category, x + "\nEvery item in this category, unless a filter or Never says otherwise.")));
        if (filters.Length != 0)
            chips.Add(new RuleChip(Bound("only " + filters), ChipKind.Filter,
                "Only " + filters + "\nAn item must match every filter. Rarity, upgrade level, damaged and crafter are checked on each stack as it moves."));
        chips.AddRange(alwaysNames.Select(x => new RuleChip(Bound(x), ChipKind.Always,
            "Always: " + x + "\nTaken whatever the categories and filters say, and preferred over chests that take it only by category, at the same priority.")));
        chips.AddRange(neverNames.Select(x => new RuleChip(Bound(x), ChipKind.Never, "Never: " + x + "\nNever stored here, whatever the categories say.")));
        if (ignored > 0)
            chips.Add(new RuleChip(ignored + " ignored categor" + (ignored == 1 ? "y" : "ies"), ChipKind.Note,
                "Categories this rule names that this game doesn't have (removed, or from another version). They take nothing; Edit lists them."));
        return chips.AsReadOnly();
    }

    /// <summary>The chip for a chest without a rule (D101), drawn where the rule's chips would be.</summary>
    public static RuleChip NoRule(NoRuleStash stash) => stash switch
    {
        NoRuleStash.ExistingOnly => new RuleChip("No rule: stashing adds only items already in this chest.", ChipKind.Note,
            "Stashing puts an item here only if this chest already holds some of it.\n\n" + NoRuleCleanup),
        NoRuleStash.Anything => new RuleChip("No rule: stashing can put anything here.", ChipKind.Note,
            "AzuAutoStore's \"Must Have Existing Item To Pull\" is off, so its stashing fills chests without a rule with anything, after chests whose rules take the item.\n\n" + NoRuleCleanup),
        _ => new RuleChip("No rule: AzuAutoStore treats it normally.", ChipKind.Note, "AzuAutoStore stores into this chest as it would without YAVSOS.\n\n" + NoRuleCleanup)
    };

    private const string NoRuleCleanup = "Clean up nearby can move items out of a chest without a rule into a chest whose rule takes them.";

    /// <summary>What a rule of only filters, or only Never items, means; null when the chips say it all.</summary>
    public static RuleChip? Note(int categories, int always, int never, bool filters, int ignored, string stasher)
    {
        if (categories + ignored == 0 && filters)
            return new RuleChip(always != 0 ? "Any item that matches the filters, and the Always items." : "Any item that matches the filters.", ChipKind.Note,
                "No category is ticked, so the filters alone decide: every item that matches them is taken.");
        if (categories + always + ignored != 0) return null;
        return never != 0
            ? new RuleChip("Takes nothing: Never alone takes nothing.", ChipKind.Caution,
                "A rule with only Never items stores nothing here (" + stasher + " included). Tick a category or mark an item Always.")
            : new RuleChip("Takes nothing from " + stasher + ".", ChipKind.Caution, "This rule has no part this version can use. Tick a category or mark an item Always.");
    }

    /// <summary>
    /// D101: how many chips fit in <see cref="Rows"/> rows of <paramref name="rowWidth"/>, keeping room for a "+N more"
    /// chip of width <paramref name="moreWidth"/>(N) when some don't. Returns the shown count and each shown chip's
    /// position (x, row); the "+N more" chip, if any, goes at <paramref name="more"/>. A chip wider than a row is drawn
    /// at the row's width, as the panel does.
    /// </summary>
    public static int Fit(IReadOnlyList<float> widths, float rowWidth, float gap, Func<int, float> moreWidth, out (float X, int Row)[] places, out (float X, int Row)? more)
    {
        if (widths == null || moreWidth == null) throw new ArgumentNullException(nameof(widths));
        var all = Place(widths, widths.Count, rowWidth, gap);
        if (all != null) { places = all; more = null; return widths.Count; }
        // Fewer chips until the shown ones and the "+N more" chip fit together.
        for (var shown = widths.Count - 1; shown >= 0; shown--)
        {
            var list = widths.Take(shown).Append(Math.Min(moreWidth(widths.Count - shown), rowWidth)).ToList();
            var placed = Place(list, list.Count, rowWidth, gap);
            if (placed == null) continue;
            places = placed.Take(shown).ToArray(); more = placed[shown];
            return shown;
        }
        places = Array.Empty<(float, int)>(); more = (0, 0);
        return 0;
    }

    private static (float X, int Row)[]? Place(IReadOnlyList<float> widths, int count, float rowWidth, float gap)
    {
        var places = new (float X, int Row)[count];
        float x = 0; var row = 0;
        for (var i = 0; i < count; i++)
        {
            var size = Math.Min(widths[i], rowWidth);
            if (x > 0 && x + size > rowWidth) { x = 0; row++; }
            if (row >= Rows) return null;
            places[i] = (x, row);
            x += size + gap;
        }
        return places;
    }

    /// <summary>The "+N more" chip's tooltip: the folded chips' names, at most <see cref="MaximumListed"/>.</summary>
    public static string More(IReadOnlyList<RuleChip> chips, int shown)
    {
        if (chips == null) throw new ArgumentNullException(nameof(chips));
        var rest = chips.Skip(shown).ToList();
        static string Name(RuleChip chip) => chip.Kind switch { ChipKind.Always => "Always: " + chip.Text, ChipKind.Never => "Never: " + chip.Text, _ => chip.Text };
        return "Also in this rule\n" + string.Join("\n", rest.Take(MaximumListed).Select(Name)) + (rest.Count > MaximumListed ? "\n… and " + (rest.Count - MaximumListed) + " more" : "");
    }

    private static string Bound(string text) => text.Length <= MaximumText ? text : text.Substring(0, MaximumText - 1) + "…";
}
