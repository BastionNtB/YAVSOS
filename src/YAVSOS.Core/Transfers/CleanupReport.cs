using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// Totals of one cleanup or stash run and its one-line summary. Moved counts only verified units; refused moves moved
/// nothing; unconfirmed moves may or may not have run and are never retried, replayed or refunded (D041).
/// MultiUserChest outcomes are counted separately so they are attributed to it (D043).
/// </summary>
public sealed class CleanupReport
{
    private readonly Dictionary<CleanupReason, int> remaining = new();
    private readonly Dictionary<TransferRefusal, int> refused = new();
    public int MovedStacks { get; private set; }
    public int MovedUnits { get; private set; }
    public int MucMovedStacks { get; private set; }
    public int Unconfirmed { get; private set; }
    public int MucUnconfirmed { get; private set; }
    public int Refused => refused.Values.Sum();
    public int Remaining => remaining.Values.Sum();
    public int Count(CleanupReason reason) => remaining.TryGetValue(reason, out var count) ? count : 0;
    public int Count(TransferRefusal reason) => refused.TryGetValue(reason, out var count) ? count : 0;
    private readonly string title, verb, left;

    /// <summary>A cleanup report; <see cref="ForStash"/> words the same totals for a stash run.</summary>
    public CleanupReport() : this("Cleanup", "moved", "left in place") { }
    private CleanupReport(string title, string verb, string left) { this.title = title; this.verb = verb; this.left = left; }
    /// <summary>"Stash: stored 4 stacks (63 items) · 2 left in your inventory (no room)".</summary>
    public static CleanupReport ForStash() => new("Stash", "stored", "left in your inventory");

    /// <param name="item">The item's identity (prefab) for the result view (P6-04); null counts it unnamed.</param>
    /// <summary>D086 D9: chests in range, and how many of the nearest a run could handle; the summary says when some weren't.</summary>
    public void Range(int inRange, int handled)
    {
        if (inRange < 0 || handled < 0 || handled > inRange) throw new ArgumentOutOfRangeException(nameof(handled));
        ChestsInRange = inRange; ChestsHandled = handled;
    }
    public int ChestsInRange { get; private set; }
    public int ChestsHandled { get; private set; }

    public void Remain(CleanupReason reason, string? item = null)
    { remaining[reason] = Count(reason) + 1; Name(ResultKind.Remaining, (int)reason, item, 1); }
    public void Refuse(TransferRefusal reason, string? item = null)
    {
        if (reason == TransferRefusal.None || reason == TransferRefusal.Unconfirmed) throw new ArgumentException("Not a refusal.", nameof(reason));
        refused[reason] = Count(reason) + 1; Name(ResultKind.Refused, (int)reason, item, 1);
    }
    public void Moved(int units, bool viaMuc = false, string? item = null)
    {
        if (units < 1) throw new ArgumentOutOfRangeException(nameof(units));
        MovedStacks++; MovedUnits += units; if (viaMuc) MucMovedStacks++;
        Name(ResultKind.Moved, 0, item, units);
    }
    public void Unconfirm(bool viaMuc = false, string? item = null) { Unconfirmed++; if (viaMuc) MucUnconfirmed++; Name(ResultKind.Unconfirmed, 0, item, 1); }

    /// <summary>Distinct names kept per result line; further ones only count (bounded memory per run).</summary>
    public const int MaximumNamesPerLine = 16;
    // (kind, reason) -> item tallies (stacks, and units for moves), in first-seen order.
    private sealed class Tally { internal string Item = ""; internal int Stacks, Units; }
    private readonly Dictionary<(ResultKind Kind, int Reason), List<Tally>> names = new();
    private void Name(ResultKind kind, int reason, string? item, int units)
    {
        if (item == null) return;
        if (!names.TryGetValue((kind, reason), out var rows)) names.Add((kind, reason), rows = new List<Tally>());
        var row = rows.Find(x => StringComparer.Ordinal.Equals(x.Item, item));
        if (row == null)
        {
            if (rows.Count == MaximumNamesPerLine) return;
            rows.Add(row = new Tally { Item = item });
        }
        row.Stacks++; row.Units += units;
    }

