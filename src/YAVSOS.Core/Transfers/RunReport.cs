using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Transfers;

/// <summary>Which run a <see cref="RunReport"/> is about; it picks the title and the words (stashed or moved, in your inventory or in place).</summary>
public enum RunKind
{
    Cleanup,
    Stash,
    /// <summary>X14: AzuAutoStore's stash key (the code's "store key"), judged by arrival.</summary>
    StoreKey
}

/// <summary>
/// Totals of one cleanup, stash or stash-key run and its one-line summary. Moved counts only verified units; refused moves moved
/// nothing; unconfirmed moves may or may not have run and are never retried, replayed or refunded (D041).
/// MultiUserChest outcomes are counted separately so they are attributed to it (D043).
/// The summary and the result lines are messages (P10-04): Core says what happened, the game side finds the words.
/// </summary>
public sealed class RunReport
{
    private readonly Dictionary<RunReason, int> remaining = new();
    private readonly Dictionary<TransferRefusal, int> refused = new();
    public int MovedStacks { get; private set; }
    public int MovedUnits { get; private set; }
    public int MucMovedStacks { get; private set; }
    public int Unconfirmed { get; private set; }
    public int MucUnconfirmed { get; private set; }
    public int Refused => refused.Values.Sum();
    public int Remaining => remaining.Values.Sum();
    public int Count(RunReason reason) => remaining.TryGetValue(reason, out var count) ? count : 0;
    public int Count(TransferRefusal reason) => refused.TryGetValue(reason, out var count) ? count : 0;
    public RunKind Kind { get; }

    private RunReport(RunKind kind) { Kind = kind; }
    /// <summary>A cleanup run's report; <see cref="ForStash"/> and <see cref="ForStoreKey"/> word the same totals for the others.</summary>
    public static RunReport ForCleanup() => new(RunKind.Cleanup);
    /// <summary>"Stashed 4 stacks (63 items) · 2 stay in your inventory (no room)."</summary>
    public static RunReport ForStash() => new(RunKind.Stash);
    /// <summary>X14: AzuAutoStore's stash key, judged by arrival: "Stashed 4 stacks (63 items) · 1 unconfirmed (not retried)."</summary>
    public static RunReport ForStoreKey() => new(RunKind.StoreKey);

    /// <summary>D086 D9: chests in range, and how many of the nearest a run could handle; the summary says when some weren't.</summary>
    public void Range(int inRange, int handled)
    {
        if (inRange < 0 || handled < 0 || handled > inRange) throw new ArgumentOutOfRangeException(nameof(handled));
        ChestsInRange = inRange; ChestsHandled = handled;
    }
    public int ChestsInRange { get; private set; }
    public int ChestsHandled { get; private set; }

    /// <param name="item">The item's identity (prefab) for the result view (P6-04); null counts it unnamed.</param>
    /// <param name="excludedFrom">For <see cref="RunReason.AzuExcluded"/>: the chest type AzuAutoStore keeps it out of, as the
    /// game names it ("Reinforced chest"), so the player knows which chest to replace (D086 D10).</param>
    public void Remain(RunReason reason, string? item = null, string? excludedFrom = null)
    {
        remaining[reason] = Count(reason) + 1; Name(ResultKind.Remaining, (int)reason, item, 1);
        if (reason == RunReason.AzuExcluded && !string.IsNullOrWhiteSpace(excludedFrom) && !excludedTypes.Contains(excludedFrom!))
            if (excludedTypes.Count < MaximumExcludedTypes) excludedTypes.Add(excludedFrom!); else excludedTypesMore = true;
    }
    public const int MaximumExcludedTypes = 3;
    /// <summary>Characters kept of an item or chest-type name in a result (inventory case 9: tuned to English; P10-05 measures).</summary>
    public const int MaximumName = 40;
    private readonly List<string> excludedTypes = new();
    private bool excludedTypesMore;
    public IReadOnlyList<string> ExcludedChestTypes => excludedTypes;

