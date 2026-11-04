using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Transfers;

/// <summary>What a stash or cleanup run would do with one stack right now (P6-05).</summary>
public enum PlacementOutcome
{
    /// <summary>A chest takes it: <see cref="PlacementExplanation.ChosenId"/>.</summary>
    Goes,
    /// <summary>The chests that take it are full.</summary>
    NoRoom,
    /// <summary>The chests that take it are all closed to it: <see cref="PlacementExplanation.Blocked"/> says why.</summary>
    Blocked,
    /// <summary>No chest in range takes it.</summary>
    NoChest,
    /// <summary>Cleanup: where it is is valid and no strictly better chest can take it.</summary>
    Stays,
    /// <summary>Protected (equipped, quest, hotbar), held on the cursor or not movable exactly.</summary>
    Kept,
    /// <summary>Categories are not ready yet (D048).</summary>
    NotReady,
    /// <summary>Cleanup: its chest is set to "Don't clean up this container" (D057).</summary>
    OptedOut,
    /// <summary>The run's chest limits leave its chest out; run again nearer.</summary>
    Skipped
}

/// <summary>X1: what made the chosen chest take the stack, for "because it counts as Metals".</summary>
public enum PlacementBasis
{
    /// <summary>No chest was chosen.</summary>
    None,
    /// <summary>A rule category takes it: <see cref="PlacementExplanation.Category"/> names the deciding one.</summary>
    Category,
    /// <summary>The chest lists the item under "Always".</summary>
    Always,
    /// <summary>The chest already holds the item (no rule, or the rule's existing-item fallback).</summary>
    Holds,
    /// <summary>A rule that takes anything.</summary>
    Anything,
    /// <summary>A rule of filters alone, without a category.</summary>
    Filters
}

/// <summary>One chest's part in the answer.</summary>
public enum ChestVerdict
{
    Chosen,
    /// <summary>Takes it too, ranked lower; used once the chosen chest is full.</summary>
    Backup,
    /// <summary>Takes it, but has no room.</summary>
    Full,
    /// <summary>Cleanup: the chest the item is in.</summary>
    Current,
    /// <summary>Cleanup: takes it, but ranks no higher than where it is (D057, D061).</summary>
    NotBetter,
    RuleRejects,
    /// <summary>The chest lists the item under "Never".</summary>
    Never,
    /// <summary>No rule: stash only fills it with items it already holds; cleanup never fills it.</summary>
    NoRule,
    RuleUnreadable,
    /// <summary>The rule was cleared; only its "Never" list applies.</summary>
    RuleCleared,
    OtherOwnerNoMuc,
    OtherOwnerFormatUnknown,
    /// <summary>The owner's MultiUserChest can't take requests (F3).</summary>
    OtherOwnerMucUnusable,
    /// <summary>No MultiUserChest answered the owner's probe (F3).</summary>
    OtherOwnerMucSilent,
    /// <summary>The owner's MultiUserChest is still being checked (F3).</summary>
    OtherOwnerMucChecking,
    BothRemote,
    AzuExcluded,
    CategoriesPending,
    OutOfRange,
    /// <summary>Beyond the run's chest limit.</summary>
    OverLimit,
    /// <summary>Not looked at: the item stays for a reason of its own.</summary>
    NotConsidered
}

public sealed class ChestExplanation
{
    public string ChestId { get; }
    public ChestVerdict Verdict { get; }
    /// <summary>Short player-facing reason, e.g. "category Metals, priority 4 (Normal)".</summary>
    public string Text { get; }
    /// <summary>The routing rank when the chest's rule takes the item (the planners' own <see cref="DestinationRouting.Rank"/> row).</summary>
    public RankedDestination? Rank { get; }
    internal ChestExplanation(string id, ChestVerdict verdict, string text, RankedDestination? rank)
    { ChestId = id; Verdict = verdict; Text = text; Rank = rank; }
}