    /// <summary>
    /// The result view after a run (P6-04): the one-line <see cref="Summary"/> plus one line per outcome and reason,
    /// with item names. Bounded: at most <paramref name="maximumLines"/> lines, <paramref name="namesPerLine"/> names
    /// each, and every text at most <see cref="ResultSummary.MaximumText"/> characters.
    /// </summary>
    public ResultSummary Detail(Func<string, string>? itemName = null, int maximumLines = 8, int namesPerLine = 4)
    {
        if (maximumLines < 1) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        if (namesPerLine < 0) throw new ArgumentOutOfRangeException(nameof(namesPerLine));
        var lines = new List<ResultLine>();
        void Add(ResultKind kind, int reason, int count, string label)
        {
            if (count == 0) return;
            var rows = names.TryGetValue((kind, reason), out var tallied) ? tallied.Take(namesPerLine).ToArray() : Array.Empty<Tally>();
            // Moves show item counts ("Copper ×40"); other lines show stacks ("Sword ×2").
            var shown = rows.Select(x => ResultSummary.Bound(Display(itemName, x.Item), 40) +
                ((kind == ResultKind.Moved ? x.Units : x.Stacks) is var n && n > 1 ? " ×" + n : "")).ToArray();
            lines.Add(new ResultLine(kind, kind == ResultKind.Remaining ? (CleanupReason?)reason : null,
                kind == ResultKind.Refused ? (TransferRefusal?)reason : null, count, label, shown, Math.Max(0, count - rows.Sum(x => x.Stacks))));
        }
        Add(ResultKind.Moved, 0, MovedStacks, $"{verb} {MovedStacks} stack{Plural(MovedStacks)} ({MovedUnits} item{Plural(MovedUnits)})" +
            (MucMovedStacks == 0 ? "" : $", {MucMovedStacks} via MultiUserChest"));
        foreach (var reason in Left.Concat(Skipped))
            Add(ResultKind.Remaining, (int)reason, Count(reason), Count(reason) + " " + (Skipped.Contains(reason) ? "skipped" : left) + " (" + Describe(reason) + ")");
        foreach (var pair in refused.OrderBy(x => x.Key))
            Add(ResultKind.Refused, (int)pair.Key, pair.Value, pair.Value + " refused (" + Describe(pair.Key) + ")");
        Add(ResultKind.Unconfirmed, 0, Unconfirmed, Unconfirmed + " unconfirmed" + (MucUnconfirmed == 0 ? "" : $" ({MucUnconfirmed} via MultiUserChest)") +
            ", not retried");
        var kept = lines.Take(maximumLines).ToList();
        var dropped = lines.Count - kept.Count;
        return new ResultSummary(title, Summary(), kept, dropped);
    }
    private static string Display(Func<string, string>? itemName, string item)
    {
        var shown = itemName?.Invoke(item);
        return string.IsNullOrWhiteSpace(shown) ? item : shown!;
    }
    private static readonly CleanupReason[] Left = { CleanupReason.NoCapacity, CleanupReason.NoDestination, CleanupReason.AzuExcluded,
        CleanupReason.Held, CleanupReason.Unsupported, CleanupReason.MucBusy, CleanupReason.Budget, CleanupReason.ChestLimit, CleanupReason.CategoriesPending };
    private static readonly CleanupReason[] Skipped = { CleanupReason.OtherOwnerNoMuc, CleanupReason.BothRemote, CleanupReason.OtherOwnerFormatUnknown };

