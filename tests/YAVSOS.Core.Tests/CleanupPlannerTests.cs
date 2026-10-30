using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class CleanupPlannerTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static readonly ClassificationIndex Index = Build(new[] { Item("Copper"), Item("Wood"), Item("SwordBronze"), Item("Bread") },
        new ClassificationRuleSet(Taxonomy, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal", "materials.crafting" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" })
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

    private static string Rule(string[]? categories = null, string[]? always = null, string[]? never = null, int priority = 0, bool overflow = false) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            categories == null ? null : new[] { ClassificationContractFixture.Rule(categories) }, always, never, priority, overflow));

    /// <summary>Mutable test world: chests with stacks; plans are applied to it like a successful writer would.</summary>
    private sealed class World
    {
        internal sealed class Slot
        {
            public Guid Handle = Guid.NewGuid(); public int X, Y, Quantity, Maximum = 50;
            public TransferItemIdentity Identity = null!; public bool Protected, Unsupported;
        }
        internal sealed class Chest
        {
            public string Id = ""; public string Payload = ""; public int Width = 4, Height = 1;
            public bool Local = true, OptOut; public List<Slot> Slots = new(); public List<int> Blocked = new(); public double Distance;
        }
        public readonly List<Chest> Chests = new();
        public Chest Add(string id, string payload = "", int width = 4, bool local = true, bool optOut = false)
        { var chest = new Chest { Id = id, Payload = payload, Width = width, Local = local, OptOut = optOut }; Chests.Add(chest); return chest; }
        public Chest this[string id] => Chests.Single(x => x.Id == id);
        public IEnumerable<CleanupChest> Snapshot() => Chests.Select(chest => new CleanupChest(chest.Id, chest.Payload,
            new TransferInventorySnapshot(new TransferInventoryStamp("inv:" + chest.Id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"),
                chest.Width, chest.Height, chest.Slots.Select(s => new TransferStack(s.Handle, s.X, s.Y, s.Quantity, s.Maximum, s.Identity, s.Protected, mergeClass: s.Maximum > 1 && !s.Unsupported ? s.Identity.Prefab : null, transferSupported: !s.Unsupported)),
                chest.Blocked), chest.Local, chest.OptOut, chest.Distance));
        public void Apply(CleanupPlan plan)
        {
            foreach (var move in plan.Moves)
            {
                var source = Chests.Single(c => c.Slots.Any(s => s.Handle == move.Handle));
                var slot = source.Slots.Single(s => s.Handle == move.Handle);
                Assert.True(slot.Quantity >= move.Units);
                slot.Quantity -= move.Units; if (slot.Quantity == 0) source.Slots.Remove(slot);
                var destination = this[move.DestinationId];
                var occupant = destination.Slots.SingleOrDefault(s => s.X == move.DestinationX && s.Y == move.DestinationY);
                if (move.MergesPlanned) { Assert.Null(move.MergeHandle); Assert.Equal(move.Identity.Prefab, occupant!.Identity.Prefab); occupant.Quantity += move.Units; Assert.True(occupant.Quantity <= occupant.Maximum); }
                else if (move.MergeHandle == null) { Assert.Null(occupant); destination.Slots.Add(new Slot { X = move.DestinationX, Y = move.DestinationY, Quantity = move.Units, Identity = move.Identity, Maximum = slot.Maximum }); }
                else { Assert.Equal(move.MergeHandle, occupant!.Handle); Assert.Equal(move.Identity.Prefab, occupant.Identity.Prefab); occupant.Quantity += move.Units; Assert.True(occupant.Quantity <= occupant.Maximum); }
            }
        }
        public Dictionary<string, int> Totals() => Chests.SelectMany(c => c.Slots).GroupBy(s => s.Maximum > 1 ? "class:" + s.Identity.Prefab : s.Identity.Fingerprint).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));
    }

    private static World.Slot Put(World.Chest chest, string prefab, int quantity, int x = -1, long crafter = 0, bool isProtected = false, int maximum = 50)
    {
        if (x < 0) x = Enumerable.Range(0, chest.Width).First(c => chest.Slots.All(s => s.X != c));
        var slot = new World.Slot { X = x, Quantity = quantity, Identity = Identity(prefab, crafter), Protected = isProtected, Maximum = maximum };
        chest.Slots.Add(slot); return slot;
    }

    private static CleanupPlan Plan(World world, Answers? answers = null, bool muc = false, Func<string, TransferItemIdentity, bool>? azu = null,
        IEnumerable<Guid>? held = null, Func<string, int>? size = null) =>
        CleanupPlanner.Plan(new CleanupInput(Taxonomy, answers ?? new Answers(), world.Snapshot(), size, muc, azu, held));

    // F8 (D086 D16(a) for chests, B1.23): a stack the adapter couldn't capture stays where it is and holds its slot; the rest
    // of its chest still takes part, as a source and as a destination.
    [Fact]
    public void UncapturableStack_StaysAndHoldsItsSlot_TheRestOfTheChestTakesPart()
    {
        var world = new World();
        var wood = world.Add("wood", Rule(new[] { "materials.wood" }), width: 3);
        var odd = Put(wood, "Copper", 5, x: 0); odd.Unsupported = true;
        Put(wood, "Copper", 7, x: 1);
        var metal = world.Add("metal", Rule(new[] { "materials.metal" }), width: 2);
        var held = Put(metal, "Copper", 3, x: 0); held.Unsupported = true;
        var before = world.Totals();
        var plan = Plan(world);
        var move = Assert.Single(plan.Moves);
        Assert.NotEqual(odd.Handle, move.Handle);
        Assert.Equal(7, move.Units);
        Assert.Equal(1, move.DestinationX); // Not merged into, nor placed over, the uncapturable stack.
        Assert.Null(move.MergeHandle); Assert.False(move.MergesPlanned);
        Assert.Contains(plan.Remainders, x => x.Handle == odd.Handle && x.Reason == CleanupReason.Unsupported);
        world.Apply(plan);
        Assert.Contains(odd, wood.Slots); Assert.Contains(held, metal.Slots);
        Assert.Equal(before.Values.Sum(), world.Totals().Values.Sum());
    }

    [Fact]
    public void EmptyChests_DoNotUseUpTheSourceLimit()
    {
        // Runtime 2026-10-21: 65 chests in range, most empty; the 65th by id was skipped on every run.
        var world = new World();
        for (var i = 0; i < CleanupPlanner.MaximumSources + 10; i++) world.Add("a" + i.ToString("D3"), Rule(new[] { "materials.metal" }));
        Put(world.Add("z-wood", Rule(new[] { "materials.wood" })), "Copper", 5);
        var plan = Plan(world);
        Assert.Equal(0, plan.SkippedChests);
        Assert.Equal("z-wood", Assert.Single(plan.Moves).SourceId);
        // Over the limit with items in every chest: the rest are reported, not hidden, and the reason says so.
        foreach (var chest in world.Chests.Where(x => x.Id != "z-wood")) Put(chest, "Copper", 1);
        Assert.Equal(11, Plan(world).SkippedChests);
        Assert.DoesNotContain("run again", CleanupReport.Describe(CleanupReason.ChestLimit)); // It names where to stand instead (D086 D9).
    }

    // D086 D9: the nearest sources give up items; the farthest are left and reported, and the report counts chests in range.
    [Fact]
    public void SourceLimit_KeepsTheNearestSources_AndTheReportSaysHowManyChestsWereHandled()
    {
        var world = new World();
        world.Add("metal", Rule(new[] { "materials.metal" }), width: 4).Distance = 1;
        for (var i = 0; i < CleanupPlanner.MaximumSources + 2; i++)
        {
            var chest = world.Add("w" + i.ToString("D3"), Rule(new[] { "materials.wood" }));
            chest.Distance = 100 - i; // Higher IDs are nearer.
            Put(chest, "Copper", 1);
        }
        var plan = Plan(world);
        Assert.Equal(2, plan.SkippedChests);
        Assert.DoesNotContain(plan.Moves, x => x.SourceId is "w000" or "w001"); // The two farthest.
        Assert.Contains("closer", CleanupReport.Describe(CleanupReason.ChestLimit));
        var report = new CleanupReport(); report.Range(300, 256);
        Assert.EndsWith("300 chests in range; only the nearest 256 could be handled", report.Summary());
        report.Range(12, 12); Assert.DoesNotContain("in range", report.Summary());
    }

    [Fact]
    public void RejectedItem_GoesToTheHighestPriorityThenMostSpecificChest()
    {
        var world = new World();
        Put(world.Add("a-wood", Rule(new[] { "materials.wood" })), "Copper", 20);
        world.Add("b-materials", Rule(new[] { "materials" }));
        world.Add("c-metal", Rule(new[] { "materials.metal" }));
        var plan = Plan(world);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(("a-wood", "c-metal", 20, CleanupRoute.Direct, CleanupKind.Rejected), (move.SourceId, move.DestinationId, move.Units, move.Route, move.Kind));
        // D061: priority first.
        world["b-materials"].Payload = Rule(new[] { "materials" }, priority: 1);
        Assert.Equal("b-materials", Assert.Single(Plan(world).Moves).DestinationId);
        Assert.Empty(plan.Remainders);
        Assert.Equal(Taxonomy.Fingerprint + ":1", plan.PolicyStamp);
    }

    [Fact]
    public void ValidPlacementsStay_AndEquallySpecificChestsNeverTrade()
    {
        var world = new World();
        Put(world.Add("wood", Rule(new[] { "materials.wood" })), "Wood", 10);
        Put(world.Add("metal", Rule(new[] { "materials.metal" })), "Copper", 5);
        Put(world.Add("crafting", Rule(new[] { "materials.crafting" })), "Copper", 5);
        var plan = Plan(world, size: id => id == "materials.metal" ? 1 : 500);
        Assert.Empty(plan.Moves); Assert.Empty(plan.Remainders);
    }

    [Theory]
    [InlineData(false, 0, 0, true)]   // Materials -> Metals: more specific at equal priority
    [InlineData(true, 0, 1, true)]    // Materials priority 0 -> Materials priority 1 (D057 O3)
    [InlineData(true, 0, 0, false)]   // equal Materials chests: no trade
    [InlineData(true, 1, 0, false)]   // never towards lower priority
    [InlineData(false, 1, 0, false)]  // D061: a priority-1 Materials chest keeps Copper from a priority-0 Metals chest
    public void Resort_MovesOnlyToAStrictlyBetterRankedChest(bool targetIsMaterials, int sourcePriority, int targetPriority, bool moves)
    {
        var world = new World();
        Put(world.Add("a-source", Rule(new[] { "materials" }, priority: sourcePriority)), "Copper", 7);
        world.Add("b-target", Rule(targetIsMaterials ? new[] { "materials" } : new[] { "materials.metal" }, priority: targetPriority));
        var plan = Plan(world);
        Assert.Empty(plan.Remainders);
        if (!moves) { Assert.Empty(plan.Moves); return; }
        var move = Assert.Single(plan.Moves);
        Assert.Equal(("b-target", CleanupKind.Resort, 7), (move.DestinationId, move.Kind, move.Units));
    }

    [Fact]
    public void Resort_OverflowChestGivesItemsBackToAnOrdinaryChest()
    {
        var world = new World();
        Put(world.Add("a-overflow", Rule(new[] { "materials" }, overflow: true)), "Copper", 3);
        world.Add("b-ordinary", Rule(new[] { "materials" }));
        Assert.Equal("b-ordinary", Assert.Single(Plan(world).Moves).DestinationId);
    }

    // D092 (amends D057): cleanup follows the stash order, so Always marks where an item is accepted, not where it is kept.
    [Fact]
    public void AlwaysListedItem_MovesOnlyToAStrictlyBetterRankedChest()
    {
        var world = new World();
        Put(world.Add("a-food", Rule(new[] { "food" }, always: new[] { "Copper" })), "Copper", 3);
        world.Add("b-metal", Rule(new[] { "materials.metal" }));
        Assert.Empty(Plan(world).Moves); // Same priority: Always outranks a category, so it stays.
        world.Add("c-always", Rule(always: new[] { "Copper" }));
        Assert.Empty(Plan(world).Moves); // Another Always chest of the same priority is no better.
        world["b-metal"].Payload = Rule(new[] { "materials.metal" }, priority: 9);
        var move = Assert.Single(Plan(world).Moves); // Higher priority wins, as in the stash.
        Assert.Equal(("a-food", "b-metal", 3, CleanupKind.Resort), (move.SourceId, move.DestinationId, move.Units, move.Kind));
    }

    // B3.19 (D092): the runner's write-time recheck agrees with the plan, so an Always item's re-sort is not refused at the write.
    [Fact]
    public void ResortStillBetter_MatchesThePlan_ForAlwaysAndUnresolvedItems()
    {
        ChestRules Read(string payload) => new ChestRuleEvaluator().Read(payload, Taxonomy).Rules!;
        var copper = new Answers().Resolve("Copper");
        var always = Read(Rule(new[] { "food" }, always: new[] { "Copper" }));
        Assert.True(CleanupPlanner.ResortStillBetter(copper, "Copper", always, Read(Rule(new[] { "materials.metal" }, priority: 9)), null, Taxonomy));
        Assert.False(CleanupPlanner.ResortStillBetter(copper, "Copper", always, Read(Rule(new[] { "materials.metal" })), null, Taxonomy)); // Always wins a tie
        var mod = Read(Rule(always: new[] { "ModThing" }));
        Assert.True(CleanupPlanner.ResortStillBetter(null, "ModThing", mod, Read(Rule(always: new[] { "ModThing" }, priority: 5)), null, Taxonomy));
        Assert.False(CleanupPlanner.ResortStillBetter(null, "ModThing", mod, Read(Rule(new[] { "materials" }, priority: 5)), null, Taxonomy)); // no answer: Always only
        Assert.False(CleanupPlanner.ResortStillBetter(null, "ModThing", mod, Read(Rule(always: new[] { "ModThing" })), null, Taxonomy));
    }

    // B4.14: an item the answers don't know goes to a chest that lists it under Always, and on to a higher-priority one (D092).
    [Fact]
    public void UnresolvedItem_MovesToAnAlwaysChest_AndUpThePriorities()
    {
        var world = new World();
        Put(world.Add("a-cart"), "ModThing", 3);
        world.Add("b-always", Rule(always: new[] { "ModThing" }));
        var move = Assert.Single(Plan(world).Moves);
        Assert.Equal(("a-cart", "b-always", 3, CleanupKind.Unruled), (move.SourceId, move.DestinationId, move.Units, move.Kind));
        world.Apply(Plan(world));
        Assert.Empty(Plan(world).Moves);
        world.Add("c-always", Rule(always: new[] { "ModThing" }));
        Assert.Empty(Plan(world).Moves); // An equal Always chest is no better: it stays.
        world["c-always"].Payload = Rule(always: new[] { "ModThing" }, priority: 5);
        move = Assert.Single(Plan(world).Moves); // A higher-priority Always chest: it moves, as the stash would place it (D092).
        Assert.Equal(("b-always", "c-always", 3, CleanupKind.Resort), (move.SourceId, move.DestinationId, move.Units, move.Kind));
        // Without an Always chest, a rejected unresolved item is reported as having no destination, as before.
        var lone = new World(); Put(lone.Add("a-food", Rule(new[] { "food" })), "ModThing", 1);
        Assert.Equal(CleanupReason.NoDestination, Assert.Single(Plan(lone).Remainders).Reason);
    }

    [Fact]
    public void UnruledContainer_GivesUpOnlyWhatARuledChestTakes_AndNeverReceives()
    {
        var world = new World();
        var cart = world.Add("a-cart");
        Put(cart, "Copper", 4); Put(cart, "Bread", 2);
        world.Add("b-metal", Rule(new[] { "materials.metal" }));
        Put(world.Add("c-wood", Rule(new[] { "materials.wood" })), "SwordBronze", 1);
        world.Add("d-unruled");
        var plan = Plan(world);
        var move = Assert.Single(plan.Moves);
        Assert.Equal(("a-cart", "b-metal", CleanupKind.Unruled), (move.SourceId, move.DestinationId, move.Kind));
        // Bread stays silently in the cart; the sword has no ruled destination and is reported.
        var rest = Assert.Single(plan.Remainders);
        Assert.Equal(("c-wood", "SwordBronze", CleanupReason.NoDestination), (rest.SourceId, rest.Identity.Prefab, rest.Reason));
    }

    [Fact]
    public void OptOut_KeepsEverythingInPlace()
    {
        var world = new World();
        Put(world.Add("a-wood", Rule(new[] { "materials.wood" }), optOut: true), "Copper", 4);
        Put(world.Add("b-cart", optOut: true), "Copper", 4);
        world.Add("c-metal", Rule(new[] { "materials.metal" }));
        var plan = Plan(world);
        Assert.Empty(plan.Moves); Assert.Empty(plan.Remainders);
    }

    [Fact]
    public void PendingCategoriesRefuseTheRun_AndUnreadableRulesNeverGiveUpItems()
    {
        var world = new World();
        Put(world.Add("a-wood", Rule(new[] { "materials.wood" })), "Copper", 4);
        world.Add("b-metal", Rule(new[] { "materials.metal" }));
        var pending = Plan(world, new Answers { CategoriesReady = false });
        Assert.True(pending.CategoriesPending); Assert.Empty(pending.Moves); Assert.Empty(pending.Remainders);

        var unreadable = new World();
        Put(unreadable.Add("a-future", "{\"future\":1}"), "Copper", 4);
        unreadable.Add("b-metal", Rule(new[] { "materials.metal" }));
        Assert.Empty(Plan(unreadable).Moves);
    }

    [Fact]
    public void Capacity_MergesPartialsWhoeverCraftedThem_SkipsProtectedCells_AndSplits()
    {
        var world = new World();
        var source = world.Add("a-wood", Rule(new[] { "materials.wood" }));
        var copper = Put(source, "Copper", 30, crafter: 7);
        var metal = world.Add("b-metal", Rule(new[] { "materials.metal" }), width: 3);
        var partial = Put(metal, "Copper", 45, x: 0, crafter: 7);
        var other = Put(metal, "Copper", 45, x: 1, crafter: 8); // another crafter still stacks (D061 Q11)
        metal.Blocked.Add(2);
        world.Add("c-materials", Rule(new[] { "materials" }));
        var plan = Plan(world);
        Assert.Equal(3, plan.Moves.Count);
        Assert.Equal(("b-metal", 5, (Guid?)partial.Handle), (plan.Moves[0].DestinationId, plan.Moves[0].Units, plan.Moves[0].MergeHandle));
        Assert.Equal(("b-metal", 5, (Guid?)other.Handle), (plan.Moves[1].DestinationId, plan.Moves[1].Units, plan.Moves[1].MergeHandle));
        Assert.Equal(("c-materials", 20, 0, (Guid?)null), (plan.Moves[2].DestinationId, plan.Moves[2].Units, plan.Moves[2].DestinationX, plan.Moves[2].MergeHandle));
        Assert.All(plan.Moves, m => Assert.Equal(copper.Handle, m.Handle));
        Assert.Empty(plan.Remainders);
    }

    [Fact]
    public void Capacity_NoCellIsUsedTwice_AndTheRestIsReported()
    {
        var world = new World();
        var source = world.Add("a-wood", Rule(new[] { "materials.wood" }));
        // Full stacks: a planned top-up (D070) cannot apply, so the second stack has no cell.
        Put(source, "Copper", 10, crafter: 1, maximum: 10); Put(source, "Copper", 10, crafter: 2, maximum: 10);
        world.Add("b-metal", Rule(new[] { "materials.metal" }), width: 1);
        var plan = Plan(world);
        var move = Assert.Single(plan.Moves);
        var rest = Assert.Single(plan.Remainders);
        Assert.Equal((CleanupReason.NoCapacity, 10), (rest.Reason, rest.Units));
        Assert.NotEqual(move.Handle, rest.Handle);
    }

    [Fact]
    public void LaterStack_TopsUpAStackThisRunCreated()
    {
        var world = new World();
        var source = world.Add("a-wood", Rule(new[] { "materials.wood" }));
        Put(source, "Copper", 10, crafter: 1); Put(source, "Copper", 30, crafter: 2);
        world.Add("b-metal", Rule(new[] { "materials.metal" }), width: 1);
        var plan = Plan(world);
        Assert.Equal(new[] { (10, false), (30, true) }, plan.Moves.Select(m => (m.Units, m.MergesPlanned)));
        Assert.Empty(plan.Remainders);
    }

    [Fact]
    public void AzuExclusions_AreRespectedAndNamed()
    {
        var world = new World();
        Put(world.Add("a-wood", Rule(new[] { "materials.wood" })), "SwordBronze", 1);
        world.Add("b-swords", Rule(new[] { "weapons.swords" }));
        var blocked = Plan(world, azu: (_, _) => false);
        Assert.Empty(blocked.Moves);
        var rest = Assert.Single(blocked.Remainders);
        Assert.Equal((CleanupReason.AzuExcluded, "b-swords"), (rest.Reason, rest.DestinationId));

        world.Add("c-weapons", Rule(new[] { "weapons" }));
        var routed = Plan(world, azu: (chest, _) => chest != "b-swords");
        Assert.Equal("c-weapons", Assert.Single(routed.Moves).DestinationId);
        Assert.Empty(routed.Remainders);
    }

    // A valid placement whose only better chest is excluded by Azu stays without a report. (Azu's pause is not read, D097.)
    [Fact]
    public void Resort_WhoseOnlyBetterChestIsAzuExcluded_StaysQuietly()
    {
        var resort = new World();
        Put(resort.Add("a-metal", Rule(new[] { "materials.metal" })), "Copper", 4);
        resort.Add("b-metal", Rule(new[] { "materials.metal" }, priority: 9));
        var quiet = Plan(resort, azu: (id, _) => id != "b-metal");
        Assert.Empty(quiet.Moves); Assert.Empty(quiet.Remainders);
        Assert.Equal("b-metal", Assert.Single(Plan(resort).Moves).DestinationId);
    }

    [Fact]
    public void Routes_FollowOwnershipAndMultiUserChestPresence()
    {
        var world = new World();
        Put(world.Add("a-wood", Rule(new[] { "materials.wood" })), "Copper", 4);
        world.Add("b-metal", Rule(new[] { "materials.metal" }), local: false);
        Assert.Equal(CleanupReason.OtherOwnerNoMuc, Assert.Single(Plan(world).Remainders).Reason);
        Assert.Equal(CleanupRoute.MucAdd, Assert.Single(Plan(world, muc: true).Moves).Route);

        var remoteSource = new World();
        Put(remoteSource.Add("a-wood", Rule(new[] { "materials.wood" }), local: false), "Copper", 4);
        remoteSource.Add("b-metal", Rule(new[] { "materials.metal" }));
        Assert.Equal(CleanupRoute.MucRemove, Assert.Single(Plan(remoteSource, muc: true).Moves).Route);
        Assert.Equal(CleanupReason.OtherOwnerNoMuc, Assert.Single(Plan(remoteSource).Remainders).Reason);

        remoteSource["b-metal"].Local = false;
        Assert.Equal(CleanupReason.BothRemote, Assert.Single(Plan(remoteSource, muc: true).Remainders).Reason);
    }

    [Fact]
    public void MultiUserChest_OneRequestPerOtherPlayersChest_ThenFallsBack()
    {
        var world = new World();
        var source = world.Add("a-wood", Rule(new[] { "materials.wood" }));
        Put(source, "Copper", 4, crafter: 1, maximum: 4); Put(source, "Copper", 4, crafter: 2, maximum: 4); Put(source, "Copper", 4, crafter: 3, maximum: 4);
        world.Add("b-metal", Rule(new[] { "materials.metal" }), local: false);
        world.Add("c-materials", Rule(new[] { "materials" }), width: 1);
        var plan = Plan(world, muc: true);
        Assert.Equal(new[] { ("b-metal", CleanupRoute.MucAdd), ("c-materials", CleanupRoute.Direct) },
            plan.Moves.Select(m => (m.DestinationId, m.Route)));
        Assert.Equal(CleanupReason.MucBusy, Assert.Single(plan.Remainders).Reason);
    }

    [Fact]
    public void ProtectedUnsupportedAndHeldStacks_AreReportedNotMoved()
    {
        var world = new World();
        var source = world.Add("a-wood", Rule(new[] { "materials.wood" }));
        Put(source, "Copper", 4, crafter: 1, isProtected: true);
        var held = Put(source, "Copper", 4, crafter: 2);
        world.Add("b-metal", Rule(new[] { "materials.metal" }));
        var plan = Plan(world, held: new[] { held.Handle });
        Assert.Empty(plan.Moves);
        Assert.Equal(new[] { CleanupReason.Unsupported, CleanupReason.Held }, plan.Remainders.Select(r => r.Reason));
    }

    [Fact]
    public void Plan_IsDeterministicUnderShuffledInput()
    {
        var world = RandomWorld(new Random(5));
        var expected = Describe(Plan(world));
        for (var seed = 0; seed < 5; seed++)
        {
            var shuffled = new World(); shuffled.Chests.AddRange(world.Chests.OrderBy(_ => new Random(seed).Next()));
            Assert.Equal(expected, Describe(CleanupPlanner.Plan(new CleanupInput(Taxonomy, new Answers(),
                shuffled.Snapshot().Reverse()))));
        }
    }

    [Fact]
    public void Bounds_MovesBeyondTheBudgetAreReported()
    {
        var world = new World();
        for (var chest = 0; chest < 20; chest++)
        {
            var source = world.Add("s" + chest.ToString("D2"), Rule(new[] { "materials.wood" }), width: 30);
            for (var cell = 0; cell < 30; cell++) Put(source, "Copper", 1, x: cell, crafter: chest * 100 + cell + 1);
        }
        world.Add("z-metal", Rule(new[] { "materials.metal" }), width: 256);
        world.Chests[^1].Height = 4;
        var plan = Plan(world);
        Assert.Equal(CleanupPlanner.MaximumMoves, plan.Moves.Count);
        Assert.Equal(600 - CleanupPlanner.MaximumMoves, plan.Remainders.Count(r => r.Reason == CleanupReason.Budget));
    }

    [Fact]
    public void Property_RandomWorldsConserveItems_RespectRules_AndSettle()
    {
        var evaluator = new ChestRuleEvaluator(); var answers = new Answers();
        var kinds = new HashSet<CleanupKind>(); var multiRun = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var world = RandomWorld(random);
            var totals = world.Totals();
            var runs = 0;
            while (true)
            {
                var plan = Plan(world);
                if (plan.Moves.Count == 0) break;
                Assert.True(++runs <= 6, "cleanup did not settle for seed " + seed);
                foreach (var move in plan.Moves)
                {
                    var destination = world[move.DestinationId];
                    Assert.NotEqual("", destination.Payload); // never into an unruled container
                    Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(destination.Payload, move.Identity.Prefab, Taxonomy, answers));
                    var source = world.Chests.Single(c => c.Slots.Any(s => s.Handle == move.Handle));
                    Assert.False(source.OptOut);
                    if (move.Kind == CleanupKind.Resort)
                        Assert.Equal(ChestRuleDecision.Accept, evaluator.Evaluate(source.Payload, move.Identity.Prefab, Taxonomy, answers));
                }
                foreach (var move in plan.Moves) kinds.Add(move.Kind);
                world.Apply(plan);
                Assert.Equal(totals, world.Totals());
            }
            if (runs > 1) multiRun++;
        }
        // The worlds exercise every kind of move, and some need more than one run (freed cells, new stacks).
        Assert.Equal(3, kinds.Count);
        Assert.True(multiRun > 0);
    }

    private static readonly string[][] RuleChoices =
    {
        new[] { "materials" }, new[] { "materials.metal" }, new[] { "materials.crafting" }, new[] { "materials.wood" },
        new[] { "weapons" }, new[] { "weapons.swords" }, new[] { "food" }
    };
    private static readonly string[] Prefabs = { "Copper", "Wood", "SwordBronze", "Bread" };

    private static World RandomWorld(Random random)
    {
        var world = new World();
        var chests = random.Next(2, 7);
        for (var index = 0; index < chests; index++)
        {
            var payload = random.Next(4) == 0 ? "" : Rule(RuleChoices[random.Next(RuleChoices.Length)],
                random.Next(6) == 0 ? new[] { Prefabs[random.Next(Prefabs.Length)] } : null, priority: random.Next(3), overflow: random.Next(8) == 0);
            var chest = world.Add("c" + index, payload, width: random.Next(1, 5), optOut: random.Next(10) == 0);
            for (var cell = 0; cell < chest.Width; cell++)
                if (random.Next(3) != 0)
                {
                    var prefab = Prefabs[random.Next(Prefabs.Length)];
                    var maximum = prefab == "SwordBronze" ? 1 : 20;
                    Put(chest, prefab, random.Next(1, maximum + 1), x: cell, crafter: random.Next(3), maximum: maximum);
                }
        }
        return world;
    }

    private static string Describe(CleanupPlan plan) => string.Join("|", plan.Moves.Select(m => $"{m.SourceId}>{m.DestinationId}:{m.Identity.Fingerprint}:{m.Units}@{m.DestinationX},{m.DestinationY}:{m.Route}"))
        + "#" + string.Join("|", plan.Remainders.Select(r => $"{r.SourceId}:{r.Identity.Fingerprint}:{r.Units}:{r.Reason}"));
}
