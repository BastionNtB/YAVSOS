using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>P6-07b built-in filters (Q6, D062): rarity, usage, origin mod, teleportable, upgraded/damaged, food stat.</summary>
public sealed class Phase6FilterTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();

    private static CatalogFact With(CatalogFact fact, params (string Key, string Value)[] extra) =>
        new(fact.Kind, fact.Identity, fact.Values.Concat(extra.Select(x => new KeyValuePair<string, string>(x.Key, x.Value))));
    private static bool Accepts(ClassificationResult item, ClassificationFilterRule rule, IEnumerable<FacetEvidence>? instance = null) =>
        ClassificationFilters.Matches(item, rule, instance);
    private static ClassificationFilterRule Filter(string dimension, params string[] values) => Rule(null, (dimension, values));
    private static FacetEvidence Rarity(string value) => new("rarity", value, "optional:epicloot", "Current instance.");

    [Fact]
    public void Teleportable_IsTheItemsOwnFlag_AndMissingEvidenceFailsASelectedValue()
    {
        var index = Build(new[] { With(Item("Copper"), ("teleportable", "False")), With(Item("Wood"), ("teleportable", "True")), Item("Stone") });
        var copper = index.Items["Copper"]; var wood = index.Items["Wood"]; var stone = index.Items["Stone"];
        Assert.True(Accepts(copper, Filter("teleportable", "no"))); Assert.False(Accepts(copper, Filter("teleportable", "yes")));
        Assert.True(Accepts(wood, Filter("teleportable", "yes"))); Assert.False(Accepts(wood, Filter("teleportable", "no")));
        // An older catalog without the flag: Unknown, never guessed.
        Assert.False(Accepts(stone, Filter("teleportable", "no"))); Assert.False(Accepts(stone, Filter("teleportable", "yes")));
        Assert.True(Accepts(stone, Filter("teleportable", "unknown")));
        Assert.Equal("misc", Build(new[] { With(Item("Copper"), ("teleportable", "maybe")) }).Items["Copper"].Primary); // D086 D3
        Assert.False(Accepts(wood, Filter("teleportable", "sometimes"))); Assert.False(Accepts(stone, Filter("teleportable", "sometimes"))); // D086 D6
    }

    [Theory]
    [InlineData(10, 30, 0, "stamina")]
    [InlineData(40, 10, 10, "health")]
    [InlineData(10, 10, 40, "eitr")]
    [InlineData(20, 20, 5, "balanced")]
    // D064: the game's fork-icon rule. Health ignores eitr; a stat must be over twice the others (strictly).
    [InlineData(40, 10, 30, "health")]
    [InlineData(20, 10, 0, "balanced")]
    // Production-catalog foods (Therzie Warfare) the game shows white; the old largest-stat rule picked a stat.
    [InlineData(38, 23, 0, "balanced")]
    [InlineData(33, 45, 62, "balanced")]
    [InlineData(40, 74, 0, "balanced")]
    [InlineData(36, 40, 39, "balanced")]
    public void FoodStat_FollowsTheFoodCategoryRule_AndCombinesWithAnyCategory(double health, double stamina, double eitr, string expected)
    {
        var item = Build(new[] { Item("Meal", "Consumable", health: health, stamina: stamina, eitr: eitr, duration: 600) }).Items["Meal"];
        Assert.Contains(item.Memberships, x => x.Category == "food." + expected);
        Assert.Equal(expected, Assert.Single(item.Facets, x => x.Dimension == "food-stat").Value);
        Assert.True(Accepts(item, Rule(new[] { "food" }, ("food-stat", new[] { expected }))));
        foreach (var other in ClassificationFilters.FoodStats.Where(x => x != expected)) Assert.False(Accepts(item, Filter("food-stat", other)));
        // Not food: no food stat at all.
        var wood = Build(new[] { Item("Wood") }).Items["Wood"];
        Assert.False(Accepts(wood, Filter("food-stat", expected))); Assert.True(Accepts(wood, Filter("food-stat", "unknown")));
    }

    [Fact]
    public void Usage_ComesFromRecipeEvidence_AndAnUnusedItemHasNone()
    {
        var index = Build(new[] { Item("Wood"), Item("Bread", "Consumable", health: 10, duration: 600), Recipe(), Requirement() });
        Assert.True(Accepts(index.Items["Wood"], Rule(new[] { "materials" }, ("usage", new[] { "crafting" }))));
        Assert.False(Accepts(index.Items["Bread"], Filter("usage", "crafting")));
        Assert.True(Accepts(index.Items["Bread"], Filter("usage", "unknown", "crafting")));
    }

    [Fact]
    public void OriginMod_IsOpenValued_AndMissingOwnerIsUnknown()
    {
        var index = Build(new[] { Item("SwordWarfare", "OneHandedWeapon"), Item("SwordIron", "OneHandedWeapon") }, evidence: new[]
            { new ItemClassificationEvidence("SwordWarfare", facets: new[] { new FacetEvidence("mod", "Therzie.Warfare", "loaded-registration", "Owner.") }) });
        Assert.True(Accepts(index.Items["SwordWarfare"], Rule(new[] { "weapons" }, ("mod", new[] { "Therzie.Warfare" }))));
        Assert.False(Accepts(index.Items["SwordIron"], Filter("mod", "Therzie.Warfare")));
        Assert.True(Accepts(index.Items["SwordIron"], Filter("mod", "unknown")));
        Assert.Null(ClassificationFilters.Values("mod"));
    }

    [Fact]
    public void Rarity_IsPerInstanceForEquipment_AndOnlyFreshEvidenceCounts()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        Assert.True(sword.InstanceRarity);
        var magicOrBetter = Rule(new[] { "weapons" }, ("rarity", new[] { "magic", "rare", "epic", "legendary", "mythic" }));
        Assert.True(Accepts(sword, magicOrBetter, new[] { Rarity("epic") }));
        Assert.False(Accepts(sword, magicOrBetter)); // plain sword, or EpicLoot absent: Unknown
        Assert.True(Accepts(sword, Filter("rarity", "unknown")));
        Assert.False(Accepts(sword, Filter("rarity", "unknown"), new[] { Rarity("rare") }));
        var wood = Build(new[] { Item("Wood") }).Items["Wood"];
        Assert.Throws<ArgumentException>(() => Accepts(wood, magicOrBetter, new[] { Rarity("epic") }));
        Assert.Throws<ArgumentException>(() => Accepts(sword, magicOrBetter, new[] { Rarity("epic"), Rarity("rare") }));
    }

    [Fact]
    public void UnreadableInstanceEvidence_NeverMatchesPlain_OrAnyOtherInstanceFilter()
    {
        var index = Build(new[] { Item("SwordIron", "OneHandedWeapon"), Item("Wood") });
        var sword = index.Items["SwordIron"]; var wood = index.Items["Wood"];
        var plain = Rule(new[] { "weapons" }, ("rarity", new[] { "unknown" }));
        var unreadable = ClassificationFilters.UnreadableInstance(sword);
        Assert.Equal(new[] { "rarity", "upgraded", "damaged", "crafter", "upgradable", "quality" }, unreadable.Select(x => x.Dimension));
        Assert.True(Accepts(sword, plain, Array.Empty<FacetEvidence>()));
        Assert.False(Accepts(sword, plain, unreadable));
        Assert.False(Accepts(sword, Filter("rarity", ClassificationFilters.Rarities.Concat(new[] { "unknown" }).ToArray()), unreadable));
        Assert.False(Accepts(sword, Filter("damaged", "unknown", "yes", "no"), unreadable));
        // A rule without instance filters ignores it.
        Assert.True(Accepts(sword, Rule(new[] { "weapons" }), unreadable));
        Assert.DoesNotContain(ClassificationFilters.UnreadableInstance(wood), x => x.Dimension == "rarity");
        // Evidence only: a rule cannot name it, and a definition fact cannot carry it.
        Assert.Throws<ArgumentException>(() => Filter("rarity", ClassificationFilters.Unreadable));
        Assert.Throws<ArgumentException>(() => new FacetEvidence("teleportable", ClassificationFilters.Unreadable, "x", "y"));

        // The evaluator: "Weapons, rarity plain" refuses a sword whose rarity cannot be read, and does not cache that.
        var answers = new Answers(index);
        var payload = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { plain }));
        var evaluator = new ChestRuleEvaluator();
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers, unreadable));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers, ClassificationFilters.Condition(1, true, 1, 1)));
        // The planners do not stash it into the plain chest either.
        var stack = new TransferStack(Guid.NewGuid(), 0, 0, 1, 1, Identity("SwordIron"));
        var input = new StashInput(Taxonomy, answers, Snapshot("player", stack), new[] { new StashChest("plain", payload, Snapshot("plain"), true) },
            instanceFacets: _ => unreadable);
        Assert.Empty(StashPlanner.Plan(input).Moves);
    }

    [Fact]
    public void EpicLootFilterRead_KeepsNoRarityApartFromCouldNotRead()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        var wood = Build(new[] { Item("Wood") }).Items["Wood"];
        var supported = new OptionalCapability("epicloot", OptionalCapabilityState.Supported, "0.14.13", "test");
        var incompatible = OptionalCapability.Incompatible("epicloot", "9", "reshaped");
        const string key = OptionalEpicLootRarity.InstanceDataKey;
        IReadOnlyDictionary<string, string> Data(string payload) => new Dictionary<string, string> { [key] = payload };
        string Read(OptionalCapability capability, ClassificationResult item, IReadOnlyDictionary<string, string>? data) =>
            string.Join(",", OptionalEpicLootRarity.ReadInstanceForFilters(capability, item, data).Select(x => x.Value));
        // Plain: EpicLoot absent, no component, an empty value, or a null magic item.
        Assert.Equal("", Read(OptionalCapability.Absent("epicloot"), sword, Data("{\"Version\":3,\"Rarity\":2}")));
        Assert.Equal("", Read(supported, sword, null));
        Assert.Equal("", Read(supported, sword, new Dictionary<string, string>()));
        Assert.Equal("", Read(supported, sword, Data("")));
        Assert.Equal("", Read(supported, sword, Data("null")));
        Assert.Equal("epic", Read(supported, sword, Data("{\"Version\":3,\"Rarity\":2}")));
        // Could not read: an incompatible adapter, an unsupported version, malformed or oversized data.
        Assert.Equal(ClassificationFilters.Unreadable, Read(incompatible, sword, Data("{\"Version\":3,\"Rarity\":2}")));
        Assert.Equal(ClassificationFilters.Unreadable, Read(supported, sword, Data("{\"Version\":4,\"Rarity\":2}")));
        Assert.Equal(ClassificationFilters.Unreadable, Read(supported, sword, Data("{\"Rarity\":")));
        Assert.Equal(ClassificationFilters.Unreadable, Read(supported, sword, Data(new string(' ', OptionalEpicLootRarity.MaximumInstancePayload + 1))));
        // Not an equipment item: rarity is never instance evidence.
        Assert.Equal("", Read(incompatible, wood, Data("x")));
        // The older reader keeps its behaviour: anything not read is simply absent.
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(supported, sword, Data("{\"Version\":4,\"Rarity\":2}")));
    }

    [Fact]
    public void Condition_UpgradedAndDamagedAreInstanceFacts()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        var wood = Build(new[] { Item("Wood") }).Items["Wood"];
        var worn = ClassificationFilters.Condition(2, true, 40f, 200f);
        Assert.Equal(new[] { "upgraded:yes", "upgradable:no", "damaged:yes" }, worn.Select(x => x.Dimension + ":" + x.Value));
        Assert.Equal(new[] { "upgraded:no", "upgradable:no", "damaged:no" }, ClassificationFilters.Condition(1, true, 200f, 200f).Select(x => x.Dimension + ":" + x.Value));
        var material = ClassificationFilters.Condition(1, false, 0f, 0f);
        Assert.Equal(new[] { "upgraded:no", "upgradable:no" }, material.Select(x => x.Dimension + ":" + x.Value));
        Assert.Equal(new[] { "upgraded:no", "upgradable:no" }, ClassificationFilters.Condition(1, true, 5f, float.NaN).Select(x => x.Dimension + ":" + x.Value));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassificationFilters.Condition(0, true, 1, 1));

        var damagedGear = Rule(new[] { "weapons" }, ("damaged", new[] { "yes" }));
        Assert.True(Accepts(sword, damagedGear, worn));
        Assert.False(Accepts(sword, damagedGear)); // no fresh evidence: Unknown fails
        Assert.True(Accepts(sword, Filter("upgraded", "yes"), worn));
        // A material has no durability: "damaged: no" does not collect it, "unknown" does.
        Assert.False(Accepts(wood, Filter("damaged", "no"), material));
        Assert.True(Accepts(wood, Filter("damaged", "unknown"), material));
        // Condition can supplement any item, but never twice per dimension and never as a prefab fact.
        Assert.Throws<ArgumentException>(() => Accepts(sword, damagedGear, worn.Concat(worn)));
        Assert.Throws<ArgumentException>(() => Build(new[] { Item("Wood") }, evidence: new[]
            { new ItemClassificationEvidence("Wood", facets: new[] { new FacetEvidence("damaged", "yes", "adapter", "Static.") }) }));
        Assert.Throws<ArgumentException>(() => new FacetOverride("upgraded", OverrideOperation.Add, new[] { "yes" }));
    }

    [Fact]
    public void Crafter_IsJudgedAgainstThePlayerWhoSetTheFilter_NotWhoeverStoresIt()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        Assert.True(ClassificationFilters.IsInstanceDimension(sword, "crafter"));
        FacetEvidence[] By(long crafter) => ClassificationFilters.Crafter(crafter) is { } row ? new[] { row } : Array.Empty<FacetEvidence>();
        Assert.Equal("7", By(7).Single().Value);
        Assert.Empty(By(0)); // nobody crafted it: unknown

        // Player 7 (Bastion) sets "crafted by me" on their chest; player 9 sets "anyone but me" on the armoury.
        var bastions = Rule(new[] { "weapons" }, ("crafter", new[] { ClassificationFilters.CrafterValue(true, 7, "Bastion") }));
        var armoury = Rule(new[] { "weapons" }, ("crafter", new[] { ClassificationFilters.CrafterValue(false, 9, "Lin"), "unknown" }));
        Assert.True(Accepts(sword, bastions, By(7)));   // whoever stores it
        Assert.False(Accepts(sword, bastions, By(9)));
        Assert.False(Accepts(sword, bastions, By(0)));  // not crafted
        Assert.True(Accepts(sword, armoury, By(7)));
        Assert.False(Accepts(sword, armoury, By(9)));   // Lin's own gear stays out
        Assert.True(Accepts(sword, armoury, By(0)));    // found loot counts as "not crafted"
        var unreadable = ClassificationFilters.UnreadableInstance(sword);
        Assert.Contains(unreadable, x => x.Dimension == "crafter");
        Assert.False(Accepts(sword, bastions, unreadable)); Assert.False(Accepts(sword, armoury, unreadable)); // never widens a rule

        // Stored form: the ID decides, the name is display only and cannot break parsing.
        var value = ClassificationFilters.CrafterValue(false, -42, "A:b\n");
        Assert.True(ClassificationFilters.TryParseCrafter(value, out var by, out var id, out var name));
        Assert.False(by); Assert.Equal(-42, id); Assert.Equal("A:b", name);
        // D086 D6: a crafter value this version can't read is kept and matches no stack.
        Assert.False(Accepts(sword, Filter("crafter", "me"), By(7))); Assert.False(Accepts(sword, Filter("crafter", "by:0:x"), By(7)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassificationFilters.CrafterValue(true, 0, "x"));
        Assert.Equal("anyone but Lin", RuleText.ValueName("crafter", ClassificationFilters.CrafterValue(false, 9, "Lin")));
    }

    [Fact]
    public void InstanceDimensions_AreRarityOnlyForEquipmentAndConditionForAll()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        var wood = Build(new[] { Item("Wood") }).Items["Wood"];
        Assert.True(ClassificationFilters.IsInstanceDimension(sword, "rarity")); Assert.False(ClassificationFilters.IsInstanceDimension(wood, "rarity"));
        Assert.True(ClassificationFilters.IsInstanceDimension(wood, "damaged")); Assert.False(ClassificationFilters.IsInstanceDimension(wood, "teleportable"));
        foreach (var dimension in ClassificationFilters.Dimensions)
        {
            var values = ClassificationFilters.Values(dimension);
            if (values != null) foreach (var value in values) _ = Filter(dimension, value);
        }
    }

    [Fact]
    public void Evaluator_UsesFreshInstanceEvidence_WithoutCachingIt()
    {
        var index = Build(new[] { Item("SwordIron", "OneHandedWeapon") });
        var answers = new Answers(index);
        var payload = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { Rule(new[] { "weapons" }, ("rarity", new[] { "epic" })) }));
        var evaluator = new ChestRuleEvaluator();
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers, new[] { Rarity("epic") }));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "SwordIron", Taxonomy, answers, new[] { Rarity("magic") }));
        Assert.Equal(1, evaluator.CachedDecisions);
    }

    [Fact]
    public void Planners_PassEachStacksInstanceEvidence_SoTheFilteredChestWins()
    {
        var index = Build(new[] { Item("SwordIron", "OneHandedWeapon") });
        var answers = new Answers(index);
        string Payload(ClassificationFilterRule rule) => ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { rule }));
        var loot = Payload(Rule(new[] { "weapons" }, ("rarity", new[] { "epic" })));
        var armoury = Payload(Rule(new[] { "weapons" }));
        var sword = new TransferStack(Guid.NewGuid(), 0, 0, 1, 1, Identity("SwordIron"));
        var player = Snapshot("player", sword);
        StashInput Input(Func<TransferStack, IReadOnlyList<FacetEvidence>>? facets) => new(Taxonomy, answers, player,
            new[] { new StashChest("armoury", armoury, Snapshot("armoury"), true), new StashChest("loot", loot, Snapshot("loot"), true) }, instanceFacets: facets);
        Assert.Equal("armoury", Assert.Single(StashPlanner.Plan(Input(null)).Moves).DestinationId);
        Assert.Equal("loot", Assert.Single(StashPlanner.Plan(Input(_ => new[] { Rarity("epic") })).Moves).DestinationId);

        // Cleanup: an epic sword in the armoury re-sorts to the loot chest (filtered category beats category); a plain one stays.
        CleanupInput Cleanup(Func<TransferStack, IReadOnlyList<FacetEvidence>>? facets) => new(Taxonomy, answers,
            new[] { new CleanupChest("armoury", armoury, Snapshot("armoury", sword), true), new CleanupChest("loot", loot, Snapshot("loot"), true) }, instanceFacets: facets);
        Assert.Empty(CleanupPlanner.Plan(Cleanup(null)).Moves);
        Assert.Equal("loot", Assert.Single(CleanupPlanner.Plan(Cleanup(_ => new[] { Rarity("epic") })).Moves).DestinationId);
    }

    [Fact]
    public void Codec_StoresNewDimensionsInTheExistingFormat()
    {
        var rules = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "materials" }, ("teleportable", new[] { "no" })),
            Rule(new[] { "food" }, ("food-stat", new[] { "stamina" }), ("usage", new[] { "food" })), Rule(null, ("damaged", new[] { "yes" }), ("upgraded", new[] { "no", "unknown" })) });
        var xml = ChestRuleCodec.Serialize(rules);
        Assert.Contains("<filter dimension=\"teleportable\"><value id=\"no\" /></filter>", xml.Replace("\"/>", "\" />"));
        var parsed = ChestRuleCodec.Parse(xml, Taxonomy);
        Assert.Equal(rules.Fingerprint, parsed.Fingerprint); Assert.Equal(3, parsed.FullRules.Count);
        // Crafter and eaten-by values (setter ID and name, a creature's name token) survive the codec.
        var crafter = ClassificationFilters.CrafterValue(false, -42, "Bjørn <\"&'>");
        var open = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "weapons" }, ("crafter", new[] { crafter })),
            Rule(new[] { "food" }, ("eaten-by", new[] { "$enemy_boar" })) });
        var reread = ChestRuleCodec.Parse(ChestRuleCodec.Serialize(open), Taxonomy);
        Assert.Equal(open.Fingerprint, reread.Fingerprint);
        Assert.Equal(new[] { crafter }, reread.FullRules.Single(x => x.Filters.ContainsKey("crafter")).Filters["crafter"]);
        // A rule saved before Phase 6 reads exactly as before.
        var old = "<chestRules schema=\"1\" state=\"configured\" priority=\"0\" overflow=\"false\" existingItemFallback=\"false\">" +
            "<rule><category id=\"weapons\"/><filter dimension=\"rarity\"><value id=\"epic\"/></filter></rule></chestRules>";
        Assert.Equal(new[] { "epic" }, Assert.Single(ChestRuleCodec.Parse(old, Taxonomy).FullRules).Filters["rarity"]);
    }

    // ChestRuleEdit filters

    private static ChestRules Saved(string? payload, ChestRuleEdit edit)
    {
        payload ??= "";
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit(payload, payload, Taxonomy, edit, out var updated));
        return ContainerPolicyEnvelope.Read(updated!, Taxonomy).Rules!;
    }
    private static string Payload(ChestRules rules) => ContainerPolicyEnvelope.Serialize(1, rules);
    private static KeyValuePair<string, IEnumerable<string>>[] F(params (string Dimension, string[] Values)[] filters) =>
        filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Dimension, x.Values)).ToArray();
    private static string Shape(ChestRules rules) => string.Join(" | ", rules.FullRules.Select(r => string.Join("+", r.Categories) + "[" +
        string.Join(";", r.Filters.Select(f => f.Key + "=" + string.Join(",", f.Value))) + "]").OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void Edit_SavesCategoriesWithFilters_AndReEditReplacesThatAlternative()
    {
        var first = Saved(null, new ChestRuleEdit(new[] { "weapons" }, null, null, priority: 1, filters: F(("rarity", new[] { "epic", "legendary" }))));
        Assert.Equal("weapons[rarity=epic,legendary]", Shape(first));
        var view = ChestRuleEdit.From(first, withFilters: true);
        Assert.Equal(new[] { "weapons" }, view.Categories);
        Assert.Equal(new[] { "epic", "legendary" }, view.Filters["rarity"]); Assert.Equal(view.Filters, view.EditedFilters);
        var second = Saved(Payload(first), new ChestRuleEdit(new[] { "weapons", "equipment.armor" }, null, null,
            filters: F(("rarity", new[] { "magic" })), editedFilters: view.EditedFilters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value))));
        Assert.Equal("equipment.armor+weapons[rarity=magic]", Shape(second)); Assert.Equal(1, second.Priority);
        // Clearing the filters makes it a plain category alternative again.
        var third = Saved(Payload(second), new ChestRuleEdit(new[] { "weapons" }, null, null, editedFilters: F(("rarity", new[] { "magic" }))));
        Assert.Equal("weapons[]", Shape(third));
    }

    [Fact]
    public void Edit_LegacyViewNeverShowsOrDropsFilteredAlternatives()
    {
        var filtered = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "weapons" }, ("rarity", new[] { "epic" })) });
        var legacy = ChestRuleEdit.From(filtered);
        Assert.Empty(legacy.Categories); Assert.Empty(legacy.Filters); Assert.Empty(legacy.EditedFilters);
        // The Phase 3 panel saves categories only; the filtered alternative stays as stored, never broadened.
        Assert.Equal("food[] | weapons[rarity=epic]", Shape(Saved(Payload(filtered), new ChestRuleEdit(new[] { "food" }, null, null))));
    }

    [Fact]
    public void Edit_PresentsOnlyOneRepresentableFilterSet_AndKeepsTheRest()
    {
        var mixed = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "weapons" }),
            Rule(new[] { "equipment.armor" }, ("rarity", new[] { "epic" })) });
        var view = ChestRuleEdit.From(mixed, withFilters: true);
        Assert.Equal(new[] { "weapons" }, view.Categories); Assert.Empty(view.EditedFilters);
        Assert.Equal("equipment.armor[rarity=epic] | weapons[damaged=yes]", Shape(Saved(Payload(mixed),
            new ChestRuleEdit(view.Categories, null, null, filters: F(("damaged", new[] { "yes" }))))));
        // Two different filter sets: neither is shown, both are kept.
        var two = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "weapons" }, ("rarity", new[] { "rare" })),
            Rule(new[] { "equipment.armor" }, ("rarity", new[] { "epic" })) });
        Assert.Empty(ChestRuleEdit.From(two, withFilters: true).Categories);
        // A filters-only alternative ("all Warfare items") is edited as such.
        var warfare = Saved(null, new ChestRuleEdit(null, null, null, filters: F(("mod", new[] { "Therzie.Warfare" }))));
        Assert.Equal("[mod=Therzie.Warfare]", Shape(warfare));
        var warfareView = ChestRuleEdit.From(warfare, withFilters: true);
        Assert.Empty(warfareView.Categories); Assert.Equal(new[] { "Therzie.Warfare" }, warfareView.Filters["mod"]);
        Assert.Equal("weapons[mod=Therzie.Warfare]", Shape(Saved(Payload(warfare), new ChestRuleEdit(new[] { "weapons" }, null, null,
            filters: F(("mod", new[] { "Therzie.Warfare" })), editedFilters: F(("mod", new[] { "Therzie.Warfare" }))))));
    }

    [Fact]
    public void Edit_KeepsUnknownDimensionsAndRejectsInvalidValues()
    {
        // D077 B01: a dimension this version doesn't know is kept (it matches nothing); a malformed one is refused.
        Assert.Equal(new[] { "red" }, new ChestRuleEdit(null, null, null, filters: F(("colour", new[] { "red" }))).Filters["colour"]);
        Assert.Throws<ArgumentException>(() => new ChestRuleEdit(null, null, null, filters: F(("Colour!", new[] { "red" }))));
        // D086 D6: a value the dimension doesn't have is kept too (it matches nothing).
        Assert.Equal(new[] { "shiny" }, new ChestRuleEdit(null, null, null, filters: F(("rarity", new[] { "shiny" }))).Filters["rarity"]);
        Assert.Throws<ArgumentException>(() => new ChestRuleEdit(null, null, null, filters: F(("rarity", new[] { "epic" }), ("rarity", new[] { "rare" }))));
        Assert.Empty(new ChestRuleEdit(null, null, null, filters: F(("rarity", Array.Empty<string>()))).Filters);
    }

    private sealed class Answers : ICategoryAnswers
    {
        private readonly ClassificationIndex index;
        public Answers(ClassificationIndex index) { this.index = index; }
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) => index.Items.TryGetValue(prefab, out var row) ? row : null;
    }
    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());
    private static TransferInventorySnapshot Snapshot(string id, params TransferStack[] stacks) =>
        new(new TransferInventoryStamp("inv:" + id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"), 4, 2, stacks, Array.Empty<int>());

    // P6-21: the stack's upgrade level as stars 1-4 and 5+, only for items a recipe upgrades; a saved "upgraded" becomes levels.
    [Fact]
    public void UpgradeLevel_MatchesTheStacksLevel_AndLegacyUpgradedBecomesLevels()
    {
        var sword = Build(new[] { Item("SwordIron", "OneHandedWeapon") }).Items["SwordIron"];
        var egg = Build(new[] { Item("AsksvinEgg") }).Items["AsksvinEgg"];
        Assert.Equal("upgradable:yes quality:3", string.Join(" ", ClassificationFilters.Condition(3, false, 0, 0, upgradable: true)
            .Where(x => x.Dimension is "upgradable" or "quality").Select(x => x.Dimension + ":" + x.Value)));
        Assert.Equal("5+", ClassificationFilters.QualityValue(7));
        var levels = Filter("quality", "2", "3");
        Assert.True(Accepts(sword, levels, ClassificationFilters.Condition(3, true, 10, 10, upgradable: true)));
        Assert.False(Accepts(sword, levels, ClassificationFilters.Condition(1, true, 10, 10, upgradable: true)));
        Assert.True(Accepts(sword, Filter("quality", "5+"), ClassificationFilters.Condition(6, true, 10, 10, upgradable: true)));
        // An egg's quality (tame level) is no upgrade level: no level row, so a level filter rejects it.
        Assert.False(Accepts(egg, Filter("quality", "1", "2", "3", "4", "5+"), ClassificationFilters.Condition(2, false, 0, 0)));
        Assert.True(Accepts(egg, Filter("upgradable", "no"), ClassificationFilters.Condition(2, false, 0, 0)));
        Assert.False(Accepts(egg, Filter("upgradable", "yes"), ClassificationFilters.Condition(2, false, 0, 0)));
        Assert.Equal(new[] { "2", "3", "4", "5+" }, ClassificationFilters.LevelsFor(new[] { "yes" }));
        Assert.Equal(new[] { "1" }, ClassificationFilters.LevelsFor(new[] { "no" }));
        Assert.Equal("Upgrade level", RuleText.FilterName("quality"));
        Assert.Equal("Can be upgraded", RuleText.FilterName("upgradable"));
        Assert.Equal("upgrade level 2 or 3", RuleText.Short(new Dictionary<string, IReadOnlyList<string>> { ["quality"] = new[] { "2", "3" } }));
    }

    // P6-21: "Item origin" names the base game positively ("vanilla"); missing evidence is "origin not known", never vanilla.
    [Fact]
    public void ItemOrigin_VanillaIsEvidence_UnknownIsNotVanilla()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("Wood"), Item("SwordWarfare_TW", "OneHandedWeapon") });
        var capability = new OptionalCapability("base-game", OptionalCapabilityState.Supported, "", "items=1");
        var rows = OptionalDefinitionEvidence.Build(catalog, capability,
            new[] { new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, "Wood", RuleText.Vanilla, "Wood") });
        Assert.Contains(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence && x.Method == "mod" && x.Detail == RuleText.Vanilla && x.Output == "Wood");
        Assert.Equal("Item origin", RuleText.FilterName("mod"));
        Assert.Equal("Base game", RuleText.ValueName("mod", RuleText.Vanilla));
        Assert.Equal("origin not known", RuleText.ValueName("mod", "unknown"));
        Assert.Equal("from the base game", RuleText.Short(new Dictionary<string, IReadOnlyList<string>> { ["mod"] = new[] { RuleText.Vanilla } }));
        Assert.Equal("from Therzie.Warfare", RuleText.Short(new Dictionary<string, IReadOnlyList<string>> { ["mod"] = new[] { "Therzie.Warfare" } }));
    }
}
