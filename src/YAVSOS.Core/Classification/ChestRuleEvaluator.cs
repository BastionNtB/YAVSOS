using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Classification;

/// <summary>Current per-item category answers. Generation changes whenever any answer may have changed.</summary>
public interface ICategoryAnswers
{
    long Generation { get; }
    bool CategoriesReady { get; }
    ClassificationResult? Resolve(string prefab);
}

public enum ChestRuleDecision
{
    /// <summary>No rule, or an unconfigured one: the stasher keeps its own defaults.</summary>
    NoRule,
    Accept,
    Reject,
    /// <summary>Only the category part could accept, and categories are not ready yet (D048).</summary>
    RejectCategoriesPending,
    /// <summary>A stored rule this build cannot read. It never broadens to "no rule".</summary>
    RejectUnreadable
}

/// <summary>
/// P3-02 <c>ruleAccepts(chest, item)</c> over a chest's stored policy payload. Exact excludes win, then exact
/// includes; both apply before categories are ready. The category part accepts nothing until it is (D048).
/// Parsed rules are cached by payload and decisions by (rule, item); both clear when the taxonomy or the
/// category-answer generation changes. Single-threaded (Unity game thread) use only.
/// </summary>
public sealed class ChestRuleEvaluator
{
    private readonly int maximumRules;
    private readonly int maximumDecisions;
    private readonly Dictionary<string, ContainerPolicyRead> rules = new Dictionary<string, ContainerPolicyRead>(StringComparer.Ordinal);
    private readonly Dictionary<(ContainerPolicyRead Rule, string Prefab), ChestRuleDecision> decisions =
        new Dictionary<(ContainerPolicyRead Rule, string Prefab), ChestRuleDecision>();
    private readonly Dictionary<ContainerPolicyRead, bool> instanceRules = new Dictionary<ContainerPolicyRead, bool>();
    private ClassificationTaxonomy? taxonomy;
    private long generation = long.MinValue;

    public ChestRuleEvaluator(int maximumRules = 256, int maximumDecisions = 65536)
    {
        if (maximumRules < 1) throw new ArgumentOutOfRangeException(nameof(maximumRules));
        if (maximumDecisions < 1) throw new ArgumentOutOfRangeException(nameof(maximumDecisions));
        this.maximumRules = maximumRules; this.maximumDecisions = maximumDecisions;
    }

    public int CachedRules => rules.Count;
    public int CachedDecisions => decisions.Count;

