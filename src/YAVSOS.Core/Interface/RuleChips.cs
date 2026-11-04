using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Interface;

/// <summary>Caution (D101): a note the player must see, such as a rule that accepts nothing; drawn amber, never folded away.</summary>
public enum ChipKind { Category, Filter, Always, Never, Note, Caution }

/// <summary>One short piece of the panel's Rule section (D071): a category, the filters, or an Always or Never item.</summary>
public sealed class RuleChip
{
    /// <summary>A name shown as it is (a category or item name, cut at <see cref="RuleChips.MaximumText"/>); "" when <see cref="Said"/> is the text.</summary>
    public string Name { get; }
    /// <summary>The chip's text as a message (P10-04); null when <see cref="Name"/> is the text.</summary>
    public Message? Said { get; }
    public ChipKind Kind { get; }
    /// <summary>The chip's tooltip (D101): its full text and what it means; null for none.</summary>
    public Tooltip? Tip { get; }
    public RuleChip(string name, ChipKind kind, Tooltip? tip = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name)); Kind = kind; Tip = tip;
    }
    public RuleChip(Message said, ChipKind kind, Tooltip? tip = null)
    {
        Said = said ?? throw new ArgumentNullException(nameof(said)); Name = ""; Kind = kind; Tip = tip;
    }
    /// <summary>For logs and tests; the panel formats <see cref="Said"/>.</summary>
    public override string ToString() => Said?.ToString() ?? Name;
}

/// <summary>What a chest without a rule does with stashing (D101): only items it already holds, anything, or not known.</summary>
public enum NoRuleStash { ExistingOnly, Anything, Unknown }

/// <summary>
/// D071: what a chest's rule accepts, as chips for the Rule section: categories, then the filters in short words ("only
/// eaten by Fox"), then Always and Never items. D101: the chips area is at most <see cref="Rows"/> rows, then one "+N
/// more" chip whose tooltip lists the rest (<see cref="Fit"/>); a chest without a rule, or a rule that accepts nothing,
/// says so as a chip in the same area. Pure and Unity-free.
/// </summary>
public static class RuleChips
{
    public const int MaximumText = 40, Rows = 2, MaximumListed = 20;

