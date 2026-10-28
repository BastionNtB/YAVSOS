using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Core.Tests.ChestHighlightFakes;
using YAVSOS.Integrations;
using YAVSOS.Interface;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>P6-06 search and highlight, P6-07 labels, P6-07b instance evidence in the evaluator (D062).</summary>
public sealed class Phase6InterfaceTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal)
    { ["materials"] = 120, ["materials.ore"] = 9, ["materials.metal"] = 8, ["food"] = 40, ["food.health"] = 12 };
    private static int Count(string id) => Counts.TryGetValue(id, out var count) ? count : 0;
    private static readonly SearchableItem[] Items =
    {
        new("CopperOre", "Copper ore", new[] { "materials", "materials.ore" }),
        new("Copper", "Copper", new[] { "materials", "materials.metal" }),
        new("CookedMeat", "Cooked boar meat", new[] { "food", "food.health" }),
        new("Wood", "Wood", new[] { "materials", "materials.wood" }),
    };

    [Fact]
    public void Search_AnItemLeadsToItsSmallestCategory_BestNameFirst()
    {
        var result = CategorySearch.Find(Taxonomy, " copper ", Items, Count);
        Assert.True(result.Active);
        // "Copper" is an exact prefix and shorter than "Copper ore"; each goes to its smallest category, not "materials".
        Assert.Equal(new[] { "Copper:materials.metal", "CopperOre:materials.ore" }, result.Items.Select(x => x.Prefab + ":" + x.Category));
        Assert.Contains("materials.ore", result.Hits); Assert.DoesNotContain("materials", result.Hits);
        // Wood's own subcategory has no counted members: it falls back to one that has.
        Assert.Equal("materials", CategorySearch.Smallest(Taxonomy, new[] { "materials", "materials.wood", "unknown.id" }, Count));
        // By prefab ID as well, after name matches.
        Assert.Equal("CookedMeat", Assert.Single(CategorySearch.Find(Taxonomy, "cookedmeat", Items, Count).Items).Prefab);
    }

    [Fact]
    public void Search_MatchesCategoryNames_AndIgnoresShortQueries()
    {
        var result = CategorySearch.Find(Taxonomy, "ORES", Items, Count);
        Assert.Contains("materials.ore", result.Categories);
        Assert.DoesNotContain(result.Categories, x => Count(x) == 0); // Empty categories are not offered.
        Assert.False(CategorySearch.Find(Taxonomy, "c", Items, Count).Active);
        Assert.False(CategorySearch.Find(Taxonomy, null, Items, Count).Active);
        Assert.Empty(CategorySearch.Find(Taxonomy, "zzzz", Items, Count).Hits);
        var many = Enumerable.Range(0, 50).Select(i => new SearchableItem("Copper" + i, "Copper " + i, new[] { "materials.metal" }));
        Assert.Equal(CategorySearch.MaximumItems, CategorySearch.Find(Taxonomy, "copper", many, Count).Items.Count);
    }

    [Fact]
    public void SearchRows_RevealTheHitUnderItsCollapsedUmbrella_AndHideTheRest()
    {
        var stored = new HashSet<string>(StringComparer.Ordinal);
        var collapsed = new HashSet<string>(new[] { "materials" }, StringComparer.Ordinal);
        bool Members(string id) => Count(id) > 0;
        var plain = CategorySearch.Rows(Taxonomy, stored, Members, new HashSet<string>(), collapsed, CategorySearchResult.None);
        Assert.DoesNotContain(plain, x => x.Id == "materials.ore"); // Collapsed by the player.
        var search = CategorySearch.Find(Taxonomy, "copper ore", Items, Count);
        var rows = CategorySearch.Rows(Taxonomy, stored, Members, new HashSet<string>(), collapsed, search);
        Assert.Equal(new[] { "materials", "materials.ore" }, rows.Select(x => x.Id));
        Assert.True(rows[0].Expanded);
        Assert.Equal(1, rows[1].Depth);
    }

    private static ChestRules Rules(string[] categories, string[]? includes = null, int priority = 0, params (string, string[])[] filters) =>
        new(Taxonomy, ChestRuleState.Configured, new[] { Rule(categories, filters) }, includes, priority: priority);

    [Fact]
    public void Label_NamesTheRuleShortly()
    {
        string Name(string prefab) => prefab == "Copper" ? "Copper bar" : prefab;
        Assert.Equal("Ores & metal scrap · priority 4 (Normal)", RuleText.Label(Rules(new[] { "materials.ore" }, priority: 1), Taxonomy, Name));
        Assert.Equal("Copper bar", RuleText.Label(Rules(Array.Empty<string>(), new[] { "Copper" }), Taxonomy, Name));
        // Q8: the filters in a few words instead of "filtered".
        Assert.Equal("Weapons › magic or legendary", RuleText.Label(Rules(new[] { "weapons" }, null, 0, ("rarity", new[] { "magic", "legendary" })), Taxonomy, Name));
        Assert.Equal("Food › eaten by Boar · priority 3 (Urgent)", RuleText.Label(Rules(new[] { "food" }, null, 2, ("eaten-by", new[] { "$enemy_boar" })), Taxonomy, Name,
            (dimension, value) => value == "$enemy_boar" ? "Boar" : value));
        Assert.Equal("crafted by Bastion, not teleportable", RuleText.Short(new Dictionary<string, IReadOnlyList<string>>
        {
            ["teleportable"] = new[] { "no" }, ["crafter"] = new[] { ClassificationFilters.CrafterValue(true, 7, "Bastion") }
        }));
        Assert.Equal("used for fuel, stamina or eitr food", RuleText.Short(new Dictionary<string, IReadOnlyList<string>>
        { ["food-stat"] = new[] { "stamina", "eitr" }, ["usage"] = new[] { "fuel" } }));
        Assert.Equal("plain", RuleText.Short(new Dictionary<string, IReadOnlyList<string>> { ["rarity"] = new[] { "unknown" } }));
        Assert.Null(RuleText.Label(new ChestRules(Taxonomy, ChestRuleState.Unconfigured), Taxonomy, Name));
        Assert.Null(RuleText.Label(null, Taxonomy, Name));
        // Many categories stay within the bound and say how many more there are.
        var wide = RuleText.Label(Rules(Taxonomy.Categories.Keys.Where(x => x.StartsWith("weapons.", StringComparison.Ordinal) && !x.EndsWith(".other", StringComparison.Ordinal)).ToArray(),
            priority: -3), Taxonomy, Name)!;
        Assert.True(wide.Length <= RuleText.MaximumLabel, wide);
        Assert.Contains(" +", wide); Assert.EndsWith(" · priority 8 (Minor)", wide);
    }

    // D069: levels 1 (fills first) to 10 over the stored rank, larger rank first; an unset rank (0) is 5, Normal.
    [Fact]
    public void Priority_LevelsMapToTheStoredRankAndHaveNames()
    {
        Assert.Equal(5, ChestPriority.Level(0)); Assert.Equal(0, ChestPriority.Rank(5));
        for (var level = 1; level <= 10; level++) Assert.Equal(level, ChestPriority.Level(ChestPriority.Rank(level)));
        Assert.True(ChestPriority.Rank(1) > ChestPriority.Rank(2) && ChestPriority.Rank(9) > ChestPriority.Rank(10));
        Assert.Equal(new[] { "Critical", "Urgent", "Urgent", "Normal", "Normal", "Moderate", "Moderate", "Minor", "Minor", "Fuhgeddaboudit" },
            Enumerable.Range(1, 10).Select(ChestPriority.Name).ToArray());
        // Ranks set before D069 keep their order and show as the nearest level.
        Assert.Equal(1, ChestPriority.Level(9)); Assert.Equal(10, ChestPriority.Level(-999));
        Assert.Equal(ChestPriority.Rank(2), ChestPriority.Step(9, 1));
        Assert.Null(ChestPriority.Step(ChestPriority.Rank(1), -1)); Assert.Null(ChestPriority.Step(ChestPriority.Rank(10), 1));
        Assert.Equal(ChestPriority.Rank(4), ChestPriority.Step(0, -1));
        Assert.Equal("", ChestPriority.Short(0)); Assert.Equal("priority 10 (Fuhgeddaboudit)", ChestPriority.Short(-5));
    }

    [Fact]
    public void TakesCategory_CoversUmbrellasAndSubcategories_NotSiblings()
    {
        var ores = Rules(new[] { "materials.ore" });
        Assert.True(RuleText.TakesCategory(ores, Taxonomy, "materials.ore"));
        Assert.True(RuleText.TakesCategory(ores, Taxonomy, "materials")); // Searching the umbrella finds a part of it.
        Assert.True(RuleText.TakesCategory(Rules(new[] { "materials" }), Taxonomy, "materials.ore"));
        Assert.False(RuleText.TakesCategory(ores, Taxonomy, "materials.metal"));
        Assert.False(RuleText.TakesCategory(new ChestRules(Taxonomy, ChestRuleState.Unconfigured), Taxonomy, "materials"));
    }

    [Fact]
    public void FilterSummary_ReadsAsWords()
    {
        var filters = new Dictionary<string, IReadOnlyList<string>>
        { ["usage"] = new[] { "fuel" }, ["rarity"] = new[] { "magic", "rare", "unknown" }, ["damaged"] = Array.Empty<string>() };
        Assert.Equal("EpicLoot rarity: magic, rare or plain; Used for: fuel", RuleText.Filters(filters));
        Assert.Equal("", RuleText.Filters(new Dictionary<string, IReadOnlyList<string>>()));
        foreach (var dimension in RuleText.EditableFilters) Assert.Contains(dimension, ClassificationFilters.Dimensions);
    }

    private sealed class Answers : ICategoryAnswers
    {
        private readonly IReadOnlyDictionary<string, ClassificationResult> items;
        public int Resolves;
        public Answers(IReadOnlyDictionary<string, ClassificationResult> items) { this.items = items; }
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) { Resolves++; return items.TryGetValue(prefab, out var item) ? item : null; }
    }

    [Fact]
    public void Evaluator_UsesInstanceEvidenceOnlyWhenTheRuleHasAnInstanceFilter()
    {
        var index = Build(new[] { Item("SwordIron", "OneHandedWeapon"), Item("Wood") });
        var answers = new Answers(index.Items);
        var evaluator = new ChestRuleEvaluator();
        var worn = ClassificationFilters.Condition(2, true, 40f, 200f);
        var damaged = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(null, ("damaged", new[] { "yes" })) }));
        Assert.True(evaluator.UsesInstanceFilters(damaged, Taxonomy));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(damaged, "SwordIron", Taxonomy, answers, worn));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(damaged, "SwordIron", Taxonomy, answers)); // Unknown without evidence.

        var plain = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(null, ("teleportable", new[] { "unknown" })) }));
        Assert.False(evaluator.UsesInstanceFilters(plain, Taxonomy));
        Assert.False(evaluator.UsesInstanceFilters("", Taxonomy));
        var first = evaluator.Evaluate(plain, "Wood", Taxonomy, answers, worn);
        var resolves = answers.Resolves;
        // Evidence that cannot change the answer keeps the cached decision.
        Assert.Equal(first, evaluator.Evaluate(plain, "Wood", Taxonomy, answers, worn));
        Assert.Equal(resolves, answers.Resolves);
    }

    private static OptionalTypeResolver Resolver(Type manager, Type? props = null) => name => name switch
    {
        ChestHighlightCapability.MaterialMan => manager, ChestHighlightCapability.ShaderProps => props ?? typeof(ShaderProps),
        ChestHighlightCapability.GameObject => typeof(GameObject), _ => null
    };

    [Fact]
    public void HighlightProbe_AcceptsTheGameShape_AndRejectsAReshapedMember()
    {
        Assert.True(ChestHighlightCapability.Probe(Resolver(typeof(MaterialMan))).Supported);
        Assert.Contains("SetValue", ChestHighlightCapability.Probe(Resolver(typeof(NoGenericSet))).Diagnostic);
        Assert.Contains("ResetValue", ChestHighlightCapability.Probe(Resolver(typeof(NoReset))).Diagnostic);
        Assert.Contains("instance", ChestHighlightCapability.Probe(Resolver(typeof(NoInstance))).Diagnostic);
        Assert.Contains("_EmissionColor", ChestHighlightCapability.Probe(Resolver(typeof(MaterialMan), typeof(NoEmission))).Diagnostic);
        Assert.False(ChestHighlightCapability.Probe(name => null).Supported);
        Assert.False(ChestHighlightCapability.Probe(null).Supported);
    }
}