    /// <summary>
    /// Example: "Cleanup: moved 4 stacks (63 items) · 2 left in place (no room) · 1 refused (chest changed) ·
    /// 1 skipped (another player's chest, no MultiUserChest)". Zero parts after the first are left out.
    /// </summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            $"{verb} {MovedStacks} stack{Plural(MovedStacks)} ({MovedUnits} item{Plural(MovedUnits)})" +
            (MucMovedStacks == 0 ? "" : $", {MucMovedStacks} via MultiUserChest")
        };
        var left = new[] { CleanupReason.NoCapacity, CleanupReason.NoDestination, CleanupReason.AzuExcluded, CleanupReason.Held,
            CleanupReason.Unsupported, CleanupReason.MucBusy, CleanupReason.Budget, CleanupReason.ChestLimit, CleanupReason.CategoriesPending };
        var leftParts = left.Where(x => Count(x) != 0).Select(x => Count(x) + " " + Describe(x)).ToList();
        if (leftParts.Count != 0) parts.Add(LeftTotal(left) + " " + this.left + " (" + string.Join(", ", leftParts) + ")");
        var skipped = new[] { CleanupReason.OtherOwnerNoMuc, CleanupReason.BothRemote, CleanupReason.OtherOwnerFormatUnknown };
        foreach (var reason in skipped.Where(x => Count(x) != 0)) parts.Add(Count(reason) + " skipped (" + Describe(reason) + ")");
        if (Refused != 0)
            parts.Add(Refused + " refused (" + string.Join(", ", refused.OrderBy(x => x.Key).Select(x => x.Value + " " + Describe(x.Key))) + ")");
        if (Unconfirmed != 0)
            parts.Add(Unconfirmed + " unconfirmed" + (MucUnconfirmed == 0 ? "" : $" ({MucUnconfirmed} via MultiUserChest)"));
        if (ChestsInRange > ChestsHandled)
            parts.Add($"{ChestsInRange} chests in range; only the nearest {ChestsHandled} could be handled");
        return title + ": " + string.Join(" · ", parts);
    }

    private int LeftTotal(IEnumerable<CleanupReason> reasons) => reasons.Sum(Count);
    private static string Plural(int count) => count == 1 ? "" : "s";

    public static string Describe(CleanupReason reason) => reason switch
    {
        CleanupReason.NoCapacity => "no room",
        CleanupReason.NoDestination => "no chest takes it",
        CleanupReason.AzuExcluded => "AzuAutoStore's config excludes it",
        CleanupReason.Held => "in use",
        CleanupReason.Unsupported => "protected",
        CleanupReason.MucBusy => "other player's chest busy, run again",
        CleanupReason.Budget => "run limit reached, run again",
        CleanupReason.ChestLimit => "in a chest farther than the nearest " + CleanupPlanner.MaximumSources + " with items; clean up again closer to it",
        CleanupReason.OtherOwnerNoMuc => "another player's chest, no MultiUserChest",
        CleanupReason.BothRemote => "both chests are other players'",
        CleanupReason.OtherOwnerFormatUnknown => "another player's chest, their MultiUserChest format is unknown",
        CleanupReason.CategoriesPending => "waiting for categories to load, run again shortly",
        _ => reason.ToString()
    };

    public static string Describe(TransferRefusal reason) => reason switch
    {
        TransferRefusal.InventoryChanged => "chest changed",
        TransferRefusal.OwnerChanged => "a chest changed owner, run again",
        TransferRefusal.PolicyChanged => "rules or categories changed, run again",
        TransferRefusal.RuleMismatch => "rules changed, run again",
        TransferRefusal.AccessDenied => "no access",
        TransferRefusal.NoCapacity => "no room",
        TransferRefusal.Protected => "protected",
        TransferRefusal.Disconnected => "left the world",
        TransferRefusal.Expired => "too far or too late",
        TransferRefusal.UnsupportedCapability => "unsupported item",
        TransferRefusal.UnsupportedAuthority => "not the chest owner",
        TransferRefusal.Busy => "MultiUserChest refused or refunded it",
        TransferRefusal.TurnedOff => "turned off on this server",
        _ => reason.ToString()
    };
}

public enum ResultKind { Moved, Remaining, Refused, Unconfirmed }

/// <summary>One outcome and reason of a run, with the items it concerned.</summary>
public sealed class ResultLine
{
    public ResultKind Kind { get; }
    public CleanupReason? Reason { get; }
    public TransferRefusal? Refusal { get; }
    /// <summary>Stacks on this line.</summary>
    public int Count { get; }
    /// <summary>E.g. "2 left in place (no room)".</summary>
    public string Label { get; }
    /// <summary>Up to the requested number of item names, e.g. "Copper ×40".</summary>
    public IReadOnlyList<string> Items { get; }
    /// <summary>Stacks on this line not covered by <see cref="Items"/>.</summary>
    public int More { get; }
    /// <summary>"2 left in place (no room): Copper, Tin +1 more", at most <see cref="ResultSummary.MaximumText"/> characters.</summary>
    public string Text { get; }
    internal ResultLine(ResultKind kind, CleanupReason? reason, TransferRefusal? refusal, int count, string label, IReadOnlyList<string> items, int more)
    {
        Kind = kind; Reason = reason; Refusal = refusal; Count = count; Label = label; Items = items; More = more;
        var list = string.Join(", ", items) + (more == 0 ? "" : (items.Count == 0 ? "" : " ") + "+" + more + " more");
        Text = ResultSummary.Bound(label + (list.Length == 0 ? "" : ": " + list), ResultSummary.MaximumText);
    }
}

/// <summary>The short result view of one stash or cleanup run (P6-04). Read only; built from <see cref="CleanupReport"/>.</summary>
public sealed class ResultSummary
{
    public const int MaximumText = 160;
    /// <summary>"Stash" or "Cleanup".</summary>
    public string Title { get; }
    /// <summary>The report's one-line summary, as shown today.</summary>
    public string Headline { get; }
    public IReadOnlyList<ResultLine> Lines { get; }
    /// <summary>Lines left out by the line bound; their totals are still in <see cref="Headline"/>.</summary>
    public int MoreLines { get; }
    /// <summary>Nothing moved and nothing to report.</summary>
    public bool Empty => Lines.Count == 0;
    internal ResultSummary(string title, string headline, IReadOnlyList<ResultLine> lines, int more)
    { Title = title; Headline = headline; Lines = lines; MoreLines = more; }
    internal static string Bound(string text, int maximum) => text.Length <= maximum ? text : text.Substring(0, maximum - 1) + "…";
}
