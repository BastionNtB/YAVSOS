using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class StashPlannerTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static readonly ClassificationIndex Index = Build(new[] { Item("Copper"), Item("Wood"), Item("SwordBronze"), Item("Bread"), Item("Torch") },
        new ClassificationRuleSet(Taxonomy, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal", "materials.crafting" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" }),
            new ClassificationOverride("exact", "Torch", OverrideOperation.Replace, new[] { "equipment.tools" })
        }));

    private sealed class Answers : ICategoryAnswers
    {
        public long Generation { get; set; } = 1;
        public bool CategoriesReady { get; set; } = true;
        public ClassificationResult? Resolve(string prefab) => Index.Items.TryGetValue(prefab, out var row) ? row : null;
    }

    private static TransferItemIdentity Identity(string prefab, long crafter = 0) =>
        new(prefab, "catalog", 1, 0, 0, 100, crafter, crafter == 0 ? "" : "Crafter", false, false,
            new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());

    private static string Rule(string[]? categories = null, string[]? always = null, string[]? never = null, int priority = 0) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            categories == null ? null : new[] { ClassificationContractFixture.Rule(categories) }, always, never, priority));

    /// <summary>Mutable test world: the player and chests with stacks; plans are applied like a successful writer would.</summary>
    private sealed class World
    {
        internal sealed class Slot
        {
            public Guid Handle = Guid.NewGuid(); public int X, Y, Quantity, Maximum = 50;
            public TransferItemIdentity Identity = null!; public bool Protected;
        }
        internal sealed class Box
        {
            public string Id = ""; public string Payload = ""; public int Width = 4, Height = 1;
            public bool Local = true, FormatKnown = true; public List<Slot> Slots = new(); public List<int> Blocked = new();
            public TransferInventorySnapshot Snapshot() => new(new TransferInventoryStamp("inv:" + Id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"),
                Width, Height, Slots.Select(s => new TransferStack(s.Handle, s.X, s.Y, s.Quantity, s.Maximum, s.Identity, s.Protected, mergeClass: s.Maximum > 1 ? s.Identity.Prefab : null)), Blocked);
        }
        public readonly Box Player = new() { Id = "player", Width = 8, Height = 4 };
        public readonly List<Box> Chests = new();
        public Box Add(string id, string payload = "", int width = 4, bool local = true)
        { var chest = new Box { Id = id, Payload = payload, Width = width, Local = local }; Chests.Add(chest); return chest; }
        public Box this[string id] => Chests.Single(x => x.Id == id);
        public IEnumerable<StashChest> Snapshot() => Chests.Select(x => new StashChest(x.Id, x.Payload, x.Snapshot(), x.Local, x.FormatKnown));
        public void Apply(StashPlan plan)
        {
            foreach (var move in plan.Moves)
            {
                var slot = Player.Slots.Single(s => s.Handle == move.Handle);
                Assert.True(slot.Quantity >= move.Units);
                slot.Quantity -= move.Units; if (slot.Quantity == 0) Player.Slots.Remove(slot);
                var destination = this[move.DestinationId];
                var occupant = destination.Slots.SingleOrDefault(s => s.X == move.DestinationX && s.Y == move.DestinationY);
                if (move.MergesPlanned) { Assert.Null(move.MergeHandle); Assert.Equal(move.Identity.Prefab, occupant!.Identity.Prefab); occupant.Quantity += move.Units; Assert.True(occupant.Quantity <= occupant.Maximum); }
                else if (move.MergeHandle == null) { Assert.Null(occupant); destination.Slots.Add(new Slot { X = move.DestinationX, Y = move.DestinationY, Quantity = move.Units, Identity = move.Identity, Maximum = slot.Maximum }); }
                else { Assert.Equal(move.MergeHandle, occupant!.Handle); occupant.Quantity += move.Units; Assert.True(occupant.Quantity <= occupant.Maximum); }
            }
        }
        public Dictionary<string, int> Totals() => Chests.Append(Player).SelectMany(c => c.Slots).GroupBy(s => s.Maximum > 1 ? "class:" + s.Identity.Prefab : s.Identity.Fingerprint).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
    }

    private static World.Slot Put(World.Box box, string prefab, int quantity, int x = -1, int y = 0, long crafter = 0, bool isProtected = false, int maximum = 50)
    {
        if (x < 0) x = Enumerable.Range(0, box.Width).First(c => box.Slots.All(s => s.X != c || s.Y != y));
        var slot = new World.Slot { X = x, Y = y, Quantity = quantity, Identity = Identity(prefab, crafter), Protected = isProtected, Maximum = maximum };
        box.Slots.Add(slot); return slot;
    }

    private static StashPlan Plan(World world, Answers? answers = null, bool muc = false, IEnumerable<Guid>? held = null, IEnumerable<Guid>? only = null,
        Func<string, int>? size = null) =>
        StashPlanner.Plan(new StashInput(Taxonomy, answers ?? new Answers(), world.Player.Snapshot(), world.Snapshot(), size, muc, held, only));

    [Fact]
    public void EmptyRuledChest_ReceivesItsCategory_AndUnwantedItemsStaySilently()
    {
        var world = new World();
        Put(world.Player, "Copper", 20, y: 1); Put(world.Player, "Bread", 3, y: 1);
        world.Add("metal", Rule(new[] { "materials.metal" }));
        var plan = Plan(world);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(("metal", 20, CleanupRoute.Direct, StashTier.Ruled, 0, 0), (move.DestinationId, move.Units, move.Route, move.Tier, move.DestinationX, move.DestinationY));
        Assert.Empty(plan.Remainders);
        Assert.Equal(Taxonomy.Fingerprint + ":1", plan.PolicyStamp);
    }

    [Fact]
    public void Ranking_PriorityFirst_ThenSpecificity()
    {
        var world = new World();
        Put(world.Player, "Copper", 20, y: 1);
        world.Add("a-materials", Rule(new[] { "materials" }));
        world.Add("b-metal", Rule(new[] { "materials.metal" }));
        Assert.Equal("b-metal", Assert.Single(Plan(world).Moves).DestinationId);
        world["a-materials"].Payload = Rule(new[] { "materials" }, priority: 1);
        Assert.Equal("a-materials", Assert.Single(Plan(world).Moves).DestinationId);
    }

    [Fact]
    public void Overflow_FillsTheNextChestWhenTheFirstIsFull()
    {
        var world = new World();
        Put(world.Player, "Copper", 50, y: 1); Put(world.Player, "Copper", 30, y: 1);
        Put(world.Add("a-metal", Rule(new[] { "materials.metal" }), width: 1), "Wood", 1);
        world.Add("b-metal", Rule(new[] { "materials.metal" }), width: 1);
        world.Add("c-metal", Rule(new[] { "materials.metal" }), width: 1);
        var plan = Plan(world);
        Assert.Equal(new[] { ("b-metal", 50), ("c-metal", 30) }, plan.Moves.Select(x => (x.DestinationId, x.Units)));
        Assert.Empty(plan.Remainders);
        world.Chests.RemoveAt(2);
        var full = Plan(world);
        Assert.Equal(50, full.Moves.Sum(x => x.Units));
        Assert.Equal((CleanupReason.NoCapacity, 30), (Assert.Single(full.Remainders).Reason, full.Remainders[0].Units));
    }

    [Fact]
    public void UnruledChest_ReceivesOnlyItemsItAlreadyHolds_AfterRuledChests_NearestFirst()
    {
        var world = new World();
        Put(world.Player, "Wood", 30, y: 1); Put(world.Player, "Bread", 5, y: 1);
        Put(world.Add("near", width: 2), "Wood", 45);
        Put(world.Add("far", width: 2), "Wood", 1);
        world.Add("empty-unruled");
        var plan = Plan(world);
        Assert.Equal(new[] { ("near", 5, StashTier.Holder, (Guid?)world["near"].Slots[0].Handle), ("near", 25, StashTier.Holder, null) },
            plan.Moves.Select(x => (x.DestinationId, x.Units, x.Tier, x.MergeHandle)));
        // D086 M1: a chest whose rule was cleared before the fix (a stored unconfigured rule) is unruled too.
        var cleared = ContainerPolicyEnvelope.Serialize(2, new ChestRules(Taxonomy, ChestRuleState.Unconfigured));
        world.Chests.Single(x => x.Id == "near").Payload = cleared; world.Chests.Single(x => x.Id == "far").Payload = cleared;
        Assert.Equal(30, Plan(world).Moves.Where(x => x.DestinationId == "near").Sum(x => x.Units));
        // A ruled chest that accepts the item comes before any unruled one.
        world.Chests.Add(new World.Box { Id = "ruled", Payload = Rule(new[] { "materials.wood" }) });
        Assert.All(Plan(world).Moves, x => Assert.Equal("ruled", x.DestinationId));
    }

    // D086 D4 (M12): while categories load, Always rules take their items at once; category parts wait, and an item a
    // category chest may want later does not go to an unruled holder meanwhile.
    [Fact]
    public void WhileCategoriesLoad_AlwaysRulesAndHoldersStillTakeItems()
    {
        var world = new World();
        Put(world.Player, "Wood", 30, y: 1); Put(world.Player, "Copper", 10, y: 1); Put(world.Player, "Bread", 5, y: 1);
        world.Add("always-wood", Rule(always: new[] { "Wood" }));
        world.Add("metal", Rule(new[] { "materials.metal" }));
        Put(world.Add("bread-holder", width: 2), "Bread", 1);
        Put(world.Add("copper-holder", width: 2), "Copper", 1);
        var plan = Plan(world, new Answers { CategoriesReady = false });
        Assert.True(plan.CategoriesPending);
        // Only Wood moves: a category chest (Metals) may want any other item once categories are ready.
        Assert.Equal(30, plan.Moves.Where(x => x.DestinationId == "always-wood").Sum(x => x.Units));
        Assert.All(plan.Moves, x => Assert.Equal(("always-wood", StashTier.Ruled), (x.DestinationId, x.Tier)));
        // The items that wait are reported, not left silently (D087).
        Assert.Equal(new[] { "Bread", "Copper" }, plan.Remainders.Where(x => x.Reason == CleanupReason.CategoriesPending).Select(x => x.Identity.Prefab).OrderBy(x => x));
        // A category chest that would outrank the Always chest once ready keeps the item back (cleanup would only have to move it again, D092).
        world["metal"].Payload = Rule(new[] { "materials.wood" }, priority: 3);
        plan = Plan(world, new Answers { CategoriesReady = false });
        Assert.DoesNotContain(plan.Moves, x => x.Identity.Prefab == "Wood");
        Assert.Contains(plan.Remainders, x => x.Identity.Prefab == "Wood" && x.Reason == CleanupReason.CategoriesPending);
        world["always-wood"].Payload = Rule(always: new[] { "Wood" }, priority: 3); // A tie: Always wins.
        Assert.Equal(30, Plan(world, new Answers { CategoriesReady = false }).Moves.Where(x => x.DestinationId == "always-wood").Sum(x => x.Units));
        Assert.Contains("waiting for categories", CleanupReport.Describe(CleanupReason.CategoriesPending));
        // Without a category chest, holders take what they already hold.
        world.Chests.Remove(world["metal"]);
        plan = Plan(world, new Answers { CategoriesReady = false });
        Assert.Equal(5, plan.Moves.Where(x => x.DestinationId == "bread-holder").Sum(x => x.Units));
        Assert.Equal(10, plan.Moves.Where(x => x.DestinationId == "copper-holder").Sum(x => x.Units));
    }

    // B4.14: an item the category answers don't know still goes to a chest that lists it under Always.
    [Fact]
    public void UnresolvedItem_GoesToItsAlwaysChest()
    {
        var world = new World();
        Put(world.Player, "ModThing", 4, y: 1);
        world.Add("misc", Rule(new[] { "misc" }));
        world.Add("a-always", Rule(always: new[] { "ModThing" }));
        world.Add("b-always", Rule(always: new[] { "ModThing" }, priority: 2));
        world.Add("never", Rule(always: new[] { "Wood" }, never: new[] { "ModThing" }));
        var move = Assert.Single(Plan(world).Moves);
        Assert.Equal(("b-always", 4), (move.DestinationId, move.Units));
    }

    [Fact]
    public void UnreadableOrPendingRules_AcceptNothing_AndPendingCategoriesRefuseTheRun()
    {
        var world = new World();
        Put(world.Player, "Wood", 30, y: 1);
        Put(world.Add("future", "{\"future\":1}"), "Wood", 1);
        Assert.Empty(Plan(world).Moves);
        world.Add("wood", Rule(new[] { "materials.wood" }));
        var pending = Plan(world, new Answers { CategoriesReady = false });
        Assert.True(pending.CategoriesPending); Assert.Empty(pending.Moves);
    }

    [Fact]
    public void ProtectedItems_AndProtectedCells_NeverMove_EvenWhenPicked()
    {
        var world = new World();
        var equipped = Put(world.Player, "SwordBronze", 1, y: 1, isProtected: true, maximum: 1);
        var hotbar = Put(world.Player, "Torch", 1, x: 0, y: 0, maximum: 1);
        world.Player.Blocked.AddRange(Enumerable.Range(0, 8));
        world.Add("swords", Rule(new[] { "weapons.swords" }));
        world.Add("tools", Rule(new[] { "equipment.tools" }));
        Assert.Empty(Plan(world).Moves);
        Assert.Empty(Plan(world).Remainders);
        // D065: the single-item key follows the same protection as AzuAutoStore's: a protected hotbar cell stays.
        Assert.Empty(Plan(world, only: new[] { hotbar.Handle }).Moves);
        world.Player.Blocked.Clear(); // the player's setting includes the hotbar
        Assert.Equal("tools", Assert.Single(Plan(world, only: new[] { hotbar.Handle }).Moves).DestinationId);
        Assert.Empty(Plan(world, only: new[] { equipped.Handle }).Moves);
    }

    [Fact]
    public void SingleItem_StashesOnlyThatStack()
    {
        var world = new World();
        var first = Put(world.Player, "Copper", 10, y: 1, crafter: 1);
        Put(world.Player, "Copper", 10, y: 1, crafter: 2);
        world.Add("metal", Rule(new[] { "materials.metal" }));
        var move = Assert.Single(Plan(world, only: new[] { first.Handle }).Moves);
        Assert.Equal(first.Handle, move.Handle);
    }

    [Fact]
    public void HeldStack_StaysAndIsReported()
    {
        var world = new World();
        var held = Put(world.Player, "Copper", 10, y: 1);
        world.Add("metal", Rule(new[] { "materials.metal" }));
        var plan = Plan(world, held: new[] { held.Handle });
        Assert.Empty(plan.Moves);
        Assert.Equal(CleanupReason.Held, Assert.Single(plan.Remainders).Reason);
    }

    [Fact]
    public void OtherPlayersChest_GoesThroughMultiUserChest_OnlyWhenPresentAndItsFormatIsKnown()
    {
        var world = new World();
        Put(world.Player, "Copper", 10, y: 1);
        world.Add("metal", Rule(new[] { "materials.metal" }), local: false);
        Assert.Equal(CleanupReason.OtherOwnerNoMuc, Assert.Single(Plan(world).Remainders).Reason);
        var move = Assert.Single(Plan(world, muc: true).Moves);
        Assert.Equal(CleanupRoute.MucAdd, move.Route);
        world["metal"].FormatKnown = false;
        Assert.Equal(CleanupReason.OtherOwnerFormatUnknown, Assert.Single(Plan(world, muc: true).Remainders).Reason);
        // A local chest further down the ranking still takes it.
        world.Add("crafting", Rule(new[] { "materials.crafting" }));
        Assert.Equal("crafting", Assert.Single(Plan(world, muc: true).Moves).DestinationId);
    }

    [Fact]
    public void Stackables_MergeWhoeverCraftedThem()
    {
        // D061 Q11: the crafter does not keep stackables apart.
        var world = new World();
        Put(world.Player, "Copper", 10, y: 1, crafter: 7);
        var existing = Put(world.Add("metal", Rule(new[] { "materials.metal" })), "Copper", 5, crafter: 8);
        var move = Assert.Single(Plan(world).Moves);
        Assert.Equal(existing.Handle, move.MergeHandle);
        Assert.Equal(0, move.DestinationX);
    }

    [Fact]
    public void Budget_StopsAtTheMoveBound()
    {
        var world = new World();
        world.Player.Width = 32; world.Player.Height = 20;
        for (var y = 1; y < 20; y++) for (var x = 0; x < 32; x++) Put(world.Player, "Copper", 1, x, y, crafter: y * 100 + x + 1);
        world.Add("metal", Rule(new[] { "materials.metal" }), width: 256).Height = 4;
        var plan = Plan(world);
        Assert.Equal(StashPlanner.MaximumMoves, plan.Moves.Count);
        Assert.Equal(19 * 32 - StashPlanner.MaximumMoves, plan.Remainders.Count(x => x.Reason == CleanupReason.Budget));
    }

    [Fact]
    public void Report_WordsAStashRun()
    {
        var report = CleanupReport.ForStash();
        report.Moved(20); report.Moved(5, viaMuc: true); report.Remain(CleanupReason.NoCapacity); report.Remain(CleanupReason.OtherOwnerFormatUnknown);
        Assert.Equal("Stash: stored 2 stacks (25 items), 1 via MultiUserChest · 1 left in your inventory (1 no room) · " +
            "1 skipped (another player's chest, their MultiUserChest format is unknown)", report.Summary());
        Assert.StartsWith("Cleanup: moved 0 stacks", new CleanupReport().Summary());
    }

    [Fact]
    public void Property_RandomWorldsConserveItems_RespectRulesAndProtections()
    {
        var evaluator = new ChestRuleEvaluator(); var answers = new Answers();
        var tiers = new HashSet<StashTier>();
        for (var seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            var world = RandomWorld(random);
            var totals = world.Totals();
            var protectedSlots = world.Player.Slots.Where(s => s.Protected || world.Player.Blocked.Contains(s.Y * world.Player.Width + s.X))
                .Select(s => (s.Handle, s.Quantity)).ToList();
            var muc = random.Next(2) == 0;
            var runs = 0;
            StashPlan plan;
            while ((plan = Plan(world, muc: muc)).Moves.Count != 0)
            {
            Assert.True(++runs <= 4, "stash did not settle for seed " + seed);
            foreach (var move in plan.Moves)
            {
                var destination = world[move.DestinationId];
                if (move.Tier == StashTier.Ruled)
                    Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(destination.Payload, move.Identity.Prefab, Taxonomy, answers));
                else
                {
                    Assert.Equal("", destination.Payload);
                    Assert.Contains(destination.Slots, s => s.Identity.Prefab == move.Identity.Prefab);
                }
                if (!destination.Local) Assert.Equal(CleanupRoute.MucAdd, move.Route);
                tiers.Add(move.Tier);
            }
            world.Apply(plan);
            Assert.Equal(totals, world.Totals());
            foreach (var (handle, quantity) in protectedSlots)
                Assert.Equal(quantity, world.Player.Slots.Single(s => s.Handle == handle).Quantity);
            }
        }
        Assert.Equal(2, tiers.Count);
    }

    private static readonly string[][] RuleChoices =
    {
        new[] { "materials" }, new[] { "materials.metal" }, new[] { "materials.crafting" }, new[] { "materials.wood" },
        new[] { "weapons" }, new[] { "weapons.swords" }, new[] { "food" }, new[] { "equipment.tools" }
    };
    private static readonly string[] Prefabs = { "Copper", "Wood", "SwordBronze", "Bread", "Torch" };

    private static World RandomWorld(Random random)
    {
        var world = new World();
        world.Player.Blocked.AddRange(Enumerable.Range(0, random.Next(2) == 0 ? 8 : 0));
        void Fill(World.Box box, int chance, bool player)
        {
            for (var y = 0; y < box.Height; y++)
                for (var x = 0; x < box.Width; x++)
                    if (random.Next(chance) == 0)
                    {
                        var prefab = Prefabs[random.Next(Prefabs.Length)];
                        var maximum = prefab is "SwordBronze" or "Torch" ? 1 : 20;
                        Put(box, prefab, random.Next(1, maximum + 1), x, y, crafter: random.Next(3), isProtected: player && random.Next(8) == 0, maximum: maximum);
                    }
        }
        Fill(world.Player, 2, true);
        var chests = random.Next(1, 6);
        for (var index = 0; index < chests; index++)
        {
            var payload = random.Next(3) == 0 ? "" : Rule(RuleChoices[random.Next(RuleChoices.Length)],
                random.Next(6) == 0 ? new[] { Prefabs[random.Next(Prefabs.Length)] } : null, priority: random.Next(3));
            var chest = world.Add("c" + index, payload, width: random.Next(1, 6), local: random.Next(4) != 0);
            chest.FormatKnown = random.Next(5) != 0;
            Fill(chest, 3, false);
        }
        return world;
    }
}
