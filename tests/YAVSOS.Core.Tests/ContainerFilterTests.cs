using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>X60 (D155, D160): container filters read from the game's answers, their categories, the automatic rule and narrow-only.</summary>
public sealed class ContainerFilterTests
{
    private static readonly ClassificationTaxonomy Base = DefaultClassificationTaxonomy.Create();
    private static readonly ClassificationIndex Index = Build(new[] { Item("Copper"), Item("Wood"), Item("FineWood"), Item("SwordBronze"), Item("Bread") },
        new ClassificationRuleSet(Base, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "FineWood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" })
        }));
    private static readonly string[] Items = { "Bread", "Copper", "FineWood", "SwordBronze", "Wood" };

    private sealed class Answers : ICategoryAnswers
    {
        public long Generation { get; set; } = 1;
        public bool CategoriesReady { get; set; } = true;
        public ClassificationResult? Resolve(string prefab) => Index.Items.TryGetValue(prefab, out var row) ? row : null;
    }

    private static ContainerTypeInfo Type(string prefab, string name, string key) => new(prefab, name, key);

    // The spike's stores: a Timber Crib that accepts wood, two armor-stand types sharing one name and size, a plain chest,
    // and a Shelf that accepts everything (no filter).
    private static readonly ContainerTypeInfo[] Types =
    {
        Type("piece_storehouse_timbercrib", "Storehouse Timber Crib", "$crib|8x8"),
        Type("piece_storehouse_metalrack", "Storehouse Metal Rack", "$rack|8x8"),
        Type("piece_storehouse_metalrackhalf", "Storehouse Metal Rack (half)", "$rack|8x8"),
        Type("piece_chest_wood", "Chest", "$chest|5x2"),
        Type("piece_storehouse_emergencyshelf_1", "Storehouse Emergency Shelf", "$shelf|6x1"),
    };

    private static bool? Ask(int type, int item) => Types[type].Prefab switch
    {
        "piece_storehouse_timbercrib" => Items[item] is "Wood" or "FineWood",
        "piece_storehouse_metalrack" or "piece_storehouse_metalrackhalf" => Items[item] == "Copper",
        _ => true
    };

    private static ContainerFilterSet Read(Func<int, int, bool?>? ask = null)
    {
        var build = new ContainerFilterBuild(Types, Items, ask ?? Ask);
        Assert.True(build.Step(() => true));
        Assert.False(build.Failed);
        return build.Result!;
    }

    [Fact]
    public void Build_TypesThatRefuseSomeItemAreFilters_TakeEverythingIsNone()
    {
        var set = Read();
        Assert.Equal(5, set.Types);
        Assert.Equal(new[] { "piece_storehouse_metalrack", "piece_storehouse_metalrackhalf", "piece_storehouse_timbercrib" },
            set.Filters.Select(x => x.Prefab));
        Assert.Null(set.ForPrefab("piece_chest_wood"));
        Assert.Null(set.ForPrefab("piece_storehouse_emergencyshelf_1"));
        var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        Assert.Equal("store.piece_storehouse_timbercrib", crib.Category);
        Assert.Equal("Storehouse Timber Crib", crib.Name);
        Assert.Equal(new[] { "FineWood", "Wood" }, crib.Members.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(crib.Takes("Wood")); Assert.False(crib.Takes("Copper"));
        Assert.Equal(new[] { "store.piece_storehouse_metalrack", "store.piece_storehouse_metalrackhalf" }, set.CategoriesOf("Copper"));
        Assert.Empty(set.CategoriesOf("Bread"));
    }

    [Fact]
    public void Build_SameNameAndSizeIsAskedOnce_AndSpreadsOverSteps()
    {
        var asked = new List<int>();
        var build = new ContainerFilterBuild(Types, Items, (type, item) => { asked.Add(type); return Ask(type, item); });
        var steps = 0; int budget;
        do { budget = 3; steps++; } while (!build.Step(() => budget-- > 0)); // Three asks per step.
        Assert.True(steps > 1);
        Assert.Equal(4, build.Groups);
        Assert.Equal(4 * Items.Length, build.Asks);
        Assert.DoesNotContain(2, asked); // The half rack shares the rack's name and size.
        Assert.Equal(build.Result!.ForPrefab("piece_storehouse_metalrack")!.Members, build.Result.ForPrefab("piece_storehouse_metalrackhalf")!.Members);
    }

    [Fact]
    public void Build_AnUnansweredAsk_FailsTheWholeRead()
    {
        var build = new ContainerFilterBuild(Types, Items, (type, item) => type == 1 && item == 2 ? null : Ask(type, item));
        Assert.True(build.Step(() => true));
        Assert.True(build.Failed);
        Assert.Null(build.Result);
    }

    [Fact]
    public void Build_StoreModNotLoaded_EverythingTakesEverything_NoFilters()
    {
        Assert.Empty(Read((_, _) => true).Filters);
        Assert.Same(Base, Read((_, _) => true).Extend(Base));
    }

    [Fact]
    public void CategoryId_IsStableSanitizedAndBounded()
    {
        Assert.Equal("store.piece_storehouse_timbercrib", ContainerFilterSet.CategoryId("piece_storehouse_timbercrib"));
        Assert.Equal("store.morkhalla_chest_ancient", ContainerFilterSet.CategoryId("Morkhalla.Chest Ancient"));
        var longId = ContainerFilterSet.CategoryId(new string('x', 80));
        Assert.True(longId.Length <= 64);
        Assert.Equal(longId, ContainerFilterSet.CategoryId(new string('x', 80)));
        Assert.NotEqual(ContainerFilterSet.CategoryId("A_b"), ContainerFilterSet.CategoryId("A_b", hashed: true));
        // Two prefabs that differ only in case get different IDs.
        var set = ContainerFilterSet.From(new[] { (Type("Crib", "Crib", "a"), (IReadOnlyCollection<string>)new[] { "Wood" }),
            (Type("crib", "crib", "b"), (IReadOnlyCollection<string>)new[] { "Wood" }) }, 2);
        Assert.Equal(2, set.Filters.Select(x => x.Category).Distinct().Count());
    }

    [Fact]
    public void Extend_AddsACategoryPerFilter_KeepsTheBaseRows_AndReturnsTheSameObject()
    {
        var set = Read();
        var extended = set.Extend(Base);
        Assert.Same(extended, set.Extend(Base));
        Assert.Equal(Base.Categories.Count + 3, extended.Categories.Count);
        var crib = extended.Categories["store.piece_storehouse_timbercrib"];
        Assert.Equal("Storehouse Timber Crib", crib.Name);
        Assert.Null(extended.ParentOf(crib.Id));
        Assert.Equal(Base.Categories["materials.wood"].NameKey, extended.Categories["materials.wood"].NameKey);
        Assert.NotEqual(Base.Fingerprint, extended.Fingerprint);
    }

    [Fact]
    public void Answers_AddTheFilterCategories_AndChangeGenerationWithTheFilters()
    {
        var inner = new Answers(); var view = new ContainerFilterAnswers();
        view.Over(inner, ContainerFilterSet.Empty, pending: true);
        var before = view.Generation;
        Assert.True(view.FilterCategoriesPending);
        var plain = view.Resolve("Wood")!.Memberships.Select(x => x.Category).Distinct().ToArray();
        Assert.DoesNotContain(plain, ContainerFilterSet.IsFilterCategory);
        var set = Read();
        view.Over(inner, set, pending: false);
        Assert.NotEqual(before, view.Generation);
        Assert.False(view.FilterCategoriesPending);
        var wood = view.Resolve("Wood")!;
        Assert.Equal(plain.Append("store.piece_storehouse_timbercrib").OrderBy(x => x, StringComparer.Ordinal),
            wood.Memberships.Select(x => x.Category).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("materials.wood", wood.Primary);
        Assert.Same(wood, view.Resolve("Wood")); // One answer object per generation.
        var generation = view.Generation;
        inner.Generation++;
        Assert.NotEqual(generation, view.Generation);
        Assert.NotSame(wood, view.Resolve("Wood"));
        Assert.Equal(3, view.FilterMembers("store.piece_storehouse_metalrack") + view.FilterMembers("store.piece_storehouse_timbercrib"));
        Assert.Null(view.FilterMembers("materials.wood"));
    }

    private static string Payload(ClassificationTaxonomy taxonomy, string[] categories, string[]? always = null, int priority = 0) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(taxonomy, ChestRuleState.Configured, new[] { new ClassificationFilterRule(taxonomy, categories) }, always, null, priority));

    [Fact]
    public void Evaluator_AFilterCategoryWaitsUntilTheFiltersAreRead_ThenDecides()
    {
        var set = Read(); var extended = set.Extend(Base);
        var payload = Payload(extended, new[] { "store.piece_storehouse_timbercrib" });
        var inner = new Answers(); var evaluator = new ChestRuleEvaluator();
        var view = new ContainerFilterAnswers().Over(inner, ContainerFilterSet.Empty, pending: true);
        // Before the read: the base category set has no such category; the part waits instead of rejecting (nothing moves out).
        Assert.Equal(ChestRuleDecision.RejectCategoriesPending, evaluator.Evaluate(payload, "Wood", Base, view));
        Assert.Equal(ChestRuleDecision.RejectCategoriesPending, evaluator.Evaluate(payload, "Copper", Base, view));
        // A rule with another part still accepts by it, and its Never and Always still apply.
        var mixed = Payload(extended, new[] { "store.piece_storehouse_timbercrib", "food.health" });
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(mixed, "Bread", Base, view));
        view.Over(inner, set, pending: false);
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "Wood", extended, view));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Copper", extended, view));
        // Read, but the category isn't there (the mod was removed): an ordinary unknown category, which accepts nothing (D050).
        view.Over(inner, ContainerFilterSet.Empty, pending: false);
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Wood", Base, view));
        // Without a container-filter view (a plain answers object), as before.
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Wood", Base, inner));
    }

    [Fact]
    public void AutomaticPayload_TicksTheStoresOwnCategory_AtTheGivenPriority()
    {
        var set = Read(); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var rank = ChestPriority.Rank(4);
        var payload = set.AutomaticPayload(crib, rank, Base)!;
        Assert.Same(payload, set.AutomaticPayload(crib, rank, Base));
        var extended = set.Extend(Base);
        var rules = ContainerPolicyEnvelope.Read(payload, extended).Rules!;
        Assert.Equal(ChestRuleState.Configured, rules.State);
        Assert.Equal(1, rules.Priority);
        Assert.Equal(4, ChestPriority.Level(rules.Priority));
        Assert.Equal(new[] { "store.piece_storehouse_timbercrib" }, rules.FullRules.Single().Categories);
        var view = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        var evaluator = new ChestRuleEvaluator();
        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(payload, "FineWood", extended, view));
        Assert.Equal(ChestRuleDecision.Reject, evaluator.Evaluate(payload, "Bread", extended, view));
        // The panel's first edit starts from it and saves a normal rule (compare-and-set against no stored rule).
        var edit = ChestRuleEdit.From(rules);
        Assert.Equal(TransferRefusal.None, ContainerPolicyEnvelope.TryEdit("", "", extended, new ChestRuleEdit(edit.Categories, new[] { "Copper" }, null, priority: edit.Priority), out var saved));
        var stored = ContainerPolicyEnvelope.Read(saved!, extended).Rules!;
        Assert.Equal(new[] { "store.piece_storehouse_timbercrib" }, stored.FullRules.Single().Categories);
        Assert.Equal(1, stored.Priority);
    }

    [Fact]
    public void Narrow_TheFilterWins_ARuleNarrowsButNeverWidens()
    {
        var crib = Read().ForPrefab("piece_storehouse_timbercrib")!;
        Assert.Equal(ChestRuleDecision.Reject, ContainerFilter.Narrow(ChestRuleDecision.Accept, crib, "Copper"));
        Assert.Equal(ChestRuleDecision.Accept, ContainerFilter.Narrow(ChestRuleDecision.Accept, crib, "Wood"));
        Assert.Equal(ChestRuleDecision.NoRule, ContainerFilter.Narrow(ChestRuleDecision.NoRule, crib, "Copper"));
        Assert.Equal(ChestRuleDecision.RejectCategoriesPending, ContainerFilter.Narrow(ChestRuleDecision.RejectCategoriesPending, crib, "Copper"));
        Assert.Equal(ChestRuleDecision.Accept, ContainerFilter.Narrow(ChestRuleDecision.Accept, null, "Copper"));
        Assert.True(ContainerFilter.Refuses(crib, "Copper")); Assert.False(ContainerFilter.Refuses(null, "Copper"));
    }

    [Fact]
    public void Chips_PartsOutsideTheFilterAreGreyed_AndTheAutomaticChipSaysSo()
    {
        var chips = RuleChips.Build(new[] { "Wood", "Metals" }, null, new[] { "Copper", "Fine Wood" }, Array.Empty<string>(),
            outside: new HashSet<string> { "Metals", "Copper" }, container: "Timber Crib");
        Assert.Equal(ChipKind.Category, chips.Single(x => x.Name == "Wood").Kind);
        Assert.Equal(ChipKind.Note, chips.Single(x => x.Name == "Metals").Kind);
        Assert.Equal(ChipKind.Always, chips.Single(x => x.Name == "Fine Wood").Kind);
        Assert.Equal(ChipKind.Note, chips.Single(x => x.Name == "Copper").Kind);
        Assert.Equal("A Timber Crib doesn't accept these items, so this part of the rule does nothing here. It stays saved.",
            EnglishText.Of(chips.Single(x => x.Name == "Metals").Tip!.Body!));
        var automatic = RuleChips.Automatic("Timber Crib", 4, ChestPriority.Name(4));
        Assert.Equal("Automatic rule", EnglishText.Of(automatic.Said!));
        Assert.Equal("This Timber Crib has no YAVSOS rule, so it accepts what its own filter accepts, at priority 4 (Normal). " +
            "Change anything here to save this as its rule.", EnglishText.Of(automatic.Tip!.Body!));
    }

    // ---- The planners with filtered containers: conservation and narrow-only.

    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());

    private sealed class Box
    {
        public string Id = ""; public string Payload = ""; public int Width = 4; public ContainerFilter? Filter;
        public List<(Guid Handle, int X, string Prefab, int Quantity)> Stacks = new();
        public Box Put(string prefab, int quantity) { Stacks.Add((Guid.NewGuid(), Enumerable.Range(0, Width).First(x => Stacks.All(s => s.X != x)), prefab, quantity)); return this; }
        public TransferInventorySnapshot Snapshot() => new(new TransferInventoryStamp("inv:" + Id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"),
            Width, 1, Stacks.Select(s => new TransferStack(s.Handle, s.X, 0, s.Quantity, 50, Identity(s.Prefab), false, mergeClass: s.Prefab)), Array.Empty<int>());
    }

    private static Dictionary<string, int> Totals(IEnumerable<Box> boxes) =>
        boxes.SelectMany(b => b.Stacks).GroupBy(s => s.Prefab).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));

    private static void Apply(List<Box> boxes, CleanupPlan plan)
    {
        foreach (var move in plan.Moves)
        {
            var source = boxes.Single(b => b.Stacks.Any(s => s.Handle == move.Handle));
            var at = source.Stacks.FindIndex(s => s.Handle == move.Handle);
            var stack = source.Stacks[at];
            Assert.True(stack.Quantity >= move.Units);
            if (stack.Quantity == move.Units) source.Stacks.RemoveAt(at); else source.Stacks[at] = (stack.Handle, stack.X, stack.Prefab, stack.Quantity - move.Units);
            var destination = boxes.Single(b => b.Id == move.DestinationId);
            var there = destination.Stacks.FindIndex(s => s.X == move.DestinationX);
            if (there < 0) destination.Stacks.Add((Guid.NewGuid(), move.DestinationX, move.Identity.Prefab, move.Units));
            else { var row = destination.Stacks[there]; Assert.Equal(row.Prefab, move.Identity.Prefab); destination.Stacks[there] = (row.Handle, row.X, row.Prefab, row.Quantity + move.Units); }
        }
    }

    [Fact]
    public void Cleanup_NeverPlansAnItemIntoAStoreWhoseFilterRefusesIt_AndConservesItems()
    {
        var set = Read(); var taxonomy = set.Extend(Base); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var answers = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        // D160's example: someone ticked Metals on a store that accepts only wood, at a high priority.
        var store = new Box { Id = "a-crib", Payload = Payload(taxonomy, new[] { "materials.metal", "materials.wood" }, priority: 3), Filter = crib }.Put("Wood", 10);
        var metal = new Box { Id = "b-metal", Payload = Payload(taxonomy, new[] { "materials.metal" }) };
        var loose = new Box { Id = "c-box" }.Put("Copper", 5).Put("FineWood", 4);
        var boxes = new List<Box> { store, metal, loose };
        var before = Totals(boxes);
        var plan = CleanupPlanner.Plan(new CleanupInput(taxonomy, answers, boxes.Select(Chest)));
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "Copper" && x.DestinationId == "b-metal");
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "FineWood" && x.DestinationId == "a-crib");
        Assert.DoesNotContain(plan.Moves, x => x.Identity.Prefab == "Copper" && x.DestinationId == "a-crib");
        Assert.DoesNotContain(plan.Moves, x => x.SourceId == "a-crib"); // Its Wood is accepted by rule and filter.
        Apply(boxes, plan);
        Assert.Equal(before, Totals(boxes));
        // A second press has nothing left to do.
        Assert.Empty(CleanupPlanner.Plan(new CleanupInput(taxonomy, answers, boxes.Select(Chest))).Moves);
    }

    [Fact]
    public void Cleanup_AFullStoreThatRefusesTheItem_IsNoDestination_NotNoRoom()
    {
        var set = Read(); var taxonomy = set.Extend(Base); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var answers = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        var store = new Box { Id = "a-crib", Payload = Payload(taxonomy, new[] { "materials.metal" }), Width = 1, Filter = crib }.Put("Copper", 50);
        var plan = CleanupPlanner.Plan(new CleanupInput(taxonomy, answers, new[] { Chest(store), Chest(new Box { Id = "b-box" }.Put("Copper", 3)) }));
        // Its stored Metals part is outside the filter: its own Copper is rejected (narrow only) and finds no chest.
        Assert.Empty(plan.Moves);
        Assert.Contains(plan.Remainders, x => x.SourceId == "a-crib" && x.Reason == RunReason.NoDestination);
        Assert.DoesNotContain(plan.Remainders, x => x.Reason == RunReason.NoCapacity);
    }

    [Fact]
    public void Cleanup_AnUnopenedStoreWithItsAutomaticRule_ReceivesItsItems()
    {
        var set = Read(); var taxonomy = set.Extend(Base); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var answers = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        var store = new Box { Id = "a-crib", Payload = set.AutomaticPayload(crib, ChestPriority.Rank(4), Base)!, Filter = crib };
        var wood = new Box { Id = "b-wood", Payload = Payload(taxonomy, new[] { "materials.wood" }) }.Put("Wood", 7);
        var loose = new Box { Id = "c-box" }.Put("FineWood", 2).Put("Bread", 1);
        var boxes = new List<Box> { store, wood, loose };
        var before = Totals(boxes);
        var plan = CleanupPlanner.Plan(new CleanupInput(taxonomy, answers, boxes.Select(Chest)));
        // Priority 4 ranks above an ordinary chest's 5, so the store wins the wood; Bread has no chest and stays.
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "Wood" && x.SourceId == "b-wood" && x.DestinationId == "a-crib" && x.Kind == CleanupKind.Resort);
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "FineWood" && x.DestinationId == "a-crib");
        Assert.DoesNotContain(plan.Moves, x => x.Identity.Prefab == "Bread");
        Apply(boxes, plan);
        Assert.Equal(before, Totals(boxes));
    }

    [Fact]
    public void Stash_SkipsAStoreWhoseFilterRefusesTheItem_AlsoAsAHolder()
    {
        var set = Read(); var taxonomy = set.Extend(Base); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var answers = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        var player = new Box { Id = "player", Width = 4 }.Put("Copper", 5).Put("Wood", 3);
        // A store that somehow holds Copper and has no rule, and one ruled for metals: neither receives Copper.
        var holder = new Box { Id = "a-crib", Filter = crib }.Put("Copper", 1);
        var ruled = new Box { Id = "b-crib", Payload = Payload(taxonomy, new[] { "materials.metal", "materials.wood" }), Filter = crib };
        StashChest Stash(Box box) => new(box.Id, box.Payload, box.Snapshot(), true, filter: box.Filter);
        var plan = StashPlanner.Plan(new StashInput(taxonomy, answers, player.Snapshot(), new[] { Stash(holder), Stash(ruled) }));
        Assert.DoesNotContain(plan.Moves, x => x.Identity.Prefab == "Copper");
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "Wood" && x.DestinationId == "b-crib");
        Assert.Equal(3, plan.Moves.Where(x => x.Identity.Prefab == "Wood").Sum(x => x.Units));
    }

    [Fact]
    public void StoreOrder_LeavesOutAChestWhoseFilterRefusesTheItem()
    {
        var set = Read(); var taxonomy = set.Extend(Base); var crib = set.ForPrefab("piece_storehouse_timbercrib")!;
        var answers = new ContainerFilterAnswers().Over(new Answers(), set, pending: false);
        var payloads = new[] { new KeyValuePair<string, string>("crib", Payload(taxonomy, new[] { "materials.metal" }, priority: 4)),
            new KeyValuePair<string, string>("metal", Payload(taxonomy, new[] { "materials.metal" })) };
        Assert.Equal(new[] { "crib", "metal" }, new StoreOrder(taxonomy, answers, payloads).For("Copper"));
        Assert.Equal(new[] { "metal" }, new StoreOrder(taxonomy, answers, payloads, filters: id => id == "crib" ? crib : null).For("Copper"));
    }

    private static CleanupChest Chest(Box box) => new(box.Id, box.Payload, box.Snapshot(), true, filter: box.Filter);
}
