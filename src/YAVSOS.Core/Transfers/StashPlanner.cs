using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// One loaded container the caller has already found eligible to receive a stash: player-built, accessible to the
/// local player and within range. World-spawned containers are never passed in.
/// </summary>
public sealed class StashChest
{
    public string Id { get; }
    /// <summary>The chest's stored rule payload; empty when the chest has none.</summary>
    public string RulePayload { get; }
    public TransferInventorySnapshot Inventory { get; }
    /// <summary>The local peer is the chest's current owner (direct writes possible).</summary>
    public bool LocallyOwned { get; }
    /// <summary>For another player's chest: a MultiUserChest request to its owner can be read correctly (D061 Q8).</summary>
    public bool OwnerFormatKnown { get; }
    public StashChest(string id, string rulePayload, TransferInventorySnapshot inventory, bool locallyOwned, bool ownerFormatKnown = true)
    {
        Id = TransferCanonical.Text(id, 256, false);
        RulePayload = rulePayload ?? throw new ArgumentNullException(nameof(rulePayload));
        Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        LocallyOwned = locallyOwned; OwnerFormatKnown = ownerFormatKnown;
    }
}

public sealed class StashInput
{
    public ClassificationTaxonomy Taxonomy { get; }
    public ICategoryAnswers Answers { get; }
    /// <summary>The player's inventory. Its protected cells (the hotbar unless the player allows it, AzuEPI quick slots) never give items.</summary>
    public TransferInventorySnapshot Player { get; }
    /// <summary>Receiving chests, nearest first; that order decides among unruled chests (D061 Q2).</summary>
    public IReadOnlyList<StashChest> Chests { get; }
    public Func<string, int>? CategorySize { get; }
    /// <summary>A MultiUserChest intercept is present, so another player's chest can receive through it (D043).</summary>
    public bool MucAvailable { get; }
    /// <summary>Stacks on the cursor; they stay where they are.</summary>
    public IReadOnlyCollection<Guid> Held { get; }
    /// <summary>
    /// Single-item key: only these stacks are stashed. Equipped and quest items and protected cells still never move
    /// (<see cref="Kept"/>, D065). Null stashes everything the player may give.
    /// </summary>
    public IReadOnlyCollection<Guid>? Only { get; }
    /// <summary>
    /// Fresh evidence of one stack's own instance (EpicLoot rarity, condition) for instance filters (P6-07b). Null:
    /// those filters see Unknown. The writer's recheck must use the same evidence.
    /// </summary>
    public Func<TransferStack, IReadOnlyList<FacetEvidence>>? InstanceFacets { get; }
    /// <summary>
    /// AzuAutoStore does the storing (D042, D070): the ruled tier is what its store key does in YAVSOS's order, and what
    /// no ruled chest takes is left to Azu's own settings, so there is no holder tier.
    /// </summary>
    public bool AzuStores { get; }
    /// <summary>AzuAutoStore's own exclusions, by chest id; a chest it excludes the item from is no destination.</summary>
    public Func<string, TransferItemIdentity, bool>? AzuAllows { get; }
    public StashInput(ClassificationTaxonomy taxonomy, ICategoryAnswers answers, TransferInventorySnapshot player, IEnumerable<StashChest> chests,
        Func<string, int>? categorySize = null, bool mucAvailable = false, IEnumerable<Guid>? held = null, IEnumerable<Guid>? only = null,
        Func<TransferStack, IReadOnlyList<FacetEvidence>>? instanceFacets = null, bool azuStores = false,
        Func<string, TransferItemIdentity, bool>? azuAllows = null)
    {
        InstanceFacets = instanceFacets; AzuStores = azuStores; AzuAllows = azuAllows;
        Taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        Answers = answers ?? throw new ArgumentNullException(nameof(answers));
        Player = player ?? throw new ArgumentNullException(nameof(player));
        Chests = Array.AsReadOnly((chests ?? throw new ArgumentNullException(nameof(chests))).ToArray());
        if (Chests.Any(x => x == null)) throw new ArgumentException("Null stash chest.");
        if (Chests.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Chests.Count) throw new ArgumentException("Duplicate stash chest id.");
        var inventories = Chests.Select(x => x.Inventory.Stamp.InventoryId).Append(player.Stamp.InventoryId);
        if (inventories.Distinct(StringComparer.Ordinal).Count() != Chests.Count + 1) throw new ArgumentException("Two stash inventories share one id.");
        CategorySize = categorySize; MucAvailable = mucAvailable;
        Held = new HashSet<Guid>(held ?? Array.Empty<Guid>());
        Only = only == null ? null : new HashSet<Guid>(only);
    }
}

