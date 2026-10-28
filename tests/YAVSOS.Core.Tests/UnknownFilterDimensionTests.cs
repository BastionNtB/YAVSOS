using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>D077 B01: a filter dimension this version doesn't know is kept as stored and matches nothing.</summary>
public sealed class UnknownFilterDimensionTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static ClassificationResult Wood(params (string Dimension, string Value)[] facets) => Build(new[] { Item() }, evidence: new[] {
        new ItemClassificationEvidence("Wood", new[] { new MembershipEvidence("materials.wood", "adapter", "Wood evidence.") },
            facets.Select(f => new FacetEvidence(f.Dimension, f.Value, "adapter", "Observed."))) }).Items["Wood"];
    private const string Stored =
        "<chestRules schema=\"1\" state=\"configured\" priority=\"0\" overflow=\"false\" existingItemFallback=\"false\">" +
        "<rule><category id=\"food\" /></rule>" +
        "<rule><category id=\"materials\" /><filter dimension=\"biome\"><value id=\"ocean\" /><value id=\"unknown\" /></filter></rule></chestRules>";

    [Fact]
    public void UnknownDimension_MatchesNothingEvenForUnknownValue_AndTheRestOfTheRuleWorks()
    {
        Assert.False(ClassificationFilters.IsKnown("biome"));
        var unknownOnly = Rule(new[] { "materials" }, ("biome", new[] { "unknown" }));
        Assert.False(ClassificationFilters.Matches(Wood(), unknownOnly));
        Assert.False(ClassificationFilters.Matches(Wood(("usage", "fuel")), Rule(filters: new[] { ("biome", new[] { "ocean" }), ("usage", new[] { "fuel" }) })));
        // An empty value list is no filter at all.
        Assert.True(ClassificationFilters.Matches(Wood(), Rule(new[] { "materials" }, ("biome", Array.Empty<string>()))));
        var acceptance = new ClassificationAcceptance(new[] { unknownOnly, Rule(new[] { "materials.wood" }) });
        Assert.Equal(AcceptanceMatch.Category, acceptance.Match(Wood()));
    }

    [Fact]
    public void StoredRule_RoundTripsUnchanged_AndIsSupportedNotUnreadable()
    {
        var parsed = ChestRuleCodec.Parse(Stored, Taxonomy);
        Assert.Equal(Stored, ChestRuleCodec.Serialize(parsed));
        var filtered = Assert.Single(parsed.FullRules, x => x.Filters.Count != 0);
        Assert.Equal(new[] { "ocean", "unknown" }, filtered.Filters["biome"]);
        Assert.False(parsed.Acceptance.Accepts(Wood()));
        var payload = ContainerPolicyEnvelope.Serialize(4, parsed);
        var read = ContainerPolicyEnvelope.Read(payload, Taxonomy);
        Assert.Equal(ContainerPolicyReadState.Supported, read.State);
        Assert.True(read.HasRule);
    }

    [Fact]
    public void Editing_KeepsTheUnknownFilterOnTheEditedAndOtherAlternatives_UnlessRemoved()
    {
        var payload = ContainerPolicyEnvelope.Serialize(1, ChestRuleCodec.Parse(Stored, Taxonomy));
        var rules = ContainerPolicyEnvelope.Read(payload, Taxonomy).Rules!;
        // The panel's view: the unfiltered category alternative ("food") is the edited one; the biome one stays as stored.
        var view = ChestRuleEdit.From(rules, withFilters: true);
        Assert.Equal(new[] { "food" }, view.Categories);
        var edit = new ChestRuleEdit(view.Categories.Concat(new[] { "materials.wood" }), new[] { "Stone" }, null, view.UnknownCategories,
            view.Priority, Pairs(view.Filters), Pairs(view.EditedFilters));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, edit, out var updated));
        var after = ContainerPolicyEnvelope.Read(updated!, Taxonomy).Rules!;
        Assert.Equal(new[] { "ocean", "unknown" }, Assert.Single(after.FullRules, x => x.Filters.Count != 0).Filters["biome"]);

        // A filtered-only rule is edited with its filters: unchanged filters keep "biome"; removing it drops it.
        var only = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { Rule(new[] { "materials" }, ("biome", new[] { "ocean" }), ("usage", new[] { "fuel" })) }));
        var shown = ChestRuleEdit.From(ContainerPolicyEnvelope.Read(only, Taxonomy).Rules!, withFilters: true);
        Assert.True(shown.Filters.ContainsKey("biome"));
        var kept = new ChestRuleEdit(shown.Categories, null, null, null, null, Pairs(shown.Filters), Pairs(shown.EditedFilters));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(only, only, Taxonomy, kept, out var same));
        Assert.True(Assert.Single(ContainerPolicyEnvelope.Read(same!, Taxonomy).Rules!.FullRules).Filters.ContainsKey("biome"));
        var removed = new ChestRuleEdit(shown.Categories, null, null, null, null,
            Pairs(shown.Filters.Where(x => x.Key != "biome").ToDictionary(x => x.Key, x => x.Value)), Pairs(shown.EditedFilters));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(only, only, Taxonomy, removed, out var dropped));
        var left = Assert.Single(ContainerPolicyEnvelope.Read(dropped!, Taxonomy).Rules!.FullRules);
        Assert.False(left.Filters.ContainsKey("biome")); Assert.True(left.Filters.ContainsKey("usage"));
    }

    [Fact]
    public void UnknownValueInAKnownDimension_IsKeptAndMatchesNothing_AndTheRuleStaysEditable()
    {
        // D086 D6 (M13): a later version adds a usage value; this version keeps it, it matches nothing, "fuel" still does.
        const string later = "<chestRules schema=\"1\" state=\"configured\" priority=\"0\" overflow=\"false\" existingItemFallback=\"false\">" +
            "<rule><category id=\"materials\" /><filter dimension=\"usage\"><value id=\"fuel\" /><value id=\"future-use\" /></filter></rule></chestRules>";
        var parsed = ChestRuleCodec.Parse(later, Taxonomy);
        Assert.Equal(later, ChestRuleCodec.Serialize(parsed));
        Assert.True(parsed.Acceptance.Accepts(Wood(("usage", "fuel"))));
        Assert.False(parsed.Acceptance.Accepts(Wood(("usage", "building"))));
        Assert.False(ClassificationFilters.Matches(Wood(), Rule(new[] { "materials" }, ("usage", new[] { "future-use" }))));
        Assert.False(ClassificationFilters.Matches(Wood(("usage", "fuel")), Rule(new[] { "materials" }, ("usage", new[] { "future-use" }))));
        Assert.False(ClassificationFilters.IsKnownRuleValue("usage", "future-use"));
        Assert.True(ClassificationFilters.IsKnownRuleValue("usage", "unknown"));
        Assert.Throws<ArgumentException>(() => Rule(new[] { "materials" }, ("damaged", new[] { ClassificationFilters.Unreadable })));
        var payload = ContainerPolicyEnvelope.Serialize(1, parsed);
        var read = ContainerPolicyEnvelope.Read(payload, Taxonomy);
        Assert.Equal(ContainerPolicyReadState.Supported, read.State);
        var view = ChestRuleEdit.From(read.Rules!, withFilters: true);
        var edit = new ChestRuleEdit(view.Categories, new[] { "Stone" }, null, null, null, Pairs(view.Filters), Pairs(view.EditedFilters));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, edit, out var updated));
        Assert.Equal(new[] { "fuel", "future-use" }, Assert.Single(ContainerPolicyEnvelope.Read(updated!, Taxonomy).Rules!.FullRules).Filters["usage"]);
    }

    [Fact]
    public void UnknownDimensions_AreBounded()
    {
        var many = Enumerable.Range(0, ClassificationFilters.MaximumRuleFilters + 1).Select(i => ("future-" + i, new[] { "x" })).ToArray();
        Assert.Throws<ArgumentException>(() => Rule(filters: many));
        Assert.Equal(ClassificationFilters.MaximumRuleFilters, Rule(filters: many.Take(ClassificationFilters.MaximumRuleFilters).ToArray()).Filters.Count);
    }

    private static KeyValuePair<string, IEnumerable<string>>[] Pairs(IReadOnlyDictionary<string, IReadOnlyList<string>> filters) =>
        filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray();
}
