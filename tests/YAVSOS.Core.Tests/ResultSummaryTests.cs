using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>P6-04: the result view after a stash or cleanup, from the run's own report.</summary>
public sealed class ResultSummaryTests
{
    [Fact]
    public void Detail_GroupsByOutcomeAndReason_WithItemNames()
    {
        var report = RunReport.ForStash();
        report.Moved(40, item: "Copper"); report.Moved(10, item: "Copper"); report.Moved(20, viaMuc: true, item: "Wood");
        report.Remain(RunReason.NoCapacity, "Stone"); report.Remain(RunReason.NoCapacity, "Stone"); report.Remain(RunReason.NoCapacity, "Flint");
        report.Remain(RunReason.OtherOwnerNoMuc, "Tin");
        report.Refuse(TransferRefusal.InventoryChanged, "Bronze");
        report.Unconfirm(viaMuc: true, item: "SwordIron");
        var detail = report.Detail(p => p == "SwordIron" ? "Iron sword" : p);
        Assert.Equal(RunKind.Stash, detail.Kind);
        Assert.Equal("YAVSOS stash result", EnglishText.Of(detail.Title));
        Assert.Equal(report.Summary(), detail.Headline);
        Assert.Equal(new[]
        {
            "Stashed 3 stacks (70 items), 1 via MultiUserChest: Copper ×50, Wood ×20",
            "3 stay in your inventory (no room): Stone ×2, Flint",
            "1 stays in your inventory (another player's chest, and MultiUserChest isn't available): Tin",
            "1 stays in your inventory (a chest changed): Bronze",
            "1 unconfirmed (1 via MultiUserChest, not retried): Iron sword"
        }, detail.Lines.Select(x => EnglishText.Of(x.Text)));
        Assert.Equal((ResultKind.Remaining, RunReason.NoCapacity, 3), (detail.Lines[1].Kind, detail.Lines[1].Reason!.Value, detail.Lines[1].Count));
        Assert.Equal(new[] { new ResultItem("Stone", 2), new ResultItem("Flint", 1) }, detail.Lines[1].Items);
        Assert.Equal(new[] { new ResultItem("Copper", 50), new ResultItem("Wood", 20) }, detail.Lines[0].Items);
        Assert.Equal((ResultKind.Refused, TransferRefusal.InventoryChanged), (detail.Lines[3].Kind, detail.Lines[3].Refusal!.Value));
        Assert.Equal(0, detail.MoreLines); Assert.False(detail.Empty);
    }