    // The reason with this run's detail: the chest types behind AzuExcluded (D086 D10).
    private Message ReasonInRun(RunReason reason)
    {
        if (reason != RunReason.AzuExcluded || excludedTypes.Count == 0) return Reason(reason);
        var types = excludedTypes.Select(x => ResultSummary.Bound(x, MaximumName));
        return excludedTypesMore
            ? Message.Of("yavsos_reason_azu_excluded_types_more").With("types", MessageList.Of(ListKind.Comma, types))
            : Message.Of("yavsos_reason_azu_excluded_types").With("types", MessageList.Of(ListKind.And, types));
    }
    public void Refuse(TransferRefusal reason, string? item = null)
    {
        if (reason == TransferRefusal.None || reason == TransferRefusal.Unconfirmed) throw new ArgumentException("Not a refusal.", nameof(reason));
        refused[reason] = Count(reason) + 1; Name(ResultKind.Refused, (int)reason, item, 1);
    }
    /// <param name="stack">The source stack's handle (B4.3): a stack split across several chests counts once. Null counts each call.</param>
    /// <param name="destination">X55: the chest or drawer the units went to, as the run's own ID for it; the result view
    /// lists what each received. Null when the caller doesn't know; then the view keeps the single moved line.</param>
    public void Moved(int units, bool viaMuc = false, string? item = null, Guid? stack = null, string? destination = null)
    {
        if (units < 1) throw new ArgumentOutOfRangeException(nameof(units));
        MovedUnits += units;
        if (stack == null || movedStacks.Add(stack.Value)) MovedStacks++;
        if (viaMuc && (stack == null || mucMovedStacks.Add(stack.Value))) MucMovedStacks++;
        Name(ResultKind.Moved, 0, item, units);
        Place(destination, item, units, stack);
    }
    private readonly HashSet<Guid> movedStacks = new(), mucMovedStacks = new();

    /// <summary>X55: destinations kept with their items; further ones only count (bounded memory per run).</summary>
    public const int MaximumPlaces = 64;
    // A destination's received items in first-seen order: units per item, stacks per destination (a stack split across
    // two chests counts at each).
    private sealed class Destination
    {
        internal string Id = ""; internal int Stacks; internal readonly HashSet<Guid> Seen = new(); internal readonly List<Tally> Items = new();
    }
    private readonly List<Destination> destinations = new();
    private readonly HashSet<string> destinationIds = new(StringComparer.Ordinal);
    private bool unplaced;
    /// <summary>X55: how many chests and drawers received items (every one, also past <see cref="MaximumPlaces"/>).</summary>
    public int Places => destinationIds.Count;

