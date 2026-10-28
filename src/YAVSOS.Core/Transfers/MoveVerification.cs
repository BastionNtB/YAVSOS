using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Transfers;

/// <summary>What recapture shows after one direct move, compared with the captures before it.</summary>
public readonly struct DirectMoveObservation
{
    /// <summary>Units of the moved stack key gone from the source.</summary>
    public int Decrease { get; }
    /// <summary>Units of the moved stack key added to the destination.</summary>
    public int Increase { get; }
    /// <summary>Every other stack key kept its count on both sides.</summary>
    public bool OthersUnchanged { get; }
    /// <summary>The planned destination cell now holds the moved stack key.</summary>
    public bool CellHoldsKey { get; }
    /// <summary>Neither inventory changed at all: the game refused the move, so nothing moved.</summary>
    public bool NothingChanged { get; }
    internal DirectMoveObservation(int decrease, int increase, bool others, bool cell, bool nothing)
    { Decrease = decrease; Increase = increase; OthersUnchanged = others; CellHoldsKey = cell; NothingChanged = nothing; }
}

/// <summary>
/// D07: the conservation checks both runners (stash and cleanup) apply to a move, by stack key. Stackables are counted
/// by stack key (D061 Q11), so a stack that merges into one crafted by someone else still counts. Pure and Unity-free.
/// </summary>
public static class MoveVerification
{
    /// <summary>The stack key of the planned stack, or its identity when the stack is gone.</summary>
    public static string MergeKey(TransferInventorySnapshot snapshot, Guid handle, TransferItemIdentity identity)
    {
        if (snapshot == null || identity == null) throw new ArgumentNullException(nameof(snapshot));
        return snapshot.Stacks.FirstOrDefault(x => x.Handle == handle)?.MergeKey ?? identity.Fingerprint;
    }

    public static Dictionary<string, int> Totals(TransferInventorySnapshot snapshot)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stack in snapshot.Stacks) totals[stack.MergeKey] = Get(totals, stack.MergeKey) + stack.Quantity;
        return totals;
    }

    public static int Get(IReadOnlyDictionary<string, int> totals, string key) => totals.TryGetValue(key, out var value) ? value : 0;

    public static bool SameExcept(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after, string key)
    {
        foreach (var row in before) if (row.Key != key && Get(after, row.Key) != row.Value) return false;
        foreach (var row in after) if (row.Key != key && Get(before, row.Key) != row.Value) return false;
        return true;
    }

    /// <summary>Compares the captures around one direct move of stack key <paramref name="key"/> into cell (x, y).</summary>
    public static DirectMoveObservation Observe(TransferInventorySnapshot sourceBefore, TransferInventorySnapshot destinationBefore,
        TransferInventorySnapshot sourceAfter, TransferInventorySnapshot destinationAfter, string key, int x, int y)
    {
        if (sourceBefore == null || destinationBefore == null || sourceAfter == null || destinationAfter == null || key == null)
            throw new ArgumentNullException(nameof(sourceBefore));
        var sb = Totals(sourceBefore); var sa = Totals(sourceAfter); var db = Totals(destinationBefore); var da = Totals(destinationAfter);
        var cell = destinationAfter.Stacks.FirstOrDefault(s => s.X == x && s.Y == y);
        return new DirectMoveObservation(Get(sb, key) - Get(sa, key), Get(da, key) - Get(db, key),
            SameExcept(sb, sa, key) && SameExcept(db, da, key), cell != null && StringComparer.Ordinal.Equals(cell.MergeKey, key),
            StringComparer.Ordinal.Equals(sourceBefore.ContentFingerprint, sourceAfter.ContentFingerprint) &&
            StringComparer.Ordinal.Equals(destinationBefore.ContentFingerprint, destinationAfter.ContentFingerprint));
    }

    /// <summary>The counts a <see cref="DelegatedMoveObserver"/> compares: the key in the source and destination, and everything else in the destination.</summary>
    public static (int Source, int Destination, int OtherDestination) Counts(TransferInventorySnapshot source, TransferInventorySnapshot destination, string key)
    {
        if (source == null || destination == null || key == null) throw new ArgumentNullException(nameof(source));
        var destinationTotals = Totals(destination);
        return (Get(Totals(source), key), Get(destinationTotals, key), destinationTotals.Where(x => x.Key != key).Sum(x => x.Value));
    }
}
