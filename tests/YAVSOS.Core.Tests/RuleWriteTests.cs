using System;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>X32 (D154): remote rule writes, the codec, the owner's checks, the rate limit and the requester's wait.</summary>
public sealed class RuleWriteTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static string Payload(ulong revision, string item = "Wood") => ContainerPolicyEnvelope.Serialize(revision,
        new ChestRules(Taxonomy, ChestRuleState.Configured, includes: new[] { item }));
    private static RuleWriteOwnerView Chest(string payload, bool loaded = true, bool owner = true, bool eligible = true, bool access = true,
        bool rate = true, bool optOut = false) => new RuleWriteOwnerView(loaded, owner, eligible, access, rate, payload, optOut);

    [Fact]
    public void Codec_RoundTripsRequestsAndReplies()
    {
        var request = RuleWriteRequest.WriteRule(7, -42, Payload(1), Payload(2, "Stone"));
        Assert.True(RuleWriteCodec.TryDecode(RuleWriteCodec.Encode(request), out var back));
        Assert.Equal(RuleWriteRequest.Protocol, back!.Version); Assert.Equal(7, back.RequestId); Assert.Equal(-42, back.PlayerId);
        Assert.Equal(RuleWriteOperation.WriteRule, back.Operation); Assert.Equal(request.Expected, back.Expected); Assert.Equal(request.Value, back.Value);
        var optOut = RuleWriteRequest.SetCleanupOptOut(8, 1, true);
        Assert.True(RuleWriteCodec.TryDecode(RuleWriteCodec.Encode(optOut), out back));
        Assert.Equal(RuleWriteOperation.SetCleanupOptOut, back!.Operation); Assert.True(back.OptOut); Assert.Empty(back.Expected);
        Assert.True(RuleWriteCodec.TryDecodeReply(RuleWriteCodec.EncodeReply(9, RuleWriteOutcome.RuleChanged), out var id, out var outcome));
        Assert.Equal(9, id); Assert.Equal(RuleWriteOutcome.RuleChanged, outcome);
    }

    [Fact]
    public void Codec_RefusesMalformedOversizedAndRequesterOnlyOutcomes()
    {
        var bytes = RuleWriteCodec.Encode(RuleWriteRequest.WriteRule(1, 1, "", Payload(1)));
        Assert.False(RuleWriteCodec.TryDecode(null, out _));
        Assert.False(RuleWriteCodec.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), out _));
        Assert.False(RuleWriteCodec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _));
        Assert.False(RuleWriteCodec.TryDecode(new byte[RuleWriteCodec.MaximumRequestBytes + 1], out _));
        Assert.Throws<System.IO.InvalidDataException>(() => RuleWriteCodec.Encode(RuleWriteRequest.WriteRule(1, 1, "", new string('a', TransferLimits.MaximumPolicyBytes + 1))));
        var reply = RuleWriteCodec.EncodeReply(1, RuleWriteOutcome.NoAnswer);
        Assert.False(RuleWriteCodec.TryDecodeReply(reply, out _, out _));
        Assert.False(RuleWriteCodec.TryDecodeReply(new byte[3], out _, out _));
    }

    [Fact]
    public void Codec_OtherProtocolDecodesAsUnsupported()
    {
        var bytes = RuleWriteCodec.Encode(RuleWriteRequest.WriteRule(5, 1, "", Payload(1)));
        BitConverter.GetBytes(2).CopyTo(bytes, 0);
        Assert.True(RuleWriteCodec.TryDecode(bytes, out var request));
        Assert.Equal(2, request!.Version); Assert.Equal(5, request.RequestId);
        Assert.Equal(RuleWriteOutcome.Unsupported, RuleWriteOwner.Decide(request, Chest("")).Outcome);
    }

    [Fact]
    public void Owner_ChecksInOrder()
    {
        var request = RuleWriteRequest.WriteRule(1, 1, "", Payload(1));
        Assert.Equal(RuleWriteOutcome.NotLoaded, RuleWriteOwner.Decide(request, Chest("", loaded: false, owner: false)).Outcome);
        Assert.Equal(RuleWriteOutcome.NotOwner, RuleWriteOwner.Decide(request, Chest("", owner: false, eligible: false)).Outcome);
        Assert.Equal(RuleWriteOutcome.NotEligible, RuleWriteOwner.Decide(request, Chest("", eligible: false, access: false)).Outcome);
        Assert.Equal(RuleWriteOutcome.AccessDenied, RuleWriteOwner.Decide(request, Chest("", access: false, rate: false)).Outcome);
        Assert.Equal(RuleWriteOutcome.Busy, RuleWriteOwner.Decide(request, Chest("", rate: false)).Outcome);
        var write = RuleWriteOwner.Decide(request, Chest(""));
        Assert.Equal(RuleWriteOutcome.Done, write.Outcome); Assert.True(write.Write); Assert.Equal(Payload(1), write.Payload);
    }

    [Fact]
    public void Owner_CompareAndSetByDigest_AndDoneWhenAlreadyThere()
    {
        var seen = Payload(1); var value = Payload(2, "Stone");
        var request = RuleWriteRequest.WriteRule(1, 1, seen, value);
        Assert.True(RuleWriteOwner.Decide(request, Chest(seen)).Write);
        // Someone else's change: refused, and the requester reloads.
        Assert.Equal(RuleWriteOutcome.RuleChanged, RuleWriteOwner.Decide(request, Chest(Payload(2, "Copper"))).Outcome);
        // A resend after a lost reply finds its own value already there.
        var again = RuleWriteOwner.Decide(request, Chest(value));
        Assert.Equal(RuleWriteOutcome.Done, again.Outcome); Assert.False(again.Write);
    }

    [Fact]
    public void Owner_ValueMustBeANewerReadableEnvelopeOrEmpty()
    {
        var seen = Payload(5);
        Assert.Equal(RuleWriteOutcome.Invalid, RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, Payload(5, "Stone")), Chest(seen)).Outcome);
        Assert.Equal(RuleWriteOutcome.Invalid, RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, Payload(4, "Stone")), Chest(seen)).Outcome);
        Assert.Equal(RuleWriteOutcome.Invalid, RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, "{not an envelope"), Chest(seen)).Outcome);
        Assert.True(RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, Payload(6, "Stone")), Chest(seen)).Write);
        var clear = RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, ""), Chest(seen));
        Assert.True(clear.Write); Assert.Equal("", clear.Payload);
    }

    [Fact]
    public void Owner_UnknownOperationIsUnsupported_AndAnEmptyDigestIsAChangedRule()
    {
        var request = RuleWriteRequest.WriteRule(1, 1, "", Payload(1));
        var bytes = RuleWriteCodec.Encode(request);
        bytes[4 + 8 + 8] = 9; // The operation byte.
        Assert.True(RuleWriteCodec.TryDecode(bytes, out var unknown));
        Assert.Equal(RuleWriteOutcome.Unsupported, RuleWriteOwner.Decide(unknown!, Chest("")).Outcome);

        // A write request without a digest (only a modified game sends one) never matches the current rule.
        var noDigest = RuleWriteCodec.Encode(RuleWriteRequest.SetCleanupOptOut(2, 1, false));
        noDigest[4 + 8 + 8] = (byte)RuleWriteOperation.WriteRule;
        Assert.True(RuleWriteCodec.TryDecode(noDigest, out var blind));
        Assert.Equal(RuleWriteOutcome.RuleChanged, RuleWriteOwner.Decide(blind!, Chest(Payload(1))).Outcome);

        // A negative value length doesn't decode.
        var negative = RuleWriteCodec.Encode(request);
        var at = 4 + 8 + 8 + 1 + 1 + RuleWriteRequest.DigestBytes;
        BitConverter.GetBytes(-1).CopyTo(negative, at);
        Assert.False(RuleWriteCodec.TryDecode(negative, out _));
    }

    [Fact]
    public void Owner_AnUnreadableCurrentRuleCountsAsRevisionZero()
    {
        const string unreadable = "{not an envelope";
        var decision = RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, unreadable, Payload(1)), Chest(unreadable));
        Assert.True(decision.Write); Assert.Equal(Payload(1), decision.Payload);
    }

    [Fact]
    public void Owner_PassesARuleFromAnotherVersionThroughUnread()
    {
        // The rules text is another version's (not schema 1 XML this version reads), inside a schema-1 envelope.
        var seen = Payload(3);
        var other = seen.Replace("<rules", "<rulesFromLater");
        Assert.True(ContainerPolicyEnvelope.TryRevision(other, out var revision)); Assert.Equal(3UL, revision);
        var newer = ContainerPolicyEnvelope.TryRestore(seen, seen, other, out var restored) == TransferRefusal.None ? restored! : "";
        var decision = RuleWriteOwner.Decide(RuleWriteRequest.WriteRule(1, 1, seen, newer), Chest(seen));
        Assert.True(decision.Write); Assert.Equal(newer, decision.Payload);
    }

    [Fact]
    public void Owner_OptOutHasNoCompareAndSet()
    {
        var on = RuleWriteOwner.Decide(RuleWriteRequest.SetCleanupOptOut(1, 1, true), Chest(Payload(1)));
        Assert.True(on.Write); Assert.True(on.OptOut);
        var already = RuleWriteOwner.Decide(RuleWriteRequest.SetCleanupOptOut(1, 1, true), Chest(Payload(1), optOut: true));
        Assert.Equal(RuleWriteOutcome.Done, already.Outcome); Assert.False(already.Write);
        Assert.Equal(RuleWriteOutcome.AccessDenied, RuleWriteOwner.Decide(RuleWriteRequest.SetCleanupOptOut(1, 1, true), Chest("", access: false)).Outcome);
    }

    [Fact]
    public void Rate_AllowsTwentyASecondPerPeer()
    {
        var rate = new RuleWriteRate();
        for (var i = 0; i < RuleWriteRate.PerSecond; i++) Assert.True(rate.Allow(1, 10));
        Assert.False(rate.Allow(1, 10));
        Assert.True(rate.Allow(2, 10)); // Another player's bucket.
        Assert.True(rate.Allow(1, 10.06)); // About one token back after 50 ms.
        Assert.False(rate.Allow(1, 10.06));
        for (var i = 0; i < RuleWriteRate.PerSecond; i++) Assert.True(rate.Allow(1, 12));
    }

    [Fact]
    public void Rate_StaysBoundedWithManyPeers()
    {
        var rate = new RuleWriteRate();
        for (var peer = 0; peer < RuleWriteRate.MaximumPeers * 3; peer++)
        {
            Assert.True(rate.Allow(peer, peer * 0.001));
            Assert.InRange(rate.Peers, 1, RuleWriteRate.MaximumPeers);
        }
    }

    [Fact]
    public void Tracker_DoneAnswerWaitsToSeeTheValue_AndDropsStrangers()
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, target: 100, RuleWriteKind.Edit, "a", "b", false, 0, now: 0);
        Assert.True(tracker.Blocking(1)); Assert.False(tracker.CanStart(1));
        Assert.Null(tracker.Reply(sender: 200, write.RequestId, RuleWriteOutcome.Done, 100, 0.1, out _)); // Not the game it went to.
        Assert.Null(tracker.Reply(100, write.RequestId + 50, RuleWriteOutcome.Done, 100, 0.1, out _)); // No such request.
        // Review finding 1: the owner's answer arrives before the synced rule, so the chest stays blocked until it shows.
        Assert.Null(tracker.Reply(100, write.RequestId, RuleWriteOutcome.Done, 100, 0.1, out var resend));
        Assert.Null(resend); Assert.Equal(0.1, write.AnsweredAt); Assert.True(tracker.Blocking(1));
        Assert.Empty(tracker.Step(0.3, _ => false));
        Assert.Empty(tracker.Step(5, _ => false)); // Answered: no "No answer yet" at 5 s.
        var settled = tracker.Step(5.05, w => w.Value == "b");
        Assert.Single(settled); Assert.Equal(RuleWriteOutcome.Done, settled[0].Outcome); Assert.True(settled[0].Final);
        Assert.Equal(0, tracker.Count);
        Assert.Null(tracker.Reply(100, write.RequestId, RuleWriteOutcome.Done, 100, 7, out _)); // A late duplicate.
    }

    [Fact]
    public void Tracker_DoneAnswerSettlesAfterTheConfirmWindowEvenUnseen()
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        tracker.Reply(100, write.RequestId, RuleWriteOutcome.Done, 100, 1, out _);
        Assert.Empty(tracker.Step(1 + RuleWriteTracker<int>.ConfirmedSeconds - 0.01, _ => false));
        var settled = tracker.Step(1 + RuleWriteTracker<int>.ConfirmedSeconds, _ => null);
        Assert.Equal(RuleWriteOutcome.Done, Assert.Single(settled).Outcome);
    }

    [Fact]
    public void Tracker_RefusalsSettleAtOnce()
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        var settled = tracker.Reply(100, write.RequestId, RuleWriteOutcome.RuleChanged, 100, 0.1, out _);
        Assert.Equal(RuleWriteOutcome.RuleChanged, settled!.Value.Outcome); Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Tracker_SeeingTheValueCountsAsSaved()
    {
        var tracker = new RuleWriteTracker<int>();
        tracker.Start(1, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        Assert.Empty(tracker.Step(1, _ => false));
        Assert.Empty(tracker.Step(1.5, _ => null)); // The chest can't be read here: not seen.
        var settled = tracker.Step(2, write => write.Value == "b");
        Assert.Single(settled); Assert.Equal(RuleWriteOutcome.Done, settled[0].Outcome); Assert.Equal(0, tracker.Count);
        // The opt-out is seen by its bool.
        tracker.Start(2, 100, RuleWriteKind.OptOut, "", "", true, 0, 3);
        Assert.Equal(RuleWriteKind.OptOut, Assert.Single(tracker.Step(3.1, write => write.OptOut)).Write.Kind);
    }

    [Fact]
    public void Tracker_StopsBlockingAfterFiveSeconds_WatchesUntilForty()
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, 100, RuleWriteKind.Undo, "a", "b", false, 0, 0);
        Assert.Empty(tracker.Step(4.9, _ => false));
        var unanswered = tracker.Step(5, _ => false);
        Assert.Single(unanswered); Assert.Equal(RuleWriteOutcome.NoAnswer, unanswered[0].Outcome); Assert.False(unanswered[0].Final);
        Assert.False(tracker.Blocking(1)); Assert.Equal(1, tracker.Count); Assert.True(write.WasUnanswered);
        Assert.Empty(tracker.Step(20, _ => false)); // NoAnswer is said once.
        // Seen between 5 s and 40 s: saved, and the panel clears its "No answer yet".
        var seen = Assert.Single(tracker.Step(21, _ => true));
        Assert.Equal(RuleWriteOutcome.Done, seen.Outcome);
        Assert.True(RuleWriteEffect.Of(seen).ClearMessage);

        // A late answer within the window settles it too (once the value is seen or confirmed).
        var late = tracker.Start(3, 100, RuleWriteKind.Edit, "a", "c", false, 0, 0);
        tracker.Step(5, _ => false);
        Assert.Null(tracker.Reply(100, late.RequestId, RuleWriteOutcome.Done, 100, 10, out _));
        Assert.Equal(RuleWriteOutcome.Done, Assert.Single(tracker.Step(10 + RuleWriteTracker<int>.ConfirmedSeconds, _ => false)).Outcome);

        tracker.Start(2, 100, RuleWriteKind.Edit, "a", "c", false, 0, 0);
        tracker.Step(5, _ => false);
        var gave = tracker.Step(RuleWriteTracker<int>.WatchSeconds, _ => false);
        Assert.Single(gave); Assert.Equal(RuleWriteOutcome.GaveUp, gave[0].Outcome); Assert.True(gave[0].Final); Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void Tracker_AnUnansweredWriteIsReplacedByANewOne_OrForgottenOnALocalWrite()
    {
        var tracker = new RuleWriteTracker<int>();
        var old = tracker.Start(1, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        tracker.Step(5, _ => false);
        Assert.True(tracker.CanStart(1)); // Review finding 3: no 35 s of "still saving".
        var next = tracker.Start(1, 300, RuleWriteKind.Edit, "a", "c", false, 0, 6);
        Assert.Same(next, tracker.For(1)); Assert.Equal(1, tracker.Count);
        Assert.Null(tracker.Reply(100, old.RequestId, RuleWriteOutcome.Done, 300, 7, out _)); // The replaced write's answer.
        tracker.Step(11, _ => false);
        tracker.Forget(1); // This game owns the chest now and wrote it itself.
        Assert.Equal(0, tracker.Count);
        // A blocking write isn't forgotten.
        tracker.Start(2, 100, RuleWriteKind.Edit, "a", "b", false, 0, 20);
        tracker.Forget(2); Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Tracker_ResendsOnceToANewOwner()
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        var first = write.RequestId;
        Assert.Null(tracker.Reply(100, first, RuleWriteOutcome.NotOwner, currentOwner: 300, 0.1, out var resend));
        Assert.Same(write, resend); Assert.Equal(300, write.Target); Assert.NotEqual(first, write.RequestId);
        Assert.Null(tracker.Reply(100, first, RuleWriteOutcome.Done, 300, 0.2, out _)); // The old owner's late answer.
        var settled = tracker.Reply(300, write.RequestId, RuleWriteOutcome.NotOwner, currentOwner: 400, 0.3, out resend);
        Assert.Null(resend); Assert.Equal(RuleWriteOutcome.NotOwner, settled!.Value.Outcome);
        // No owner, or the same owner again: no resend.
        var other = tracker.Start(2, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        Assert.Equal(RuleWriteOutcome.NotOwner, tracker.Reply(100, other.RequestId, RuleWriteOutcome.NotOwner, 0, 0.1, out resend)!.Value.Outcome);
        Assert.Null(resend);
        var same = tracker.Start(3, 100, RuleWriteKind.Edit, "a", "b", false, 0, 0);
        Assert.Equal(RuleWriteOutcome.NotOwner, tracker.Reply(100, same.RequestId, RuleWriteOutcome.NotOwner, 100, 0.1, out resend)!.Value.Outcome);
        Assert.Null(resend);
    }

    [Fact]
    public void Tracker_BoundsWaitingWrites_AndClearsOnLeavingTheWorld()
    {
        var tracker = new RuleWriteTracker<int>();
        for (var chest = 0; chest < RuleWriteTracker<int>.MaximumWaiting; chest++) tracker.Start(chest, 100, RuleWriteKind.Edit, "", "x", false, 0, 0);
        Assert.False(tracker.CanStart(99));
        Assert.Throws<InvalidOperationException>(() => tracker.Start(99, 100, RuleWriteKind.Edit, "", "x", false, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleWriteTracker<int>().Start(1, 0, RuleWriteKind.Edit, "", "x", false, 0, 0));
        tracker.Clear(); Assert.Equal(0, tracker.Count); Assert.True(tracker.CanStart(99));
    }

    [Theory]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.Done, true, false, null)]
    [InlineData(RuleWriteKind.Undo, RuleWriteOutcome.Done, true, false, null)]
    [InlineData(RuleWriteKind.Redo, RuleWriteOutcome.Done, true, false, null)]
    [InlineData(RuleWriteKind.OptOut, RuleWriteOutcome.Done, false, false, null)]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.RuleChanged, false, false, "yavsos_edit_rule_changed")]
    [InlineData(RuleWriteKind.Undo, RuleWriteOutcome.RuleChanged, false, true, "yavsos_edit_undo_changed")]
    [InlineData(RuleWriteKind.Redo, RuleWriteOutcome.RuleChanged, false, true, "yavsos_edit_redo_changed")]
    [InlineData(RuleWriteKind.Undo, RuleWriteOutcome.NoAnswer, false, false, "yavsos_edit_no_answer")]
    [InlineData(RuleWriteKind.Undo, RuleWriteOutcome.GaveUp, false, false, "yavsos_edit_unconfirmed")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.Unconfirmed, false, false, "yavsos_edit_unconfirmed")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.NotOwner, false, false, "yavsos_edit_try_again")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.NotLoaded, false, false, "yavsos_edit_try_again")]
    [InlineData(RuleWriteKind.OptOut, RuleWriteOutcome.AccessDenied, false, false, "yavsos_edit_no_access")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.NotEligible, false, false, "yavsos_edit_not_eligible")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.Busy, false, false, "yavsos_edit_busy")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.Invalid, false, false, "yavsos_edit_remote_refused")]
    [InlineData(RuleWriteKind.Edit, RuleWriteOutcome.Unsupported, false, false, "yavsos_edit_remote_refused")]
    public void Effect_RecordsWhatWasSaved_AndSaysTheRest(RuleWriteKind kind, RuleWriteOutcome outcome, bool record, bool drop, string? key)
    {
        var tracker = new RuleWriteTracker<int>();
        var write = tracker.Start(1, 100, kind, "a", "b", false, 0, 0);
        var settled = outcome == RuleWriteOutcome.Done ? Assert.Single(tracker.Step(1, _ => true))
            : outcome is RuleWriteOutcome.NoAnswer or RuleWriteOutcome.GaveUp ? Assert.Single(tracker.Step(outcome == RuleWriteOutcome.NoAnswer ? 5 : 40, _ => false))
            : tracker.Reply(100, write.RequestId, outcome, 0, 0.1, out _)!.Value;
        var effect = RuleWriteEffect.Of(settled);
        Assert.Equal((record, drop, key), (effect.Record, effect.DropRecord, effect.MessageKey));
    }

    [Fact]
    public void Digest_IsSha256OfUtf8()
    {
        var digest = RuleWriteCodec.Digest("é");
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(sha.ComputeHash(Encoding.UTF8.GetBytes("é")), digest);
        Assert.True(RuleWriteCodec.SameDigest(digest, RuleWriteCodec.Digest("é")));
        Assert.False(RuleWriteCodec.SameDigest(digest, RuleWriteCodec.Digest("e")));
    }
}
