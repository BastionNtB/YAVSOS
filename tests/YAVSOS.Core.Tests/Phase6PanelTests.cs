using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

/// <summary>P6-24 (D071): the redesigned panel's rule chips and priority bar bands (the status marks: PanelMarksTests).</summary>
public sealed class Phase6PanelTests
{
    [Fact]
    public void Chips_CategoriesFiltersThenItems()
    {
        var fox = Fox();
        var chips = RuleChips.Build(new[] { "Food" }, fox, new[] { "Wolf meat" }, new[] { "Raspberries" });
        Assert.Equal(new[] { (ChipKind.Category, "Food"), (ChipKind.Filter, "only eaten by Fox"), (ChipKind.Always, "Wolf meat"), (ChipKind.Never, "Raspberries") },
            chips.Select(x => (x.Kind, Words(x))));
        Assert.Equal(Message.Of("yavsos_chip_filters").With("filters", fox!), chips[1].Said); // Names stay names; the rest are messages.
        Assert.Null(chips[0].Said); Assert.Null(chips[2].Said);
        Assert.All(chips, x => Assert.NotNull(x.Tip)); // D101: every chip has a tooltip.
        Assert.Equal(("Always: Wolf meat", "Accepted whatever the categories and filters say, and preferred over chests that accept it only by category, at the same priority."),
            EnglishText.Of(chips[2].Tip!));
        Assert.Equal(("Food", "Every item in this category, unless a filter or Never says otherwise."), EnglishText.Of(chips[0].Tip!));
        Assert.Equal("Only eaten by Fox", EnglishText.Of(chips[1].Tip!).Topic);
        var many = RuleChips.Build(Enumerable.Range(1, 9).Select(x => "Item " + x), null, new string[0], new string[0], ignored: 2);
        Assert.Equal(10, many.Count); // No per-kind cap any more: the two-row fit folds the rest (D101).
        Assert.Equal((ChipKind.Note, "2 ignored categories"), (many.Last().Kind, Words(many.Last())));
        Assert.Equal("1 ignored category", Words(RuleChips.Build(new[] { "Food" }, null, new string[0], new string[0], ignored: 1).Last())); // Inventory case 1.
        Assert.Single(RuleChips.Build(new string[0], null, new[] { "Wolf cape", "Wolf cape", "Wolf cape" }, new string[0])); // Look-alike copies.
        var longName = RuleChips.Build(new[] { new string('x', 60) }, null, new string[0], new string[0]).Single();
        Assert.Equal(RuleChips.MaximumText, longName.Name.Length);
        Assert.EndsWith("…", longName.Name);
        Assert.Equal(new string('x', 60), EnglishText.Of(longName.Tip!).Topic); // The tooltip has the whole name.
        Assert.Equal(RuleChips.MaximumText, RuleChips.Cut("only " + new string('y', 60)).Length); // The panel cuts the filters chip as before.
    }

    /// <summary>The filters "eaten by Fox" in short words, as the panel builds them (RuleText.Short).</summary>
    internal static MessageList? Fox() => RuleText.Short(new Dictionary<string, IReadOnlyList<string>> { ["eaten-by"] = new[] { "Fox" } });

    /// <summary>A chip's English: a name as it is, else its message from the English file.</summary>
    internal static string Words(RuleChip chip) => chip.Said is { } said ? EnglishText.Of(said) : chip.Name;

    // D101: what a rule of only filters, or only Never items, means is a chip in the chips area; a rule that accepts
    // nothing is a caution, first, so the two-row limit never folds it away.
    [Fact]
    public void ChipNote_ExplainsRulesWithoutCategories()
    {
        Assert.Null(RuleChips.Note(1, 0, 0, false, 0, Stasher.AzuAutoStore));
        Assert.Null(RuleChips.Note(0, 2, 0, false, 0, Stasher.AzuAutoStore));
        Assert.Equal((ChipKind.Note, "Any item that matches the filters."), Pair(RuleChips.Note(0, 0, 0, true, 0, Stasher.AzuAutoStore)!));
        Assert.Equal("Any item that matches the filters, and the Always items.", Words(RuleChips.Note(0, 1, 0, true, 0, Stasher.AzuAutoStore)!));
        Assert.Equal((ChipKind.Caution, "Accepts nothing: only Never items."), Pair(RuleChips.Note(0, 0, 3, false, 0, Stasher.Yavsos)!));
        Assert.Equal((ChipKind.Caution, "Accepts nothing: no usable part."), Pair(RuleChips.Note(0, 0, 0, false, 0, Stasher.Yavsos)!));
        Assert.Null(RuleChips.Note(0, 0, 0, false, 1, Stasher.Yavsos)); // Ignored categories: the chip says so.
        var neverOnly = RuleChips.Build(new string[0], null, new string[0], new[] { "Bronze" }, stasher: Stasher.AzuAutoStore);
        Assert.Equal(new[] { ChipKind.Caution, ChipKind.Never }, neverOnly.Select(x => x.Kind));
        static (ChipKind, string) Pair(RuleChip chip) => (chip.Kind, Words(chip));
    }

    // P10-04 (the stasher word): a sentence that names the stasher is one message per stasher, chosen by the typed value.
    [Fact]
    public void ChipNote_NamesTheStasher_ByItsOwnMessage()
    {
        Assert.Equal(Message.Of("yavsos_chip_never_only_tip"), RuleChips.Note(0, 0, 3, false, 0, Stasher.Yavsos)!.Tip!.Heading);
        Assert.Equal(Message.Of("yavsos_chip_never_only_tip_azu"), RuleChips.Note(0, 0, 3, false, 0, Stasher.AzuAutoStore)!.Tip!.Heading);
        Assert.Contains("so stashing puts nothing here", EnglishText.Of(RuleChips.Note(0, 0, 3, false, 0, Stasher.Yavsos)!.Tip!).Topic);
        Assert.Contains("so AzuAutoStore puts nothing here", EnglishText.Of(RuleChips.Note(0, 0, 3, false, 0, Stasher.AzuAutoStore)!.Tip!).Topic);
    }

    // D071: one colour band per D069 name along the 1–10 bar.
    [Fact]
    public void PriorityBands_FollowTheNames()
    {
        for (var level = ChestPriority.Highest; level <= ChestPriority.Lowest; level++)
            for (var other = ChestPriority.Highest; other <= ChestPriority.Lowest; other++)
                Assert.Equal(ChestPriority.Name(level).Equals(ChestPriority.Name(other)), ChestPriority.Band(level) == ChestPriority.Band(other));
        Assert.Equal(0, ChestPriority.Band(1)); Assert.Equal(5, ChestPriority.Band(10));
    }
}
