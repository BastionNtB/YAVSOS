using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// One loaded container the caller has already found eligible for cleanup: player-built, accessible to the local
/// player, within range and detected the way AzuAutoStore detects chests. World-spawned containers are never passed in.
/// </summary>
public sealed class CleanupChest
{
    public string Id { get; }
    /// <summary>The chest's stored rule payload; empty when the chest has none.</summary>
    public string RulePayload { get; }
    public TransferInventorySnapshot Inventory { get; }
    /// <summary>The local peer is the chest's current owner (direct writes possible).</summary>
    public bool LocallyOwned { get; }
    /// <summary>"Don't clean up this container": nothing is taken out of it (D057).</summary>
    public bool OptOut { get; }
    /// <summary>Distance from the player; the run's chest and source limits keep the nearest (D086 D9).</summary>
    public double Distance { get; }
    public CleanupChest(string id, string rulePayload, TransferInventorySnapshot inventory, bool locallyOwned, bool optOut = false,
        double distance = 0)
    {
        Id = TransferCanonical.Text(id, 256, false);
        RulePayload = rulePayload ?? throw new ArgumentNullException(nameof(rulePayload));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        if (double.IsNaN(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        LocallyOwned = locallyOwned; OptOut = optOut; Distance = distance;
    }
}

public sealed class CleanupInput
{
    public ClassificationTaxonomy Taxonomy { get; }
    public ICategoryAnswers Answers { get; }
    public IReadOnlyList<CleanupChest> Chests { get; }
    /// <summary>Current members of a category, for the size tie-break (D054). Null: no size tie-break.</summary>
    public Func<string, int>? CategorySize { get; }
    /// <summary>A MultiUserChest intercept is present, so a move with one side owned by another player can be delegated (D043, D057).</summary>
    public bool MucAvailable { get; }
    /// <summary>
    /// AzuAutoStore's own exclusions for a destination chest (D056): false when Azu's config forbids the item there.
    /// Null when Azu is absent; there is then nothing to respect.
    /// </summary>
    public Func<string, TransferItemIdentity, bool>? AzuAllows { get; }
    /// <summary>Stacks held on the cursor or otherwise in use by an open inventory screen (D057 O8).</summary>
    public IReadOnlyCollection<Guid> Held { get; }
    /// <summary>
    /// Fresh evidence of one stack's own instance (EpicLoot rarity, condition) for instance filters (P6-07b). Null:
    /// those filters see Unknown. The writer's recheck must use the same evidence.
    /// </summary>
    public Func<TransferStack, IReadOnlyList<FacetEvidence>>? InstanceFacets { get; }
    public CleanupInput(ClassificationTaxonomy taxonomy, ICategoryAnswers answers, IEnumerable<CleanupChest> chests,
        Func<string, int>? categorySize = null, bool mucAvailable = false, Func<string, TransferItemIdentity, bool>? azuAllows = null,
        IEnumerable<Guid>? held = null, Func<TransferStack, IReadOnlyList<FacetEvidence>>? instanceFacets = null)
    {
        InstanceFacets = instanceFacets;
        Taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        Answers = answers ?? throw new ArgumentNullException(nameof(answers));
        Chests = Array.AsReadOnly((chests ?? throw new ArgumentNullException(nameof(chests))).ToArray());
        if (Chests.Any(x => x == null)) throw new ArgumentException("Null cleanup chest.");
        if (Chests.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Chests.Count) throw new ArgumentException("Duplicate cleanup chest id.");
        if (Chests.Select(x => x.Inventory.Stamp.InventoryId).Distinct(StringComparer.Ordinal).Count() != Chests.Count)
            throw new ArgumentException("Two cleanup chests share one inventory.");
        CategorySize = categorySize; MucAvailable = mucAvailable; AzuAllows = azuAllows;
        Held = new HashSet<Guid>(held ?? Array.Empty<Guid>());
    }
}

public enum CleanupRoute
{
    /// <summary>The local peer owns both chests and writes itself.</summary>
    Direct,
    /// <summary>Local source, destination owned by another player: MultiUserChest adds it there.</summary>
    MucAdd,
    /// <summary>Source owned by another player, local destination: MultiUserChest removes it there.</summary>
    MucRemove
}

/// <summary>Why the planner moves a stack (D057).</summary>
public enum CleanupKind
{
    /// <summary>The source chest's rule rejects it.</summary>
    Rejected,
    /// <summary>The source container has no rule and a ruled chest accepts it.</summary>
    Unruled,
    /// <summary>The source chest accepts it, and a strictly better-ranked ruled chest accepts it too (D057, D061).</summary>
    Resort
}

public enum CleanupReason
{
    /// <summary>No ruled chest in range accepts it.</summary>
    NoDestination,
    /// <summary>Accepting chests have no room left.</summary>
    NoCapacity,
    /// <summary>The only accepting chests are of a type AzuAutoStore's config excludes it from (D056).</summary>
    AzuExcluded,
    /// <summary>One side is another player's chest and no MultiUserChest is present.</summary>
    OtherOwnerNoMuc,
    /// <summary>Both sides are other players' chests.</summary>
    BothRemote,
    /// <summary>The other player's MultiUserChest item format is unknown, so YAVSOS's copy cannot send it a request (D061 Q8).</summary>
    OtherOwnerFormatUnknown,
    /// <summary>The other player's chest already has a MultiUserChest request this run.</summary>
    MucBusy,
    /// <summary>The stack is protected or cannot be moved exactly.</summary>
    Unsupported,
    /// <summary>The stack is on the cursor or in use by an open inventory screen (D057 O8).</summary>
    Held,
    /// <summary>The run's bounds were reached; run again.</summary>
    Budget,
    /// <summary>More chests in range than one run reads; the same ones are left out on every run.</summary>
    ChestLimit,
    /// <summary>A category chest may take it once categories finish loading (D086 D4); try again shortly.</summary>
    CategoriesPending
}

public sealed class CleanupMove
{
    public string SourceId { get; }
    public string DestinationId { get; }
    public Guid Handle { get; }
    public TransferItemIdentity Identity { get; }
    public int Units { get; }
    public int DestinationX { get; }
    public int DestinationY { get; }
    public Guid? MergeHandle { get; }
    public CleanupRoute Route { get; }
    public CleanupKind Kind { get; }
    /// <summary>Merges into a stack an earlier move of this run creates; see <see cref="TryMergeTarget"/> (D070: one press settles).</summary>
    public bool MergesPlanned { get; }
    internal CleanupMove(string source, string destination, Guid handle, TransferItemIdentity identity, int units, int x, int y,
        Guid? merge, CleanupRoute route, CleanupKind kind, bool mergesPlanned = false)
    {
        MergesPlanned = mergesPlanned;
        SourceId = source; DestinationId = destination; Handle = handle; Identity = identity; Units = units;
        DestinationX = x; DestinationY = y; MergeHandle = merge; Route = route; Kind = kind;
    }

    /// <summary>The stack to merge into, from the writer's fresh captures of the source and destination.</summary>
    public bool TryMergeTarget(TransferInventorySnapshot source, TransferInventorySnapshot destination, out Guid? merge) =>
        TransferGrid.TryMergeTarget(MergesPlanned, MergeHandle, source, Handle, destination, DestinationX, DestinationY, out merge);
}

public sealed class CleanupRemainder
{
    public string SourceId { get; }
    public Guid Handle { get; }
    public TransferItemIdentity Identity { get; }
    public int Units { get; }
    public CleanupReason Reason { get; }
    /// <summary>For <see cref="CleanupReason.AzuExcluded"/>, the first excluded chest; otherwise null.</summary>
    public string? DestinationId { get; }
    internal CleanupRemainder(string source, Guid handle, TransferItemIdentity identity, int units, CleanupReason reason, string? destination)
    { SourceId = source; Handle = handle; Identity = identity; Units = units; Reason = reason; DestinationId = destination; }
}

public sealed class CleanupPlan
{
    /// <summary>Rule taxonomy fingerprint and category-answer generation; any change refuses the rest of the run.</summary>
    public string PolicyStamp { get; }
    /// <summary>Categories are not ready: the run is refused with one message and plans nothing (D057 O6).</summary>
    public bool CategoriesPending { get; }
    public IReadOnlyList<CleanupMove> Moves { get; }
    public IReadOnlyList<CleanupRemainder> Remainders { get; }
    /// <summary>Chests beyond <see cref="CleanupPlanner.MaximumChests"/> that were left out of this run.</summary>
    public int SkippedChests { get; }
    internal CleanupPlan(string stamp, bool pending, IReadOnlyList<CleanupMove> moves, IReadOnlyList<CleanupRemainder> remainders, int skipped)
    { PolicyStamp = stamp; CategoriesPending = pending; Moves = moves; Remainders = remainders; SkippedChests = skipped; }
}

/// <summary>
/// Deterministic, advisory cleanup planning (P4-01; D003 as amended by D057, D056). Chests are taken in id order and
/// stacks in cell order. A stack moves when its chest's rule rejects it, when its container has no rule and a ruled
/// chest accepts it, or when a strictly better-ranked ruled chest accepts it ("re-sort"). "Strictly better" compares
/// the <see cref="DestinationRouting.Rank"/> key without the size tie-break: ordinary before overflow, priority, match
/// class, then generality (D061). So with equal priorities Copper moves from a Materials chest to a Metals chest, it
/// moves from a priority-0 chest to a priority-1 one that accepts it, and it never moves between equally ranked
/// chests such as Metals and Crafting materials. Every move is rechecked at its write. Each move strictly improves
/// its item's placement, so repeated runs settle.
/// </summary>
public static class CleanupPlanner
{
    public const int MaximumChests = 256;
    public const int MaximumSources = 64;
    public const int MaximumMoves = 512;

    public static CleanupPlan Plan(CleanupInput input, ChestRuleEvaluator? evaluator = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        evaluator ??= new ChestRuleEvaluator();
        var taxonomy = input.Taxonomy; var answers = input.Answers;
        var stamp = taxonomy.Fingerprint + ":" + answers.Generation;
        var moves = new List<CleanupMove>(); var remainders = new List<CleanupRemainder>();
        if (!answers.CategoriesReady)
            return new CleanupPlan(stamp, true, moves.AsReadOnly(), remainders.AsReadOnly(), 0);

        var ordered = Considered(input.Chests);
        var skipped = input.Chests.Count - ordered.Count;
        var allowed = Sources(ordered);
        // A later stack may top up a stack this run created, so one press settles (D070).
        var grids = ordered.ToDictionary(x => x.Id, x => new TransferGrid(x.Inventory, mergePlanned: true), StringComparer.Ordinal);
        var ruled = Receivers(ordered, evaluator, taxonomy);
        var mucUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in ordered)
        {
            // Empty chests have nothing to clean up and do not use up the source limit (runtime 2026-10-21: 65 chests in range,
            // the 65th always skipped). They still receive items.
            if (source.OptOut || source.Inventory.Stacks.Count == 0) continue;
            if (!allowed.Contains(source.Id)) { skipped++; continue; }
            var sourceGrid = grids[source.Id];
            foreach (var stack in source.Inventory.Stacks)
            {
                var assessed = Assess(input, evaluator, ruled, source, stack);
                if (assessed.Kind == null) continue;
                var kind = assessed.Kind.Value;
                if (assessed.Item == null && assessed.Destinations.Count == 0 && !assessed.Stays)
                {
                    if (kind == CleanupKind.Rejected) remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, CleanupReason.NoDestination, null));
                    continue;
                }
                if (assessed.Stays) continue;
                var usable = assessed.Destinations.Where(x => x.Gate == CleanupGate.Usable).Select(x => (x.Chest, Route: x.Via)).ToList();
                var blocked = assessed.Destinations.FirstOrDefault(x => x.Gate == CleanupGate.Blocked);
                CleanupReason? dropped = blocked?.Blocked;
                var azuChest = assessed.Destinations.FirstOrDefault(x => x.Blocked == CleanupReason.AzuExcluded)?.Chest.Id;
                if (usable.Count == 0)
                {
                    var reason = dropped ?? CleanupReason.NoDestination;
                    // A valid placement stays valid, whatever kept it from a better chest (no room, Azu's exclusions);
                    // that is not news. Neither is an unruled container's item no chest takes.
                    if (kind != CleanupKind.Resort && (dropped != null || kind == CleanupKind.Rejected))
                        remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, reason,
                            reason == CleanupReason.AzuExcluded ? azuChest : null));
                    continue;
                }
                if (stack.Protected || !stack.TransferSupported || source.Inventory.ProtectedCells.Contains(stack.Y * source.Inventory.Width + stack.X))
                { remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, CleanupReason.Unsupported, null)); continue; }
                if (input.Held.Contains(stack.Handle))
                { remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, CleanupReason.Held, null)); continue; }

                var left = stack.Quantity; var busy = false;
                foreach (var (chest, route) in usable)
                {
                    if (left == 0) break;
                    var remote = route == CleanupRoute.MucAdd ? chest.Id : route == CleanupRoute.MucRemove ? source.Id : null;
                    // At most one MultiUserChest request per other player's chest per run (D043).
                    if (remote != null && mucUsed.Contains(remote)) { busy = true; continue; }
                    var grid = grids[chest.Id];
                    while (left > 0 && moves.Count < MaximumMoves &&
                           grid.TryFind(stack.MergeKey, stack.MaximumStack, out var x, out var y, out var merge, out var room, out var planned))
                    {
                        var units = Math.Min(left, room);
                        grid.Add(x, y, stack.MergeKey, stack.MaximumStack, units);
                        sourceGrid.Remove(stack.X, stack.Y, units);
                        moves.Add(new CleanupMove(source.Id, chest.Id, stack.Handle, stack.Identity, units, x, y, merge, route, kind, planned));
                        left -= units;
                        if (remote != null) { mucUsed.Add(remote); break; }
                    }
                    if (moves.Count >= MaximumMoves) break;
                }
                if (left > 0)
                {
                    var reason = moves.Count >= MaximumMoves ? CleanupReason.Budget : busy ? CleanupReason.MucBusy : CleanupReason.NoCapacity;
                    // A re-sort that finds no room just stays where it is: that placement is valid.
                    if (kind != CleanupKind.Resort || reason == CleanupReason.Budget)
                        remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, left, reason, null));
                }
            }
        }
        return new CleanupPlan(stamp, false, moves.AsReadOnly(), remainders.AsReadOnly(), skipped);
    }

    /// <summary>
    /// The write-time recheck of a re-sort: the destination still ranks strictly better than the source for this item,
    /// by the same rule the plan used (D061, D092: Always items included). An item without a category answer is ranked
    /// among Always listings only (B4.14). Both chests must have a configured rule.
    /// </summary>
    public static bool ResortStillBetter(ClassificationResult? item, string prefab, ChestRules source, ChestRules destination,
        IEnumerable<FacetEvidence>? facets, ClassificationTaxonomy taxonomy)
    {
        if (source == null || destination == null || source.State != ChestRuleState.Configured || destination.State != ChestRuleState.Configured) return false;
        var any = new DestinationEligibility(true, true, true);
        var from = new[] { new DestinationCandidate("source", source, any) };
        var to = new[] { new DestinationCandidate("destination", destination, any) };
        var facetRows = facets?.ToArray();
        var current = item == null ? DestinationRouting.ExactOnly(prefab, from).FirstOrDefault() : DestinationRouting.Rank(item, prefab, from, facetRows, taxonomy).FirstOrDefault();
        var target = item == null ? DestinationRouting.ExactOnly(prefab, to).FirstOrDefault() : DestinationRouting.Rank(item, prefab, to, facetRows, taxonomy).FirstOrDefault();
        return current != null && target != null && Better(target, current);
    }

    /// <summary>Strictly better ranked: the Rank key without the size tie-break and the id (D061).</summary>
    public static bool Better(RankedDestination candidate, RankedDestination current)
    {
        int Compare<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b);
        var order = Compare(current.Candidate.Rules.Overflow, candidate.Candidate.Rules.Overflow); // false (ordinary) first
        if (order != 0) return order > 0;
        order = Compare(candidate.Candidate.Rules.Priority, current.Candidate.Rules.Priority);
        if (order != 0) return order > 0;
        order = Compare((int)candidate.Match, (int)current.Match);
        if (order != 0) return order > 0;
        return Compare(current.Generality, candidate.Generality) > 0;
    }

    // The helpers below are shared with PlacementExplainer, so an explanation is the plan's own choice, not a copy of it.

    /// <summary>Configured rule chests are the only destinations; unruled containers never receive (D057).</summary>
    internal static List<(CleanupChest Chest, DestinationCandidate Candidate)> Receivers(IEnumerable<CleanupChest> chests,
        ChestRuleEvaluator evaluator, ClassificationTaxonomy taxonomy)
    {
        var ruled = new List<(CleanupChest Chest, DestinationCandidate Candidate)>();
        // D08: the store key's own test (D070), so cleanup and stash agree on which chests are destinations.
        foreach (var chest in chests)
            if (StoreOrder.Candidate(chest.Id, chest.RulePayload, evaluator, taxonomy) is { } candidate) ruled.Add((chest, candidate));
        return ruled;
    }

    internal sealed class Destination
    {
        internal readonly CleanupChest Chest; internal readonly RankedDestination Ranked; internal readonly CleanupGate Gate;
        internal readonly CleanupRoute Via; internal readonly CleanupReason? Blocked;
        internal Destination(CleanupChest chest, RankedDestination ranked, CleanupGate gate, CleanupRoute via = CleanupRoute.Direct, CleanupReason? blocked = null)
        { Chest = chest; Ranked = ranked; Gate = gate; Via = via; Blocked = blocked; }
    }

    internal sealed class Assessment
    {
        internal ChestRuleDecision Decision; internal CleanupKind? Kind; internal ClassificationResult? Item;
        /// <summary>A re-sort with no ranked own rule: the stack stays where it is (D057).</summary>
        internal bool Stays;
        internal RankedDestination? Current;
        internal List<Destination> Destinations = new();
    }

    /// <summary>
    /// Why one stack would move and where to, in plan order: every other ruled chest that ranks it, with the gate that
    /// keeps it out (not better than the current placement, rule mismatch, Azu exclusion, ownership) or its route.
    /// </summary>
    /// <summary>
    /// D086 D9: the nearest <see cref="MaximumChests"/> chests (then by ID), in ID order for a deterministic plan. Chests
    /// beyond them are not considered; the runner reports how many were in range.
    /// </summary>
    internal static List<CleanupChest> Considered(IEnumerable<CleanupChest> chests) =>
        chests.OrderBy(x => x.Distance).ThenBy(x => x.Id, StringComparer.Ordinal).Take(MaximumChests)
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToList();

    /// <summary>D086 D9: the nearest <see cref="MaximumSources"/> chests that hold items and aren't opted out give up items.</summary>
    internal static HashSet<string> Sources(IEnumerable<CleanupChest> considered) =>
        new(considered.Where(x => !x.OptOut && x.Inventory.Stacks.Count != 0).OrderBy(x => x.Distance).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(MaximumSources).Select(x => x.Id), StringComparer.Ordinal);

    internal static Assessment Assess(CleanupInput input, ChestRuleEvaluator evaluator,
        List<(CleanupChest Chest, DestinationCandidate Candidate)> ruled, CleanupChest source, TransferStack stack)
    {
        var taxonomy = input.Taxonomy; var answers = input.Answers; var prefab = stack.Identity.Prefab;
        var facets = input.InstanceFacets?.Invoke(stack);
        var result = new Assessment { Decision = evaluator.Evaluate(source.RulePayload, prefab, taxonomy, answers, facets) };
        result.Kind = result.Decision switch
        {
            ChestRuleDecision.Reject => CleanupKind.Rejected, ChestRuleDecision.NoRule => CleanupKind.Unruled,
            ChestRuleDecision.Accept => CleanupKind.Resort,
            _ => null // Pending or unreadable rules never make a stack movable (D048/D050).
        };
        if (result.Kind == null) return result;
        var item = result.Item = answers.Resolve(prefab);
        if (item == null)
        {
            // B4.14: an item the answers don't know still goes to a chest that lists it under Always. One already listed
            // under Always here moves only to a strictly better-ranked Always chest, as the stash would place it (D092).
            if (result.Kind == CleanupKind.Resort)
            {
                var own = ruled.FirstOrDefault(x => ReferenceEquals(x.Chest, source)).Candidate;
                result.Current = own == null ? null : DestinationRouting.ExactOnly(prefab, new[] { own }).FirstOrDefault();
                if (result.Current == null) { result.Stays = true; return result; }
            }
            foreach (var row in DestinationRouting.ExactOnly(prefab, ruled.Where(x => !ReferenceEquals(x.Chest, source)).Select(x => x.Candidate)))
                AddDestination(row);
            return result;
        }
        if (result.Kind == CleanupKind.Resort)
        {
            var own = ruled.FirstOrDefault(x => ReferenceEquals(x.Chest, source)).Candidate;
            result.Current = own == null ? null : DestinationRouting.Rank(item, prefab, new[] { own }, facets, taxonomy).FirstOrDefault();
            // D092: cleanup follows the stash order, so an item a chest lists under Always moves too when another chest
            // ranks strictly better for it (higher priority; Always still wins a tie). Amends D057.
            if (result.Current == null) { result.Stays = true; return result; }
        }
        var ranked = DestinationRouting.Rank(item, prefab, ruled.Where(x => !ReferenceEquals(x.Chest, source)).Select(x => x.Candidate),
            facets, taxonomy, input.CategorySize);
        foreach (var row in ranked) AddDestination(row);
        return result;

        void AddDestination(RankedDestination row)
        {
            if (row.ExistingItemFallback) return;
            var chest = ruled.First(x => ReferenceEquals(x.Candidate, row.Candidate)).Chest;
            Destination Gate(CleanupGate gate, CleanupReason? reason = null, CleanupRoute via = CleanupRoute.Direct) => new(chest, row, gate, via, reason);
            if (result.Current != null && !Better(row, result.Current)) { result.Destinations.Add(Gate(CleanupGate.NotBetter)); return; }
            // Rank and Evaluate take different paths; a destination must pass both.
            if (evaluator.Evaluate(chest.RulePayload, prefab, taxonomy, answers, facets) != ChestRuleDecision.Accept)
            { result.Destinations.Add(Gate(CleanupGate.RuleMismatch)); return; }
            if (input.AzuAllows != null && !input.AzuAllows(chest.Id, stack.Identity)) { result.Destinations.Add(Gate(CleanupGate.Blocked, CleanupReason.AzuExcluded)); return; }
            if (source.LocallyOwned && chest.LocallyOwned) result.Destinations.Add(Gate(CleanupGate.Usable));
            else if (!source.LocallyOwned && !chest.LocallyOwned) result.Destinations.Add(Gate(CleanupGate.Blocked, CleanupReason.BothRemote));
            else if (!input.MucAvailable) result.Destinations.Add(Gate(CleanupGate.Blocked, CleanupReason.OtherOwnerNoMuc));
            else result.Destinations.Add(Gate(CleanupGate.Usable, via: source.LocallyOwned ? CleanupRoute.MucAdd : CleanupRoute.MucRemove));
        }
    }
}

internal enum CleanupGate { Usable, NotBetter, RuleMismatch, Blocked }
