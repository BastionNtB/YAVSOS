using System;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class CleanupReportTests
{
    [Fact]
    public void Summary_ListsEachOutcomeWithItsReason()
    {
        var report = new CleanupReport();
        report.Moved(20); report.Moved(30); report.Moved(10); report.Moved(3);
        report.Remain(CleanupReason.NoCapacity); report.Remain(CleanupReason.NoCapacity);
        report.Refuse(TransferRefusal.InventoryChanged);
        report.Remain(CleanupReason.OtherOwnerNoMuc);
        Assert.Equal("Cleanup: moved 4 stacks (63 items) · 2 left in place (2 no room) · 1 skipped (another player's chest, no MultiUserChest) · 1 refused (1 chest changed)",
            report.Summary());
    }

    [Fact]
    public void Summary_EmptyRunAndMultiUserChestAttribution()
    {
        Assert.Equal("Cleanup: moved 0 stacks (0 items)", new CleanupReport().Summary());
        var report = new CleanupReport();
        report.Moved(1, viaMuc: true); report.Unconfirm(viaMuc: true); report.Unconfirm();
        Assert.Equal("Cleanup: moved 1 stack (1 item), 1 via MultiUserChest · 2 unconfirmed (1 via MultiUserChest)", report.Summary());
        Assert.Equal(2, report.Unconfirmed);
    }

    // A01: a run the server turns off mid-way says so, rather than "rules changed".
    [Fact]
    public void Summary_SaysTurnedOff()
    {
        var report = new CleanupReport(); report.Refuse(TransferRefusal.TurnedOff, "Wood");
        Assert.Contains("turned off on this server", report.Summary());
    }

    [Fact]
    public void Summary_NamesEveryLeftInPlaceReason()
    {
        foreach (CleanupReason reason in Enum.GetValues(typeof(CleanupReason)))
        {
            var report = new CleanupReport(); report.Remain(reason);
            Assert.Contains(CleanupReport.Describe(reason), report.Summary());
            Assert.NotEqual(reason.ToString(), CleanupReport.Describe(reason));
        }
    }

    // SF3 (batch C): unreadable chests are counted as chests, apart from the stacks left in the inventory.
    [Fact]
    public void Summary_UnreadableChests_AreTheirOwnPart()
    {
        var report = CleanupReport.ForStash();
        report.Remain(CleanupReason.NoDestination, "Tin");
        for (var i = 0; i < 3; i++) report.Remain(CleanupReason.ChestUnreadable);
        Assert.Equal("Stash: stored 0 stacks (0 items) · 1 left in your inventory (1 no chest takes it) · 3 nearby chests couldn't be read (see the log)", report.Summary());
        Assert.Contains(report.Detail().Lines, x => x.Label == "3 nearby chests couldn't be read (see the log)");
    }

    // B4.3: one stack split across two chests is one stack moved.
    [Fact]
    public void Moved_CountsASplitStackOnce()
    {
        var report = new CleanupReport(); var copper = Guid.NewGuid();
        report.Moved(30, item: "Copper", stack: copper); report.Moved(10, viaMuc: true, item: "Copper", stack: copper);
        report.Moved(5, item: "Tin", stack: Guid.NewGuid());
        Assert.Equal(2, report.MovedStacks); Assert.Equal(45, report.MovedUnits); Assert.Equal(1, report.MucMovedStacks);
        Assert.StartsWith("Cleanup: moved 2 stacks (45 items), 1 via MultiUserChest", report.Summary());
    }

    // D086 D10: the player text names the chest type Azu's config excludes the item from, once and without "chests".
    [Fact]
    public void Summary_AzuExcluded_NamesTheChestTypes()
    {
        var report = new CleanupReport();
        report.Remain(CleanupReason.AzuExcluded, "SwordBronze", "Reinforced chest");
        report.Remain(CleanupReason.AzuExcluded, "SwordIron", "Reinforced chest");
        Assert.Contains("2 left in place (2 AzuAutoStore's config excludes it from Reinforced chest)", report.Summary());
        Assert.Contains(report.Detail().Lines, x => x.Label == "2 left in place (AzuAutoStore's config excludes it from Reinforced chest)");
        foreach (var type in new[] { "Wood chest", "Black metal chest", "Barrel", "Crate" }) report.Remain(CleanupReason.AzuExcluded, "Wood", type);
        Assert.Contains("excludes it from Reinforced chest, Wood chest, Black metal chest and others", report.Summary());
        Assert.Equal(CleanupReport.MaximumExcludedTypes, report.ExcludedChestTypes.Count);
        var unnamed = new CleanupReport(); unnamed.Remain(CleanupReason.AzuExcluded, "Wood");
        Assert.Contains("(1 AzuAutoStore's config excludes it)", unnamed.Summary());
    }

    [Fact]
    public void Refuse_RejectsNonRefusals()
    {
        var report = new CleanupReport();
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.None));
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.Unconfirmed));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Moved(0));
    }
}
