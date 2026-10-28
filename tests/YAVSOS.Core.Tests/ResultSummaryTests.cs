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
        var report = CleanupReport.ForStash();
        report.Moved(40, item: "Copper"); report.Moved(10, item: "Copper"); report.Moved(20, viaMuc: true, item: "Wood");
        report.Remain(CleanupReason.NoCapacity, "Stone"); report.Remain(CleanupReason.NoCapacity, "Stone"); report.Remain(CleanupReason.NoCapacity, "Flint");
        report.Remain(CleanupReason.OtherOwnerNoMuc, "Tin");
        report.Refuse(TransferRefusal.InventoryChanged, "Bronze");
        report.Unconfirm(viaMuc: true, item: "SwordIron");
        var detail = report.Detail(p => p == "SwordIron" ? "Iron sword" : p);
        Assert.Equal("Stash", detail.Title);
        Assert.Equal(report.Summary(), detail.Headline);
        Assert.Equal(new[]
        {
            "stored 3 stacks (70 items), 1 via MultiUserChest: Copper ×50, Wood ×20",
            "3 left in your inventory (no room): Stone ×2, Flint",
            "1 skipped (another player's chest, no MultiUserChest): Tin",
            "1 refused (chest changed): Bronze",
            "1 unconfirmed (1 via MultiUserChest), not retried: Iron sword"
        }, detail.Lines.Select(x => x.Text));
        Assert.Equal((ResultKind.Remaining, CleanupReason.NoCapacity, 3), (detail.Lines[1].Kind, detail.Lines[1].Reason!.Value, detail.Lines[1].Count));
        Assert.Equal(TransferRefusal.InventoryChanged, detail.Lines[3].Refusal);
        Assert.Equal(0, detail.MoreLines); Assert.False(detail.Empty);
    }

    [Fact]
    public void Detail_IsBounded_AndCountsUnnamedAndOverflowItems()
    {
        var report = new CleanupReport();
        for (var i = 0; i < 30; i++) report.Remain(CleanupReason.NoDestination, "Item" + i);
        report.Remain(CleanupReason.NoDestination); // unnamed (older caller)
        foreach (var reason in new[] { CleanupReason.NoCapacity, CleanupReason.Held, CleanupReason.Budget, CleanupReason.AzuExcluded })
            report.Remain(reason, "X");
        var detail = report.Detail(namesPerLine: 2, maximumLines: 3);
        Assert.Equal(3, detail.Lines.Count); Assert.Equal(2, detail.MoreLines);
        // Reasons in report order; nothing moved, so no moved line. The headline still has every total.
        Assert.Equal(new CleanupReason?[] { CleanupReason.NoCapacity, CleanupReason.NoDestination, CleanupReason.AzuExcluded }, detail.Lines.Select(x => x.Reason));
        Assert.StartsWith("Cleanup: moved 0 stacks (0 items) · 35 left in place", detail.Headline);
        var none = detail.Lines.Single(x => x.Reason == CleanupReason.NoDestination);
        Assert.Equal(new[] { "Item0", "Item1" }, none.Items); Assert.Equal(29, none.More);
        Assert.Equal("31 left in place (no chest takes it): Item0, Item1 +29 more", none.Text);
        var longName = new CleanupReport(); longName.Refuse(TransferRefusal.NoCapacity, new string('n', 500));
        Assert.All(longName.Detail().Lines, x => Assert.True(x.Text.Length <= ResultSummary.MaximumText));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Detail(maximumLines: 0));
    }

    [Fact]
    public void Detail_NamesTallyIsBoundedPerLine()
    {
        var report = new CleanupReport();
        for (var i = 0; i < 100; i++) report.Moved(1, item: "Item" + i);
        var line = Assert.Single(report.Detail(namesPerLine: 100).Lines);
        Assert.Equal(CleanupReport.MaximumNamesPerLine, line.Items.Count);
        Assert.Equal(100 - CleanupReport.MaximumNamesPerLine, line.More);
        Assert.Equal("Cleanup: moved 100 stacks (100 items)", report.Summary());
    }
}
