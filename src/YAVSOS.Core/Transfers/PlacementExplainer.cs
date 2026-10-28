using System;
using System.Collections.Generic;
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
    public CleanupReason? Blocked { get; }
    /// <summary>One line, e.g. "Copper → Metals chest: category Metals, priority 4 (Normal)".</summary>
    public string Summary { get; }
    /// <summary>Every chest passed in, in run order, then those out of range.</summary>
    public IReadOnlyList<ChestExplanation> Chests { get; }
    internal PlacementExplanation(string prefab, PlacementOutcome outcome, string? chosen, CleanupReason? blocked, string summary, List<ChestExplanation> chests)
    { Prefab = prefab; Outcome = outcome; ChosenId = chosen; Blocked = blocked; Summary = summary; Chests = chests.AsReadOnly(); }
    public ChestExplanation? For(string chestId) => Chests.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ChestId, chestId));
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
    public static PlacementExplanation Stash(StashInput input, Guid handle, ChestRuleEvaluator? evaluator = null,
        Func<string, string>? chestName = null, Func<string, string>? itemName = null, IEnumerable<string>? outOfRange = null,
        Func<string, string, string>? openName = null, bool detail = true)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var stack = input.Player.Stacks.FirstOrDefault(x => x.Handle == handle) ?? throw new ArgumentException("Unknown player stack.", nameof(handle));
        evaluator ??= new ChestRuleEvaluator();
        var names = new Names(input.Taxonomy, chestName, itemName, openName);
        var prefab = stack.Identity.Prefab; var item = names.Item(prefab);
        var rows = new List<ChestExplanation>();
        PlacementExplanation Done(PlacementOutcome outcome, string summary, string? chosen = null, CleanupReason? blocked = null)
        {
            if (!detail) rows.Clear();
            else foreach (var id in outOfRange ?? Array.Empty<string>()) rows.Add(new ChestExplanation(id, ChestVerdict.OutOfRange, "out of range", null));
            return new PlacementExplanation(prefab, outcome, chosen, blocked, summary, rows);
        }
        var chests = input.Chests.Take(StashPlanner.MaximumChests).ToList();
        var (ruled, unruled) = StashPlanner.Receivers(chests, evaluator, input.Taxonomy);
        var unselected = input.Only != null && !input.Only.Contains(stack.Handle);
        var kept = unselected || StashPlanner.Kept(input, stack);
        var routes = kept ? new List<StashPlanner.Route>() : StashPlanner.Routes(input, evaluator, ruled, unruled, stack);
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
        StashPlanner.Route? chosen = null; var full = new HashSet<StashPlanner.Route>(); CleanupReason? blocked = null;
        PlacementOutcome outcome; string summary;
        if (kept) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + (unselected ? "not selected" : "protected (equipped, quest item, hotbar or quick slot)"); }
        else if (routes.Count == 0) { outcome = PlacementOutcome.NoChest; summary = item + " stays: no chest in range takes it"; }
        else if (!stack.TransferSupported) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + CleanupReport.Describe(CleanupReason.Unsupported); }
        else if (input.Held.Contains(stack.Handle)) { outcome = PlacementOutcome.Kept; summary = item + " stays: " + CleanupReport.Describe(CleanupReason.Held); }
        else
        {
            foreach (var route in routes)
            {
                if (route.Blocked != null) { blocked ??= route.Blocked; continue; }
                if (Room(route.Chest.Inventory, stack)) { chosen = route; break; }
                full.Add(route);
            }
            if (chosen != null) { outcome = PlacementOutcome.Goes; summary = item + " → " + names.Chest(chosen.Chest.Id) + ": " + Why(chosen); blocked = null; }
            else if (blocked != null) { outcome = PlacementOutcome.Blocked; summary = item + " stays: " + CleanupReport.Describe(blocked.Value); }
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
        return Done(outcome, summary, chosen?.Chest.Id, blocked);
    }

    /// <summary>Where "Clean up nearby" would move stack <paramref name="handle"/> of chest <paramref name="sourceId"/>. Without <paramref name="detail"/>, no per-chest rows.</summary>
    public static PlacementExplanation Cleanup(CleanupInput input, string sourceId, Guid handle, ChestRuleEvaluator? evaluator = null,
        Func<string, string>? chestName = null, Func<string, string>? itemName = null, IEnumerable<string>? outOfRange = null,
        Func<string, string, string>? openName = null, bool detail = true)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var source = input.Chests.FirstOrDefault(x => StringComparer.Ordinal.Equals(x.Id, sourceId)) ?? throw new ArgumentException("Unknown source chest.", nameof(sourceId));
        var stack = source.Inventory.Stacks.FirstOrDefault(x => x.Handle == handle) ?? throw new ArgumentException("Unknown source stack.", nameof(handle));
        evaluator ??= new ChestRuleEvaluator();
        var names = new Names(input.Taxonomy, chestName, itemName, openName);
        var prefab = stack.Identity.Prefab; var item = names.Item(prefab);
        var rows = new List<ChestExplanation>();
        PlacementExplanation Done(PlacementOutcome outcome, string summary, string? chosen = null, CleanupReason? blocked = null)
        {
            if (!detail) rows.Clear();
            else foreach (var id in outOfRange ?? Array.Empty<string>()) rows.Add(new ChestExplanation(id, ChestVerdict.OutOfRange, "out of range", null));
            return new PlacementExplanation(prefab, outcome, chosen, blocked, summary, rows);
        }
        void Rest(ChestVerdict verdict, string text)
        { foreach (var chest in input.Chests) rows.Add(new ChestExplanation(chest.Id, ReferenceEquals(chest, source) ? ChestVerdict.Current : verdict, ReferenceEquals(chest, source) ? "where it is" : text, null)); }
        if (!input.Answers.CategoriesReady)
        { Rest(ChestVerdict.CategoriesPending, "categories not ready yet"); return Done(PlacementOutcome.NotReady, item + " stays: categories are not ready yet, try again shortly"); }
        if (source.OptOut)
        { Rest(ChestVerdict.NotConsidered, "not considered"); return Done(PlacementOutcome.OptedOut, item + " stays: its chest is set to \"Don't clean up\""); }
        // The planner's own limits (D086 D9): the nearest chests, then the nearest sources that are not opted out and hold items.
        var ordered = CleanupPlanner.Considered(input.Chests);
        if (!ordered.Contains(source) || !CleanupPlanner.Sources(ordered).Contains(source.Id))
        { Rest(ChestVerdict.NotConsidered, "not considered"); return Done(PlacementOutcome.Skipped, item + " stays: " + CleanupReport.Describe(CleanupReason.ChestLimit)); }

        var ruled = CleanupPlanner.Receivers(ordered, evaluator, input.Taxonomy);
        var assessed = CleanupPlanner.Assess(input, evaluator, ruled, source, stack);
        var resolved = assessed.Item;
        var facets = resolved == null ? null : ClassificationFilters.InstanceFacets(resolved, input.InstanceFacets?.Invoke(stack));
        var usable = assessed.Destinations.Where(x => x.Gate == CleanupGate.Usable).ToList();
        var blockedRow = assessed.Destinations.FirstOrDefault(x => x.Gate == CleanupGate.Blocked);
        CleanupPlanner.Destination? chosen = null; var full = new HashSet<CleanupPlanner.Destination>();
        PlacementOutcome outcome; string summary; CleanupReason? blocked = null; var considered = true;
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
            else if (blockedRow != null) { outcome = PlacementOutcome.Blocked; blocked = blockedRow.Blocked; summary = item + " stays: " + CleanupReport.Describe(blocked!.Value); }
            else { outcome = PlacementOutcome.NoChest; summary = item + " stays: no chest in range takes it"; }
        }
        else if (stack.Protected || !stack.TransferSupported || source.Inventory.ProtectedCells.Contains(stack.Y * source.Inventory.Width + stack.X))
        { outcome = PlacementOutcome.Kept; summary = item + " stays: " + CleanupReport.Describe(CleanupReason.Unsupported); }
        else if (input.Held.Contains(stack.Handle))
        { outcome = PlacementOutcome.Kept; summary = item + " stays: " + CleanupReport.Describe(CleanupReason.Held); }
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
        return Done(outcome, summary, chosen?.Chest.Id, blocked);
    }

    private static ChestExplanation Routed(string id, CleanupReason? blocked, bool chosen, bool backup, string why, RankedDestination? rank)
    {
        if (blocked != null)
            return new ChestExplanation(id, blocked switch
            {
                CleanupReason.OtherOwnerNoMuc => ChestVerdict.OtherOwnerNoMuc, CleanupReason.OtherOwnerFormatUnknown => ChestVerdict.OtherOwnerFormatUnknown,
                CleanupReason.BothRemote => ChestVerdict.BothRemote, CleanupReason.AzuExcluded => ChestVerdict.AzuExcluded,
                _ => ChestVerdict.RuleRejects
            }, why + "; but " + CleanupReport.Describe(blocked.Value), rank);
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
        /// <summary>A filter value as players see it: a tame's name, "me" for the crafter, "plain" for no rarity.</summary>
        internal static string Value(string dimension, string value, Func<string, string, string>? open) =>
            value != "unknown" && ClassificationFilters.Values(dimension) == null && open != null ? open(dimension, value) : RuleText.ValueName(dimension, value);
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
        /// <summary>The rule part that took the item, its specificity and the chest's priority.</summary>
        internal string Why(RankedDestination rank, ClassificationResult item, FacetEvidence[] facets)
        {
            var rules = rank.Candidate.Rules; string part;
            if (rank.ExistingItemFallback) part = "already holds this item";
            else if (rank.Match == AcceptanceMatch.ExactItem) part = "lists it under Always";
            else
            {
                var rule = rank.Rule ?? rules.Acceptance.Rules.FirstOrDefault(x => x.Categories.Count == 0 && ClassificationFilters.MatchesValidated(item, x, facets)
                    || rank.Match is AcceptanceMatch.Category or AcceptanceMatch.FilteredCategory && x.Categories.Count != 0 && ClassificationFilters.MatchesValidated(item, x, facets));
                var category = rank.Category ?? rule?.Categories.FirstOrDefault(c => item.Memberships.Any(m => m.Category == c));
                part = category != null ? "category " + Category(category) : rank.Match == AcceptanceMatch.Any ? "takes anything" : "its filters";
                // The chest label's words ("eaten by Boar or Lox", "crafted by me"), not raw tokens (runtime 2026-10-21).
                var filters = rule == null ? "" : RuleText.Short(rule.Filters, open);
                if (filters.Length != 0) part += (category != null ? RuleText.FilterMark : " ") + filters;
                if (rank.Generality > 0) part += ", " + rank.Generality + " level" + (rank.Generality == 1 ? "" : "s") + " above the item's own category";
            }
            if (rules.Priority != 0) part += ", " + ChestPriority.Short(rules.Priority);
            if (rules.Overflow) part += ", overflow";
            return part;
        }
    }
}