    private void Place(string? destination, string? item, int units, Guid? stack)
    {
        if (destination == null) { unplaced = true; return; }
        destinationIds.Add(destination);
        var place = destinations.Find(x => StringComparer.Ordinal.Equals(x.Id, destination));
        if (place == null)
        {
            if (destinations.Count == MaximumPlaces) return;
            destinations.Add(place = new Destination { Id = destination });
        }
        var newStack = stack == null || place.Seen.Add(stack.Value);
        if (newStack) place.Stacks++;
        if (item == null) return;
        var row = place.Items.Find(x => StringComparer.Ordinal.Equals(x.Item, item));
        if (row == null)
        {
            if (place.Items.Count == MaximumNamesPerLine) return;
            place.Items.Add(row = new Tally { Item = item });
        }
        if (newStack) row.Stacks++;
        row.Units += units;
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

    // One outcome before item names: the summary has one part per outcome, the result panel one line each.
    private readonly struct Part
    {
        internal Part(ResultKind kind, int reason, int count, Message label) { Kind = kind; Reason = reason; Count = count; Label = label; }
        internal ResultKind Kind { get; }
        internal int Reason { get; }
        internal int Count { get; }
        internal Message Label { get; }
    }

    // Every outcome with a count, in report order: moved, what stays (each reason, then MultiUserChest's causes),
    // unreadable chests, refusals, unconfirmed.
    private List<Part> Parts()
    {
        var parts = new List<Part>();
        if (MovedStacks != 0) parts.Add(new Part(ResultKind.Moved, 0, MovedStacks, MovedLabel()));
        foreach (var reason in Left.Concat(Skipped).Where(x => Count(x) != 0))
            parts.Add(new Part(ResultKind.Remaining, (int)reason, Count(reason), Stays(Count(reason), ReasonInRun(reason))));
        var unreadable = Count(RunReason.ChestUnreadable);
        if (unreadable != 0)
            parts.Add(new Part(ResultKind.Remaining, (int)RunReason.ChestUnreadable, unreadable, Message.Of("yavsos_result_unreadable").Count(unreadable)));
        // D136: a refused move moved nothing, so its stacks stay too; the reason says what changed.
        foreach (var pair in refused.OrderBy(x => x.Key))
            parts.Add(new Part(ResultKind.Refused, (int)pair.Key, pair.Value, Stays(pair.Value, Reason(pair.Key))));
        if (Unconfirmed != 0)
            parts.Add(new Part(ResultKind.Unconfirmed, 0, Unconfirmed, MucUnconfirmed == 0
                ? Message.Of("yavsos_result_unconfirmed").Count(Unconfirmed)
                : Message.Of("yavsos_result_unconfirmed_via").Count(Unconfirmed).With("via", Message.Of("yavsos_result_via_muc").Count(MucUnconfirmed))));
        return parts;
    }

    private bool Stashing => Kind != RunKind.Cleanup;

    // "Moved 4 stacks (63 items), 1 via MultiUserChest", or "Stashed …" for a stash or the stash key.
    private Message MovedLabel()
    {
        var key = (Stashing, MucMovedStacks != 0) switch
        {
            (false, false) => "yavsos_result_moved",
            (false, true) => "yavsos_result_moved_via",
            (true, false) => "yavsos_result_stashed",
            _ => "yavsos_result_stashed_via"
        };
        return MovedWords(Message.Of(key));
    }

    // X55: the moved line above the place lines: "Stashed 29 stacks (623 items) in 26 places:".
    private Message PlacesLabel()
    {
        var key = (Stashing, MucMovedStacks != 0) switch
        {
            (false, false) => "yavsos_result_moved_places",
            (false, true) => "yavsos_result_moved_places_via",
            (true, false) => "yavsos_result_stashed_places",
            _ => "yavsos_result_stashed_places_via"
        };
        return MovedWords(Message.Of(key)).With("places", Message.Of("yavsos_result_places").Count(Places));
    }

    private Message MovedWords(Message key)
    {
        var label = key.With("stacks", Message.Of("yavsos_result_stacks").Count(MovedStacks))
            .With("items", Message.Of("yavsos_result_items").Count(MovedUnits));
        return MucMovedStacks == 0 ? label : label.With("via", Message.Of("yavsos_result_via_muc").Count(MucMovedStacks));
    }

    // "2 stay in place (no room)" or "… in your inventory (…)": D136's "stays" for every stack a run didn't move.
    private Message Stays(int stacks, Message reason) =>
        Message.Of(Stashing ? "yavsos_result_stays_inventory" : "yavsos_result_stays_place").Count(stacks).With("reason", reason);

    /// <summary>
    /// The result view after a run (P6-04): the one-line <see cref="Summary"/> plus one line per outcome and reason,
    /// with item names. Bounded: at most <paramref name="maximumLines"/> lines, <paramref name="namesPerLine"/> names
    /// each, every name at most <see cref="MaximumName"/> characters. The game side cuts a formatted line at
    /// <see cref="ResultSummary.MaximumText"/>.
    /// </summary>
    /// <param name="placeName">X55 (D130 variant A): how to name a destination the run recorded (<see cref="Moved"/>'s
    /// <c>destination</c>); null for one no longer there. With it, the moved line is followed by a line per chest or
    /// drawer that received items, in the order they first did, within <paramref name="maximumLines"/> together with the
    /// other lines; when they don't all fit, the last place line is "+N more places". Without it, or when a move had no
    /// destination, or when there is no room for one place line and the "+N more places" line, the moved line lists the
    /// items as before.</param>
    public ResultSummary Detail(Func<string, string>? itemName = null, int maximumLines = 8, int namesPerLine = 4,
        Func<string, ResultPlace?>? placeName = null)
    {
        if (maximumLines < 1) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        if (namesPerLine < 0) throw new ArgumentOutOfRangeException(nameof(namesPerLine));
        var lines = new List<ResultLine>();
        foreach (var part in Parts())
        {
            var rows = names.TryGetValue((part.Kind, part.Reason), out var tallied) ? tallied.Take(namesPerLine).ToArray() : Array.Empty<Tally>();
            // Moves show item counts ("Copper ×40"); other lines show stacks ("Sword ×2").
            var shown = Shown(rows, itemName, part.Kind == ResultKind.Moved);
            lines.Add(new ResultLine(part.Kind, part.Kind == ResultKind.Remaining ? (RunReason?)part.Reason : null,
                part.Kind == ResultKind.Refused ? (TransferRefusal?)part.Reason : null, part.Count, part.Label, shown,
                Math.Max(0, part.Count - rows.Sum(x => x.Stacks))));
        }
        if (placeName != null && Placed(lines, maximumLines, itemName, namesPerLine, placeName) is { } placed)
            return new ResultSummary(Kind, Summary(), placed, 0);
        var kept = lines.Take(maximumLines).ToList();
        var dropped = lines.Count - kept.Count;
        return new ResultSummary(Kind, Summary(), kept, dropped);
    }

    private static ResultItem[] Shown(IEnumerable<Tally> rows, Func<string, string>? itemName, bool units) =>
        rows.Select(x => new ResultItem(ResultSummary.Bound(Display(itemName, x.Item), MaximumName), units ? x.Units : x.Stacks)).ToArray();

    // X55: the moved line, its place lines and the other lines, or null when they can't be shown that way.
    private List<ResultLine>? Placed(List<ResultLine> lines, int maximumLines, Func<string, string>? itemName, int namesPerLine,
        Func<string, ResultPlace?> placeName)
    {
        if (MovedStacks == 0 || unplaced || destinations.Count == 0 || lines.Count == 0 || lines[0].Kind != ResultKind.Moved) return null;
        var others = lines.Skip(1).ToList();
        var room = maximumLines - 1 - others.Count;
        // Every place fits, or all but those the "+N more places" line counts; at least one place line shows.
        var shown = Places <= room && destinations.Count == Places ? Places : Math.Min(room - 1, destinations.Count);
        if (shown < 1) return null;
        var moved = lines[0];
        var placed = new List<ResultLine>
        {
            new(ResultKind.Moved, null, null, moved.Count, moved.Label, Array.Empty<ResultItem>(), 0, PlacesLabel())
        };
        foreach (var place in destinations.Take(shown))
        {
            var rows = place.Items.Take(namesPerLine).ToArray();
            var name = (placeName(place.Id) ?? ResultPlace.Gone).Said;
            placed.Add(new ResultLine(ResultKind.Place, null, null, place.Stacks, name, Shown(rows, itemName, units: true),
                Math.Max(0, place.Stacks - rows.Sum(x => x.Stacks))));
        }
        var more = Places - shown;
        if (more > 0)
            placed.Add(new ResultLine(ResultKind.MorePlaces, null, null, more, Message.Of("yavsos_result_places_more").Count(more), Array.Empty<ResultItem>(), 0));
        placed.AddRange(others);
        return placed;
    }
    private static string Display(Func<string, string>? itemName, string item)
    {
        var shown = itemName?.Invoke(item);
        return string.IsNullOrWhiteSpace(shown) ? item : shown!;
    }
    private static readonly RunReason[] Left = { RunReason.NoCapacity, RunReason.NoDestination, RunReason.AzuExcluded,
        RunReason.Held, RunReason.Unsupported, RunReason.MucBusy, RunReason.Budget, RunReason.ChestLimit, RunReason.CategoriesPending, RunReason.OptedOut };
    // F3: one line per MultiUserChest cause, counted in stacks; only the probe still checking says to try again.
    private static readonly RunReason[] Skipped = { RunReason.OtherOwnerNoMuc, RunReason.BothRemote, RunReason.OtherOwnerMucUnusable,
        RunReason.OtherOwnerMucSilent, RunReason.OtherOwnerMucChecking, RunReason.OtherOwnerFormatUnknown };

    /// <summary>
    /// The one-line summary for the on-screen message, the console and the Clean up message spot, one part per result
    /// line: "Moved 4 stacks (63 items) · 2 stay in place (no room) · 1 stays in place (a chest changed)." A run that moved
    /// nothing says so first ("Nothing moved: …", "Nothing stashed: …"); one with nothing to report is "Nothing to clean
    /// up." or "Nothing to stash."
    /// </summary>
    public Message Summary()
    {
        var parts = Parts().Select(x => x.Label).ToList();
        if (ChestsInRange > ChestsHandled)
            parts.Add(Message.Of("yavsos_result_range").Count(ChestsInRange).With("nearest", Message.Of("yavsos_result_range_nearest").Count(ChestsHandled)));
        if (MovedStacks != 0) return Message.Of("yavsos_result_summary").With("parts", MessageList.Of(ListKind.Parts, parts));
        if (parts.Count == 0) return Message.Of(Stashing ? "yavsos_result_empty_stash" : "yavsos_result_empty_cleanup");
        return Message.Of(Stashing ? "yavsos_result_nothing_stashed" : "yavsos_result_nothing_moved").With("parts", MessageList.Of(ListKind.Parts, parts));
    }

    /// <summary>
    /// Why a stack stays, as a phrase in brackets after "stays in place" or "stays in your inventory" (and, once the
    /// labels worker converts the item tooltip, after "stays here"). Two causes with the same words share a key.
    /// </summary>
    public static Message Reason(RunReason reason) => reason switch
    {
        RunReason.NoCapacity => Message.Of("yavsos_reason_no_room"),
        RunReason.NoDestination => Message.Of("yavsos_reason_no_chest"),
        RunReason.AzuExcluded => Message.Of("yavsos_reason_azu_excluded"),
        RunReason.Held => Message.Of("yavsos_reason_held"),
        RunReason.Unsupported => Message.Of("yavsos_reason_protected"),
        RunReason.MucBusy => Message.Of("yavsos_reason_muc_busy"),
        RunReason.Budget => Message.Of("yavsos_reason_budget"),
        RunReason.ChestLimit => Message.Of("yavsos_reason_chest_limit").Count(CleanupPlanner.MaximumSources),
        RunReason.OtherOwnerNoMuc => Message.Of("yavsos_reason_no_muc"),
        RunReason.BothRemote => Message.Of("yavsos_reason_both_remote"),
        RunReason.OtherOwnerMucUnusable => Message.Of("yavsos_reason_muc_unusable"),
        RunReason.OtherOwnerMucSilent => Message.Of("yavsos_reason_muc_silent"),
        RunReason.OtherOwnerMucChecking => Message.Of("yavsos_reason_muc_checking"),
        RunReason.OtherOwnerFormatUnknown => Message.Of("yavsos_reason_muc_format"),
        RunReason.CategoriesPending => Message.Of("yavsos_reason_categories_loading"),
        RunReason.OptedOut => Message.Of("yavsos_reason_opted_out").With("option", Message.Of("yavsos_cleanup_optout")),
        RunReason.ChestUnreadable => Message.Of("yavsos_reason_unreadable"),
        _ => Message.Of("yavsos_refusal_check_failed")
    };

    /// <summary>Why a move was refused (it moved nothing), as a phrase in brackets after "stays in place" or "stays in your inventory".</summary>
    public static Message Reason(TransferRefusal reason) => reason switch
    {
        TransferRefusal.InventoryChanged => Message.Of("yavsos_refusal_chest_changed"),
        TransferRefusal.OwnerChanged => Message.Of("yavsos_refusal_owner_changed"),
        TransferRefusal.PolicyChanged => Message.Of("yavsos_refusal_policy_changed"),
        TransferRefusal.RuleMismatch => Message.Of("yavsos_refusal_rules_changed"),
        TransferRefusal.AccessDenied => Message.Of("yavsos_refusal_no_access"),
        TransferRefusal.NoCapacity => Message.Of("yavsos_reason_no_room"),
        TransferRefusal.Protected => Message.Of("yavsos_reason_protected"),
        TransferRefusal.Disconnected => Message.Of("yavsos_refusal_left_world"),
        TransferRefusal.Expired => Message.Of("yavsos_refusal_expired"),
        TransferRefusal.UnsupportedCapability => Message.Of("yavsos_refusal_unsupported"),
        TransferRefusal.UnsupportedAuthority => Message.Of("yavsos_refusal_not_owner"),
        TransferRefusal.Busy => Message.Of("yavsos_refusal_muc_refused"),
        TransferRefusal.TurnedOff => Message.Of("yavsos_refusal_turned_off"),
        // ProtocolMismatch and InvariantFailed: checks that should never fail.
        _ => Message.Of("yavsos_refusal_check_failed")
    };
}

public enum ResultKind
{
    Moved, Remaining, Refused, Unconfirmed,
    /// <summary>X55: one chest or drawer that received items, under the moved line; its label names the place.</summary>
    Place,
    /// <summary>X55: "+N more places", the places the line bound left out; <see cref="ResultLine.Count"/> is N.</summary>
    MorePlaces
}

/// <summary>
/// X55: how the result panel names a chest or drawer that received items: the where-line's chest name (inventory case 3:
/// the open chest, or the game's name and distance), and the chest label when the chest has a rule. Immutable.
/// </summary>
public sealed class ResultPlace : IEquatable<ResultPlace>
{
    private ResultPlace(Interface.ChestName? chest, string? label) { Chest = chest; Label = string.IsNullOrWhiteSpace(label) ? null : label; }
    /// <param name="chest">The where-line's name for it; null for a chest without a name.</param>
    /// <param name="label">Its chest label (the rule's text, as the hover text shows it), already safe for rich text; null without a rule.</param>
    public static ResultPlace Of(Interface.ChestName? chest, string? label = null) => new(chest, label);
    /// <summary>A destination no longer there when the result is shown.</summary>
    public static ResultPlace Gone { get; } = new(null, null);
    public Interface.ChestName? Chest { get; }
    public string? Label { get; }