public enum StashTier
{
    /// <summary>A ruled chest accepts the item.</summary>
    Ruled,
    /// <summary>An unruled chest that already holds the item (content matching, D061 Q2).</summary>
    Holder
}

public sealed class StashMove
{
    public string DestinationId { get; }
    public Guid Handle { get; }
    public TransferItemIdentity Identity { get; }
    public int Units { get; }
    public int SourceX { get; }
    public int SourceY { get; }
    public int DestinationX { get; }
    public int DestinationY { get; }
    public Guid? MergeHandle { get; }
    /// <summary><see cref="CleanupRoute.Direct"/> or <see cref="CleanupRoute.MucAdd"/>; the player's inventory is always local.</summary>
    public CleanupRoute Route { get; }
    public StashTier Tier { get; }
    /// <summary>
    /// Merges into the stack an earlier move of this run creates at the destination cell; <see cref="MergeHandle"/> is
    /// null, and the writer takes the handle of the stack it finds there (same item kind) or refuses (D070).
    /// </summary>
    public bool MergesPlanned { get; }
    internal StashMove(string destination, Guid handle, TransferItemIdentity identity, int units, int sourceX, int sourceY, int x, int y,
        Guid? merge, CleanupRoute route, StashTier tier, bool mergesPlanned = false)
    {
        MergesPlanned = mergesPlanned;
        DestinationId = destination; Handle = handle; Identity = identity; Units = units; SourceX = sourceX; SourceY = sourceY;
        DestinationX = x; DestinationY = y; MergeHandle = merge; Route = route; Tier = tier;
    }

    /// <summary>
    /// The stack to merge into, from the writer's fresh captures: <see cref="MergeHandle"/>, or for a planned merge the
    /// stack now in the destination cell when it is the moving stack's kind. False: the inventory changed, refuse.
    /// <see cref="DepositPlan"/> checks the rest (room, maximum, protection).
    /// </summary>
    public bool TryMergeTarget(TransferInventorySnapshot source, TransferInventorySnapshot destination, out Guid? merge) =>
        TransferGrid.TryMergeTarget(MergesPlanned, MergeHandle, source, Handle, destination, DestinationX, DestinationY, out merge);
}

public sealed class StashRemainder
{
    public Guid Handle { get; }
    public TransferItemIdentity Identity { get; }
    public int Units { get; }
    public CleanupReason Reason { get; }
    internal StashRemainder(Guid handle, TransferItemIdentity identity, int units, CleanupReason reason)
    { Handle = handle; Identity = identity; Units = units; Reason = reason; }
}

public sealed class StashPlan
{
    /// <summary>Rule taxonomy fingerprint and category-answer generation; any change refuses the rest of the run.</summary>
    public string PolicyStamp { get; }
    /// <summary>Categories are not ready: the run is refused with one message and plans nothing.</summary>
    public bool CategoriesPending { get; }
    public IReadOnlyList<StashMove> Moves { get; }
    public IReadOnlyList<StashRemainder> Remainders { get; }
    /// <summary>Chests beyond <see cref="StashPlanner.MaximumChests"/> that were left out of this run.</summary>
    public int SkippedChests { get; }
    internal StashPlan(string stamp, bool pending, IReadOnlyList<StashMove> moves, IReadOnlyList<StashRemainder> remainders, int skipped)
    { PolicyStamp = stamp; CategoriesPending = pending; Moves = moves; Remainders = remainders; SkippedChests = skipped; }
}

/// <summary>
/// Deterministic, advisory stash planning (P5-01, D061): the player's inventory into nearby chests when YAVSOS does
/// the stashing (AzuAutoStore absent). Each stack goes first to ruled chests that accept it, in
/// <see cref="DestinationRouting.Rank"/> order (priority, then specificity), then to unruled chests that already hold
/// that item, nearest first. Equipped and quest items and protected cells never give items. Stacks are taken in cell
/// order. An item no chest takes stays in the inventory without a report; only an item a chest would take but cannot
/// is reported. Every move is rechecked at its write.
/// </summary>
public static class StashPlanner
{
    public const int MaximumChests = 256;
    public const int MaximumMoves = 512;

