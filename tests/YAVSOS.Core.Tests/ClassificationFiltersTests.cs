using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationFiltersTests
{
    private static ClassificationResult Material(params (string Dimension, string Value)[] facets) => Build(new[] { Item() }, evidence: new[] {
        new ItemClassificationEvidence("Wood", new[] { new MembershipEvidence("materials.wood", "adapter", "Wood evidence.") },
            facets.Select(f => new FacetEvidence(f.Dimension, f.Value, "adapter", "Observed."))) }).Items["Wood"];

    [Fact]
    public void Matches_OrSelectionsAndDimensionsWithoutUsingPrimary()
    {
        var item = Material(("usage", "fuel"), ("mod", "vanilla"), ("related-mod", "monstrum"));
        Assert.True(ClassificationFilters.Matches(item, Rule(new[] { "food", "materials.wood" },
            ("usage", new[] { "food", "fuel" }), ("mod", new[] { "vanilla" }))));
        Assert.False(ClassificationFilters.Matches(item, Rule(new[] { "materials.wood" },
            ("usage", new[] { "fuel" }), ("mod", new[] { "monstrum" }))));
        Assert.False(ClassificationFilters.Matches(item, Rule(new[] { "food" })));
        Assert.True(ClassificationFilters.Matches(item, Rule(filters: new[] { ("usage", Array.Empty<string>()) })));
    }

    [Fact]
    public void Acceptance_AlternativeRulesDoNotCrossCombineDimensions()
    {
        var rules = new[] { Rule(filters: new[] { ("usage", new[] { "crafting" }), ("mod", new[] { "monstrum" }) }),
            Rule(filters: new[] { ("usage", new[] { "fuel" }), ("mod", new[] { "vanilla" }) }) };
        var acceptance = new ClassificationAcceptance(rules);
        Assert.False(acceptance.Accepts(Material(("usage", "crafting"), ("mod", "vanilla"))));
        Assert.False(acceptance.Accepts(Material(("usage", "fuel"), ("mod", "monstrum"))));
        Assert.True(acceptance.Accepts(Material(("usage", "crafting"), ("mod", "monstrum"))));
        Assert.True(acceptance.Accepts(Material(("usage", "fuel"), ("mod", "vanilla"))));
    }

    [Fact]
    public void Matches_UnknownMeansMissingEvidenceAndEmptySelectionIsUnrestricted()
    {
        var unknown = Material(); var known = Material(("usage", "fuel"));
        Assert.True(ClassificationFilters.Matches(unknown, Rule(filters: new[] { ("usage", new[] { "unknown" }) })));
        Assert.False(ClassificationFilters.Matches(unknown, Rule(filters: new[] { ("usage", new[] { "fuel" }) })));
        Assert.False(ClassificationFilters.Matches(known, Rule(filters: new[] { ("usage", new[] { "unknown" }) })));
        Assert.True(ClassificationFilters.Matches(known, Rule(filters: new[] { ("usage", new[] { "unknown", "fuel" }) })));
        Assert.True(ClassificationFilters.Matches(unknown, Rule()));
        Assert.True(ClassificationFilters.Matches(known, Rule(filters: new[] { ("usage", Array.Empty<string>()) })));
    }

    [Theory]
    [InlineData("mod", "origin-pack")]
    [InlineData("related-mod", "creature-pack")]
    [InlineData("construction-mod", "build-pack")]
    [InlineData("usage", "building")]
    [InlineData("acquisition", "trader")]
    public void Matches_OriginAssociationConstructionUsageAndAcquisitionRemainIndependent(string dimension, string value)
    {
        var item = Material((dimension, value));
        Assert.True(ClassificationFilters.Matches(item, Rule(filters: new[] { (dimension, new[] { value }) })));
        foreach (var other in new[] { "mod", "related-mod", "construction-mod", "usage", "acquisition" }.Where(x => x != dimension))
            Assert.True(ClassificationFilters.Matches(item, Rule(filters: new[] { (other, new[] { "unknown" }) })));
    }

    [Fact]
    public void Acceptance_ExclusionBeatsInclusionAndInclusionBeatsFilters()
    {
        var item = Material();
        var both = new ClassificationAcceptance(new[] { Rule() }, new[] { "Wood" }, new[] { "Wood" });
        Assert.Equal(AcceptanceMatch.Excluded, both.Match(item)); Assert.False(both.Accepts(item));
        var include = new ClassificationAcceptance(new[] { Rule(new[] { "food" }) }, new[] { "Wood" });
        Assert.Equal(AcceptanceMatch.ExactItem, include.Match(item)); Assert.True(include.Accepts(item));
        Assert.Equal(AcceptanceMatch.None, new ClassificationAcceptance(Array.Empty<ClassificationFilterRule>(), new[] { "wood" }).Match(item));
    }

    [Theory]
    [InlineData("none", AcceptanceMatch.None)]
    [InlineData("any", AcceptanceMatch.Any)]
    [InlineData("filters", AcceptanceMatch.Filters)]
    [InlineData("category", AcceptanceMatch.Category)]
    [InlineData("filtered-category", AcceptanceMatch.FilteredCategory)]
    public void Acceptance_ReportsStrongestMatchingClass(string caseId, AcceptanceMatch expected)
    {
        var rule = caseId switch { "none" => Rule(new[] { "food" }), "any" => Rule(),
            "filters" => Rule(filters: new[] { ("usage", new[] { "fuel" }) }),
            "category" => Rule(new[] { "materials.wood" }),
            _ => Rule(new[] { "materials.wood" }, ("usage", new[] { "fuel" })) };
        var rows = caseId == "none" ? new[] { rule } : new[] { Rule(), rule };
        Assert.Equal(expected, new ClassificationAcceptance(rows).Match(Material(("usage", "fuel"))));
    }

    [Fact]
    public void Matches_EquipmentRarityUsesCurrentInstanceAndCannotReplaceStaticFacets()
    {
        var item = Build(new[] { Item("Sword", "OneHandedWeapon") }, evidence: new[] {
            new ItemClassificationEvidence("Sword", facets: new[] { new FacetEvidence("usage", "food", "proof", "Complete route.") }) }).Items["Sword"];
        var rule = Rule(new[] { "weapons" }, ("rarity", new[] { "rare" }), ("usage", new[] { "food" }));
        var rare = new[] { new FacetEvidence("rarity", "rare", "instance", "Current metadata.") };
        var epic = new[] { new FacetEvidence("rarity", "epic", "instance", "Current metadata.") };
        Assert.False(ClassificationFilters.Matches(item, rule));
        Assert.True(ClassificationFilters.Matches(item, rule, rare));
        Assert.False(ClassificationFilters.Matches(item, rule, epic));
        Assert.True(ClassificationFilters.Matches(item, Rule(filters: new[] { ("rarity", new[] { "unknown" }) })));
        Assert.False(ClassificationFilters.Matches(item, Rule(filters: new[] { ("rarity", new[] { "unknown" }) }), rare));
        Assert.Throws<ArgumentException>(() => ClassificationFilters.Matches(item, rule, rare.Concat(epic)));
        Assert.Throws<ArgumentException>(() => ClassificationFilters.Matches(item, rule, new[] { new FacetEvidence("usage", "fuel", "instance", "Forbidden.") }));
        Assert.DoesNotContain(item.Facets, x => x.Dimension == "rarity");
        Assert.Equal("food", Assert.Single(item.Facets).Value);
    }

    [Fact]
    public void Matches_StaticMaterialRarityRejectsInstanceSupplement()
    {
        var item = Material(("rarity", "ancient"));
        var rule = Rule(filters: new[] { ("rarity", new[] { "ancient" }) });
        Assert.True(ClassificationFilters.Matches(item, rule));
        Assert.Throws<ArgumentException>(() => ClassificationFilters.Matches(item, rule,
            new[] { new FacetEvidence("rarity", "rare", "instance", "Forbidden material override.") }));
    }

    [Fact]
    public void Rule_RejectsInvalidDimensionsValuesReferencesAndDuplicateDimensions()
    {
        foreach (var invalid in new[] { ("Tier", "1"), ("a b", "1"), ("tier", ClassificationFilters.Unreadable), ("usage", ClassificationFilters.Unreadable), ("usage", new string('x', 129)) })
            Assert.Throws<ArgumentException>(() => Rule(filters: new[] { (invalid.Item1, new[] { invalid.Item2 }) }));
        // D086 D6: values a known dimension doesn't have (a later version's) are kept and match nothing.
        foreach (var later in new[] { ("usage", "Fuel"), ("rarity", "Rare"), ("usage", "arbitrary") })
            Assert.False(ClassificationFilters.IsKnownRuleValue(later.Item1, later.Item2));
        Assert.Equal(new[] { "arbitrary" }, Rule(filters: new[] { ("usage", new[] { "arbitrary" }) }).Filters["usage"]);
        Assert.Throws<ArgumentException>(() => Rule(new[] { "retired" }));
        Assert.Throws<ArgumentException>(() => Rule(filters: new[] { ("mod", new[] { "one" }), ("mod", new[] { "two" }) }));
        Assert.Throws<ArgumentException>(() => new FacetEvidence("usage", "unknown", "source", "Missing is not known."));
    }

    [Fact]
    public void RuleAndAcceptance_CountBoundsAndInputOwnershipAreEnforced()
    {
        var categories = new[] { "materials.wood" }; var values = new[] { "fuel" }; var rules = new[] { Rule(categories, ("usage", values)) };
        var includes = new[] { "Stone" }; var acceptance = new ClassificationAcceptance(rules, includes);
        categories[0] = "food"; values[0] = "food"; includes[0] = "Wood"; rules[0] = Rule(new[] { "food" });
        Assert.Equal(AcceptanceMatch.FilteredCategory, acceptance.Match(Material(("usage", "fuel"))));
        Assert.Equal(64, new ClassificationAcceptance(Enumerable.Repeat(Rule(), 64)).Rules.Count);
        Assert.Throws<ArgumentException>(() => new ClassificationAcceptance(Enumerable.Repeat(Rule(), 65)));
        Assert.Throws<ArgumentException>(() => new ClassificationAcceptance(Array.Empty<ClassificationFilterRule>(), Enumerable.Range(0, 2049).Select(i => "I" + i)));
        Assert.Equal(64, Rule(filters: new[] { ("mod", Enumerable.Range(0, 64).Select(i => "mod" + i).ToArray()) }).Filters["mod"].Count);
        Assert.Throws<ArgumentException>(() => Rule(filters: new[] { ("mod", Enumerable.Range(0, 65).Select(i => "mod" + i).ToArray()) }));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, IReadOnlyList<string>>)acceptance.Rules[0].Filters).Clear());
    }
}
