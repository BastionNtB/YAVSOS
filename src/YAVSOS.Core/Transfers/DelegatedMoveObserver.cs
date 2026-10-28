using System;

namespace YAVSOS.Core.Transfers;

public enum DelegatedMoveState { Pending, Moved, Refused, Unconfirmed }

/// <summary>
/// P4-03 (D043, D057 O5): judges a move delegated to MultiUserChest from what the local peer can see. It counts the
/// moved identity in both chests (one of them a replica of another player's chest) before the request and on every
/// later observation. A move counts as moved only once the item shows up in the other chest; MultiUserChest's own
/// refund counts as refused; anything else after the window is unconfirmed. Never retried, replayed or refunded (D041).
/// </summary>
public sealed class DelegatedMoveObserver
{
    public const double DefaultWindowSeconds = 40;
    public CleanupRoute Route { get; }
    public int Units { get; }
    public DelegatedMoveState State { get; private set; } = DelegatedMoveState.Pending;
    /// <summary>Units seen arriving in the other chest; with a partial refund the rest stayed in place.</summary>
    public int MovedUnits { get; private set; }
    public string Detail { get; private set; } = "";
    private readonly int destinationBefore, otherDestinationBefore;
    private int sourceBefore;
    private bool sourceUnknown;
    private readonly double deadline;

    /// <param name="sourceBefore">Units of the identity in the source before the request.</param>
    /// <param name="destinationBefore">Units of the identity in the destination before the request.</param>
    /// <param name="otherDestinationBefore">Units of every other identity in the destination before the request.</param>
    public DelegatedMoveObserver(CleanupRoute route, int units, int sourceBefore, int destinationBefore, int otherDestinationBefore,
        double now, double windowSeconds = DefaultWindowSeconds)
    {
        if (route == CleanupRoute.Direct) throw new ArgumentException("Direct moves are verified synchronously.", nameof(route));
        if (units < 1 || sourceBefore < units || destinationBefore < 0 || otherDestinationBefore < 0) throw new ArgumentOutOfRangeException(nameof(units));
        if (double.IsNaN(now) || double.IsInfinity(now) || windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(now));
        Route = route; Units = units; this.sourceBefore = sourceBefore; this.destinationBefore = destinationBefore;
        this.otherDestinationBefore = otherDestinationBefore; deadline = now + windowSeconds;
    }

    /// <summary>
    /// D093: a later direct move of the same stack key, verified, took <paramref name="units"/> out of the same source while
    /// this request was pending. The source baseline follows it, so the refund check still reads this request alone.
    /// </summary>
    public void SourceTaken(int units)
    {
        if (units < 0) throw new ArgumentOutOfRangeException(nameof(units));
        sourceBefore -= units;
    }

    /// <summary>
    /// D093: the source changed by an amount this observer can't know (a later same-key move was unconfirmed). From now on
    /// only an arrival counts; a refund or partial arrival can no longer be told apart and ends unconfirmed.
    /// </summary>
    public void SourceUnknown() => sourceUnknown = true;

    /// <summary>One observation of both chests. Returns the state, which stays fixed once it is not Pending.</summary>
    public DelegatedMoveState Observe(double now, int source, int destination, int otherDestination, bool sessionCurrent)
    {
        if (State != DelegatedMoveState.Pending) return State;
        if (!sessionCurrent) return Finish(DelegatedMoveState.Unconfirmed, 0, "the session ended while MultiUserChest was moving it");
        var arrived = Math.Max(0, destination - destinationBefore);
        var left = sourceBefore - source; // units gone from the source
        if (Route == CleanupRoute.MucAdd)
        {
            // MultiUserChest takes the item out of the local source first; the owner then adds it, or the rest is refunded.
            if (left > 0) taken = true;
            if (arrived >= Units) return Finish(DelegatedMoveState.Moved, Units, "arrived");
            if (sourceUnknown) { if (now >= deadline) return Finish(DelegatedMoveState.Unconfirmed, 0, $"after {DefaultWindowSeconds:0} s: destination +{arrived}; another move changed the source"); return State; }
            if (taken && left <= 0 && arrived == 0) return Finish(DelegatedMoveState.Refused, 0, "refunded by MultiUserChest");
            if (taken && arrived > 0 && left == arrived) return Finish(DelegatedMoveState.Moved, arrived, "partly arrived; the rest was refunded");
        }
        else
        {
            // The owner removes the slot from its chest; the item then arrives in the local destination.
            if (otherDestination > otherDestinationBefore) return Finish(DelegatedMoveState.Unconfirmed, 0, "a different item arrived");
            if (arrived >= Units) return Finish(DelegatedMoveState.Moved, Units, "arrived");
        }
        if (now >= deadline)
            return Finish(DelegatedMoveState.Unconfirmed, 0, $"after {DefaultWindowSeconds:0} s: source −{left}, destination +{arrived}");
        return State;
    }

    private bool taken;

    private DelegatedMoveState Finish(DelegatedMoveState state, int moved, string detail)
    { State = state; MovedUnits = moved; Detail = detail; return state; }
}
