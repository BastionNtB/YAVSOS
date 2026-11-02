using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// X14: the result of one press of AzuAutoStore's store key, judged by arrival, as YAVSOS's own stash judges a
/// MultiUserChest move (D043, D087). Azu counts a store into a chest another player's game runs as done when it sends
/// the request; here units count as stored only once the destination shows them, within <see cref="WindowSeconds"/>,
/// and are unconfirmed otherwise. Azu sends every request at once, so a refund can't be told apart from the inventory
/// (unlike D093's one request per stack key): only arrival decides. Another player's deposit of the same item into the
/// same chest within the window also counts as arrival (as A04). Read only: nothing is moved, retried or refunded.
/// </summary>
public sealed class StoreKeyWatch
{
    public const double WindowSeconds = DelegatedMoveObserver.DefaultWindowSeconds;

    private sealed class Part { internal Guid Stack; internal int Units; }
    private sealed class Entry
    {
        internal string Destination = "", Item = ""; internal bool ViaMuc; internal int Baseline, Claimed; internal double Deadline;
        internal readonly List<Part> Parts = new(); internal bool Done;
    }

    private readonly List<Entry> entries = new();

    /// <summary>"Store key: stored 4 stacks (63 items) · 1 unconfirmed".</summary>
    public CleanupReport Report { get; } = CleanupReport.ForStoreKey();

    /// <summary>Whether some stores still wait for their arrival.</summary>
    public bool Pending => entries.Any(x => !x.Done);

    /// <summary>
    /// One store of <paramref name="units"/> of <paramref name="item"/> into <paramref name="destination"/>, from stack
    /// <paramref name="stack"/>. <paramref name="before"/> is the destination's count of the item before the store, or null
    /// when the destination can't be read; such a store is unconfirmed at once.
    /// </summary>
    public void Stored(string destination, string item, int units, int? before, bool viaMuc, Guid stack, double now)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (units < 1) return;
        if (before == null) { Report.Unconfirm(viaMuc, item); return; }
        if (before < 0 || double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(before));
        // Several stores of one item into one chest share the count from before the first.
        var entry = entries.FirstOrDefault(x => !x.Done && x.Destination == destination && x.Item == item);
        if (entry == null) entries.Add(entry = new Entry { Destination = destination, Item = item, ViaMuc = viaMuc, Baseline = before.Value });
        entry.Claimed += units; entry.Deadline = now + WindowSeconds;
        var part = entry.Parts.Find(x => x.Stack == stack);
        if (part == null) entry.Parts.Add(new Part { Stack = stack, Units = units }); else part.Units += units;
    }

    /// <summary>
    /// Looks at every waiting destination: <paramref name="count"/> gives its count of the item now, or null when it is
    /// gone. Returns true once nothing waits.
    /// </summary>
    public bool Observe(double now, Func<string, string, int?> count)
    {
        if (count == null) throw new ArgumentNullException(nameof(count));
        foreach (var entry in entries.Where(x => !x.Done))
        {
            var seen = count(entry.Destination, entry.Item);
            var arrived = seen == null ? 0 : Math.Max(0, seen.Value - entry.Baseline);
            if (seen != null && arrived < entry.Claimed && now < entry.Deadline) continue;
            Finish(entry, Math.Min(arrived, entry.Claimed));
        }
        return !Pending;
    }

    // Arrived units go to the stacks in store order; what is left of each stack is unconfirmed.
    private void Finish(Entry entry, int arrived)
    {
        entry.Done = true;
        foreach (var part in entry.Parts)
        {
            var moved = Math.Min(arrived, part.Units); arrived -= moved;
            if (moved > 0) Report.Moved(moved, entry.ViaMuc, entry.Item, part.Stack);
            if (moved < part.Units) Report.Unconfirm(entry.ViaMuc, entry.Item);
        }
    }
}