    public static StashPlan Plan(StashInput input, ChestRuleEvaluator? evaluator = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        evaluator ??= new ChestRuleEvaluator();
        var taxonomy = input.Taxonomy; var answers = input.Answers;
        var stamp = taxonomy.Fingerprint + ":" + answers.Generation;
        var moves = new List<StashMove>(); var remainders = new List<StashRemainder>();
        // D086 D4 (M12): while categories load, exact-item (Always) rules and chests already holding the item still take it;
        // category parts wait (D048). The plan says categories were pending.
        var pending = !answers.CategoriesReady;

        var chests = input.Chests.Take(MaximumChests).ToList();
        var skipped = input.Chests.Count - chests.Count;
        // A later stack of the same item may top up a stack this run created, as the game's own stacking would (D070).
        var grids = chests.ToDictionary(x => x.Id, x => new TransferGrid(x.Inventory, mergePlanned: true), StringComparer.Ordinal);
        var playerGrid = new TransferGrid(input.Player);
        var (ruled, unruled) = Receivers(chests, evaluator, taxonomy);
        foreach (var stack in input.Player.Stacks)
        {
            if (input.Only != null && !input.Only.Contains(stack.Handle)) continue;
            if (Kept(input, stack)) continue;
            var routes = Routes(input, evaluator, ruled, unruled, stack);
            if (routes.Count == 0)
            {
                // D087: an item that waits for categories is reported, not left silently (D086 D4).
                if (Waiting(input, evaluator, ruled, stack.Identity.Prefab).Count != 0 && !Kept(input, stack))
                    remainders.Add(new StashRemainder(stack.Handle, stack.Identity, stack.Quantity, CleanupReason.CategoriesPending));
                continue;
            }
            if (!stack.TransferSupported) { remainders.Add(new StashRemainder(stack.Handle, stack.Identity, stack.Quantity, CleanupReason.Unsupported)); continue; }
            if (input.Held.Contains(stack.Handle)) { remainders.Add(new StashRemainder(stack.Handle, stack.Identity, stack.Quantity, CleanupReason.Held)); continue; }

            var left = stack.Quantity; CleanupReason? dropped = null;
            foreach (var row in routes)
            {
                if (left == 0 || moves.Count >= MaximumMoves) break;
                if (row.Blocked != null) { dropped ??= row.Blocked; continue; }
                var grid = grids[row.Chest.Id];
                while (left > 0 && moves.Count < MaximumMoves &&
                       grid.TryFind(stack.MergeKey, stack.MaximumStack, out var x, out var y, out var merge, out var room, out var planned))
                {
                    var units = Math.Min(left, room);
                    grid.Add(x, y, stack.MergeKey, stack.MaximumStack, units);
                    playerGrid.Remove(stack.X, stack.Y, units);
                    moves.Add(new StashMove(row.Chest.Id, stack.Handle, stack.Identity, units, stack.X, stack.Y, x, y, merge, row.Via, row.Tier, planned));
                    left -= units;
                }
            }
            if (left > 0)
                remainders.Add(new StashRemainder(stack.Handle, stack.Identity, left,
                    moves.Count >= MaximumMoves ? CleanupReason.Budget : left == stack.Quantity && dropped != null ? dropped.Value : CleanupReason.NoCapacity));
        }
        return new StashPlan(stamp, pending, moves.AsReadOnly(), remainders.AsReadOnly(), skipped);
    }

    // The helpers below are shared with PlacementExplainer, so an explanation is the plan's own choice, not a copy of it.

    /// <summary>Ruled chests (configured rules) and unruled ones (no rule, D086 M1). An unreadable rule accepts nothing and is no unruled chest either (D048).</summary>
    internal static (List<(StashChest Chest, DestinationCandidate Candidate)> Ruled, List<StashChest> Unruled) Receivers(
        IEnumerable<StashChest> chests, ChestRuleEvaluator evaluator, ClassificationTaxonomy taxonomy)
    {
        var ruled = new List<(StashChest Chest, DestinationCandidate Candidate)>();
        var unruled = new List<StashChest>();
        foreach (var chest in chests)
        {
            if (evaluator.Unruled(chest.RulePayload, taxonomy)) { unruled.Add(chest); continue; }
            var candidate = StoreOrder.Candidate(chest.Id, chest.RulePayload, evaluator, taxonomy);
            if (candidate != null) ruled.Add((chest, candidate));
        }
        return (ruled, unruled);
    }

    /// <summary>
    /// Equipped and quest items never move, nor do protected cells (the hotbar and quick slots unless the player's settings
    /// include them), for the single-item key too, as AzuAutoStore's single-item store does (D061 Q3, D065).
    /// </summary>
    internal static bool Kept(StashInput input, TransferStack stack) =>
        stack.Protected || input.Player.ProtectedCells.Contains(stack.Y * input.Player.Width + stack.X);

    /// <summary>One chest that would take the stack, and the ownership gate that keeps it from receiving (null: usable).</summary>
    internal sealed class Route
    {
        internal readonly StashChest Chest; internal readonly StashTier Tier; internal readonly RankedDestination? Ranked;
        internal readonly CleanupRoute Via; internal readonly CleanupReason? Blocked;
        internal Route(StashChest chest, StashTier tier, RankedDestination? ranked, CleanupRoute via, CleanupReason? blocked)
        { Chest = chest; Tier = tier; Ranked = ranked; Via = via; Blocked = blocked; }
    }

