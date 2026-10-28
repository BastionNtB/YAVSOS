using System;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class DelegatedMoveObserverTests
{
    // MucAdd: 10 Copper leave the local chest (20 -> 10); the other player's chest had 5.
    private static DelegatedMoveObserver Add() => new(CleanupRoute.MucAdd, 10, 20, 5, 0, now: 0);

    [Fact]
    public void MucAdd_MovedOnlyOnceTheItemShowsInTheOtherChest()
    {
        var observer = Add();
        Assert.Equal(DelegatedMoveState.Pending, observer.Observe(0.1, 10, 5, 0, true));
        Assert.Equal(DelegatedMoveState.Pending, observer.Observe(2, 10, 5, 0, true));
        Assert.Equal(DelegatedMoveState.Moved, observer.Observe(3, 10, 15, 0, true));
        Assert.Equal(10, observer.MovedUnits);
        Assert.Equal(DelegatedMoveState.Moved, observer.Observe(4, 20, 5, 0, false)); // fixed once decided
    }

    [Fact]
    public void MucAdd_RefundIsRefused_AndPartialRefundMovesTheRest()
    {
        var refunded = Add();
        refunded.Observe(0.1, 10, 5, 0, true);
        Assert.Equal(DelegatedMoveState.Refused, refunded.Observe(30, 20, 5, 0, true));
        Assert.Equal(0, refunded.MovedUnits);

        var partial = Add();
        partial.Observe(0.1, 10, 5, 0, true);
        Assert.Equal(DelegatedMoveState.Pending, partial.Observe(1, 10, 9, 0, true)); // 4 arrived, refund not yet seen
        Assert.Equal(DelegatedMoveState.Moved, partial.Observe(2, 16, 9, 0, true));
        Assert.Equal(4, partial.MovedUnits);
    }

    // D093: a same-key direct move runs while the request is pending; its verified units follow the source baseline.
    [Fact]
    public void MucAdd_LaterDirectMoveOfTheSameKey_StillTellsARefundApart()
    {
        var refunded = Add();
        refunded.Observe(0.1, 10, 5, 0, true); // MultiUserChest took 10
        refunded.SourceTaken(6);               // a direct move stored 6 more into a local chest
        Assert.Equal(DelegatedMoveState.Pending, refunded.Observe(1, 4, 5, 0, true));
        Assert.Equal(DelegatedMoveState.Refused, refunded.Observe(30, 14, 5, 0, true)); // the 10 came back

        var partial = Add();
        partial.Observe(0.1, 10, 5, 0, true);
        partial.SourceTaken(6);
        Assert.Equal(DelegatedMoveState.Moved, partial.Observe(2, 10, 9, 0, true)); // 4 arrived, 6 refunded
        Assert.Equal(4, partial.MovedUnits);

        // Without the adjustment the same refund would read as nothing decided until the window ends.
        var unadjusted = Add();
        unadjusted.Observe(0.1, 10, 5, 0, true);
        Assert.Equal(DelegatedMoveState.Pending, unadjusted.Observe(30, 14, 5, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => unadjusted.SourceTaken(-1));
    }

    // D093: after an unconfirmed same-key move the source can't be read; only an arrival decides, never a refund.
    [Fact]
    public void MucAdd_UnknownSource_OnlyArrivalDecides()
    {
        var arrives = Add();
        arrives.Observe(0.1, 10, 5, 0, true);
        arrives.SourceUnknown();
        Assert.Equal(DelegatedMoveState.Pending, arrives.Observe(1, 20, 5, 0, true)); // looks like a refund: not trusted
        Assert.Equal(DelegatedMoveState.Moved, arrives.Observe(2, 0, 15, 0, true));

        var silent = Add();
        silent.Observe(0.1, 10, 5, 0, true);
        silent.SourceUnknown();
        Assert.Equal(DelegatedMoveState.Pending, silent.Observe(39, 20, 5, 0, true));
        Assert.Equal(DelegatedMoveState.Unconfirmed, silent.Observe(40, 20, 9, 0, true)); // a partial arrival can't be told apart
    }

    [Fact]
    public void MucAdd_SilenceOrSessionEndIsUnconfirmed()
    {
        var silent = Add();
        silent.Observe(0.1, 10, 5, 0, true);
        Assert.Equal(DelegatedMoveState.Pending, silent.Observe(39, 10, 5, 0, true));
        Assert.Equal(DelegatedMoveState.Unconfirmed, silent.Observe(40, 10, 5, 0, true));

        var ended = Add();
        ended.Observe(0.1, 10, 5, 0, true);
        Assert.Equal(DelegatedMoveState.Unconfirmed, ended.Observe(1, 10, 5, 0, false));
    }

    [Fact]
    public void MucRemove_ExpectedArrivalMoves_DifferentItemOrNothingIsUnconfirmed()
    {
        var moved = new DelegatedMoveObserver(CleanupRoute.MucRemove, 3, 3, 0, 7, now: 0);
        Assert.Equal(DelegatedMoveState.Pending, moved.Observe(1, 3, 0, 7, true));
        Assert.Equal(DelegatedMoveState.Moved, moved.Observe(2, 0, 3, 7, true));

        var different = new DelegatedMoveObserver(CleanupRoute.MucRemove, 3, 3, 0, 7, now: 0);
        Assert.Equal(DelegatedMoveState.Unconfirmed, different.Observe(1, 0, 0, 8, true));
        Assert.Contains("different", different.Detail);

        var nothing = new DelegatedMoveObserver(CleanupRoute.MucRemove, 3, 3, 0, 7, now: 0);
        Assert.Equal(DelegatedMoveState.Unconfirmed, nothing.Observe(41, 3, 0, 7, true));
    }

    [Fact]
    public void Constructor_RejectsDirectAndImpossibleCounts()
    {
        Assert.Throws<ArgumentException>(() => new DelegatedMoveObserver(CleanupRoute.Direct, 1, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelegatedMoveObserver(CleanupRoute.MucAdd, 5, 4, 0, 0, 0));
    }
}