    /// <summary>
    /// The place at the start of its line: "This chest", "Reinforced chest 5 m away", "A chest nearby", and with a rule
    /// "Reinforced chest 5 m away · Metals". Capitalized, since the where-line's "this chest" and "a chest nearby" sit
    /// inside a sentence.
    /// </summary>
    public Message Said
    {
        get
        {
            var chest = Chest == null || !Chest.IsOpen && string.IsNullOrWhiteSpace(Chest.Name) ? Message.Of("yavsos_result_place_nearby")
                : Chest.IsOpen ? Message.Of("yavsos_result_place_this")
                : Chest.Said;
            return Label == null ? chest : Message.Of("yavsos_result_place_labeled").With("chest", chest).With("label", Label);
        }
    }

    public bool Equals(ResultPlace? other) => other is not null && Equals(other.Chest, Chest) && other.Label == Label;
    public override bool Equals(object? obj) => obj is ResultPlace other && Equals(other);
    public override int GetHashCode() => (Chest?.GetHashCode() ?? 0) ^ (Label == null ? 0 : StringComparer.Ordinal.GetHashCode(Label));
    public override string ToString() => (Chest?.ToString() ?? "(nearby)") + (Label == null ? "" : " · " + Label);
}

/// <summary>An item on a result line: its name, already in the player's language and cut to <see cref="RunReport.MaximumName"/>, and how many.</summary>
public sealed class ResultItem : IEquatable<ResultItem>
{
    public ResultItem(string name, int count) { Name = name ?? ""; Count = count; }
    public string Name { get; }
    /// <summary>Units on a moved line, stacks on the others.</summary>
    public int Count { get; }
    /// <summary>As the line shows it: the name, or "Copper ×40" when there is more than one.</summary>
    public object Shown => Count > 1 ? Message.Of("yavsos_result_item").With("name", Name).With("times", Count) : Name;
    public bool Equals(ResultItem? other) => other is not null && other.Name == Name && other.Count == Count;
    public override bool Equals(object? obj) => obj is ResultItem other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name) ^ Count;
    public override string ToString() => Count > 1 ? Name + " ×" + Count : Name;
}