    /// <param name="filters">The filters in short words (<see cref="RuleText.Short"/>), or null without filters.</param>
    /// <param name="ignored">Categories the rule names that this game does not have (D050).</param>
    /// <param name="stasher">What stashes in this game, for the tooltip of the caution on a rule of only Never items.</param>
    public static IReadOnlyList<RuleChip> Build(IEnumerable<string> categories, MessageList? filters, IEnumerable<string> always, IEnumerable<string> never, int ignored = 0,
        Stasher stasher = Stasher.Yavsos)
    {
        if (categories == null || always == null || never == null) throw new ArgumentNullException(nameof(categories));
        // Look-alike copies (the game's NPC capes) share a name: one chip each (P6-24, 2026-10-23).
        static List<string> Names(IEnumerable<string> names) => names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCulture).ToList();
        var categoryNames = Names(categories); var alwaysNames = Names(always); var neverNames = Names(never);
        var chips = new List<RuleChip>();
        // A must-say note comes first, so the two-row limit can never fold it into "+N more" (D101: no warning only in a tooltip).
        var note = Note(categoryNames.Count, alwaysNames.Count, neverNames.Count, filters != null, ignored, stasher);
        if (note != null) chips.Add(note);
        chips.AddRange(categoryNames.Select(x => new RuleChip(Cut(x), ChipKind.Category, Tooltip.Named(x, Message.Of("yavsos_chip_category_tip")))));
        // The panel cuts this chip's formatted text with Cut, as Core cut it before P10-04.
        if (filters != null)
            chips.Add(new RuleChip(Message.Of("yavsos_chip_filters").With("filters", filters), ChipKind.Filter,
                Tooltip.Of(Message.Of("yavsos_chip_filters_heading").With("filters", filters), Message.Of("yavsos_chip_filters_tip"))));
        chips.AddRange(alwaysNames.Select(x => new RuleChip(Cut(x), ChipKind.Always, Tooltip.Of(Always(x), Message.Of("yavsos_chip_always_tip")))));
        chips.AddRange(neverNames.Select(x => new RuleChip(Cut(x), ChipKind.Never, Tooltip.Of(Never(x), Message.Of("yavsos_chip_never_tip")))));
        if (ignored > 0)
            chips.Add(new RuleChip(Message.Of("yavsos_chip_ignored").Count(ignored), ChipKind.Note, Tooltip.Of(Message.Of("yavsos_chip_ignored_tip").With("section", Message.Of("yavsos_tab_heading")))));
        return chips.AsReadOnly();
    }

    /// <summary>The chip for a chest without a rule (D101), drawn where the rule's chips would be.</summary>
    public static RuleChip NoRule(NoRuleStash stash) => stash switch
    {
        NoRuleStash.ExistingOnly => new RuleChip(Message.Of("yavsos_chip_norule_existing"), ChipKind.Note,
            Tooltip.Of(Message.Of("yavsos_chip_norule_existing_tip"), NoRuleCleanup)),
        NoRuleStash.Anything => new RuleChip(Message.Of("yavsos_chip_norule_anything"), ChipKind.Note,
            Tooltip.Of(Message.Of("yavsos_chip_norule_anything_tip").With("setting", AzuMustHaveExisting), NoRuleCleanup)),
        _ => new RuleChip(Message.Of("yavsos_chip_norule_unknown"), ChipKind.Note, Tooltip.Of(Message.Of("yavsos_chip_norule_unknown_tip"), NoRuleCleanup))
    };

    /// <summary>AzuAutoStore's setting, by the name AzuAutoStore gives it (not YAVSOS's to translate; inventory case 11).</summary>
    public const string AzuMustHaveExisting = "Must Have Existing Item To Pull";
    private static readonly Message NoRuleCleanup = Message.Of("yavsos_chip_norule_cleanup_tip").With("button", Message.Of("yavsos_cleanup_button"));
    private static Message Always(string name) => Message.Of("yavsos_chip_always_heading").With("name", name);
    private static Message Never(string name) => Message.Of("yavsos_chip_never_heading").With("name", name);

    /// <summary>What a rule of only filters, or only Never items, means; null when the chips say it all.</summary>
    public static RuleChip? Note(int categories, int always, int never, bool filters, int ignored, Stasher stasher)
    {
        if (categories + ignored == 0 && filters)
            return new RuleChip(Message.Of(always != 0 ? "yavsos_chip_filters_only_always" : "yavsos_chip_filters_only"), ChipKind.Note,
                Tooltip.Of(Message.Of("yavsos_chip_filters_only_tip")));
        if (categories + always + ignored != 0) return null;
        // P10-04: one sentence per stasher (the stasher word changes the sentence around it in other languages).
        return never != 0
            ? new RuleChip(Message.Of("yavsos_chip_never_only"), ChipKind.Caution,
                Tooltip.Of(Message.Of(stasher == Stasher.AzuAutoStore ? "yavsos_chip_never_only_tip_azu" : "yavsos_chip_never_only_tip")))
            : new RuleChip(Message.Of("yavsos_chip_nothing_usable"), ChipKind.Caution, Tooltip.Of(Message.Of("yavsos_chip_nothing_usable_tip")));
    }

    /// <summary>The "+N more" chip (D101) for the chips after the first <paramref name="shown"/>; its tooltip lists them (<see cref="More"/>).</summary>
    public static RuleChip Folded(IReadOnlyList<RuleChip> chips, int shown)
    {
        if (chips == null) throw new ArgumentNullException(nameof(chips));
        return new RuleChip(Message.Of("yavsos_chip_folded").Count(chips.Count - shown), ChipKind.Note, More(chips, shown));
    }

    /// <summary>A "+N more" chip without its tooltip, for measuring its width.</summary>
    public static RuleChip Folded(int count) => new(Message.Of("yavsos_chip_folded").Count(count), ChipKind.Note);

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

    /// <summary>The "+N more" chip's tooltip: the folded chips' names, one per line, at most <see cref="MaximumListed"/>.</summary>
    public static Tooltip More(IReadOnlyList<RuleChip> chips, int shown)
    {
        if (chips == null) throw new ArgumentNullException(nameof(chips));
        var rest = chips.Skip(shown).ToList();
        static object Name(RuleChip chip) => chip.Kind switch
        {
            ChipKind.Always => Always(chip.Name), ChipKind.Never => Never(chip.Name), _ => chip.Said ?? (object)chip.Name
        };
        var names = MessageList.Of(ListKind.Lines, rest.Take(MaximumListed).Select(Name)).WithMore(Math.Max(0, rest.Count - MaximumListed));
        return Tooltip.Of(Message.Of("yavsos_chip_more_heading"), names);
    }

    /// <summary>A chip's text cut to <see cref="MaximumText"/> characters with "…"; the chip's tooltip has it whole.</summary>
    public static string Cut(string text) => Transfers.ResultSummary.Bound(text, MaximumText);
}
