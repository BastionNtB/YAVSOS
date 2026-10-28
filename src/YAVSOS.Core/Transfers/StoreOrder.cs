using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// D070: one placement decision for every way items reach a chest. The store key, the single-item key, AzuAutoStore's
/// ground pickup and "Clean up nearby" offer a stack to the ruled chests whose rule accepts it, in
/// <see cref="DestinationRouting.Rank"/> order (priority, then most specific); only the source differs. The
/// <see cref="StashPlanner"/> ruled tier is this order, and the AzuAutoStore adapter offers Azu's chests in it, so a
/// stash leaves nothing for cleanup to move. The rules are read once, when it is built. Detached; grants nothing.
/// </summary>
public sealed class StoreOrder
{
    private readonly ClassificationTaxonomy taxonomy;
    private readonly ICategoryAnswers answers;
    private readonly Func<string, int>? categorySize;
    private readonly ChestRuleEvaluator evaluator;
    private readonly List<(DestinationCandidate Candidate, string Payload)> ruled = new();

    /// <param name="chests">Chest id and stored rule payload; chests without a configured, readable rule are left out.</param>
    public StoreOrder(ClassificationTaxonomy taxonomy, ICategoryAnswers answers, IEnumerable<KeyValuePair<string, string>> chests,
        Func<string, int>? categorySize = null, ChestRuleEvaluator? evaluator = null)
    {
        this.taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        this.answers = answers ?? throw new ArgumentNullException(nameof(answers));
        this.categorySize = categorySize; this.evaluator = evaluator ?? new ChestRuleEvaluator();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chest in ClassificationValidation.Bounded(chests ?? throw new ArgumentNullException(nameof(chests)), DestinationRouting.MaximumCandidates))
        {
            if (!ids.Add(chest.Key)) throw new ArgumentException("Duplicate chest id.", nameof(chests));
            var candidate = Candidate(chest.Key, chest.Value ?? "", this.evaluator, taxonomy);
            if (candidate != null) ruled.Add((candidate, chest.Value!));
        }
    }

    /// <summary>Ruled chests read, whether or not they take anything.</summary>
    public int Count => ruled.Count;

    /// <summary>Ids of the ruled chests that take the item, best first. Empty when none does or categories are not ready.</summary>
    public IReadOnlyList<string> For(string prefab, IReadOnlyList<FacetEvidence>? facets = null)
    {
        if (string.IsNullOrEmpty(prefab) || ruled.Count == 0 || !answers.CategoriesReady) return Array.Empty<string>();
        var item = answers.Resolve(prefab);
        if (item == null) return Array.Empty<string>();
        var payloads = ruled.ToDictionary(x => x.Candidate, x => x.Payload);
        return Accepting(item, prefab, ruled.Select(x => x.Candidate), x => payloads[x], facets, taxonomy, answers, categorySize, evaluator)
            .Select(x => x.Candidate.Id).ToArray();
    }

    /// <summary>A configured, readable rule as a destination; null for no rule or one that accepts nothing (D048).</summary>
    internal static DestinationCandidate? Candidate(string id, string payload, ChestRuleEvaluator evaluator, ClassificationTaxonomy taxonomy)
    {
        if (payload.Length == 0) return null;
        var read = evaluator.Read(payload, taxonomy);
        if (read.State == ContainerPolicyReadState.Unsupported || read.Rules?.State != ChestRuleState.Configured) return null;
        return new DestinationCandidate(id, read.Rules, new DestinationEligibility(true, true, true));
    }

    /// <summary>The candidates that take the item, best first. Rank and Evaluate take different paths; a destination must pass both.</summary>
    internal static IEnumerable<RankedDestination> Accepting(ClassificationResult item, string prefab, IEnumerable<DestinationCandidate> candidates,
        Func<DestinationCandidate, string> payload, IReadOnlyList<FacetEvidence>? facets, ClassificationTaxonomy taxonomy, ICategoryAnswers answers,
        Func<string, int>? categorySize, ChestRuleEvaluator evaluator)
    {
        foreach (var row in DestinationRouting.Rank(item, prefab, candidates, facets, taxonomy, categorySize))
            if (!row.ExistingItemFallback && evaluator.Evaluate(payload(row.Candidate), prefab, taxonomy, answers, facets) == ChestRuleDecision.Accept)
                yield return row;
    }
}