    public ContainerPolicyRead Read(string payload, ClassificationTaxonomy currentTaxonomy)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (currentTaxonomy == null) throw new ArgumentNullException(nameof(currentTaxonomy));
        if (!ReferenceEquals(taxonomy, currentTaxonomy)) { taxonomy = currentTaxonomy; rules.Clear(); decisions.Clear(); instanceRules.Clear(); }
        if (rules.TryGetValue(payload, out var read)) return read;
        read = ContainerPolicyEnvelope.Read(payload, currentTaxonomy);
        if (rules.Count >= maximumRules) { rules.Clear(); decisions.Clear(); instanceRules.Clear(); }
        rules.Add(payload, read);
        return read;
    }

    /// <summary>
    /// A chest without a rule: nothing stored, or a readable unconfigured rule with no "never" part (what clearing wrote
    /// before D086 M1). Such a chest takes only items it already holds; an unreadable rule is neither ruled nor unruled.
    /// </summary>
    public bool Unruled(string payload, ClassificationTaxonomy currentTaxonomy)
    {
        if (string.IsNullOrEmpty(payload)) return true;
        var read = Read(payload, currentTaxonomy);
        return read.State == ContainerPolicyReadState.Supported && read.Rules is { State: ChestRuleState.Unconfigured, Excludes.Count: 0 };
    }

    /// <summary>
    /// Whether a configured rule has an instance filter (<see cref="ClassificationFilters.InstanceDimensions"/>), so
    /// callers read an item's instance evidence only when it can change the answer (P6-07b). Cached with the rule.
    /// </summary>
    public bool UsesInstanceFilters(string payload, ClassificationTaxonomy currentTaxonomy)
    {
        if (string.IsNullOrEmpty(payload)) return false;
        return UsesInstanceFilters(Read(payload, currentTaxonomy));
    }

    private bool UsesInstanceFilters(ContainerPolicyRead read)
    {
        var chest = read.Rules;
        if (chest == null || chest.State != ChestRuleState.Configured) return false;
        if (instanceRules.TryGetValue(read, out var uses)) return uses;
        uses = chest.Acceptance.Rules.Any(x => x.Filters.Any(f => f.Value.Count != 0 && ClassificationFilters.InstanceDimensions.Contains(f.Key)));
        instanceRules[read] = uses;
        return uses;
    }

    /// <param name="currentInstanceFacets">
    /// Fresh evidence of the one item instance being placed (EpicLoot rarity, condition; P6-07b). Decisions that use
    /// it are not cached; without it, instance filters see Unknown. A rule without instance filters ignores it and
    /// keeps its cached answer, since such evidence cannot change it.
    /// </param>
    public ChestRuleDecision Evaluate(string payload, string prefab, ClassificationTaxonomy currentTaxonomy, ICategoryAnswers answers,
        IReadOnlyList<FacetEvidence>? currentInstanceFacets = null)
    {
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        if (answers == null) throw new ArgumentNullException(nameof(answers));
        if (payload != null && payload.Length == 0) return ChestRuleDecision.NoRule;
        var read = Read(payload!, currentTaxonomy);
        if (read.State == ContainerPolicyReadState.Unsupported) return ChestRuleDecision.RejectUnreadable;
        var chest = read.Rules;
        if (chest == null) return ChestRuleDecision.NoRule;
        // An unconfigured rule has no positive part, but a stored "never" still applies.
        if (chest.State == ChestRuleState.Unconfigured)
            return Contains(chest.Excludes, prefab) ? ChestRuleDecision.Reject : ChestRuleDecision.NoRule;
        if (currentInstanceFacets != null && currentInstanceFacets.Count != 0 && UsesInstanceFilters(read)) return Decide(chest, prefab, answers, currentInstanceFacets);
        if (generation != answers.Generation) { generation = answers.Generation; decisions.Clear(); }
        if (decisions.TryGetValue((read, prefab), out var cached)) return cached;
        var decision = Decide(chest, prefab, answers, null);
        if (decisions.Count >= maximumDecisions) decisions.Clear();
        decisions.Add((read, prefab), decision);
        return decision;
    }

    /// <summary>
    /// P8-18 (Q14): whether the chest's rule could take some stack of <paramref name="prefab"/>, with filters on what a
    /// stack is set aside (<see cref="ClassificationAcceptance.CouldAccept"/>), for the search highlight. Always and Never
    /// still apply; while categories load only the exact lists answer. Not cached: a search asks a few chests per frame.
    /// </summary>
    public bool CouldAccept(string payload, string prefab, ClassificationTaxonomy currentTaxonomy, ICategoryAnswers answers)
    {
        if (prefab == null) throw new ArgumentNullException(nameof(prefab));
        if (answers == null) throw new ArgumentNullException(nameof(answers));
        if (string.IsNullOrEmpty(payload)) return false;
        var read = Read(payload, currentTaxonomy);
        var chest = read.Rules;
        if (read.State == ContainerPolicyReadState.Unsupported || chest == null || chest.State != ChestRuleState.Configured) return false;
        if (Contains(chest.Excludes, prefab)) return false;
        if (Contains(chest.Includes, prefab)) return true;
        if (chest.Acceptance.Rules.Count == 0 || !answers.CategoriesReady) return false;
        var item = answers.Resolve(prefab);
        return item != null && chest.Acceptance.CouldAccept(item);
    }

    private static ChestRuleDecision Decide(ChestRules chest, string prefab, ICategoryAnswers answers, IReadOnlyList<FacetEvidence>? instance)
    {
        if (Contains(chest.Excludes, prefab)) return ChestRuleDecision.Reject;
        if (Contains(chest.Includes, prefab)) return ChestRuleDecision.Accept;
        if (chest.Acceptance.Rules.Count == 0) return ChestRuleDecision.Reject;
        if (!answers.CategoriesReady) return ChestRuleDecision.RejectCategoriesPending;
        var item = answers.Resolve(prefab);
        return item != null && chest.Acceptance.Accepts(item, instance) ? ChestRuleDecision.Accept : ChestRuleDecision.Reject;
    }

    // ChestRules keeps exact lists in ordinal order.
    private static bool Contains(IReadOnlyList<string> sorted, string prefab)
    {
        int low = 0, high = sorted.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var order = string.CompareOrdinal(sorted[middle], prefab);
            if (order == 0) return true;
            if (order < 0) low = middle + 1; else high = middle - 1;
        }
        return false;
    }
}
