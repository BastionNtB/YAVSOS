using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>Fresh caller-supplied gates. These observations never grant mutation authority.</summary>
public sealed class DestinationEligibility
{
    public bool Accessible { get; }
    public bool HasCapacity { get; }
    public bool ProtectionAllows { get; }
    public bool Eligible => Accessible && HasCapacity && ProtectionAllows;
    public DestinationEligibility(bool accessible, bool hasCapacity, bool protectionAllows)
    { Accessible = accessible; HasCapacity = hasCapacity; ProtectionAllows = protectionAllows; }
}

public sealed class DestinationCandidate
{
    public string Id { get; }
    public ChestRules Rules { get; }
    public DestinationEligibility Eligibility { get; }
    /// <summary>Opaque adapter-produced full stack identity, including relevant metadata; never just a prefab name.</summary>
    public string? ExistingItemIdentity { get; }
    public DestinationCandidate(string id, ChestRules rules, DestinationEligibility eligibility, string? existingItemIdentity = null)
    {
        ClassificationValidation.Text(id, 256);
        if (existingItemIdentity != null) ClassificationValidation.Text(existingItemIdentity, 2048);
        Id = id; Rules = rules ?? throw new ArgumentNullException(nameof(rules));
        Eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility)); ExistingItemIdentity = existingItemIdentity;
    }
}

public sealed class RankedDestination
{
    public DestinationCandidate Candidate { get; }
    public AcceptanceMatch Match { get; }
    public bool ExistingItemFallback { get; }
    /// <summary>For a category match, the item's depth below the closest matching rule category; 0 names its most specific one.</summary>
    public int Generality { get; }
    /// <summary>Current members of that category; smaller wins a generality tie (D054).</summary>
    public int CategorySize { get; }
    /// <summary>For a category match ranked with a taxonomy: the rule category that decided generality and size.</summary>
    public string? Category { get; }
    /// <summary>The rule alternative that category belongs to.</summary>
    public ClassificationFilterRule? Rule { get; }
    internal RankedDestination(DestinationCandidate candidate, AcceptanceMatch match, bool fallback, int generality = 0, int size = 0,
        string? category = null, ClassificationFilterRule? rule = null)
    {
        Candidate = candidate; Match = match; ExistingItemFallback = fallback; Generality = generality; CategorySize = size;
        Category = category; Rule = rule;
    }
}