public sealed class PlacementExplanation
{
    public string Prefab { get; }
    public PlacementOutcome Outcome { get; }
    public string? ChosenId { get; }
    /// <summary>For <see cref="PlacementOutcome.Blocked"/>: the first gate that closed an accepting chest, as the run reports it.</summary>
    public RunReason? Blocked { get; }
    /// <summary>X1: why <see cref="ChosenId"/> takes it; <see cref="PlacementBasis.None"/> when nothing was chosen.</summary>
    public PlacementBasis Basis { get; }
    /// <summary>For <see cref="PlacementBasis.Category"/>: the deciding category's English source name, such as "Metals" (diagnostics).</summary>
    public string? Category { get; }
    /// <summary>
    /// For <see cref="PlacementBasis.Category"/>: the deciding category's name as players see it, a message or a name an
    /// admin set (<see cref="CategoryNames.Of"/>).
    /// </summary>
    public object? CategoryName { get; }
    /// <summary>One line, e.g. "Copper → Metals chest: category Metals, priority 4 (Normal)".</summary>
    public string Summary { get; }
    /// <summary>Every chest passed in, in run order, then those out of range.</summary>
    public IReadOnlyList<ChestExplanation> Chests { get; }
    internal PlacementExplanation(string prefab, PlacementOutcome outcome, string? chosen, RunReason? blocked, string summary, List<ChestExplanation> chests,
        PlacementBasis basis = PlacementBasis.None, (string Source, object Shown)? category = null)
    {
        Prefab = prefab; Outcome = outcome; ChosenId = chosen; Blocked = blocked; Summary = summary; Chests = chests.AsReadOnly();
        Basis = chosen == null ? PlacementBasis.None : basis;
        if (Basis == PlacementBasis.Category && category is { } named) { Category = named.Source; CategoryName = named.Shown; }
    }
    public ChestExplanation? For(string chestId) => Chests.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ChestId, chestId));
}

/// <summary>
/// X16: the stack-independent part of <c>PlacementExplainer.Stash</c>
/// (the run's chest limit and <see cref="StashPlanner.Receivers"/>), prepared once per gathered input so each stack
/// asked about costs only its routes. The answers equal the one-shot ones for the same input. Read only.
/// </summary>
public sealed class StashExplainContext
{
    public StashInput Input { get; }
    internal ChestRuleEvaluator Evaluator { get; }
    internal List<StashChest> Chests { get; }
    internal List<(StashChest Chest, DestinationCandidate Candidate)> Ruled { get; }
    internal List<StashChest> Unruled { get; }

    public StashExplainContext(StashInput input, ChestRuleEvaluator? evaluator = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Evaluator = evaluator ?? new ChestRuleEvaluator();
        Chests = input.Chests.Take(StashPlanner.MaximumChests).ToList();
        (Ruled, Unruled) = StashPlanner.Receivers(Chests, Evaluator, input.Taxonomy);
    }

    /// <summary>Whether <paramref name="handle"/> is one of the player's stacks in this input.</summary>
    public bool Has(Guid handle) => Input.Player.Stacks.Any(x => x.Handle == handle);

    /// <summary>As <c>PlacementExplainer.Stash</c>; <paramref name="timed"/> gets "routes" only.</summary>
    public PlacementExplanation Explain(Guid handle, Func<string, string>? chestName = null, Func<string, string>? itemName = null, IEnumerable<string>? outOfRange = null,
        Func<string, string, string>? openName = null, bool detail = true, Action<string, double>? timed = null) =>
        PlacementExplainer.Stash(this, handle, chestName, itemName, outOfRange, openName, detail, timed, timed == null ? null : Stopwatch.StartNew());
}

/// <summary>
/// X16: the stack-independent part of a cleanup explanation (the planner's chest and source limits and
/// <see cref="CleanupPlanner.Receivers"/>), prepared once per gathered input. The answers equal the one-shot ones. Read only.
/// </summary>
public sealed class CleanupExplainContext
{
    public CleanupInput Input { get; }
    internal ChestRuleEvaluator Evaluator { get; }
    internal List<CleanupChest> Ordered { get; }
    internal HashSet<string> Sources { get; }
    internal List<(CleanupChest Chest, DestinationCandidate Candidate)> Ruled { get; }

    public CleanupExplainContext(CleanupInput input, ChestRuleEvaluator? evaluator = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Evaluator = evaluator ?? new ChestRuleEvaluator();
        Ordered = CleanupPlanner.Considered(input.Chests);
        Sources = CleanupPlanner.Sources(Ordered);
        Ruled = CleanupPlanner.Receivers(Ordered, Evaluator, input.Taxonomy);
    }

    /// <summary>Whether <paramref name="handle"/> is a stack of chest <paramref name="sourceId"/> in this input.</summary>
    public bool Has(string sourceId, Guid handle) =>
        Input.Chests.Any(x => StringComparer.Ordinal.Equals(x.Id, sourceId) && x.Inventory.Stacks.Any(s => s.Handle == handle));

    /// <summary>As <c>PlacementExplainer.Cleanup</c>; <paramref name="timed"/> gets "routes" only.</summary>
    public PlacementExplanation Explain(string sourceId, Guid handle, Func<string, string>? chestName = null, Func<string, string>? itemName = null,
        IEnumerable<string>? outOfRange = null, Func<string, string, string>? openName = null, bool detail = true, Action<string, double>? timed = null) =>
        PlacementExplainer.Cleanup(this, sourceId, handle, chestName, itemName, outOfRange, openName, detail, timed, timed == null ? null : Stopwatch.StartNew());
}

