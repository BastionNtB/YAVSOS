using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>D070: the store key and "Clean up nearby" are one placement decision; only the source differs.</summary>
public sealed class StoreOrderTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static readonly string[] Prefabs = { "Copper", "Wood", "SwordBronze", "Bread", "Torch", "Tin" };
    private static readonly ClassificationIndex Index = Build(Prefabs.Select(x => Item(x)),
        new ClassificationRuleSet(Taxonomy, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal", "materials.crafting" }),
            new ClassificationOverride("exact", "Tin", OverrideOperation.Replace, new[] { "materials.metal" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" }),
            new ClassificationOverride("exact", "Torch", OverrideOperation.Replace, new[] { "equipment.tools" })
        }));
    private static readonly string[] Categories =
        { "materials", "materials.metal", "materials.wood", "materials.crafting", "weapons", "weapons.swords", "food", "food.health", "equipment", "equipment.tools" };

    private sealed class Answers : ICategoryAnswers
    {
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) => Index.Items.TryGetValue(prefab, out var row) ? row : null;
    }

    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());

    private static string Rule(string[]? categories, string[]? always = null, string[]? never = null, int priority = 0) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            categories == null ? null : new[] { ClassificationContractFixture.Rule(categories) }, always, never, priority));

    /// <summary>The player and chests; plans of either kind are applied like a successful writer would.</summary>
    private sealed class World
    {
        internal sealed class Slot { public Guid Handle = Guid.NewGuid(); public int X, Y, Quantity, Maximum = 20; public TransferItemIdentity Identity = null!; }
        internal sealed class Box
        {
            public string Id = ""; public string Payload = ""; public int Width = 4, Height = 1; public List<Slot> Slots = new();
            public TransferInventorySnapshot Snapshot() => new(new TransferInventoryStamp("inv:" + Id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"),
                Width, Height, Slots.Select(s => new TransferStack(s.Handle, s.X, s.Y, s.Quantity, s.Maximum, s.Identity, false, mergeClass: s.Identity.Prefab)), Array.Empty<int>());
            public void Put(string prefab, int quantity)
            {
                var free = Enumerable.Range(0, Width * Height).First(c => Slots.All(s => s.Y * Width + s.X != c));
                Slots.Add(new Slot { X = free % Width, Y = free / Width, Quantity = quantity, Identity = Identity(prefab) });
            }
        }
        public readonly Box Player = new() { Id = "player", Width = 8, Height = 2 };
        public readonly List<Box> Chests = new();
        public Box this[string id] => Chests.Single(x => x.Id == id);
        public StashInput Stash(bool azu = false, Func<string, TransferItemIdentity, bool>? azuAllows = null) =>
            new(Taxonomy, new Answers(), Player.Snapshot(), Chests.Select(x => new StashChest(x.Id, x.Payload, x.Snapshot(), true)),
                azuStores: azu, azuAllows: azuAllows);
        public CleanupInput Cleanup(Func<string, TransferItemIdentity, bool>? azuAllows = null) =>
            new(Taxonomy, new Answers(), Chests.Select(x => new CleanupChest(x.Id, x.Payload, x.Snapshot(), true)), azuAllows: azuAllows);
        private void Move(Box source, Guid handle, string destinationId, int units, int x, int y, Guid? merge, TransferItemIdentity identity)
        {
            var slot = source.Slots.Single(s => s.Handle == handle);
            slot.Quantity -= units; if (slot.Quantity == 0) source.Slots.Remove(slot);
            var destination = this[destinationId];
            var occupant = destination.Slots.SingleOrDefault(s => s.X == x && s.Y == y);
            if (merge != null) Assert.Equal(merge, occupant!.Handle);
            // A planned merge (no handle) tops up the stack an earlier move of the run created there.
            if (occupant == null) destination.Slots.Add(new Slot { X = x, Y = y, Quantity = units, Identity = identity });
            else { Assert.Equal(identity.Prefab, occupant.Identity.Prefab); occupant.Quantity += units; Assert.True(occupant.Quantity <= occupant.Maximum); }
        }
        public void Apply(StashPlan plan) { foreach (var m in plan.Moves) Move(Player, m.Handle, m.DestinationId, m.Units, m.DestinationX, m.DestinationY, m.MergeHandle, m.Identity); }
        public void Apply(CleanupPlan plan)
        { foreach (var m in plan.Moves) Move(this[m.SourceId], m.Handle, m.DestinationId, m.Units, m.DestinationX, m.DestinationY, m.MergeHandle, m.Identity); }
    }

    private static World Random(int seed)
    {
        var random = new Random(seed); var world = new World();
        string Pick(string[] from) => from[random.Next(from.Length)];
        for (var i = 0; i < random.Next(1, 7); i++)
        {
            var chest = new World.Box { Id = "c" + i, Width = random.Next(1, 4) };
            _ = random.Next(8); // was the Azu pause flag (removed, D097); kept so the seeded worlds stay the same
            chest.Payload = random.Next(4) switch
            {
                0 => "",
                1 => Rule(null, new[] { Pick(Prefabs) }, priority: random.Next(-2, 3)),
                _ => Rule(new[] { Pick(Categories) }, never: random.Next(3) == 0 ? new[] { Pick(Prefabs) } : null, priority: random.Next(-2, 3))
            };
            world.Chests.Add(chest);
        }
        foreach (var chest in world.Chests.Where(x => x.Payload.Length == 0 || random.Next(3) == 0))
            for (var i = random.Next(0, chest.Width); i > 0; i--) chest.Put(Pick(Prefabs), random.Next(1, 21));
        for (var i = random.Next(1, 12); i > 0; i--) world.Player.Put(Pick(Prefabs), random.Next(1, 21));
        return world;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StashThenCleanup_FindsNothingToMove(bool azu)
    {
        // Seeds whose chests are not settled to begin with say nothing about the stash; settle them first.
        var checkedWorlds = 0;
        for (var seed = 0; seed < 600; seed++)
        {
            var world = Random(seed);
            Func<string, TransferItemIdentity, bool>? allows = azu ? (id, item) => (id.GetHashCode() ^ item.Prefab.Length) % 5 != 0 : null;
            for (var settle = 0; settle < 8; settle++)
            {
                var before = CleanupPlanner.Plan(world.Cleanup(allows));
                if (before.Moves.Count == 0) break;
                world.Apply(before);
            }
            if (CleanupPlanner.Plan(world.Cleanup(allows)).Moves.Count != 0) continue;
            var stash = StashPlanner.Plan(world.Stash(azu, allows));
            world.Apply(stash);
            var after = CleanupPlanner.Plan(world.Cleanup(allows));
            Assert.True(after.Moves.Count == 0, $"seed {seed}: cleanup would move " + Describe(Random(seed)) + " / stash " +
                string.Join(", ", stash.Moves.Select(x => $"{x.Identity.Prefab} x{x.Units} ->{x.DestinationId}")) + " / " +
                string.Join(", ", after.Moves.Select(x => $"{x.Identity.Prefab} x{x.Units} {x.SourceId}->{x.DestinationId} ({x.Kind})")));
            checkedWorlds++;
        }
        Assert.True(checkedWorlds > 400, checkedWorlds + " worlds checked");
    }

    private static string Describe(World world) => string.Join("; ", world.Chests.Select(c =>
    {
        var rules = c.Payload.Length == 0 ? null : new ChestRuleEvaluator().Read(c.Payload, Taxonomy).Rules;
        return $"{c.Id} w{c.Width} " + (rules == null ? "unruled" :
            $"[{string.Join(",", rules.Acceptance.Rules.SelectMany(r => r.Categories))} +{string.Join(",", rules.Includes)} -{string.Join(",", rules.Excludes)} p{rules.Priority}{(rules.Overflow ? " overflow" : "")}]") +
            " holds " + string.Join(",", c.Slots.Select(s => s.Identity.Prefab + "x" + s.Quantity));
    })) + "; player " + string.Join(",", world.Player.Slots.Select(s => s.Identity.Prefab + "x" + s.Quantity));

    [Fact]
    public void Cleanup_SettlesInOnePress()
    {
        // Runtime 2026-10-23: a second press moved 20 Copper into a stack the first press had created.
        var checkedWorlds = 0; var failures = new List<string>();
        for (var seed = 0; seed < 600; seed++)
        {
            var world = Random(seed);
            foreach (var stack in world.Player.Slots.ToList()) { var chest = world.Chests[seed % world.Chests.Count]; if (chest.Slots.Count < chest.Width) chest.Put(stack.Identity.Prefab, stack.Quantity); }
            var first = CleanupPlanner.Plan(world.Cleanup());
            if (first.Moves.Count == 0) continue;
            world.Apply(first);
            var second = CleanupPlanner.Plan(world.Cleanup());
            if (second.Moves.Count != 0) { failures.Add(seed + ": " + string.Join(", ", second.Moves.Select(x => $"{x.Identity.Prefab} x{x.Units} {x.SourceId}->{x.DestinationId} ({x.Kind})"))); continue; }
            checkedWorlds++;
        }
        // Known limit (P6-22, later): one press is one pass over the sources in id order, so room a later source frees
        // (or a swap between two full chests) is used by the next press. Merges into new stacks no longer need one.
        // D092 (Always items re-sort too) and D097 (Azu's pause no longer closes chests) allow more moves, so more worlds meet the
        // same limit: 8 of 227 at 2026-10-27, each checked to be room a later source freed in the same press. Allowance 2 % -> 4 %.
        Assert.True(failures.Count * 25 <= checkedWorlds + failures.Count, failures.Count + " of " + (checkedWorlds + failures.Count) + ": " + string.Join(" | ", failures.Take(6)));
        Assert.True(checkedWorlds > 150, checkedWorlds + " worlds checked");
    }

    [Fact]
    public void LaterStack_TopsUpAStackTheRunCreated_AndTheWriterFindsItsHandle()
    {
        var world = new World();
        world.Chests.Add(new World.Box { Id = "a-metal", Payload = Rule(new[] { "materials.metal" }), Width = 2 });
        world.Chests.Add(new World.Box { Id = "b-materials", Payload = Rule(new[] { "materials" }) });
        world["a-metal"].Put("Tin", 20);
        world.Player.Put("Copper", 14); world.Player.Put("Copper", 17);
        var plan = StashPlanner.Plan(world.Stash());
        Assert.Equal(new[] { ("a-metal", 14, false), ("a-metal", 6, true), ("b-materials", 11, false) },
            plan.Moves.Select(x => (x.DestinationId, x.Units, x.MergesPlanned)));
        var topUp = plan.Moves[1];
        Assert.Null(topUp.MergeHandle);

        // The writer's fresh captures: the first move landed, so the cell holds Copper; anything else there refuses.
        var player = world.Player.Snapshot();
        var landed = new World.Box { Id = "a-metal", Width = 2 }; landed.Put("Tin", 20); landed.Put("Copper", 14);
        Assert.True(topUp.TryMergeTarget(player, landed.Snapshot(), out var merge));
        Assert.Equal(landed.Slots[1].Handle, merge);
        var other = new World.Box { Id = "a-metal", Width = 2 }; other.Put("Tin", 20); other.Put("Wood", 3);
        Assert.False(topUp.TryMergeTarget(player, other.Snapshot(), out _));
        var empty = new World.Box { Id = "a-metal", Width = 2 }; empty.Put("Tin", 20);
        Assert.False(topUp.TryMergeTarget(player, empty.Snapshot(), out _));
        Assert.True(plan.Moves[0].TryMergeTarget(player, empty.Snapshot(), out var none));
        Assert.Null(none);
    }

    [Fact]
    public void For_IsTheStashPlannersRuledOrder()
    {
        var world = new World();
        world.Chests.Add(new World.Box { Id = "a-materials", Payload = Rule(new[] { "materials" }) });
        world.Chests.Add(new World.Box { Id = "b-metal", Payload = Rule(new[] { "materials.metal" }) });
        world.Chests.Add(new World.Box { Id = "c-wood", Payload = Rule(new[] { "materials.wood" }) });
        world.Chests.Add(new World.Box { Id = "d-none" });
        world.Chests.Add(new World.Box { Id = "e-urgent", Payload = Rule(new[] { "materials" }, priority: 2) });
        var order = new StoreOrder(Taxonomy, new Answers(), world.Chests.Select(x => new KeyValuePair<string, string>(x.Id, x.Payload)));
        Assert.Equal(4, order.Count);
        Assert.Equal(new[] { "e-urgent", "b-metal", "a-materials" }, order.For("Copper"));
        Assert.Equal(new[] { "e-urgent", "c-wood", "a-materials" }, order.For("Wood"));
        Assert.Empty(order.For("Bread"));
        Assert.Empty(order.For("Unknown"));
        world.Player.Put("Copper", 5);
        Assert.Equal("e-urgent", Assert.Single(StashPlanner.Plan(world.Stash()).Moves).DestinationId);
    }

    [Fact]
    public void AzuStores_SkipsHolders_AndHonoursAzuExclusions()
    {
        var world = new World();
        world.Chests.Add(new World.Box { Id = "a-metal", Payload = Rule(new[] { "materials.metal" }) });
        world.Chests.Add(new World.Box { Id = "b-materials", Payload = Rule(new[] { "materials" }) });
        var holder = new World.Box { Id = "c-holder" }; holder.Put("Bread", 1); world.Chests.Add(holder);
        world.Player.Put("Copper", 5); world.Player.Put("Bread", 3);
        var own = StashPlanner.Plan(world.Stash());
        Assert.Equal(new[] { ("Copper", "a-metal"), ("Bread", "c-holder") }, own.Moves.Select(x => (x.Identity.Prefab, x.DestinationId)));

        // With AzuAutoStore, Bread is Azu's own business, and Azu's exclusions skip a chest the way cleanup does.
        var azu = StashPlanner.Plan(world.Stash(azu: true, azuAllows: (id, item) => id != "a-metal"));
        Assert.Equal(new[] { ("Copper", "b-materials") }, azu.Moves.Select(x => (x.Identity.Prefab, x.DestinationId)));
        var input = world.Stash(azu: true, azuAllows: (id, item) => id != "a-metal" && id != "b-materials");
        Assert.Empty(StashPlanner.Plan(input).Moves);
        var copper = input.Player.Stacks.First(x => x.Identity.Prefab == "Copper").Handle;
        var bread = input.Player.Stacks.First(x => x.Identity.Prefab == "Bread").Handle;
        var blocked = PlacementExplainer.Stash(input, copper, detail: false);
        Assert.Equal(PlacementOutcome.Blocked, blocked.Outcome);
        Assert.Equal("No ruled chest can take Copper (AzuAutoStore's config excludes it), so AzuAutoStore's own settings decide where the store key puts it.",
            ExplanationView.Sentence(blocked, false, azuStores: true));
        Assert.Equal("No chest rule nearby takes Bread, so AzuAutoStore's own settings decide where the store key puts it.",
            ExplanationView.Sentence(PlacementExplainer.Stash(input, bread, detail: false), false, azuStores: true));
        var goes = world.Stash(azu: true);
        Assert.Equal("The store key would put Copper in the a-metal.",
            ExplanationView.Sentence(PlacementExplainer.Stash(goes, goes.Player.Stacks.First(x => x.Identity.Prefab == "Copper").Handle, detail: false), false,
                id => id, azuStores: true));
    }
}
