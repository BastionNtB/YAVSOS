using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ChestRuleEvaluatorTests;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>D050: a rule naming a category the current set lacks keeps working for everything else.</summary>
public sealed class MissingCategoryRuleTests
{
    private static readonly ClassificationTaxonomy Full = DefaultClassificationTaxonomy.Create();
    // Stands in for an update or server category set that removed "fish".
    private static readonly ClassificationTaxonomy Reduced = new ClassificationTaxonomy(Full.Categories.Values.Where(x => x.Id != "fish"));
    private static readonly CatalogSnapshot Catalog = CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100) });

    private sealed class Answers : ICategoryAnswers
    {
        private readonly AuthoritativeAssignmentSnapshot snapshot = Snapshot(Catalog, TestPolicy());
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) => snapshot.Resolve(prefab, Catalog);
    }

    private static string Stored(params string[] categories)
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Full, new ChestRuleEdit(categories, new[] { "Stone" }, null), out var payload));
        return payload!;
    }

    private static string BreadCategory() => new Answers().Resolve("Bread")!.Memberships[0].Category;

    [Fact]
    public void PartlyUnknown_RuleStaysReadableAndKnownPartsWork()
    {
        var bread = BreadCategory();
        var payload = Stored(bread, "fish");
        var read = ContainerPolicyEnvelope.Read(payload, Reduced);
        Assert.Equal(ContainerPolicyReadState.Supported, read.State); Assert.True(read.HasRule);
        Assert.Equal(new[] { "fish" }, read.Rules!.UnknownCategories);
        var evaluator = new ChestRuleEvaluator(); var answers = new Answers();
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Bread", Reduced, answers));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Stone", Reduced, answers));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Wood", Reduced, answers));
    }

    [Fact]
    public void AllUnknown_AcceptsOnlyExactItems_NeverWidens()
    {
        var payload = Stored("fish");
        var read = ContainerPolicyEnvelope.Read(payload, Reduced);
        Assert.True(read.HasRule); Assert.Empty(read.Rules!.Acceptance.Rules);
        var evaluator = new ChestRuleEvaluator(); var answers = new Answers();
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Bread", Reduced, answers));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Wood", Reduced, answers));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Stone", Reduced, answers));
    }

    [Fact]
    public void StoredIdsSurviveAndTheCategoryComesBack()
    {
        var bread = BreadCategory();
        var payload = Stored(bread, "fish");
        var reduced = ContainerPolicyEnvelope.Read(payload, Reduced).Rules!;
        // Re-serializing under the reduced set writes the same stored rule.
        Assert.Equal(ChestRuleCodec.Serialize(ContainerPolicyEnvelope.Read(payload, Full).Rules!), ChestRuleCodec.Serialize(reduced));
        var full = ContainerPolicyEnvelope.Read(payload, Full).Rules!;
        Assert.Empty(full.UnknownCategories);
        Assert.Contains(full.Acceptance.Rules, x => x.Categories.Contains("fish"));
    }

    [Fact]
    public void Edit_KeepsUnknownUnlessRemoved()
    {
        var bread = BreadCategory();
        var payload = Stored(bread, "fish");
        var view = ChestRuleEdit.From(ContainerPolicyEnvelope.Read(payload, Reduced).Rules!);
        Assert.Equal(new[] { bread }, view.Categories); Assert.Equal(new[] { "fish" }, view.UnknownCategories);

        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Reduced,
            new ChestRuleEdit(view.Categories, new[] { "Stone", "Wood" }, null, view.UnknownCategories), out var kept));
        var restored = ContainerPolicyEnvelope.Read(kept!, Full).Rules!;
        Assert.Contains(restored.Acceptance.Rules, x => x.Categories.Contains("fish"));
        Assert.Equal(new[] { "Stone", "Wood" }, restored.Includes);

        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(kept!, kept!, Reduced,
            new ChestRuleEdit(view.Categories, null, null), out var removed));
        Assert.DoesNotContain(ContainerPolicyEnvelope.Read(removed!, Full).Rules!.Acceptance.Rules, x => x.Categories.Contains("fish"));
    }

    [Fact]
    public void FilteredAlternativeWithUnknownCategory_IsPreservedVerbatimThroughEdits()
    {
        var filtered = new ClassificationFilterRule(Full, new[] { "fish", "misc" },
            new[] { new KeyValuePair<string, IEnumerable<string>>("rarity", new[] { "magic" }) });
        var payload = ContainerPolicyEnvelope.Serialize(3, new ChestRules(Full, ChestRuleState.Configured, new[] { filtered }));
        var read = ContainerPolicyEnvelope.Read(payload, Reduced);
        Assert.True(read.HasRule);
        var alternative = Assert.Single(read.Rules!.Preserved);
        Assert.True(alternative.Filtered); Assert.Equal(new[] { "misc" }, alternative.Effective!.Categories);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Reduced, new ChestRuleEdit(null, new[] { "Wood" }, null), out var edited));
        Assert.Contains(ContainerPolicyEnvelope.Read(edited!, Full).Rules!.Acceptance.Rules,
            x => x.Categories.SequenceEqual(new[] { "fish", "misc" }) && x.Filters.ContainsKey("rarity"));
    }

    [Fact]
    public void ChoosingAnUnknownCategoryInAnEdit_IsRefused()
    {
        Assert.Equal(TransferRefusal.UnsupportedCapability,
            ContainerPolicyEnvelope.TryEdit("", "", Reduced, new ChestRuleEdit(new[] { "fish" }, null, null), out var refused));
        Assert.Null(refused);
    }
}
