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

    [Fact]
    public void Refuse_RejectsNonRefusals()
    {
        var report = new CleanupReport();
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.None));
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.Unconfirmed));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Moved(0));
    }
}
