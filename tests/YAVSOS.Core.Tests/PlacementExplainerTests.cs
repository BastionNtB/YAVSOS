using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>P6-05: explanations run the planners' own steps, so the explained chest is the planned chest.</summary>
public sealed class PlacementExplainerTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static readonly ClassificationIndex Index = Build(new[] { Item("Copper"), Item("Wood"), Item("SwordBronze", "OneHandedWeapon"), Item("Bread") },
        new ClassificationRuleSet(Taxonomy, new[]
        {
            new ClassificationOverride("exact", "Copper", OverrideOperation.Replace, new[] { "materials.metal", "materials.crafting" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "materials.wood" }),
            new ClassificationOverride("exact", "SwordBronze", OverrideOperation.Replace, new[] { "weapons.swords" }),
            new ClassificationOverride("exact", "Bread", OverrideOperation.Replace, new[] { "food.health" })
        }));
    private static readonly string[] Items = { "Copper", "Wood", "SwordBronze", "Bread", "Unclassified" };
    private static readonly string[] Pool = { "materials", "materials.metal", "materials.wood", "materials.crafting", "weapons", "weapons.swords", "food", "food.health" };

    private sealed class Answers : ICategoryAnswers
    {
        public long Generation { get; set; } = 1;
        public bool CategoriesReady { get; set; } = true;
        public ClassificationResult? Resolve(string prefab) => Index.Items.TryGetValue(prefab, out var row) ? row : null;
    }
    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());
    private static TransferStack Stack(string prefab, int quantity = 10, int x = 0, int y = 0, int maximum = 50, bool isProtected = false, bool supported = true) =>
        new(Guid.NewGuid(), x, y, quantity, maximum, Identity(prefab), isProtected, supported, maximum > 1 ? prefab : null);
    private static TransferInventorySnapshot Inventory(string id, int width, IEnumerable<TransferStack> stacks, IEnumerable<int>? blocked = null) =>
        new(new TransferInventoryStamp("inv:" + id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"), width, 1, stacks, blocked ?? Array.Empty<int>());
    private static string Rule(string[]? categories = null, string[]? always = null, string[]? never = null, int priority = 0, bool overflow = false) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            categories == null ? null : new[] { ClassificationContractFixture.Rule(categories) }, always, never, priority, overflow));
    private static StashChest Box(string id, string payload, int width = 4, bool local = true, bool formatKnown = true, params TransferStack[] stacks) =>
        new(id, payload, Inventory(id, width, stacks), local, formatKnown);
    private static (StashInput Input, TransferStack Stack) Player(string prefab, IEnumerable<StashChest> chests, bool muc = false, Answers? answers = null, int quantity = 10)
    {
        var stack = Stack(prefab, quantity);
        return (new StashInput(Taxonomy, answers ?? new Answers(), Inventory("player", 8, new[] { stack }), chests, mucAvailable: muc), stack);
    }

    [Fact]
    public void Stash_NamesTheChosenChestRulePartAndPriority_AndEveryRejection()
    {
        var full = Box("a-full-metal", Rule(new[] { "materials.metal" }, priority: 2), 1, true, true, Stack("Copper", 50));
        var (input, stack) = Player("Copper", new[]
        {
            full, Box("b-materials", Rule(new[] { "materials" }, priority: 1)), Box("c-metal", Rule(new[] { "materials.metal" })),
            Box("d-wood", Rule(new[] { "materials.wood" })), Box("e-never", Rule(new[] { "materials" }, never: new[] { "Copper" })),
            Box("f-unruled", ""), Box("g-unreadable", "{not a rule"), Box("h-remote", Rule(new[] { "materials.metal" }, priority: 5), local: false)
        });
        var answer = PlacementExplainer.Stash(input, stack.Handle, chestName: id => id.Substring(2), itemName: p => p == "Copper" ? "Copper ore" : p,
            outOfRange: new[] { "z-far" });
        Assert.Equal(PlacementOutcome.Goes, answer.Outcome);
        Assert.Equal("b-materials", answer.ChosenId);
        Assert.Equal("b-materials", Assert.Single(StashPlanner.Plan(input).Moves).DestinationId);
        Assert.Equal("Copper ore → materials: category Materials, 1 level above the item's own category, priority 4 (Normal)", answer.Summary);
        Assert.Equal(ChestVerdict.Chosen, answer.For("b-materials")!.Verdict);
        Assert.Equal(ChestVerdict.Full, answer.For("a-full-metal")!.Verdict);
        Assert.Equal(ChestVerdict.Backup, answer.For("c-metal")!.Verdict);
        Assert.Equal(ChestVerdict.RuleRejects, answer.For("d-wood")!.Verdict);
        Assert.Equal(ChestVerdict.Never, answer.For("e-never")!.Verdict);
        Assert.Equal(ChestVerdict.NoRule, answer.For("f-unruled")!.Verdict);
        Assert.Equal(ChestVerdict.RuleUnreadable, answer.For("g-unreadable")!.Verdict);
        Assert.Equal(ChestVerdict.OtherOwnerNoMuc, answer.For("h-remote")!.Verdict);
        Assert.Contains("no MultiUserChest", answer.For("h-remote")!.Text);
        Assert.Equal(ChestVerdict.OutOfRange, answer.For("z-far")!.Verdict);
        Assert.Equal(2, answer.For("a-full-metal")!.Rank!.Candidate.Rules.Priority);
        Assert.Equal("materials.metal", answer.For("c-metal")!.Rank!.Category);
    }

    [Fact]
    public void Stash_WhyNot_NoRoomBlockedNoChestKeptAndNotReady()
    {
        var (full, fullStack) = Player("Copper", new[] { Box("metal", Rule(new[] { "materials.metal" }), 1, true, true, Stack("Copper", 50)) });
        Assert.Equal(PlacementOutcome.NoRoom, PlacementExplainer.Stash(full, fullStack.Handle).Outcome);
        var (remote, remoteStack) = Player("Copper", new[] { Box("metal", Rule(new[] { "materials.metal" }), local: false, formatKnown: false) }, muc: true);
        var blocked = PlacementExplainer.Stash(remote, remoteStack.Handle);
        Assert.Equal((PlacementOutcome.Blocked, CleanupReason.OtherOwnerFormatUnknown), (blocked.Outcome, blocked.Blocked!.Value));
        Assert.Equal(CleanupReason.OtherOwnerFormatUnknown, Assert.Single(StashPlanner.Plan(remote).Remainders).Reason);
        var (none, noneStack) = Player("Bread", new[] { Box("metal", Rule(new[] { "materials.metal" })) });
        var nothing = PlacementExplainer.Stash(none, noneStack.Handle);
        Assert.Equal(PlacementOutcome.NoChest, nothing.Outcome); Assert.Equal("Bread stays: no chest in range takes it", nothing.Summary);
        var (pending, pendingStack) = Player("Copper", new[] { Box("metal", Rule(new[] { "materials.metal" })) }, answers: new Answers { CategoriesReady = false });
        var waiting = PlacementExplainer.Stash(pending, pendingStack.Handle);
        Assert.Equal(PlacementOutcome.NotReady, waiting.Outcome); Assert.Equal(ChestVerdict.CategoriesPending, waiting.For("metal")!.Verdict);
        // A protected stack never moves: kept, and the chests are not considered.
        var hotbar = Stack("Copper", isProtected: true);
        var kept = new StashInput(Taxonomy, new Answers(), Inventory("player", 8, new[] { hotbar }), new[] { Box("metal", Rule(new[] { "materials.metal" })) });
        Assert.Equal(PlacementOutcome.Kept, PlacementExplainer.Stash(kept, hotbar.Handle).Outcome);
        // An unruled chest that already holds the item is a holder.
        var (holder, holderStack) = Player("Wood", new[] { Box("pile", "", 4, true, true, Stack("Wood", 5)) });
        var held = PlacementExplainer.Stash(holder, holderStack.Handle);
        Assert.Equal(("pile", "Wood → pile: no rule, already holds this item"), (held.ChosenId, held.Summary));
    }

    [Fact]
    public void Stash_ExplainsAFailedFilter()
    {
        var payload = ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { ClassificationContractFixture.Rule(new[] { "weapons" }, ("rarity", new[] { "epic" })) }));
        var (input, stack) = Player("SwordBronze", new[] { Box("loot", payload) });
        var answer = PlacementExplainer.Stash(input, stack.Handle);
        Assert.Equal(PlacementOutcome.NoChest, answer.Outcome);
        Assert.Equal("its rarity filter (epic) doesn't match", answer.For("loot")!.Text);
        var epic = new StashInput(Taxonomy, new Answers(), input.Player, input.Chests,
            instanceFacets: _ => new[] { new FacetEvidence("rarity", "epic", "optional:epicloot", "Instance.") });
        var chosen = PlacementExplainer.Stash(epic, stack.Handle);
        Assert.Equal("loot", chosen.ChosenId);
        Assert.Equal("SwordBronze → loot: category Weapons › epic, 1 level above the item's own category", chosen.Summary);
    }

    private static CleanupChest Chest(string id, string payload, int width = 4, bool local = true, bool optOut = false, params TransferStack[] stacks) =>
        new(id, payload, Inventory(id, width, stacks), local, optOut);

    [Fact]
    public void Cleanup_ExplainsMovesStaysAndGates()
    {
        var copper = Stack("Copper");
        var input = new CleanupInput(Taxonomy, new Answers(), new[]
        {
            Chest("a-wood", Rule(new[] { "materials.wood" }), stacks: copper), Chest("b-materials", Rule(new[] { "materials" })),
            Chest("c-metal", Rule(new[] { "materials.metal" })), Chest("d-loose", "")
        }, azuAllows: (id, _) => id != "b-materials");
        var answer = PlacementExplainer.Cleanup(input, "a-wood", copper.Handle);
        Assert.Equal(("c-metal", PlacementOutcome.Goes), (answer.ChosenId, answer.Outcome));
        Assert.Equal("c-metal", Assert.Single(CleanupPlanner.Plan(input).Moves).DestinationId);
        Assert.Equal(ChestVerdict.Current, answer.For("a-wood")!.Verdict);
        Assert.Equal("where it is; its rule doesn't take it", answer.For("a-wood")!.Text);
        Assert.Equal(ChestVerdict.AzuExcluded, answer.For("b-materials")!.Verdict);
        Assert.Equal(ChestVerdict.NoRule, answer.For("d-loose")!.Verdict);

        // Valid placement, equal rank elsewhere: stays, and the other chest is "no better".
        var placed = Stack("Copper");
        var settled = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("metal", Rule(new[] { "materials.metal" }), stacks: placed),
            Chest("crafting", Rule(new[] { "materials.crafting" })) });
        var stays = PlacementExplainer.Cleanup(settled, "metal", placed.Handle);
        Assert.Equal(PlacementOutcome.Stays, stays.Outcome); Assert.Equal(ChestVerdict.NotBetter, stays.For("crafting")!.Verdict);
        Assert.StartsWith("Copper stays in metal: no better chest takes it", stays.Summary);
        Assert.Equal("Clean up nearby would leave Copper in this chest: it belongs here.",
            ExplanationView.Sentence(PlacementExplainer.Cleanup(settled, "metal", placed.Handle, detail: false), true));
        var moved = PlacementExplainer.Cleanup(input, "a-wood", copper.Handle, detail: false);
        Assert.Equal(("c-metal", PlacementOutcome.Goes), (moved.ChosenId, moved.Outcome)); Assert.Empty(moved.Chests);
        Assert.Equal("Clean up nearby would move Copper to the Chest 4 m away.", ExplanationView.Sentence(moved, true, _ => "Chest 4 m away"));

        var optedOut = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("wood", Rule(new[] { "materials.wood" }), optOut: true, stacks: placed),
            Chest("metal", Rule(new[] { "materials.metal" })) });
        Assert.Equal(PlacementOutcome.OptedOut, PlacementExplainer.Cleanup(optedOut, "wood", placed.Handle).Outcome);
        var always = Stack("Copper");
        var listed = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("box", Rule(always: new[] { "Copper" }), stacks: always),
            Chest("metal", Rule(new[] { "materials.metal" }, priority: 9)) });
        // D092: a higher-priority chest outranks Always, in cleanup as in the stash.
        var promoted = PlacementExplainer.Cleanup(listed, "box", always.Handle);
        Assert.Equal(("metal", PlacementOutcome.Goes), (promoted.ChosenId, promoted.Outcome));
        var remote = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("wood", Rule(new[] { "materials.wood" }), stacks: placed),
            Chest("metal", Rule(new[] { "materials.metal" }), local: false) });
        var blocked = PlacementExplainer.Cleanup(remote, "wood", placed.Handle);
        Assert.Equal((PlacementOutcome.Blocked, CleanupReason.OtherOwnerNoMuc), (blocked.Outcome, blocked.Blocked!.Value));
        Assert.Equal(CleanupReason.OtherOwnerNoMuc, Assert.Single(CleanupPlanner.Plan(remote).Remainders).Reason);
    }

    // Property: for the first stack a run handles, the explained chest is the planned chest, and a "why not" matches the run's report.

    private static string RandomPayload(Random random)
    {
        switch (random.Next(8))
        {
            case 0: return "";
            case 1: return "{broken";
            case 2: return ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Unconfigured,
                excludes: random.Next(2) == 0 ? new[] { Items[random.Next(Items.Length)] } : null));
        }
        var categories = Enumerable.Range(0, random.Next(0, 3)).Select(_ => Pool[random.Next(Pool.Length)]).Distinct().ToArray();
        var always = random.Next(5) == 0 ? new[] { Items[random.Next(Items.Length)] } : null;
        var never = random.Next(5) == 0 ? new[] { Items[random.Next(Items.Length)] } : null;
        return Rule(categories.Length == 0 ? null : categories, always, never, random.Next(-1, 3), random.Next(6) == 0);
    }
    private static TransferStack[] Fill(Random random, int width)
    {
        var count = random.Next(0, width + 1);
        return Enumerable.Range(0, count).Select(x => Stack(Items[random.Next(Items.Length)], random.Next(1, 51), x)).ToArray();
    }

    [Fact]
    public void Property_StashExplanationChoiceEqualsPlannerChoice()
    {
        var goes = 0;
        for (var seed = 0; seed < 600; seed++)
        {
            var random = new Random(seed);
            var chests = Enumerable.Range(0, random.Next(1, 7)).Select(i =>
            {
                var width = random.Next(1, 4);
                return Box("c" + i, RandomPayload(random), width, random.Next(4) != 0, random.Next(3) != 0, Fill(random, width));
            }).ToArray();
            var stack = Stack(Items[random.Next(Items.Length)], random.Next(1, 51), isProtected: random.Next(15) == 0, supported: random.Next(15) != 0);
            var answers = new Answers { CategoriesReady = random.Next(20) != 0 };
            var input = new StashInput(Taxonomy, answers, Inventory("player", 8, new[] { stack }), chests, mucAvailable: random.Next(2) == 0,
                held: random.Next(15) == 0 ? new[] { stack.Handle } : null);
            var plan = StashPlanner.Plan(input); var answer = PlacementExplainer.Stash(input, stack.Handle);
            Assert.Equal(plan.Moves.FirstOrDefault()?.DestinationId, answer.ChosenId);
            Assert.Equal(answer.ChosenId != null, answer.Outcome == PlacementOutcome.Goes);
            var rest = plan.Remainders.SingleOrDefault();
            switch (answer.Outcome)
            {
                case PlacementOutcome.Goes: goes++; Assert.True(rest == null || rest.Units < stack.Quantity); break;
                case PlacementOutcome.NoRoom: Assert.Equal(CleanupReason.NoCapacity, rest!.Reason); break;
                case PlacementOutcome.Blocked: Assert.Equal(answer.Blocked, rest!.Reason); break;
                case PlacementOutcome.Kept: Assert.True(rest == null || rest.Reason is CleanupReason.Held or CleanupReason.Unsupported); break;
                // D086 D4, D087: an item waiting for a category chest is reported as waiting.
                case PlacementOutcome.NotReady: Assert.True(rest == null || rest.Reason == CleanupReason.CategoriesPending); break;
                default: Assert.Null(rest); break;
            }
            Assert.Equal(chests.Length, answer.Chests.Count);
        }
        Assert.True(goes > 100, "too few moving cases: " + goes);
    }

    [Fact]
    public void Property_CleanupExplanationChoiceEqualsPlannerChoice()
    {
        var goes = 0;
        for (var seed = 0; seed < 600; seed++)
        {
            var random = new Random(1000 + seed);
            var stack = Stack(Items[random.Next(Items.Length)], random.Next(1, 51), isProtected: random.Next(15) == 0, supported: random.Next(15) != 0);
            // The source sorts first and its stack sits in the first cell, so it is the first stack the run handles.
            var source = Chest("a", RandomPayload(random), 1, random.Next(4) != 0, random.Next(12) == 0, stack);
            var chests = new[] { source }.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(i =>
            {
                var width = random.Next(1, 4);
                var payload = RandomPayload(random); var local = random.Next(4) != 0; var optOut = random.Next(8) == 0;
                _ = random.Next(8); // was the Azu pause flag (removed, D097); kept so the seeded worlds stay the same
                return Chest("c" + i, payload, width, local, optOut, Fill(random, width));
            })).ToArray();
            var excluded = random.Next(3) == 0 ? "c" + random.Next(5) : null;
            var input = new CleanupInput(Taxonomy, new Answers { CategoriesReady = random.Next(20) != 0 }, chests, null, random.Next(2) == 0,
                excluded == null ? null : (id, _) => id != excluded, random.Next(15) == 0 ? new[] { stack.Handle } : null);
            var plan = CleanupPlanner.Plan(input); var answer = PlacementExplainer.Cleanup(input, "a", stack.Handle);
            Assert.Equal(plan.Moves.FirstOrDefault(x => x.Handle == stack.Handle)?.DestinationId, answer.ChosenId);
            Assert.Equal(answer.ChosenId != null, answer.Outcome == PlacementOutcome.Goes);
            var rest = plan.Remainders.FirstOrDefault(x => x.Handle == stack.Handle);
            switch (answer.Outcome)
            {
                case PlacementOutcome.Goes: goes++; break;
                case PlacementOutcome.NoRoom: Assert.Equal(CleanupReason.NoCapacity, rest!.Reason); break;
                case PlacementOutcome.Blocked: Assert.Equal(answer.Blocked, rest!.Reason); break;
                case PlacementOutcome.Kept: Assert.True(rest!.Reason is CleanupReason.Held or CleanupReason.Unsupported); break;
                case PlacementOutcome.NoChest: Assert.True(rest == null || rest.Reason == CleanupReason.NoDestination); break;
                default: Assert.Null(rest); break;
            }
            Assert.Equal(chests.Length, answer.Chests.Count);
        }
        Assert.True(goes > 60, "too few moving cases: " + goes);
    }

    // R4 review: the panel shows one plain sentence with the deterministic result, and skips the per-chest rows.
    [Fact]
    public void Sentence_SaysWhatTheRunWouldDo_WithoutChestRows()
    {
        var full = Box("a-full-metal", Rule(new[] { "materials.metal" }, priority: 2), 1, true, true, Stack("Copper", 50));
        var (input, stack) = Player("Copper", new[]
        {
            full, Box("b-materials", Rule(new[] { "materials" }, priority: 1)), Box("c-metal", Rule(new[] { "materials.metal" })), Box("e-unruled", "")
        });
        string Chest(string id) => id == "b-materials" ? "Reinforced chest 5 m away" : id;
        var detailed = PlacementExplainer.Stash(input, stack.Handle);
        var plain = PlacementExplainer.Stash(input, stack.Handle, outOfRange: new[] { "z-far" }, detail: false);
        Assert.Empty(plain.Chests);
        Assert.Equal(detailed.ChosenId, plain.ChosenId); Assert.Equal(detailed.Outcome, plain.Outcome);
        Assert.Equal("The store key would put Copper ore in the Reinforced chest 5 m away.",
            ExplanationView.Sentence(plain, false, Chest, x => x == "Copper" ? "Copper ore" : x));
        var (alone, aloneStack) = Player("Copper", new[] { Box("e-unruled", "") });
        Assert.Equal("The store key would keep Copper in your inventory: no chest nearby takes it.",
            ExplanationView.Sentence(PlacementExplainer.Stash(alone, aloneStack.Handle, detail: false), false));
    }

    // P6-23 (D071): the same answers as short tooltip lines: label first, then where, then why in brackets.
    [Fact]
    public void Tooltip_IsOneShortLine_PerRunKind()
    {
        string Named(string id) => id == "b-materials" ? "Reinforced chest 5 m away" : id;
        var full = Box("a-full-metal", Rule(new[] { "materials.metal" }, priority: 2), 1, true, true, Stack("Copper", 50));
        var (input, stack) = Player("Copper", new[] { full, Box("b-materials", Rule(new[] { "materials" }, priority: 1)) });
        var goes = PlacementExplainer.Stash(input, stack.Handle, detail: false);
        Assert.Equal("Stash stack: Reinforced chest 5 m away", ExplanationView.Tooltip(goes, false, Named));
        var (alone, aloneStack) = Player("Copper", new[] { Box("e-unruled", "") });
        var none = PlacementExplainer.Stash(alone, aloneStack.Handle, detail: false);
        Assert.Equal("Stash stack: stays in your inventory (no chest nearby takes it)", ExplanationView.Tooltip(none, false));
        Assert.Equal("Stash stack: AzuAutoStore's own settings decide (no chest rule nearby takes it)", ExplanationView.Tooltip(none, false, azuStores: true));
        // P8-17 (Q12, revised 2026-10-28): the single-stack binding that applies names the label; none leaves it out.
        Assert.Equal("Stash stack [Middle mouse]: Reinforced chest 5 m away", ExplanationView.Tooltip(goes, false, Named, stackKey: KeyNames.Format("Mouse2")));
        Assert.Equal("Stash stack [Ctrl + Q]: AzuAutoStore's own settings decide (no chest rule nearby takes it)",
            ExplanationView.Tooltip(none, false, azuStores: true, stackKey: KeyNames.Format("Q", new[] { "LeftControl" })));
        Assert.Equal("Stash stack: Reinforced chest 5 m away", ExplanationView.Tooltip(goes, false, Named, stackKey: ""));

        var placed = Stack("Copper");
        var settled = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("metal", Rule(new[] { "materials.metal" }), stacks: placed),
            Chest("crafting", Rule(new[] { "materials.crafting" })) });
        Assert.Equal("Clean up: stays here", ExplanationView.Tooltip(PlacementExplainer.Cleanup(settled, "metal", placed.Handle, detail: false), true, stackKey: "."));
        var wrong = Stack("Copper");
        var messy = new CleanupInput(Taxonomy, new Answers(), new[] { Chest("wood", Rule(new[] { "materials.wood" }), stacks: wrong), Chest("metal", Rule(new[] { "materials.metal" })) });
        Assert.Equal("Clean up: moves to Chest 4 m away", ExplanationView.Tooltip(PlacementExplainer.Cleanup(messy, "wood", wrong.Handle, detail: false), true, _ => "Chest 4 m away"));
    }
}
