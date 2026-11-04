using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class RunReportTests
{
    [Fact]
    public void Summary_ListsEachOutcomeWithItsReason()
    {
        var report = RunReport.ForCleanup();
        report.Moved(20); report.Moved(30); report.Moved(10); report.Moved(3);
        report.Remain(RunReason.NoCapacity); report.Remain(RunReason.NoCapacity);
        report.Refuse(TransferRefusal.InventoryChanged);
        report.Remain(RunReason.OtherOwnerNoMuc);
        Assert.Equal("Moved 4 stacks (63 items) · 2 stay in place (no room) · " +
            "1 stays in place (another player's chest, and MultiUserChest isn't available) · 1 stays in place (a chest changed).",
            EnglishText.Of(report.Summary()));
    }

    [Fact]
    public void Summary_EmptyRunAndMultiUserChestAttribution()
    {
        Assert.Equal(Message.Of("yavsos_result_empty_cleanup"), RunReport.ForCleanup().Summary());
        Assert.Equal("Nothing to clean up.", EnglishText.Of(RunReport.ForCleanup().Summary()));
        var report = RunReport.ForCleanup();
        report.Moved(1, viaMuc: true); report.Unconfirm(viaMuc: true); report.Unconfirm();
        Assert.Equal("Moved 1 stack (1 item), 1 via MultiUserChest · 2 unconfirmed (1 via MultiUserChest, not retried).", EnglishText.Of(report.Summary()));
        Assert.Equal(2, report.Unconfirmed);
    }

    // The word list's "Say what didn't happen first": a run that moved nothing opens with it.
    [Fact]
    public void Summary_NothingMoved_SaysSoFirst()
    {
        var cleanup = RunReport.ForCleanup(); cleanup.Remain(RunReason.NoDestination, "Tin");
        Assert.Equal("Nothing moved: 1 stays in place (no chest accepts it).", EnglishText.Of(cleanup.Summary()));
        var stash = RunReport.ForStash(); stash.Remain(RunReason.NoCapacity); stash.Remain(RunReason.NoCapacity);
        Assert.Equal("Nothing stashed: 2 stay in your inventory (no room).", EnglishText.Of(stash.Summary()));
        Assert.Equal("Nothing to stash.", EnglishText.Of(RunReport.ForStash().Summary()));
    }

    // A01: a run the server turns off mid-way says so, rather than "rules changed".
    [Fact]
    public void Summary_SaysTurnedOff()
    {
        var report = RunReport.ForCleanup(); report.Refuse(TransferRefusal.TurnedOff, "Wood");
        Assert.Equal("Nothing moved: 1 stays in place (turned off on this server).", EnglishText.Of(report.Summary()));
    }

    // F3 (maintainer, 2026-10-30): one line per MultiUserChest cause, in stacks; only the probe still checking says to try again.
    [Fact]
    public void Detail_OneLinePerMultiUserChestCause()
    {
        var report = RunReport.ForStash();
        report.Moved(20); report.Moved(30); report.Moved(10); report.Moved(3);
        report.Remain(RunReason.OtherOwnerMucUnusable, "Copper"); report.Remain(RunReason.OtherOwnerMucUnusable, "Tin");
        report.Remain(RunReason.OtherOwnerMucSilent, "Wood");
        for (var i = 0; i < 3; i++) report.Remain(RunReason.OtherOwnerMucChecking, "Stone");
        Assert.Equal(new[]
        {
            "Stashed 4 stacks (63 items)",
            "2 stay in your inventory (another player's chest, and their MultiUserChest can't handle requests)",
            "1 stays in your inventory (another player's chest, and no MultiUserChest answered from their game)",
            "3 stay in your inventory (another player's chest, and YAVSOS is still checking their MultiUserChest; try again in a moment)"
        }, report.Detail().Lines.Select(x => EnglishText.Of(x.Label)));
        Assert.Equal("Stashed 4 stacks (63 items) · 2 stay in your inventory (another player's chest, and their MultiUserChest can't handle requests) · " +
            "1 stays in your inventory (another player's chest, and no MultiUserChest answered from their game) · " +
            "3 stay in your inventory (another player's chest, and YAVSOS is still checking their MultiUserChest; try again in a moment).",
            EnglishText.Of(report.Summary()));
        var cleanup = RunReport.ForCleanup(); cleanup.Remain(RunReason.OtherOwnerFormatUnknown);
        Assert.Equal("Nothing moved: 1 stays in place (another player's chest, and YAVSOS doesn't recognize their MultiUserChest's item format).",
            EnglishText.Of(cleanup.Summary()));
        Assert.DoesNotContain("again", EnglishText.Of(RunReport.Reason(RunReason.OtherOwnerMucUnusable)) +
            EnglishText.Of(RunReport.Reason(RunReason.OtherOwnerMucSilent)) + EnglishText.Of(RunReport.Reason(RunReason.OtherOwnerFormatUnknown)));
    }

    [Fact]
    public void OwnerMucState_MapsEachCauseToItsReason()
    {
        Assert.Null(OwnerMucState.Usable.Skip());
        Assert.Equal(RunReason.OtherOwnerMucUnusable, OwnerMucState.CantTakeRequests.Skip());
        Assert.Equal(RunReason.OtherOwnerMucSilent, OwnerMucState.NoAnswer.Skip());
        Assert.Equal(RunReason.OtherOwnerMucChecking, OwnerMucState.Checking.Skip());
        Assert.Equal(RunReason.OtherOwnerFormatUnknown, OwnerMucState.FormatUnknown.Skip());
    }

    // Every reason and refusal has its own words in the English file (no key falls back to the enum's name or to [key]).
    [Fact]
    public void Summary_NamesEveryReasonAndRefusal()
    {
        foreach (RunReason reason in Enum.GetValues(typeof(RunReason)))
        {
            var report = RunReport.ForCleanup(); report.Remain(reason);
            var words = EnglishText.Of(RunReport.Reason(reason));
            Assert.DoesNotContain("[", words);
            Assert.NotEqual("yavsos_refusal_check_failed", RunReport.Reason(reason).Key);
            if (reason != RunReason.ChestUnreadable) Assert.Contains("(" + words + ")", EnglishText.Of(report.Summary()));
        }
        foreach (TransferRefusal refusal in Enum.GetValues(typeof(TransferRefusal)))
        {
            if (refusal is TransferRefusal.None or TransferRefusal.Unconfirmed) continue;
            var report = RunReport.ForStash(); report.Refuse(refusal);
            var words = EnglishText.Of(RunReport.Reason(refusal));
            Assert.DoesNotContain("[", words);
            Assert.Contains("(" + words + ")", EnglishText.Of(report.Summary()));
            if (refusal is not (TransferRefusal.ProtocolMismatch or TransferRefusal.InvariantFailed))
                Assert.NotEqual("yavsos_refusal_check_failed", RunReport.Reason(refusal).Key);
        }
        // The same words share a key.
        Assert.Equal(RunReport.Reason(RunReason.NoCapacity), RunReport.Reason(TransferRefusal.NoCapacity));
        Assert.Equal(RunReport.Reason(RunReason.Unsupported), RunReport.Reason(TransferRefusal.Protected));
    }

    // SF3 (batch C): unreadable chests are counted as chests, apart from the stacks left in the inventory.
    [Fact]
    public void Summary_UnreadableChests_AreTheirOwnPart()
    {
        var report = RunReport.ForStash();
        report.Remain(RunReason.NoDestination, "Tin");
        for (var i = 0; i < 3; i++) report.Remain(RunReason.ChestUnreadable);
        Assert.Equal("Nothing stashed: 1 stays in your inventory (no chest accepts it) · YAVSOS couldn't read 3 nearby chests (see the log).",
            EnglishText.Of(report.Summary()));
        Assert.Contains(report.Detail().Lines, x => x.Label.Equals(Message.Of("yavsos_result_unreadable").Count(3)));
        var one = RunReport.ForCleanup(); one.Remain(RunReason.ChestUnreadable);
        Assert.Equal("Nothing moved: YAVSOS couldn't read 1 nearby chest (see the log).", EnglishText.Of(one.Summary()));
    }

    // B4.3: one stack split across two chests is one stack moved.
    [Fact]
    public void Moved_CountsASplitStackOnce()
    {
        var report = RunReport.ForCleanup(); var copper = Guid.NewGuid();
        report.Moved(30, item: "Copper", stack: copper); report.Moved(10, viaMuc: true, item: "Copper", stack: copper);
        report.Moved(5, item: "Tin", stack: Guid.NewGuid());
        Assert.Equal(2, report.MovedStacks); Assert.Equal(45, report.MovedUnits); Assert.Equal(1, report.MucMovedStacks);
        Assert.Equal("Moved 2 stacks (45 items), 1 via MultiUserChest.", EnglishText.Of(report.Summary()));
    }

    // D086 D10: the player text names the chest type AzuAutoStore keeps the item out of, once and without "chests".
    [Fact]
    public void Summary_AzuExcluded_NamesTheChestTypes()
    {
        var report = RunReport.ForCleanup();
        report.Remain(RunReason.AzuExcluded, "SwordBronze", "Reinforced chest");
        report.Remain(RunReason.AzuExcluded, "SwordIron", "Reinforced chest");
        Assert.Equal("Nothing moved: 2 stay in place (AzuAutoStore keeps it out of Reinforced chest).", EnglishText.Of(report.Summary()));
        report.Remain(RunReason.AzuExcluded, "Wood", "Wood chest");
        Assert.Contains("(AzuAutoStore keeps it out of Reinforced chest and Wood chest)", EnglishText.Of(report.Summary()));
        foreach (var type in new[] { "Black metal chest", "Barrel", "Crate" }) report.Remain(RunReason.AzuExcluded, "Wood", type);
        Assert.Contains("(AzuAutoStore keeps it out of Reinforced chest, Wood chest, Black metal chest and other chest types)", EnglishText.Of(report.Summary()));
        Assert.Equal(RunReport.MaximumExcludedTypes, report.ExcludedChestTypes.Count);
        var unnamed = RunReport.ForCleanup(); unnamed.Remain(RunReason.AzuExcluded, "Wood");
        Assert.Equal("Nothing moved: 1 stays in place (AzuAutoStore's exclusions keep it out).", EnglishText.Of(unnamed.Summary()));
    }

    // Inventory case 1: counts pick a plural form; nothing adds an "s" in code.
    [Fact]
    public void Summary_CountsPickPluralForms()
    {
        var report = RunReport.ForStash();
        report.Moved(1); report.Remain(RunReason.Held);
        Assert.Equal("Stashed 1 stack (1 item) · 1 stays in your inventory (held).", EnglishText.Of(report.Summary()));
        var moved = Assert.IsType<MessageList>(Assert.Single(report.Summary().Args).Value);
        Assert.Equal(ListKind.Parts, moved.Kind);
        var lead = Assert.IsType<Message>(moved.Items[0]);
        Assert.Equal(Message.Of("yavsos_result_stashed").With("stacks", Message.Of("yavsos_result_stacks").Count(1))
            .With("items", Message.Of("yavsos_result_items").Count(1)), lead);
    }

    [Fact]
    public void Kind_PicksTheTitle()
    {
        Assert.Equal("YAVSOS cleanup result", EnglishText.Of(RunReport.ForCleanup().Detail().Title));
        Assert.Equal("YAVSOS stash result", EnglishText.Of(RunReport.ForStash().Detail().Title));
        Assert.Equal("YAVSOS result for AzuAutoStore's stash key", EnglishText.Of(RunReport.ForStoreKey().Detail().Title));
        Assert.Equal((RunKind.Cleanup, RunKind.Stash, RunKind.StoreKey), (RunReport.ForCleanup().Kind, RunReport.ForStash().Kind, RunReport.ForStoreKey().Kind));
    }

    [Fact]
    public void Refuse_RejectsNonRefusals()
    {
        var report = RunReport.ForCleanup();
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.None));
        Assert.Throws<ArgumentException>(() => report.Refuse(TransferRefusal.Unconfirmed));
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Moved(0));
    }

    [Fact]
    public void OptedOutReason_QuotesTheTickBoxByItsKey()
    {
        // X52: the reason quotes the opt-out tick box through its own key, so a translated box and its reason can't drift apart.
        Assert.Equal(Message.Of("yavsos_reason_opted_out").With("option", Message.Of("yavsos_cleanup_optout")), RunReport.Reason(RunReason.OptedOut));
        Assert.Equal("\"Don't clean up this chest\" is ticked on its chest", EnglishText.Of(RunReport.Reason(RunReason.OptedOut)));
    }
}