    /// <summary>
    /// The chests that would take this stack, in plan order: ruled chests in <see cref="StoreOrder"/>, then (unless
    /// AzuAutoStore stores the rest) unruled chests already holding the item, nearest first.
    /// </summary>
    internal static List<Route> Routes(StashInput input, ChestRuleEvaluator evaluator,
        List<(StashChest Chest, DestinationCandidate Candidate)> ruled, List<StashChest> unruled, TransferStack stack)
    {
        var taxonomy = input.Taxonomy; var answers = input.Answers; var prefab = stack.Identity.Prefab;
        var routes = new List<Route>();
        var item = answers.CategoriesReady ? answers.Resolve(prefab) : null;
        if (item == null)
        {
            // No category answer (loading, or an item the answers don't know): Always rules only (D086 D4, B4.14).
            var chests = ruled.ToDictionary(x => x.Candidate, x => x.Chest);
            foreach (var row in DestinationRouting.ExactOnly(prefab, ruled.Select(x => x.Candidate)))
                if (evaluator.Evaluate(chests[row.Candidate].RulePayload, prefab, taxonomy, answers) == ChestRuleDecision.Accept)
                    routes.Add(Gate(input, chests[row.Candidate], StashTier.Ruled, row, stack));
            // A category chest that may take it once categories are ready, and would rank above an Always chest, keeps it
            // from that chest and from unruled holders meanwhile, so cleanup need not move it again later (D092).
            var waiting = Waiting(input, evaluator, ruled, prefab);
            if (waiting.Count != 0)
            {
                var outranked = routes.FindIndex(route => waiting.Any(x => Outranks(x.Candidate.Rules, route.Ranked!.Candidate.Rules)));
                if (outranked >= 0) routes.RemoveRange(outranked, routes.Count - outranked);
                return routes;
            }
        }
        else
        {
            var facets = input.InstanceFacets?.Invoke(stack);
            var chests = ruled.ToDictionary(x => x.Candidate, x => x.Chest);
            foreach (var row in StoreOrder.Accepting(item, prefab, ruled.Select(x => x.Candidate), x => chests[x].RulePayload, facets, taxonomy, answers,
                         input.CategorySize, evaluator))
                routes.Add(Gate(input, chests[row.Candidate], StashTier.Ruled, row, stack));
        }
        if (input.AzuStores) return routes;
        foreach (var chest in unruled)
            if (chest.Inventory.Stacks.Any(x => StringComparer.Ordinal.Equals(x.Identity.Prefab, prefab))) routes.Add(Gate(input, chest, StashTier.Holder, null, stack));
        return routes;
    }

    /// <summary>Ruled chests whose category parts may take the item once categories are ready (D086 D4); none when they are.</summary>
    internal static List<(StashChest Chest, DestinationCandidate Candidate)> Waiting(StashInput input, ChestRuleEvaluator evaluator,
        List<(StashChest Chest, DestinationCandidate Candidate)> ruled, string prefab) =>
        input.Answers.CategoriesReady ? new List<(StashChest, DestinationCandidate)>()
        : ruled.Where(x => evaluator.Evaluate(x.Chest.RulePayload, prefab, input.Taxonomy, input.Answers) == ChestRuleDecision.RejectCategoriesPending).ToList();

    // DestinationRouting.Rank's order before match class: ordinary before overflow, then larger priority. On a tie the
    // Always chest wins (an exact match ranks above a category), so only a strictly better chest outranks it.
    private static bool Outranks(ChestRules a, ChestRules b) =>
        a.Overflow != b.Overflow ? !a.Overflow : a.Priority > b.Priority;

    // As in cleanup: AzuAutoStore's exclusions, then ownership. Azu's pause is not read: it only pauses Azu's ground pickup (D097).
    private static Route Gate(StashInput input, StashChest chest, StashTier tier, RankedDestination? ranked, TransferStack stack) =>
        input.AzuAllows != null && !input.AzuAllows(chest.Id, stack.Identity) ? new Route(chest, tier, ranked, CleanupRoute.Direct, CleanupReason.AzuExcluded)
        : chest.LocallyOwned ? new Route(chest, tier, ranked, CleanupRoute.Direct, null)
        : !input.MucAvailable ? new Route(chest, tier, ranked, CleanupRoute.MucAdd, CleanupReason.OtherOwnerNoMuc)
        : !chest.OwnerFormatKnown ? new Route(chest, tier, ranked, CleanupRoute.MucAdd, CleanupReason.OtherOwnerFormatUnknown)
        : new Route(chest, tier, ranked, CleanupRoute.MucAdd, null);
}