/// <summary>One outcome and reason of a run, with the items it concerned. The panel formats it from these fields (B2-09).</summary>
public sealed class ResultLine
{
    public ResultKind Kind { get; }
    public RunReason? Reason { get; }
    public TransferRefusal? Refusal { get; }
    /// <summary>Stacks on this line; places on a <see cref="ResultKind.MorePlaces"/> line.</summary>
    public int Count { get; }
    /// <summary>E.g. "2 stay in place (no room)": the same words as this line's part of the summary. On a
    /// <see cref="ResultKind.Place"/> line, the place's name (<see cref="ResultPlace.Said"/>).</summary>
    public Message Label { get; }
    /// <summary>Up to the requested number of items, e.g. Copper ×40.</summary>
    public IReadOnlyList<ResultItem> Items { get; }
    /// <summary>Stacks on this line not covered by <see cref="Items"/>.</summary>
    public int More { get; }
    private readonly Message? said;
    internal ResultLine(ResultKind kind, RunReason? reason, TransferRefusal? refusal, int count, Message label, IReadOnlyList<ResultItem> items, int more,
        Message? said = null)
    { Kind = kind; Reason = reason; Refusal = refusal; Count = count; Label = label; Items = items; More = more; this.said = said; }

    /// <summary>
    /// The whole line from its fields: "2 stay in place (no room): Copper, Tin +1 more", or the label alone when it
    /// names no items (unreadable chests, or names left out), since "+N more" would only repeat its count. A place line
    /// (X55) is "Reinforced chest 5 m away · Metals: Copper ×60, Tin ×24"; the moved line above place lines says how many
    /// places, "Stashed 29 stacks (623 items) in 26 places:". The game side cuts the formatted text at
    /// <see cref="ResultSummary.MaximumText"/>.
    /// </summary>
    public Message Text => said ?? (Items.Count == 0 ? Label
        : Message.Of("yavsos_result_line").With("label", Label).With("items", MessageList.Of(ListKind.Comma, Items.Select(x => x.Shown)).WithMore(More)));
}