    [Fact]
    public void Detail_IsBounded_AndCountsUnnamedAndOverflowItems()
    {
        var report = RunReport.ForCleanup();
        for (var i = 0; i < 30; i++) report.Remain(RunReason.NoDestination, "Item" + i);
        report.Remain(RunReason.NoDestination); // unnamed (older caller)
        foreach (var reason in new[] { RunReason.NoCapacity, RunReason.Held, RunReason.Budget, RunReason.AzuExcluded })
            report.Remain(reason, "X");
        var detail = report.Detail(namesPerLine: 2, maximumLines: 3);
        Assert.Equal(3, detail.Lines.Count); Assert.Equal(2, detail.MoreLines);
        // Reasons in report order; nothing moved, so no moved line. The headline still has every part.
        Assert.Equal(new RunReason?[] { RunReason.NoCapacity, RunReason.NoDestination, RunReason.AzuExcluded }, detail.Lines.Select(x => x.Reason));
        Assert.Equal("Nothing moved: 1 stays in place (no room) · 31 stay in place (no chest accepts it) · " +
            "1 stays in place (AzuAutoStore's exclusions keep it out) · 1 stays in place (held) · 1 stays in place (run limit reached; try again).",
            EnglishText.Of(detail.Headline));
        var none = detail.Lines.Single(x => x.Reason == RunReason.NoDestination);
        Assert.Equal(new[] { "Item0", "Item1" }, none.Items.Select(x => x.Name)); Assert.Equal(29, none.More);
        Assert.Equal("31 stay in place (no chest accepts it): Item0, Item1 +29 more", EnglishText.Of(none.Text));
        // A line that names no items is its label alone: "+N more" would only repeat its count.
        var unnamed = RunReport.ForCleanup(); unnamed.Remain(RunReason.Held); unnamed.Remain(RunReason.Held);
        Assert.Equal("2 stay in place (held)", EnglishText.Of(Assert.Single(unnamed.Detail().Lines).Text));
        var hidden = report.Detail(namesPerLine: 0).Lines.Single(x => x.Reason == RunReason.NoDestination);
        Assert.Equal((0, 31), (hidden.Items.Count, hidden.More));
        Assert.Equal("31 stay in place (no chest accepts it)", EnglishText.Of(hidden.Text));
        var unreadable = RunReport.ForStash(); for (var i = 0; i < 3; i++) unreadable.Remain(RunReason.ChestUnreadable);
        Assert.Equal("YAVSOS couldn't read 3 nearby chests (see the log)", EnglishText.Of(Assert.Single(unreadable.Detail().Lines).Text));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Detail(maximumLines: 0));
    }

    // Inventory case 9: names and lines keep their character limits, and a cut never splits a surrogate pair.
    [Fact]
    public void Bound_CutsNamesAndLines_NeverInsideASurrogatePair()
    {
        var longName = RunReport.ForCleanup(); longName.Refuse(TransferRefusal.NoCapacity, new string('n', 500));
        var item = Assert.Single(Assert.Single(longName.Detail().Lines).Items);
        Assert.Equal(RunReport.MaximumName, item.Name.Length);
        Assert.EndsWith("…", item.Name);
        Assert.True(ResultSummary.Bound(EnglishText.Of(longName.Detail().Lines[0].Text), ResultSummary.MaximumText).Length <= ResultSummary.MaximumText);

        var emoji = "ab\U0001F600cd"; // a, b, a surrogate pair, c, d
        Assert.Equal("ab…", ResultSummary.Bound(emoji, 4));    // The pair would be cut in half: it goes whole.
        Assert.Equal("ab\U0001F600…", ResultSummary.Bound(emoji, 5));
        Assert.Equal(emoji, ResultSummary.Bound(emoji, 6));
        Assert.Equal("…", ResultSummary.Bound("\U0001F600xyz", 2)); // Room for no whole character: only the mark.
        Assert.Equal("", ResultSummary.Bound(null!, 4));
    }

    [Fact]
    public void Detail_NamesTallyIsBoundedPerLine()
    {
        var report = RunReport.ForCleanup();
        for (var i = 0; i < 100; i++) report.Moved(1, item: "Item" + i);
        var line = Assert.Single(report.Detail(namesPerLine: 100).Lines);
        Assert.Equal(RunReport.MaximumNamesPerLine, line.Items.Count);
        Assert.Equal(100 - RunReport.MaximumNamesPerLine, line.More);
        Assert.Equal("Moved 100 stacks (100 items).", EnglishText.Of(report.Summary()));
    }
    [Fact]
    public void Notes_FollowTheOutcomes_AndStayWithinTheLineBound()
    {
        // #64: a favorites mod YAVSOS couldn't read is named after the outcomes, in the summary and as a result line.
        var report = RunReport.ForStash();
        report.Moved(40, item: "Copper");
        foreach (var reason in new[] { RunReason.NoCapacity, RunReason.Held, RunReason.Budget }) report.Remain(reason, "X");
        report.Note(Messages.Message.Of("yavsos_stash_favorites_unreadable").With("mod", "Quick Stack Store"));
        const string note = "YAVSOS couldn't read Quick Stack Store's favorites and stashes them like other items (see the log)";
        Assert.Equal("Stashed 1 stack (40 items) · 1 stays in your inventory (no room) · 1 stays in your inventory (held) · " +
            "1 stays in your inventory (run limit reached; try again) · " + note + ".", EnglishText.Of(report.Summary()));
        var all = report.Detail();
        Assert.Equal(ResultKind.Notice, all.Lines[^1].Kind); Assert.Equal(note, EnglishText.Of(all.Lines[^1].Text)); Assert.Equal(0, all.Lines[^1].Count);
        // A tight bound drops outcome lines, never the note.
        var tight = report.Detail(maximumLines: 2);
        Assert.Equal(new[] { ResultKind.Moved, ResultKind.Notice }, tight.Lines.Select(x => x.Kind)); Assert.Equal(3, tight.MoreLines);
        // A stash that found nothing to move still names it.
        var empty = RunReport.ForStash();
        empty.Note(Messages.Message.Of("yavsos_stash_favorites_unreadable").With("mod", "ItemFavoriteFramework"));
        Assert.StartsWith("Nothing stashed: YAVSOS couldn't read ItemFavoriteFramework's favorites", EnglishText.Of(empty.Summary()));
    }
}
