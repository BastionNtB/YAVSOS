using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ChestRuleEvaluatorTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    internal static CategoryPolicy TestPolicy(long revision = 1)
    {
        var xml = CategoryPolicyCodec.EmptyDocument.Replace("revision=\"1\"", "revision=\"" + revision + "\"");
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var error), error); return policy!;
    }
    internal static AuthoritativeAssignmentSnapshot Snapshot(CatalogSnapshot catalog, CategoryPolicy policy) =>
        AuthoritativeAssignmentSnapshot.Create(catalog, ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy)), policy);

    private sealed class Answers : ICategoryAnswers
    {
        private readonly AuthoritativeAssignmentSnapshot? snapshot; private readonly CatalogSnapshot catalog;
        public int Resolves;
        public Answers(AuthoritativeAssignmentSnapshot? snapshot, CatalogSnapshot catalog, long generation = 1)
        { this.snapshot = snapshot; this.catalog = catalog; Generation = generation; }
        public long Generation { get; set; }
        public bool CategoriesReady => snapshot?.CategoriesReady == true;
        public ClassificationResult? Resolve(string prefab) { Resolves++; return snapshot?.Resolve(prefab, catalog); }
    }

    private static readonly CatalogSnapshot Server = CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100) });
    private static readonly CatalogSnapshot Client = CatalogSnapshot.Build(new[] { Item("Wood"), Item("Bread", type: "Consumable", health: 20, duration: 100), Item("ClientOnly") });

    private static string Rule(ChestRuleEdit? edit)
    {
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", Taxonomy, edit, out var payload));
        return payload!;
    }
    private static string Category(Answers answers, string prefab) => answers.Resolve(prefab)!.Memberships[0].Category;

    [Fact]
    public void NoRuleUnconfiguredAndUnreadable_AreDistinct()
    {
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator();
        Assert.Equal(ChestRuleDecision.NoRule, evaluator.Evaluate("", "Wood", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.NoRule, evaluator.Evaluate(Rule(null), "Wood", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.RejectUnreadable, evaluator.Evaluate("{\"future\":1}", "Wood", Taxonomy, answers));
        var neverTin = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Unconfigured, excludes: new[] { "Tin" }));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(neverTin, "Tin", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.NoRule, evaluator.Evaluate(neverTin, "Wood", Taxonomy, answers));
    }

    [Fact]
    public void ExactExcludeWinsOverIncludeAndCategory()
    {
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator();
        var wood = Category(answers, "Wood");
        var payload = Rule(new ChestRuleEdit(new[] { wood }, new[] { "Wood" }, new[] { "Wood" }));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Wood", Taxonomy, answers));
        payload = Rule(new ChestRuleEdit(new[] { wood }, null, null));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Wood", Taxonomy, answers));
    }

    [Fact]
    public void ServerCategoriesAndMiscFallback_DecideCategoryRules()
    {
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator();
        var bread = Category(answers, "Bread");
        Assert.NotEqual("misc", bread);
        var food = Rule(new ChestRuleEdit(new[] { bread }, null, null));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(food, "Bread", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(food, "ClientOnly", Taxonomy, answers));
        var misc = Rule(new ChestRuleEdit(new[] { "misc" }, null, null));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(misc, "ClientOnly", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(misc, "NotLoaded", Taxonomy, answers));
    }

    [Fact]
    public void BeforeCategoriesAreReady_ExactPartsApplyAndCategoryPartWaits()
    {
        var ready = new Answers(Snapshot(Server, TestPolicy()), Client, generation: 2); var bread = Category(ready, "Bread");
        var pending = new Answers(null, Client); var evaluator = new ChestRuleEvaluator();
        var payload = Rule(new ChestRuleEdit(new[] { bread }, new[] { "Wood" }, new[] { "Stone" }));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Wood", Taxonomy, pending));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Stone", Taxonomy, pending));
        Assert.Equal(ChestRuleDecision.RejectCategoriesPending, evaluator.Evaluate(payload, "Bread", Taxonomy, pending));
        var exactOnly = Rule(new ChestRuleEdit(null, new[] { "Wood" }, null));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(exactOnly, "Bread", Taxonomy, pending));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Bread", Taxonomy, ready));
    }

    [Fact]
    public void CouldAccept_SetsStackFiltersAsideAndKeepsAlwaysNeverAndItemFilters()
    {
        // P8-18 (Q14): a search has no stack, so an upgrade-level filter cannot be judged; the rest of the rule decides.
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator();
        var bread = Category(answers, "Bread");
        static System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>[] F(string key, params string[] values) =>
            new[] { new System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>(key, values) };
        var levelThree = Rule(new ChestRuleEdit(new[] { bread }, null, null, filters: F("quality", "3")));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(levelThree, "Bread", Taxonomy, answers)); // No stack: level unknown.
        Assert.True(evaluator.CouldAccept(levelThree, "Bread", Taxonomy, answers));
        Assert.False(evaluator.CouldAccept(levelThree, "Wood", Taxonomy, answers)); // Not in the category.
        var otherMod = Rule(new ChestRuleEdit(new[] { bread }, null, null, filters: F("mod", "Someone.ElsesMod")));
        Assert.False(evaluator.CouldAccept(otherMod, "Bread", Taxonomy, answers)); // An item filter still decides.
        var never = Rule(new ChestRuleEdit(new[] { bread }, null, new[] { "Bread" }, filters: F("quality", "3")));
        Assert.False(evaluator.CouldAccept(never, "Bread", Taxonomy, answers));
        var always = Rule(new ChestRuleEdit(null, new[] { "Wood" }, null));
        Assert.True(evaluator.CouldAccept(always, "Wood", Taxonomy, answers));
        Assert.True(evaluator.CouldAccept(always, "Wood", Taxonomy, new Answers(null, Client))); // Exact lists answer while loading.
        Assert.False(evaluator.CouldAccept(levelThree, "Bread", Taxonomy, new Answers(null, Client)));
        Assert.False(evaluator.CouldAccept("", "Wood", Taxonomy, answers));
        Assert.False(evaluator.CouldAccept(Rule(null), "Wood", Taxonomy, answers));
        Assert.False(evaluator.CouldAccept("{\"future\":1}", "Wood", Taxonomy, answers));
    }

    [Fact]
    public void Decisions_AreCachedUntilAnswerGenerationChanges()
    {
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator();
        var payload = Rule(new ChestRuleEdit(new[] { "misc" }, null, null));
        evaluator.Evaluate(payload, "ClientOnly", Taxonomy, answers); var first = answers.Resolves;
        for (var i = 0; i < 100; i++) evaluator.Evaluate(new string(payload.ToCharArray()), "ClientOnly", Taxonomy, answers);
        Assert.Equal(first, answers.Resolves); Assert.Equal(1, evaluator.CachedRules);
        answers.Generation++;
        evaluator.Evaluate(payload, "ClientOnly", Taxonomy, answers);
        Assert.Equal(first + 1, answers.Resolves);
    }

    [Fact]
    public void Caches_StayBoundedAndClearOnTaxonomyChange()
    {
        var answers = new Answers(Snapshot(Server, TestPolicy()), Client); var evaluator = new ChestRuleEvaluator(4, 8);
        for (var i = 0; i < 50; i++)
        {
            var payload = Rule(new ChestRuleEdit(null, new[] { "Item" + i }, null));
            for (var j = 0; j < 5; j++) evaluator.Evaluate(payload, "Item" + j, Taxonomy, answers);
            Assert.InRange(evaluator.CachedRules, 1, 4); Assert.InRange(evaluator.CachedDecisions, 1, 8);
        }
        var other = DefaultClassificationTaxonomy.Create();
        // A taxonomy change empties both caches; only this rule and its one decision are cached afterwards.
        evaluator.Evaluate(Rule(new ChestRuleEdit(null, new[] { "Wood" }, null)), "Wood", other, answers);
        Assert.Equal(1, evaluator.CachedRules); Assert.Equal(1, evaluator.CachedDecisions);
    }

    [Fact]
    public void Edit_PreservesUneditedStoredFields()
    {
        var filtered = new ClassificationFilterRule(Taxonomy, new[] { "misc" },
            new[] { new System.Collections.Generic.KeyValuePair<string, System.Collections.Generic.IEnumerable<string>>("rarity", new[] { "magic" }) });
        var stored = ContainerPolicyEnvelope.Serialize(7, new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { filtered, new ClassificationFilterRule(Taxonomy, new[] { "food" }) }, new[] { "Wood" }, null, priority: 5, overflow: true));
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(stored, stored, Taxonomy,
            new ChestRuleEdit(new[] { "fish" }, new[] { "Stone" }, new[] { "Wood" }), out var updated));
        var rules = ContainerPolicyEnvelope.Read(updated!, Taxonomy);
        Assert.Equal(8UL, rules.Revision); Assert.Equal(5, rules.Rules!.Priority); Assert.True(rules.Rules.Overflow);
        Assert.Contains(rules.Rules.Acceptance.Rules, x => x.Filters.Count == 1 && x.Categories.SequenceEqual(new[] { "misc" }));
        var view = ChestRuleEdit.From(rules.Rules);
        Assert.Equal(new[] { "fish" }, view.Categories); Assert.Equal(new[] { "Stone" }, view.Includes); Assert.Equal(new[] { "Wood" }, view.Excludes);
        Assert.Equal(TransferRefusal.UnsupportedCapability, ContainerPolicyEnvelope.TryEdit(updated!, updated!, Taxonomy,
            new ChestRuleEdit(new[] { "retired-category" }, null, null), out var refused));
        Assert.Null(refused);
    }
}