/// <summary>The short result view of one stash, cleanup or stash-key run (P6-04). Read only; built from <see cref="RunReport"/>.</summary>
public sealed class ResultSummary
{
    /// <summary>
    /// Characters kept of a formatted result line (inventory case 9: tuned to English; P10-05 measures the room). X55
    /// raised it from 160: a place line with a chest label ("Reinforced chest 12 m away · Ores &amp; metals · priority 1:")
    /// and four items runs to about 165 in English; 200 is about three lines at the panel's width.
    /// </summary>
    public const int MaximumText = 200;
    public RunKind Kind { get; }
    /// <summary>The panel's title, one message per run kind (B2-09): "YAVSOS cleanup result".</summary>
    public Message Title => Kind switch
    {
        RunKind.Stash => Message.Of("yavsos_result_title_stash"),
        RunKind.StoreKey => Message.Of("yavsos_result_title_stash_key"),
        _ => Message.Of("yavsos_result_title_cleanup")
    };
    /// <summary>The report's one-line summary (<see cref="RunReport.Summary"/>).</summary>
    public Message Headline { get; }
    public IReadOnlyList<ResultLine> Lines { get; }
    /// <summary>Lines left out by the line bound; their totals are still in <see cref="Headline"/>.</summary>
    public int MoreLines { get; }
    /// <summary>Nothing moved and nothing to report.</summary>
    public bool Empty => Lines.Count == 0;
    internal ResultSummary(RunKind kind, Message headline, IReadOnlyList<ResultLine> lines, int more)
    { Kind = kind; Headline = headline; Lines = lines; MoreLines = more; }

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="maximum"/> characters with "…" at the end, never inside a
    /// surrogate pair (inventory case 9).
    /// </summary>
    public static string Bound(string text, int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (text == null || text.Length <= maximum) return text ?? "";
        var cut = maximum - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut) + "…";
    }
}