/// <summary>Detached preferences only. Revalidate all inventory, policy, ownership and access facts before a transfer.</summary>
public static class DestinationRouting
{
    public const int MaximumCandidates = 4096;
    /// <summary>
    /// Order: ordinary before overflow, larger priority first (D061), then match class, then for category matches
    /// generality and category size when a taxonomy is supplied (P4-00d, D051/D054), then ID. A priority-1 Materials
    /// chest gets Copper before a priority-0 Metals chest; with equal priorities, Metals wins.
    /// </summary>
    public static IReadOnlyList<RankedDestination> Rank(ClassificationResult item, string itemIdentity,
        IEnumerable<DestinationCandidate> candidates, IEnumerable<FacetEvidence>? currentInstanceFacets = null,
        ClassificationTaxonomy? taxonomy = null, Func<string, int>? categorySize = null)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        ClassificationValidation.Text(itemIdentity, 2048);
        var rows = ClassificationValidation.Bounded(candidates, MaximumCandidates);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row == null || !ids.Add(row.Id)) throw new ArgumentException("Null or duplicate destination identity.");
        // Capture once: callers may supply a one-shot collection; instance evidence must not leak between calls.
        var facets = ClassificationFilters.InstanceFacets(item, currentInstanceFacets);
        var accepted = new List<RankedDestination>();
        foreach (var row in rows)
        {
            if (!row.Eligibility.Eligible) continue;
            var match = row.Rules.Acceptance.Match(item, facets);
            if (match == AcceptanceMatch.Excluded) continue;
            if (row.Rules.State == ChestRuleState.Configured)
            {
                if (match == AcceptanceMatch.None) continue;
                var (generality, size, category, rule) = match is AcceptanceMatch.Category or AcceptanceMatch.FilteredCategory && taxonomy != null
                    ? Closest(item, row.Rules.Acceptance, match, facets, taxonomy, categorySize) : (0, 0, null, null);
                accepted.Add(new RankedDestination(row, match, false, generality, size, category, rule));
            }
            else if (row.Rules.ExistingItemFallback && string.Equals(itemIdentity, row.ExistingItemIdentity, StringComparison.Ordinal))
                accepted.Add(new RankedDestination(row, AcceptanceMatch.None, true));
        }
        return Array.AsReadOnly(accepted.OrderBy(x => x.Candidate.Rules.Overflow).ThenByDescending(x => x.Candidate.Rules.Priority)
            .ThenByDescending(x => x.Match).ThenBy(x => x.Generality).ThenBy(x => x.CategorySize)
            .ThenBy(x => x.Candidate.Id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// D086 D4 (M12), B4.14: the configured chests that list the item under Always and not under Never, for an item with no
    /// category answer (categories still loading, or an item the answers don't know). Same order as <see cref="Rank"/>:
    /// ordinary before overflow, larger priority first, then ID. Needs no categories (D048).
    /// </summary>
    public static IReadOnlyList<RankedDestination> ExactOnly(string itemIdentity, IEnumerable<DestinationCandidate> candidates)
    {
        ClassificationValidation.Text(itemIdentity, 2048);
        var rows = ClassificationValidation.Bounded(candidates, MaximumCandidates);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row == null || !ids.Add(row.Id)) throw new ArgumentException("Null or duplicate destination identity.");
        return Array.AsReadOnly(rows.Where(x => x.Eligibility.Eligible && x.Rules.State == ChestRuleState.Configured &&
                x.Rules.Includes.Contains(itemIdentity, StringComparer.Ordinal) && !x.Rules.Excludes.Contains(itemIdentity, StringComparer.Ordinal))
            .Select(x => new RankedDestination(x, AcceptanceMatch.ExactItem, false))
            .OrderBy(x => x.Candidate.Rules.Overflow).ThenByDescending(x => x.Candidate.Rules.Priority)
            .ThenBy(x => x.Candidate.Id, StringComparer.Ordinal).ToArray());
    }

    // Among the rule alternatives that produced this match class, the matching category the item sits closest
    // under, then the smallest. A category the taxonomy no longer knows was already ignored by the rule (D050).
    private static (int Generality, int Size, string? Category, ClassificationFilterRule? Rule) Closest(ClassificationResult item,
        ClassificationAcceptance acceptance, AcceptanceMatch match, FacetEvidence[] facets, ClassificationTaxonomy taxonomy, Func<string, int>? categorySize)
    {
        var best = (Generality: int.MaxValue, Size: int.MaxValue); string? bestCategory = null; ClassificationFilterRule? bestRule = null;
        foreach (var rule in acceptance.Rules)
        {
            var filtered = rule.Filters.Values.Any(x => x.Count != 0);
            if (rule.Categories.Count == 0 || filtered != (match == AcceptanceMatch.FilteredCategory) ||
                !ClassificationFilters.MatchesValidated(item, rule, facets)) continue;
            foreach (var category in rule.Categories)
            {
                var top = taxonomy.DepthOf(category);
                if (top < 0) continue;
                var deepest = -1;
                foreach (var membership in item.Memberships)
                    if (taxonomy.IsAncestorOrSelf(category, membership.Category)) deepest = Math.Max(deepest, taxonomy.DepthOf(membership.Category));
                if (deepest < 0) continue;
                var candidate = (Generality: deepest - top, Size: categorySize == null ? 0 : Math.Max(0, categorySize(category)));
                if (candidate.Generality < best.Generality || candidate.Generality == best.Generality && candidate.Size < best.Size)
                { best = candidate; bestCategory = category; bestRule = rule; }
            }
        }
        return best.Generality == int.MaxValue ? (0, 0, null, null) : (best.Generality, best.Size, bestCategory, bestRule);
    }
}
