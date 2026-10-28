using System;
using System.Globalization;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class DestinationRoutingTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static ClassificationResult Wood() => Build(new[] { Item() }, evidence: new[] {
        new ItemClassificationEvidence("Wood", new[] { new MembershipEvidence("materials.wood", "test", "Observed wood.") },
            new[] { new FacetEvidence("usage", "fuel", "test", "Complete route.") }) }).Items["Wood"];
    private static DestinationCandidate Candidate(string id, ClassificationFilterRule? rule = null, int priority = 0,
        bool overflow = false, string[]? includes = null, string[]? excludes = null, DestinationEligibility? eligibility = null) =>
        new DestinationCandidate(id, new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { rule ?? Rule() }, includes, excludes, priority, overflow), eligibility ?? new DestinationEligibility(true, true, true));
    private static DestinationCandidate Fallback(string id, bool optIn = true, string? identity = "full-wood-identity", string[]? excludes = null,
        DestinationEligibility? eligibility = null) => new DestinationCandidate(id,
            new ChestRules(Taxonomy, ChestRuleState.Unconfigured, excludes: excludes, existingItemFallback: optIn),
            eligibility ?? new DestinationEligibility(true, true, true), identity);

    private static ClassificationResult Copper() => Build(new[] { Item("Copper") }, new ClassificationRuleSet(Taxonomy,
        new[] { new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal", "materials.crafting" }) })).Items["Copper"];

    [Fact]
    public void Rank_GeneralityThenSmallerCategoryBreakEqualPriorities()
    {
        // P4-00d (D051/D054): Copper sits directly in Metals and Crafting materials, and one level below Materials.
        var rows = new[] { Candidate("materials", Rule(new[] { "materials" })),
            Candidate("crafting", Rule(new[] { "materials.crafting" })), Candidate("metal", Rule(new[] { "materials.metal" })) };
        int Size(string id) => id switch { "materials.metal" => 12, "materials.crafting" => 300, _ => 900 };
        var ranked = DestinationRouting.Rank(Copper(), "copper", rows, taxonomy: Taxonomy, categorySize: Size);
        Assert.Equal(new[] { "metal", "crafting", "materials" }, ranked.Select(x => x.Candidate.Id));
        Assert.Equal(new[] { 0, 0, 1 }, ranked.Select(x => x.Generality));
        Assert.Equal(new[] { 12, 300, 900 }, ranked.Select(x => x.CategorySize));
        // Without sizes, the id breaks the generality tie; without a taxonomy, only the id is left.
        Assert.Equal(new[] { "crafting", "metal", "materials" }, DestinationRouting.Rank(Copper(), "copper", rows, taxonomy: Taxonomy).Select(x => x.Candidate.Id));
        Assert.Equal(new[] { "crafting", "materials", "metal" }, DestinationRouting.Rank(Copper(), "copper", rows).Select(x => x.Candidate.Id));
    }

    [Fact]
    public void Rank_PriorityBeforeSpecificity()
    {
        // D061: a priority-1 Materials chest gets Copper before a priority-0 Metals chest.
        var rows = new[] { Candidate("materials", Rule(new[] { "materials" }), priority: 1),
            Candidate("crafting", Rule(new[] { "materials.crafting" })), Candidate("metal", Rule(new[] { "materials.metal" })) };
        int Size(string id) => id switch { "materials.metal" => 12, "materials.crafting" => 300, _ => 900 };
        Assert.Equal(new[] { "materials", "metal", "crafting" },
            DestinationRouting.Rank(Copper(), "copper", rows, taxonomy: Taxonomy, categorySize: Size).Select(x => x.Candidate.Id));
    }

    [Fact]
    public void Rank_GeneralityUsesTheClosestCategoryWithinOneRuleAndNeverOutranksMatchClass()
    {
        var rows = new[] { Candidate("mixed", Rule(new[] { "materials", "materials.metal" })), Candidate("broad", Rule(new[] { "materials" })),
            Candidate("exact", Rule(new[] { "food" }), includes: new[] { "Copper" }) };
        var ranked = DestinationRouting.Rank(Copper(), "copper", rows, taxonomy: Taxonomy, categorySize: _ => 1);
        Assert.Equal(new[] { "exact", "mixed", "broad" }, ranked.Select(x => x.Candidate.Id));
        Assert.Equal(0, ranked[1].Generality);
        Assert.Equal(0, ranked[0].Generality);
    }

    [Fact]
    public void Rank_MatchClassOrdersEqualPriorities()
    {
        var rows = new[] { Candidate("any"),
            Candidate("filter", Rule(filters: new[] { ("usage", new[] { "fuel" }) })),
            Candidate("category", Rule(new[] { "materials.wood" })),
            Candidate("filtered-category", Rule(new[] { "materials.wood" }, ("usage", new[] { "fuel" }))),
            Candidate("exact", includes: new[] { "Wood" }) };
        var result = DestinationRouting.Rank(Wood(), "full-wood-identity", rows);
        Assert.Equal(new[] { "exact", "filtered-category", "category", "filter", "any" }, result.Select(x => x.Candidate.Id));
        Assert.Equal(new[] { AcceptanceMatch.ExactItem, AcceptanceMatch.FilteredCategory, AcceptanceMatch.Category,
            AcceptanceMatch.Filters, AcceptanceMatch.Any }, result.Select(x => x.Match));
        Assert.All(result, destination => Assert.False(destination.ExistingItemFallback));
        // D061: priority still comes first, even over an exact-item rule.
        var prioritized = rows.Select(x => x.Id == "any" ? Candidate("any", priority: 1) : x).ToArray();
        Assert.Equal("any", DestinationRouting.Rank(Wood(), "full-wood-identity", prioritized)[0].Candidate.Id);
    }

    [Fact]
    public void Rank_LargerPriorityWinsWithinClassAndOrdinalIdBreaksTies()
    {
        var rows = new[] { Candidate("a", priority: int.MinValue), Candidate("Z", priority: 0), Candidate("a-high", priority: int.MaxValue),
            Candidate("A", priority: 0), Candidate("low", priority: int.MinValue + 1) };
        var expected = new[] { "a-high", "A", "Z", "low", "a" };
        Assert.Equal(expected, DestinationRouting.Rank(Wood(), "identity", rows).Select(x => x.Candidate.Id));
        Assert.Equal(expected, DestinationRouting.Rank(Wood(), "identity", rows.Reverse()).Select(x => x.Candidate.Id));
        Assert.Equal(0, Candidate("default").Rules.Priority);
    }

    [Fact]
    public void Rank_OrdinalTieOrderIgnoresCurrentCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var rows = new[] { Candidate("a"), Candidate("Z") };
            Assert.Equal(new[] { "Z", "a" }, DestinationRouting.Rank(Wood(), "identity", rows).Select(x => x.Candidate.Id));
            Assert.Equal(new[] { "Z", "a" }, DestinationRouting.Rank(Wood(), "identity", rows.Reverse()).Select(x => x.Candidate.Id));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void Rank_AllEligibleOrdinaryDestinationsPrecedeExplicitOverflow()
    {
        var rows = new[] { Candidate("overflow-exact", includes: new[] { "Wood" }, priority: int.MaxValue, overflow: true),
            Candidate("ordinary", priority: int.MinValue), Fallback("existing"),
            Candidate("overflow-any", priority: int.MaxValue, overflow: true) };
        Assert.Equal(new[] { "existing", "ordinary", "overflow-exact", "overflow-any" },
            DestinationRouting.Rank(Wood(), "full-wood-identity", rows).Select(x => x.Candidate.Id));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Rank_EachEligibilityGateRefusesBothExactAndFallback(bool access, bool capacity, bool protection)
    {
        var gates = new DestinationEligibility(access, capacity, protection);
        Assert.Empty(DestinationRouting.Rank(Wood(), "full-wood-identity", new[] {
            Candidate("exact", includes: new[] { "Wood" }, eligibility: gates), Fallback("fallback", eligibility: gates) }));
    }

    [Fact]
    public void Rank_ExclusionsAndNonmatchesRefuseIncludingFallback()
    {
        Assert.Empty(DestinationRouting.Rank(Wood(), "full-wood-identity", new[] {
            Candidate("excluded", includes: new[] { "Wood" }, excludes: new[] { "Wood" }),
            Candidate("nonmatch", Rule(new[] { "food" })), Fallback("fallback-excluded", excludes: new[] { "Wood" }),
            new DestinationCandidate("explicit-empty", new ChestRules(Taxonomy, ChestRuleState.Configured), new DestinationEligibility(true, true, true), "full-wood-identity") }));
    }

    [Fact]
    public void Rank_FallbackRequiresOptInAndFullOrdinalIdentityEquality()
    {
        var result = DestinationRouting.Rank(Wood(), "full-wood-identity", new[] {
            Fallback("off", false), Fallback("missing", identity: null), Fallback("prefab-only", identity: "Wood"),
            Fallback("wrong-case", identity: "Full-wood-identity"), Fallback("metadata-differs", identity: "other-quality-wood"), Fallback("valid") });
        var accepted = Assert.Single(result);
        Assert.Equal("valid", accepted.Candidate.Id); Assert.True(accepted.ExistingItemFallback); Assert.Equal(AcceptanceMatch.None, accepted.Match);
    }

    [Fact]
    public void Rank_CurrentRarityIsFreshAndOneShotEvidenceIsCapturedOnce()
    {
        var sword = Build(new[] { Item("Sword", "OneHandedWeapon") }).Items["Sword"];
        var rows = new[] { Candidate("rare", Rule(filters: new[] { ("rarity", new[] { "rare" }) })) };
        var reads = 0;
        System.Collections.Generic.IEnumerable<FacetEvidence> Once()
        {
            if (++reads != 1) throw new InvalidOperationException("Reread instance facts.");
            yield return new FacetEvidence("rarity", "rare", "instance", "Current item metadata.");
        }
        Assert.Equal("rare", Assert.Single(DestinationRouting.Rank(sword, "sword-rare", rows, Once())).Candidate.Id);
        Assert.Equal(1, reads); Assert.Empty(DestinationRouting.Rank(sword, "sword-unknown", rows));
        Assert.Empty(DestinationRouting.Rank(sword, "sword-epic", rows,
            new[] { new FacetEvidence("rarity", "epic", "instance", "Changed item metadata.") }));
    }

    [Fact]
    public void Rank_RejectsAmbiguousIdentityInvalidInstanceEvidenceAndCandidateOverflow()
    {
        Assert.Throws<ArgumentException>(() => DestinationRouting.Rank(Wood(), "identity", new[] { Candidate("same"), Candidate("same") }));
        Assert.Throws<ArgumentException>(() => DestinationRouting.Rank(Wood(), "identity", new DestinationCandidate[] { null! }));
        Assert.Throws<ArgumentException>(() => DestinationRouting.Rank(Wood(), "identity", Array.Empty<DestinationCandidate>(),
            new[] { new FacetEvidence("rarity", "rare", "instance", "Invalid material override.") }));
        var empty = new ChestRules(Taxonomy, ChestRuleState.Configured); var eligibility = new DestinationEligibility(true, true, true);
        var maximum = Enumerable.Range(0, DestinationRouting.MaximumCandidates).Select(i => new DestinationCandidate("d" + i, empty, eligibility)).ToArray();
        Assert.Empty(DestinationRouting.Rank(Wood(), "identity", maximum));
        Assert.Throws<ArgumentException>(() => DestinationRouting.Rank(Wood(), "identity", maximum.Append(new DestinationCandidate("extra", empty, eligibility))));
    }
}