/// <summary>
/// Read-only "why does this go there, or not" (P6-05). It runs the planners' own per-stack steps
/// (<see cref="StashPlanner"/> and <see cref="CleanupPlanner"/> receivers, routes and gates over
/// <see cref="DestinationRouting.Rank"/>), so its choice is the run's choice for the first stack of a run. Later
/// stacks of a real run may find chests already filled by earlier ones. It never plans a move and grants nothing.
/// </summary>
public static class PlacementExplainer
{
    /// <summary>Where the stash key would put the player's stack <paramref name="handle"/>. Without <paramref name="detail"/>, no per-chest rows.</summary>
    /// <param name="timed">X16 cost check (Lab only): gets the milliseconds of the stack-independent "receivers" step and the per-stack "routes" step.</param>
    public static PlacementExplanation Stash(StashInput input, Guid handle, ChestRuleEvaluator? evaluator = null,
        Func<string, string>? chestName = null, Func<string, string>? itemName = null, IEnumerable<string>? outOfRange = null,
        Func<string, string, string>? openName = null, bool detail = true, Action<string, double>? timed = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (input.Player.Stacks.All(x => x.Handle != handle)) throw new ArgumentException("Unknown player stack.", nameof(handle));
        var clock = timed == null ? null : Stopwatch.StartNew();
        var context = new StashExplainContext(input, evaluator);
        Lap(timed, clock, "receivers");
        return Stash(context, handle, chestName, itemName, outOfRange, openName, detail, timed, clock);
    }

