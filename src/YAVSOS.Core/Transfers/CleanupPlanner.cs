using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>For another player's chest: whether a MultiUserChest request can reach its owner, as for stash (F3, D117).</summary>
    public OwnerMucState OwnerMuc { get; }
    public CleanupChest(string id, string rulePayload, TransferInventorySnapshot inventory, bool locallyOwned, bool optOut = false,
        double distance = 0, OwnerMucState ownerMuc = OwnerMucState.Usable)
    {
        Id = TransferCanonical.Text(id, 256, false);
        RulePayload = rulePayload ?? throw new ArgumentNullException(nameof(rulePayload));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        if (double.IsNaN(distance) || distance < 0) throw new ArgumentOutOfRangeException(nameof(distance));
        LocallyOwned = locallyOwned; OptOut = optOut; Distance = distance; OwnerMuc = ownerMuc;
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
    /// <summary>
    /// A2-01 part 2: Lab cost check. When set, <see cref="CleanupPlanner.Plan"/> reports once per run how long its parts
    /// took in total (milliseconds): the instance-facets read, rule evaluation, ranking, AzuAutoStore's exclusions and
    /// the whole plan. Null (the release build): nothing is timed.
    /// </summary>
    public Action<string, double>? Timed { get; }
    public CleanupInput(ClassificationTaxonomy taxonomy, ICategoryAnswers answers, IEnumerable<CleanupChest> chests,
        Func<string, int>? categorySize = null, bool mucAvailable = false, Func<string, TransferItemIdentity, bool>? azuAllows = null,
        IEnumerable<Guid>? held = null, Func<TransferStack, IReadOnlyList<FacetEvidence>>? instanceFacets = null,
        Action<string, double>? timed = null)
    {
        InstanceFacets = instanceFacets; Timed = timed;
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
    public MoveRoute Route { get; }
    public CleanupKind Kind { get; }
    /// <summary>Merges into a stack an earlier move of this run creates; see <see cref="TryMergeTarget"/> (D070: one press settles).</summary>
    public bool MergesPlanned { get; }
    internal CleanupMove(string source, string destination, Guid handle, TransferItemIdentity identity, int units, int x, int y,
        Guid? merge, MoveRoute route, CleanupKind kind, bool mergesPlanned = false)
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
    public RunReason Reason { get; }
    /// <summary>For <see cref="RunReason.AzuExcluded"/>, the first excluded chest; otherwise null.</summary>
    public string? DestinationId { get; }
    internal CleanupRemainder(string source, Guid handle, TransferItemIdentity identity, int units, RunReason reason, string? destination)
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
        if (input.Timed == null) return Plan(input, evaluator, null);
        var clock = new PlanClock(); var started = Stopwatch.GetTimestamp();
        var plan = Plan(input, evaluator, clock);
        clock.Report(input.Timed, Stopwatch.GetTimestamp() - started);
        return plan;
    }

    /// <summary>A2-01 part 2: one run's planning time by part, summed over every stack and destination.</summary>
    internal sealed class PlanClock
    {
        internal long Facets, Evaluate, Rank, Azu;
        /// <summary>Adds the time since <paramref name="since"/> to <paramref name="bucket"/>; returns now.</summary>
        internal static long Lap(ref long bucket, long since) { var now = Stopwatch.GetTimestamp(); bucket += now - since; return now; }
        internal void Report(Action<string, double> timed, long total)
        {
            static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            timed("facets", Ms(Facets)); timed("evaluate", Ms(Evaluate)); timed("rank", Ms(Rank)); timed("azu", Ms(Azu));
            timed("other", Ms(total - Facets - Evaluate - Rank - Azu)); timed("total", Ms(total));
        }
    }

    /// <summary>
    /// Q10 (D132): one plan's rankings, so each item is ranked against the receivers once instead of once per stack. Measured
    /// 2026-11-03 (86 containers, about 920 stacks): ranking was 81–89 % of a 221 ms cold, 48 ms warm plan. The receivers and
    /// everything <see cref="DestinationRouting.Rank"/> reads are fixed for the plan, and Rank judges each chest on its own and
    /// ends in a total order (ID), so the ranking over all receivers without the source chest equals the ranking over the
    /// others. The key is the item and its instance evidence (rarity, condition, crafter), which filters read.
    /// </summary>
    internal sealed class RouteCache
    {
        private readonly Dictionary<string, IReadOnlyList<RankedDestination>> rows = new(StringComparer.Ordinal);
        private Dictionary<DestinationCandidate, CleanupChest>? chests;
        internal bool TryGet(string key, out IReadOnlyList<RankedDestination> ranked) => rows.TryGetValue(key, out ranked!);
        internal void Set(string key, IReadOnlyList<RankedDestination> ranked) => rows[key] = ranked;
        /// <summary>The receiver chest of a ranked candidate (candidates compare by reference).</summary>
        internal CleanupChest Chest(List<(CleanupChest Chest, DestinationCandidate Candidate)> ruled, DestinationCandidate candidate)
        {
            chests ??= ruled.ToDictionary(x => x.Candidate, x => x.Chest);
            return chests[candidate];
        }
        internal static string Key(string prefab, IReadOnlyList<FacetEvidence>? facets)
        {
            if (facets == null || facets.Count == 0) return prefab;
            var text = new System.Text.StringBuilder(prefab);
            foreach (var facet in facets.OrderBy(x => x.Dimension, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal))
                text.Append('\u0001').Append(facet.Dimension).Append('=').Append(facet.Value).Append(facet.Recognized ? "" : "?");
            return text.ToString();
        }
    }

    private static CleanupPlan Plan(CleanupInput input, ChestRuleEvaluator? evaluator, PlanClock? clock)
    {
        var routes = new RouteCache();
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
                var assessed = Assess(input, evaluator, ruled, source, stack, clock, routes);
                if (assessed.Kind == null) continue;
                var kind = assessed.Kind.Value;
                if (assessed.Item == null && assessed.Destinations.Count == 0 && !assessed.Stays)
                {
                    if (kind == CleanupKind.Rejected) remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, RunReason.NoDestination, null));
                    continue;
                }
                if (assessed.Stays) continue;
                var usable = assessed.Destinations.Where(x => x.Gate == CleanupGate.Usable).Select(x => (x.Chest, Route: x.Via)).ToList();
                var blocked = assessed.Destinations.FirstOrDefault(x => x.Gate == CleanupGate.Blocked);
                RunReason? dropped = blocked?.Blocked;
                var azuChest = assessed.Destinations.FirstOrDefault(x => x.Blocked == RunReason.AzuExcluded)?.Chest.Id;
                if (usable.Count == 0)
                {
                    var reason = dropped ?? RunReason.NoDestination;
                    // A valid placement stays valid, whatever kept it from a better chest (no room, Azu's exclusions);
                    // that is not news. Neither is an unruled container's item no chest takes.
                    if (kind != CleanupKind.Resort && (dropped != null || kind == CleanupKind.Rejected))
                        remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, reason,
                            reason == RunReason.AzuExcluded ? azuChest : null));
                    continue;
                }
                if (stack.Protected || !stack.TransferSupported || source.Inventory.ProtectedCells.Contains(stack.Y * source.Inventory.Width + stack.X))
                { remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, RunReason.Unsupported, null)); continue; }
                if (input.Held.Contains(stack.Handle))
                { remainders.Add(new CleanupRemainder(source.Id, stack.Handle, stack.Identity, stack.Quantity, RunReason.Held, null)); continue; }

                var left = stack.Quantity; var busy = false;
                foreach (var (chest, route) in usable)
                {
                    if (left == 0) break;
                    var remote = route == MoveRoute.MucAdd ? chest.Id : route == MoveRoute.MucRemove ? source.Id : null;
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
                    var reason = moves.Count >= MaximumMoves ? RunReason.Budget : busy ? RunReason.MucBusy : RunReason.NoCapacity;
                    // A re-sort that finds no room just stays where it is: that placement is valid.
                    if (kind != CleanupKind.Resort || reason == RunReason.Budget)
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
        internal readonly MoveRoute Via; internal readonly RunReason? Blocked;
        internal Destination(CleanupChest chest, RankedDestination ranked, CleanupGate gate, MoveRoute via = MoveRoute.Direct, RunReason? blocked = null)
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
        List<(CleanupChest Chest, DestinationCandidate Candidate)> ruled, CleanupChest source, TransferStack stack, PlanClock? clock = null,
        RouteCache? routes = null)
    {
        var taxonomy = input.Taxonomy; var answers = input.Answers; var prefab = stack.Identity.Prefab;
        var t = clock == null ? 0 : Stopwatch.GetTimestamp();
        var facets = input.InstanceFacets?.Invoke(stack);
        if (clock != null) t = PlanClock.Lap(ref clock.Facets, t);
        var result = new Assessment { Decision = evaluator.Evaluate(source.RulePayload, prefab, taxonomy, answers, facets) };
        if (clock != null) PlanClock.Lap(ref clock.Evaluate, t);
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
                t = clock == null ? 0 : Stopwatch.GetTimestamp();
                result.Current = own == null ? null : DestinationRouting.ExactOnly(prefab, new[] { own }).FirstOrDefault();
                if (clock != null) PlanClock.Lap(ref clock.Rank, t);
                if (result.Current == null) { result.Stays = true; return result; }
            }
            t = clock == null ? 0 : Stopwatch.GetTimestamp();
            var exact = Others(Routed("exact\u0001" + prefab, () => DestinationRouting.ExactOnly(prefab, ruled.Select(x => x.Candidate))),
                () => DestinationRouting.ExactOnly(prefab, ruled.Where(x => !ReferenceEquals(x.Chest, source)).Select(x => x.Candidate)));
            if (clock != null) PlanClock.Lap(ref clock.Rank, t);
            foreach (var row in exact) AddDestination(row);
            return result;
        }
        if (result.Kind == CleanupKind.Resort)
        {
            var own = ruled.FirstOrDefault(x => ReferenceEquals(x.Chest, source)).Candidate;
            t = clock == null ? 0 : Stopwatch.GetTimestamp();
            result.Current = own == null ? null : DestinationRouting.Rank(item, prefab, new[] { own }, facets, taxonomy).FirstOrDefault();
            if (clock != null) PlanClock.Lap(ref clock.Rank, t);
            // D092: cleanup follows the stash order, so an item a chest lists under Always moves too when another chest
            // ranks strictly better for it (higher priority; Always still wins a tie). Amends D057.
            if (result.Current == null) { result.Stays = true; return result; }
        }
        t = clock == null ? 0 : Stopwatch.GetTimestamp();
        var ranked = Others(Routed(RouteCache.Key(prefab, facets), () => DestinationRouting.Rank(item, prefab, ruled.Select(x => x.Candidate),
                facets, taxonomy, input.CategorySize)),
            () => DestinationRouting.Rank(item, prefab, ruled.Where(x => !ReferenceEquals(x.Chest, source)).Select(x => x.Candidate),
                facets, taxonomy, input.CategorySize));
        if (clock != null) PlanClock.Lap(ref clock.Rank, t);
        foreach (var row in ranked) AddDestination(row);
        return result;

        // Q10 (D132): with the plan's cache, the ranking over every receiver, ranked once per key; without one (explanations),
        // null, and Others ranks the receivers other than the source directly, as before.
        IReadOnlyList<RankedDestination>? Routed(string key, Func<IReadOnlyList<RankedDestination>> rankAll)
        {
            if (routes == null) return null;
            if (!routes.TryGet(key, out var all)) routes.Set(key, all = rankAll());
            return all;
        }
        IEnumerable<RankedDestination> Others(IReadOnlyList<RankedDestination>? all, Func<IReadOnlyList<RankedDestination>> rankOthers)
        {
            if (all == null) return rankOthers();
            var own = ruled.FirstOrDefault(x => ReferenceEquals(x.Chest, source)).Candidate;
            return own == null ? all : all.Where(x => !ReferenceEquals(x.Candidate, own));
        }

        void AddDestination(RankedDestination row)
        {
            if (row.ExistingItemFallback) return;
            var chest = routes != null ? routes.Chest(ruled, row.Candidate) : ruled.First(x => ReferenceEquals(x.Candidate, row.Candidate)).Chest;
            Destination Gate(CleanupGate gate, RunReason? reason = null, MoveRoute via = MoveRoute.Direct) => new(chest, row, gate, via, reason);
            if (result.Current != null && !Better(row, result.Current)) { result.Destinations.Add(Gate(CleanupGate.NotBetter)); return; }
            // Rank and Evaluate take different paths; a destination must pass both.
            var since = clock == null ? 0 : Stopwatch.GetTimestamp();
            var accepts = evaluator.Evaluate(chest.RulePayload, prefab, taxonomy, answers, facets) == ChestRuleDecision.Accept;
            if (clock != null) since = PlanClock.Lap(ref clock.Evaluate, since);
            if (!accepts) { result.Destinations.Add(Gate(CleanupGate.RuleMismatch)); return; }
            var azuAllows = input.AzuAllows == null || input.AzuAllows(chest.Id, stack.Identity);
            if (clock != null && input.AzuAllows != null) PlanClock.Lap(ref clock.Azu, since);
            if (!azuAllows) { result.Destinations.Add(Gate(CleanupGate.Blocked, RunReason.AzuExcluded)); return; }
            if (source.LocallyOwned && chest.LocallyOwned) result.Destinations.Add(Gate(CleanupGate.Usable));
            else if (!source.LocallyOwned && !chest.LocallyOwned) result.Destinations.Add(Gate(CleanupGate.Blocked, RunReason.BothRemote));
            else if (!input.MucAvailable) result.Destinations.Add(Gate(CleanupGate.Blocked, RunReason.OtherOwnerNoMuc));
            // Known at plan time, as in stash, so the plan and the explanation skip a chest the write would skip (B4.33 §3).
            else if ((source.LocallyOwned ? chest : source).OwnerMuc.Skip() is { } skip) result.Destinations.Add(Gate(CleanupGate.Blocked, skip));
            else result.Destinations.Add(Gate(CleanupGate.Usable, via: source.LocallyOwned ? MoveRoute.MucAdd : MoveRoute.MucRemove));
        }
    }
}

internal enum CleanupGate { Usable, NotBetter, RuleMismatch, Blocked }