    // The per-stack part of Stash, against receivers prepared once (X16).
    internal static PlacementExplanation Stash(StashExplainContext context, Guid handle, Func<string, string>? chestName, Func<string, string>? itemName,
        IEnumerable<string>? outOfRange, Func<string, string, string>? openName, bool detail, Action<string, double>? timed, Stopwatch? clock)
    {
        var input = context.Input; var evaluator = context.Evaluator;
        var stack = input.Player.Stacks.FirstOrDefault(x => x.Handle == handle) ?? throw new ArgumentException("Unknown player stack.", nameof(handle));
        var names = new Names(input.Taxonomy, chestName, itemName, openName);
        var prefab = stack.Identity.Prefab; var item = names.Item(prefab);
        var rows = new List<ChestExplanation>();
        PlacementExplanation Done(PlacementOutcome outcome, string summary, string? chosen = null, RunReason? blocked = null,
            (PlacementBasis Basis, (string, object)? Category) basis = default)
        {
            if (!detail) rows.Clear();
            else foreach (var id in outOfRange ?? Array.Empty<string>()) rows.Add(new ChestExplanation(id, ChestVerdict.OutOfRange, "out of range", null));
            return new PlacementExplanation(prefab, outcome, chosen, blocked, summary, rows, basis.Basis, basis.Category);
        }
        var chests = context.Chests; var ruled = context.Ruled; var unruled = context.Unruled;
        var unselected = input.Only != null && !input.Only.Contains(stack.Handle);
        var kept = unselected || StashPlanner.Kept(input, stack);
        var routes = kept ? new List<StashPlanner.Route>() : StashPlanner.Routes(input, evaluator, ruled, unruled, stack);
        Lap(timed, clock, "routes");
        // While categories load, only Always rules and holders route (D086 D4); with neither, the item waits.
        if (!input.Answers.CategoriesReady && routes.Count == 0 && !kept)
        {
            rows.AddRange(input.Chests.Select(x => new ChestExplanation(x.Id, ChestVerdict.CategoriesPending, "categories not ready yet", null)));
            return Done(PlacementOutcome.NotReady, item + " stays: categories are not ready yet, try again shortly");
        }
        var resolved = input.Answers.Resolve(prefab);
        var raw = input.InstanceFacets?.Invoke(stack);
        var facets = resolved == null ? null : ClassificationFilters.InstanceFacets(resolved, raw);
        string Why(StashPlanner.Route route) => route.Tier == StashTier.Holder ? "no rule, already holds this item" : names.Why(route.Ranked!, resolved!, facets!);

        // The planner's order: kept stacks and stacks no chest takes stay silently, then unsupported and held ones,
        // then the routes in order: gates first, then room.
        StashPlanner.Route? chosen = null; var full = new HashSet<StashPlanner.Route>(); RunReason? blocked = null;
        PlacementOutcome outcome; string summary;
        if (kept) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + (unselected ? "not selected" : "protected (equipped, quest item, hotbar or quick slot)"); }
        else if (routes.Count == 0) { outcome = PlacementOutcome.NoChest; summary = item + " stays: no chest in range takes it"; }
        else if (!stack.TransferSupported) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + Diagnostic(RunReason.Unsupported); }
        else if (input.Held.Contains(stack.Handle)) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + Diagnostic(RunReason.Held); }
        else
        {
            foreach (var route in routes)
            {
                if (route.Blocked != null) { blocked ??= route.Blocked; continue; }
                if (Room(route.Chest.Inventory, stack)) { chosen = route; break; }
                full.Add(route);
            }
            if (chosen != null) { outcome = PlacementOutcome.Goes; summary = item + " → " + names.Chest(chosen.Chest.Id) + ": " + Why(chosen); blocked = null; }
            else if (blocked != null) { outcome = PlacementOutcome.Blocked; summary = item + " stays: " + Diagnostic(blocked.Value); }
            else { outcome = PlacementOutcome.NoRoom; summary = item + " stays: the chests that take it are full"; }
        }

        // Per-chest rows only when asked for: the panel shows one sentence (R4 review), so it skips this work.
        foreach (var chest in detail ? input.Chests : Enumerable.Empty<StashChest>())
        {
            if (!chests.Contains(chest)) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.OverLimit, "beyond this run's chest limit", null)); continue; }
            var route = routes.FirstOrDefault(x => ReferenceEquals(x.Chest, chest));
            if (route != null)
            {
                rows.Add(outcome == PlacementOutcome.Kept ? new ChestExplanation(chest.Id, ChestVerdict.NotConsidered, Why(route) + "; but the item stays", route.Ranked)
                    : Routed(chest.Id, route.Blocked, ReferenceEquals(route, chosen), chosen != null && !full.Contains(route), Why(route), route.Ranked));
                continue;
            }
            if (kept) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.NotConsidered, "not considered", null)); continue; }
            if (evaluator.Unruled(chest.RulePayload, input.Taxonomy)) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.NoRule, "no rule, and it holds no " + item, null)); continue; }
            rows.Add(Refusal(chest.Id, chest.RulePayload, prefab, resolved, input.Taxonomy, input.Answers, evaluator, raw, openName));
        }
        var basis = chosen == null ? default : chosen.Tier == StashTier.Holder ? (PlacementBasis.Holds, null) : names.Basis(chosen.Ranked!, resolved, facets);
        return Done(outcome, summary, chosen?.Chest.Id, blocked, basis);
    }

    /// <summary>Where "Clean up nearby" would move stack <paramref name="handle"/> of chest <paramref name="sourceId"/>. Without <paramref name="detail"/>, no per-chest rows.</summary>
    /// <param name="timed">X16 cost check (Lab only), as for <see cref="Stash"/>: "receivers", then "routes" (the stack's assessment).</param>
    public static PlacementExplanation Cleanup(CleanupInput input, string sourceId, Guid handle, ChestRuleEvaluator? evaluator = null,
        Func<string, string>? chestName = null, Func<string, string>? itemName = null, IEnumerable<string>? outOfRange = null,
        Func<string, string, string>? openName = null, bool detail = true, Action<string, double>? timed = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var source = input.Chests.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Id, sourceId)) ?? throw new ArgumentException("Unknown source chest.", nameof(sourceId));
        if (source.Inventory.Stacks.All(x => x.Handle != handle)) throw new ArgumentException("Unknown source stack.", nameof(handle));
        var clock = timed == null ? null : Stopwatch.StartNew();
        var context = new CleanupExplainContext(input, evaluator);
        Lap(timed, clock, "receivers");
        return Cleanup(context, sourceId, handle, chestName, itemName, outOfRange, openName, detail, timed, clock);
    }

    // The per-stack part of Cleanup, against the chest limits and receivers prepared once (X16).
    internal static PlacementExplanation Cleanup(CleanupExplainContext context, string sourceId, Guid handle, Func<string, string>? chestName,
        Func<string, string>? itemName, IEnumerable<string>? outOfRange, Func<string, string, string>? openName, bool detail, Action<string, double>? timed,
        Stopwatch? clock)
    {
        var input = context.Input; var evaluator = context.Evaluator;
        var source = input.Chests.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Id, sourceId)) ?? throw new ArgumentException("Unknown source chest.", nameof(sourceId));
        var stack = source.Inventory.Stacks.FirstOrDefault(x => x.Handle == handle) ?? throw new ArgumentException("Unknown source stack.", nameof(handle));
        var names = new Names(input.Taxonomy, chestName, itemName, openName);
        var prefab = stack.Identity.Prefab; var item = names.Item(prefab);
        var rows = new List<ChestExplanation>();
        PlacementExplanation Done(PlacementOutcome outcome, string summary, string? chosen = null, RunReason? blocked = null,
            (PlacementBasis Basis, (string, object)? Category) basis = default)
        {
            if (!detail) rows.Clear();
            else foreach (var id in outOfRange ?? Array.Empty<string>()) rows.Add(new ChestExplanation(id, ChestVerdict.OutOfRange, "out of range", null));
            return new PlacementExplanation(prefab, outcome, chosen, blocked, summary, rows, basis.Basis, basis.Category);
        }
        void Rest(ChestVerdict verdict, string text)
        { foreach (var chest in input.Chests) rows.Add(new ChestExplanation(chest.Id, ReferenceEquals(chest, source) ? ChestVerdict.Current : verdict, ReferenceEquals(chest, source) ? "where it is" : text, null)); }
        if (!input.Answers.CategoriesReady)
        { Rest(ChestVerdict.CategoriesPending, "categories not ready yet"); return Done(PlacementOutcome.NotReady, item + " stays: categories are not ready yet, try again shortly"); }
        if (source.OptOut)
        { Rest(ChestVerdict.NotConsidered, "not considered"); return Done(PlacementOutcome.OptedOut, item + " stays: its chest is set to \"Don't clean up\""); }
        // The planner's own limits (D086 D9): the nearest chests, then the nearest sources that are not opted out and hold items.
        var ordered = context.Ordered;
        if (!ordered.Contains(source) || !context.Sources.Contains(source.Id))
        { Rest(ChestVerdict.NotConsidered, "not considered"); return Done(PlacementOutcome.Skipped, item + " stays: " + Diagnostic(RunReason.ChestLimit)); }

        var ruled = context.Ruled;
        var assessed = CleanupPlanner.Assess(input, evaluator, ruled, source, stack);
        Lap(timed, clock, "routes");
        var resolved = assessed.Item;
        var facets = resolved == null ? null : ClassificationFilters.InstanceFacets(resolved, input.InstanceFacets?.Invoke(stack));
        var usable = assessed.Destinations.Where(x => x.Gate == CleanupGate.Usable).ToList();
        var blockedRow = assessed.Destinations.FirstOrDefault(x => x.Gate == CleanupGate.Blocked);
        CleanupPlanner.Destination? chosen = null; var full = new HashSet<CleanupPlanner.Destination>();
        PlacementOutcome outcome; string summary; RunReason? blocked = null; var considered = true;
        var here = names.Chest(source.Id);
        if (assessed.Kind == null)
        { outcome = PlacementOutcome.Stays; summary = item + " stays: the rule of " + here + " can't be read by this version"; considered = false; }
        else if (resolved == null && assessed.Destinations.Count == 0 && !assessed.Stays)
        {
            outcome = assessed.Kind == CleanupKind.Rejected ? PlacementOutcome.NoChest : PlacementOutcome.Stays;
            summary = item + " stays: it has no categories, so no rule but an exact one takes it"; considered = false;
        }
        else if (assessed.Stays)
        {
            outcome = PlacementOutcome.Stays; considered = false;
            summary = item + " stays in " + here + ": its rule takes it";
        }
        else if (usable.Count == 0)
        {
            if (assessed.Kind == CleanupKind.Resort) { outcome = PlacementOutcome.Stays; summary = item + " stays in " + here + ": no better chest takes it"; }
            else if (blockedRow != null) { outcome = PlacementOutcome.Blocked; blocked = blockedRow.Blocked; summary = item + " stays: " + Diagnostic(blocked!.Value); }
            else { outcome = PlacementOutcome.NoChest; summary = item + " stays: no chest in range takes it"; }
        }
        else if (stack.Protected || !stack.TransferSupported || source.Inventory.ProtectedCells.Contains(stack.Y * source.Inventory.Width + stack.X))
        { outcome = PlacementOutcome.Kept; summary = item + " stays: " + Diagnostic(RunReason.Unsupported); }
        else if (input.Held.Contains(stack.Handle))
        { outcome = PlacementOutcome.Kept; summary = item + " stays: " + Diagnostic(RunReason.Held); }
        else
        {
            foreach (var row in usable)
            {
                if (Room(row.Chest.Inventory, stack)) { chosen = row; break; }
                full.Add(row);
            }
            if (chosen != null) { outcome = PlacementOutcome.Goes; summary = item + " → " + names.Chest(chosen.Chest.Id) + ": " + names.Why(chosen.Ranked, resolved, facets!); }
            else if (assessed.Kind == CleanupKind.Resort) { outcome = PlacementOutcome.Stays; summary = item + " stays in " + here + ": the better chests are full"; }
            else { outcome = PlacementOutcome.NoRoom; summary = item + " stays: the chests that take it are full"; }
        }

        foreach (var chest in detail ? input.Chests : Enumerable.Empty<CleanupChest>())
        {
            if (ReferenceEquals(chest, source))
            {
                var text = assessed.Kind switch
                {
                    CleanupKind.Rejected => "where it is; its rule doesn't take it", CleanupKind.Unruled => "where it is; no rule",
                    CleanupKind.Resort when assessed.Current != null => "where it is; " + names.Why(assessed.Current, resolved!, facets!),
                    _ => "where it is"
                };
                rows.Add(new ChestExplanation(chest.Id, ChestVerdict.Current, text, assessed.Current)); continue;
            }
            if (!ordered.Contains(chest)) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.OverLimit, "beyond this run's chest limit", null)); continue; }
            var row = assessed.Destinations.FirstOrDefault(x => ReferenceEquals(x.Chest, chest));
            if (row != null)
            {
                var why = names.Why(row.Ranked, resolved!, facets!);
                rows.Add(row.Gate switch
                {
                    CleanupGate.NotBetter => new ChestExplanation(chest.Id, ChestVerdict.NotBetter, why + "; no better than " + here, row.Ranked),
                    CleanupGate.RuleMismatch => new ChestExplanation(chest.Id, ChestVerdict.RuleRejects, "its rule doesn't take it", row.Ranked),
                    CleanupGate.Blocked => Routed(chest.Id, row.Blocked, false, false, why, row.Ranked),
                    _ => outcome == PlacementOutcome.Kept ? new ChestExplanation(chest.Id, ChestVerdict.NotConsidered, why + "; but the item stays", row.Ranked)
                        : Routed(chest.Id, null, ReferenceEquals(row, chosen), chosen != null && !full.Contains(row), why, row.Ranked)
                });
                continue;
            }
            if (!considered) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.NotConsidered, "not considered", null)); continue; }
            if (evaluator.Unruled(chest.RulePayload, input.Taxonomy)) { rows.Add(new ChestExplanation(chest.Id, ChestVerdict.NoRule, "no rule; cleanup only fills ruled chests", null)); continue; }
            rows.Add(Refusal(chest.Id, chest.RulePayload, prefab, resolved, input.Taxonomy, input.Answers, evaluator, input.InstanceFacets?.Invoke(stack), openName));
        }
        return Done(outcome, summary, chosen?.Chest.Id, blocked, chosen == null ? default : names.Basis(chosen.Ranked, resolved, facets));
    }

    private static ChestExplanation Routed(string id, RunReason? blocked, bool chosen, bool backup, string why, RankedDestination? rank)
    {
        if (blocked != null)
            return new ChestExplanation(id, blocked switch
            {
                RunReason.OtherOwnerNoMuc => ChestVerdict.OtherOwnerNoMuc, RunReason.OtherOwnerFormatUnknown => ChestVerdict.OtherOwnerFormatUnknown,
                RunReason.OtherOwnerMucUnusable => ChestVerdict.OtherOwnerMucUnusable, RunReason.OtherOwnerMucSilent => ChestVerdict.OtherOwnerMucSilent,
                RunReason.OtherOwnerMucChecking => ChestVerdict.OtherOwnerMucChecking,
                RunReason.BothRemote => ChestVerdict.BothRemote, RunReason.AzuExcluded => ChestVerdict.AzuExcluded,
                _ => ChestVerdict.RuleRejects
            }, why + "; but " + Diagnostic(blocked.Value), rank);
        if (chosen) return new ChestExplanation(id, ChestVerdict.Chosen, why, rank);
        return backup ? new ChestExplanation(id, ChestVerdict.Backup, why + "; ranked lower", rank)
            : new ChestExplanation(id, ChestVerdict.Full, why + "; no room", rank);
    }

    // A chest whose rule did not rank the item: the evaluator's answer, with "Never" and unreadable rules named.
    private static ChestExplanation Refusal(string id, string payload, string prefab, ClassificationResult? item, ClassificationTaxonomy taxonomy,
        ICategoryAnswers answers, ChestRuleEvaluator evaluator, IReadOnlyList<FacetEvidence>? facets, Func<string, string, string>? openName)
    {
        var read = evaluator.Read(payload, taxonomy);
        if (read.State == ContainerPolicyReadState.Unsupported) return new ChestExplanation(id, ChestVerdict.RuleUnreadable, "its rule can't be read by this version", null);
        if (read.Rules == null || read.Rules.State == ChestRuleState.Unconfigured) return new ChestExplanation(id, ChestVerdict.RuleCleared, "its rule was cleared", null);
        if (read.Rules.Excludes.Contains(prefab, StringComparer.Ordinal)) return new ChestExplanation(id, ChestVerdict.Never, "lists it under Never", null);
        if (!answers.CategoriesReady && read.Rules.Acceptance.Rules.Count != 0) return new ChestExplanation(id, ChestVerdict.CategoriesPending, "categories not ready yet", null);
        if (item == null) return new ChestExplanation(id, ChestVerdict.RuleRejects, "this item has no categories", null);
        return evaluator.Evaluate(payload, prefab, taxonomy, answers, facets) == ChestRuleDecision.Accept
            ? new ChestExplanation(id, ChestVerdict.RuleRejects, "its rule takes it, but routing ranked it out", null)
            : new ChestExplanation(id, ChestVerdict.RuleRejects, Missed(read.Rules, item, ClassificationFilters.InstanceFacets(item, facets), taxonomy, openName), null);
    }

    // The closest miss: a rule part whose category the item has but whose filter it fails.
    private static string Missed(ChestRules rules, ClassificationResult item, FacetEvidence[] facets, ClassificationTaxonomy taxonomy,
        Func<string, string, string>? openName)
    {
        foreach (var rule in rules.Acceptance.Rules)
        {
            if (rule.Categories.Count != 0 && !item.Memberships.Any(x => rule.Categories.Contains(x.Category, StringComparer.Ordinal))) continue;
            foreach (var filter in rule.Filters.Where(x => x.Value.Count != 0))
                if (!ClassificationFilters.IsKnown(filter.Key))
                    return "its '" + filter.Key + "' filter is from another version or an add-on that isn't installed, so it takes nothing";
                else if (!ClassificationFilters.MatchesValidated(item, new ClassificationFilterRule(taxonomy, Array.Empty<string>(),
                        new[] { new KeyValuePair<string, IEnumerable<string>>(filter.Key, filter.Value) }), facets))
                    return facets.Any(x => x.Dimension == filter.Key && x.Value == ClassificationFilters.Unreadable)
                        ? "its " + Names.Dimension(filter.Key) + " filter can't read this item's " + Names.Dimension(filter.Key) + " right now"
                        : "its " + Names.Dimension(filter.Key) + " filter (" + string.Join(", ", filter.Value.Select(v => Names.Value(filter.Key, v, openName))) + ") doesn't match";
        }
        return "its rule doesn't take it";
    }

    // P10-04: diagnostic English for Summary and ChestExplanation.Text, which no player reads (tests and Lab only), like a
    // log line. The item tooltip says these causes with RunReport.Reason (ExplanationView).
    private static string Diagnostic(RunReason reason) => reason switch
    {
        RunReason.NoCapacity => "no room",
        RunReason.NoDestination => "no chest takes it",
        RunReason.AzuExcluded => "AzuAutoStore's config excludes it",
        RunReason.Held => "in use",
        RunReason.Unsupported => "protected",
        RunReason.MucBusy => "other player's chest busy, run again",
        RunReason.Budget => "run limit reached, run again",
        RunReason.ChestLimit => "in a chest farther than the nearest " + CleanupPlanner.MaximumSources + " with items; clean up again closer to it",
        RunReason.OtherOwnerNoMuc => "another player's chest, no MultiUserChest",
        RunReason.BothRemote => "both chests are other players'",
        RunReason.OtherOwnerMucUnusable => "another player's chest; their MultiUserChest can't take requests",
        RunReason.OtherOwnerMucSilent => "another player's chest; no MultiUserChest answered",
        RunReason.OtherOwnerMucChecking => "another player's chest; still checking their MultiUserChest, run again in a moment",
        RunReason.OtherOwnerFormatUnknown => "another player's chest; their MultiUserChest format is unknown",
        RunReason.CategoriesPending => "waiting for categories to load, run again shortly",
        RunReason.OptedOut => "its chest was set to be left alone",
        RunReason.ChestUnreadable => "couldn't be read (see the log)",
        _ => reason.ToString()
    };

    private static void Lap(Action<string, double>? timed, Stopwatch? clock, string step)
    {
        if (timed == null || clock == null) return;
        timed(step, clock.Elapsed.TotalMilliseconds); clock.Restart();
    }

    // The planners' own capacity test, on the chest as it is now.
    private static bool Room(TransferInventorySnapshot inventory, TransferStack stack) =>
        new TransferGrid(inventory).TryFind(stack.MergeKey, stack.MaximumStack, out _, out _, out _, out _);

    private sealed class Names
    {
        private readonly ClassificationTaxonomy taxonomy; private readonly Func<string, string>? chest, item;
        private readonly Func<string, string, string>? open;
        internal Names(ClassificationTaxonomy taxonomy, Func<string, string>? chest, Func<string, string>? item, Func<string, string, string>? open)
        { this.taxonomy = taxonomy; this.chest = chest; this.item = item; this.open = open; }
        internal string Chest(string id) => Bounded(chest?.Invoke(id), id);
        internal string Item(string prefab) => Bounded(item?.Invoke(prefab), prefab);
        internal string Category(string id) => taxonomy.Categories.TryGetValue(id, out var row) ? row.Name : id;
        /// <summary>A filter value: an open one by the caller's name for it (a tame's name, "me" for the crafter), else as stored.</summary>
        internal static string Value(string dimension, string value, Func<string, string, string>? open) =>
            value != "unknown" && ClassificationFilters.Values(dimension) == null && open != null ? open(dimension, value) : value;
        internal static string Dimension(string id) => id switch
        {
            "mod" => "origin mod", "related-mod" => "related mod", "construction-mod" => "building mod",
            "food-stat" => "food stat", "eaten-by" => "eaten by", _ => id
        };
        private static string Bounded(string? text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            return text!.Length <= 64 ? text : text.Substring(0, 63) + "…";
        }
        /// <summary>The rule alternative and category that took the item, as <see cref="Why"/> names them.</summary>
        private static (ClassificationFilterRule? Rule, string? Category) Decider(RankedDestination rank, ClassificationResult? item, FacetEvidence[]? facets)
        {
            if (item == null || facets == null) return (rank.Rule, rank.Category);
            var rule = rank.Rule ?? rank.Candidate.Rules.Acceptance.Rules.FirstOrDefault(x => x.Categories.Count == 0 && ClassificationFilters.MatchesValidated(item, x, facets)
                || rank.Match is AcceptanceMatch.Category or AcceptanceMatch.FilteredCategory && x.Categories.Count != 0 && ClassificationFilters.MatchesValidated(item, x, facets));
            return (rule, rank.Category ?? rule?.Categories.FirstOrDefault(c => item.Memberships.Any(m => m.Category == c)));
        }

        /// <summary>X1: the basis of <see cref="Why"/> without its detail, for the tooltip's "counts as Metals".</summary>
        internal (PlacementBasis, (string, object)?) Basis(RankedDestination rank, ClassificationResult? item, FacetEvidence[]? facets)
        {
            if (rank.ExistingItemFallback) return (PlacementBasis.Holds, null);
            if (rank.Match == AcceptanceMatch.ExactItem) return (PlacementBasis.Always, null);
            var category = Decider(rank, item, facets).Category;
            if (category != null) return (PlacementBasis.Category, (Category(category), CategoryNames.Of(taxonomy, category)));
            return (rank.Match == AcceptanceMatch.Any ? PlacementBasis.Anything : PlacementBasis.Filters, null);
        }

        /// <summary>The rule part that took the item, its specificity and the chest's priority.</summary>
        internal string Why(RankedDestination rank, ClassificationResult item, FacetEvidence[] facets)
        {
            var rules = rank.Candidate.Rules; string part;
            if (rank.ExistingItemFallback) part = "already holds this item";
            else if (rank.Match == AcceptanceMatch.ExactItem) part = "lists it under Always";
            else
            {
                var (rule, category) = Decider(rank, item, facets);
                part = category != null ? "category " + Category(category) : rank.Match == AcceptanceMatch.Any ? "takes anything" : "its filters";
                // P10-04: the filters as stored, open values by name ("eaten by Boar/Lox"); the chest label's words are messages now.
                var filters = rule == null ? "" : string.Join(", ", rule.Filters.Where(x => x.Value.Count != 0)
                    .Select(x => Dimension(x.Key) + " " + string.Join("/", x.Value.Select(v => Value(x.Key, v, open)))));
                if (filters.Length != 0) part += (category != null ? " › " : " ") + filters;
                if (rank.Generality > 0) part += ", " + rank.Generality + " level" + (rank.Generality == 1 ? "" : "s") + " above the item's own category";
            }
            if (rules.Priority != 0) part += ", priority " + ChestPriority.Level(rules.Priority);
            if (rules.Overflow) part += ", overflow";
            return part;
        }
    }
}
