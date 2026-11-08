using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Transfers;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>X67 (D156, amending D095): the raised chest and source limits and cleanup's work-first source pick.</summary>
public sealed class CleanupLimitTests
{
    private readonly ITestOutputHelper output;
    public CleanupLimitTests(ITestOutputHelper output) => this.output = output;

    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    /// <summary>Leaf categories of the default set, one per made-up item family.</summary>
    private static readonly string[] Leaves = Taxonomy.Categories.Values
        .Where(x => !x.Generated && Taxonomy.ChildrenOf(x.Id).Count == 0 && x.Id != "misc").Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).Take(40).ToArray();
    private const int ItemsPerLeaf = 10;
    private static readonly string[] Items = Leaves.SelectMany((leaf, l) => Enumerable.Range(0, ItemsPerLeaf).Select(i => $"Item{l:D2}_{i}")).ToArray();
    private static readonly ClassificationIndex Index = Build(Items.Select(x => Item(x)),
        new ClassificationRuleSet(Taxonomy, Items.Select((x, i) => new ClassificationOverride("exact", x, OverrideOperation.Replace, new[] { Leaves[i / ItemsPerLeaf] }))));
    private static string LeafOf(string item) => Leaves[int.Parse(item.Substring(4, 2))];

    private sealed class Answers : ICategoryAnswers
    {
        public long Generation => 1;
        public bool CategoriesReady => true;
        public ClassificationResult? Resolve(string prefab) => Index.Items.TryGetValue(prefab, out var row) ? row : null;
    }

    private static TransferItemIdentity Identity(string prefab) =>
        new(prefab, "catalog", 1, 0, 0, 100, 0, "", false, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());

    private static string Rule(params string[] categories) => Rule(0, categories);
    private static string Rule(int priority, params string[] categories) =>
        ContainerPolicyEnvelope.Serialize(1, new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { new ClassificationFilterRule(Taxonomy, categories) }, priority: priority));

    private sealed class Box
    {
        public string Id = ""; public string Payload = ""; public int Width = 8, Height = 4; public double Distance;
        public readonly List<(Guid Handle, int Cell, string Prefab, int Quantity)> Stacks = new();
        public Box Put(string prefab, int quantity)
        {
            var cell = Enumerable.Range(0, Width * Height).First(c => Stacks.All(s => s.Cell != c));
            Stacks.Add((Guid.NewGuid(), cell, prefab, quantity)); return this;
        }
        public CleanupChest Chest() => new(Id, Payload, new TransferInventorySnapshot(
            new TransferInventoryStamp("inv:" + Id, Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671"), 0, 0, 1, 1, "p"), Width, Height,
            Stacks.Select(s => new TransferStack(s.Handle, s.Cell % Width, s.Cell / Width, s.Quantity, 50, Identity(s.Prefab), false, mergeClass: s.Prefab)), Array.Empty<int>()),
            true, distance: Distance);
    }

    private static CleanupPlan Plan(IEnumerable<Box> boxes, int sources = CleanupPlanner.DefaultSources, int chests = CleanupPlanner.MaximumChests,
        ChestRuleEvaluator? evaluator = null, Action<string, double>? timed = null) =>
        CleanupPlanner.Plan(new CleanupInput(Taxonomy, new Answers(), boxes.Select(x => x.Chest()), timed: timed, maximumChests: chests, maximumSources: sources), evaluator);

    private static void Apply(List<Box> boxes, CleanupPlan plan)
    {
        foreach (var move in plan.Moves)
        {
            var source = boxes.Single(b => b.Stacks.Any(s => s.Handle == move.Handle));
            var at = source.Stacks.FindIndex(s => s.Handle == move.Handle);
            var stack = source.Stacks[at];
            Assert.True(stack.Quantity >= move.Units);
            if (stack.Quantity == move.Units) source.Stacks.RemoveAt(at); else source.Stacks[at] = (stack.Handle, stack.Cell, stack.Prefab, stack.Quantity - move.Units);
            var destination = boxes.Single(b => b.Id == move.DestinationId);
            var cell = move.DestinationY * destination.Width + move.DestinationX;
            var there = destination.Stacks.FindIndex(s => s.Cell == cell);
            if (there < 0) destination.Stacks.Add((Guid.NewGuid(), cell, move.Identity.Prefab, move.Units));
            else
            {
                var row = destination.Stacks[there]; Assert.Equal(row.Prefab, move.Identity.Prefab);
                Assert.True(row.Quantity + move.Units <= 50);
                destination.Stacks[there] = (row.Handle, row.Cell, row.Prefab, row.Quantity + move.Units);
            }
        }
    }

    private static Dictionary<string, int> Totals(IEnumerable<Box> boxes) =>
        boxes.SelectMany(b => b.Stacks).GroupBy(s => s.Prefab).ToDictionary(g => g.Key, g => g.Sum(s => s.Quantity));

    private static string Describe(CleanupPlan plan) => string.Join("\n", plan.Moves.Select(x => $"{x.SourceId}>{x.DestinationId}:{x.Identity.Prefab}x{x.Units}@{x.DestinationX},{x.DestinationY}:{x.Kind}"))
        + "|" + string.Join(",", plan.Remainders.Select(x => x.SourceId + ":" + x.Reason)) + "|" + plan.SkippedChests;

    /// <summary>
    /// A made-up base: every chest ruled for one category (a few without a rule), each holding stacks of its own items and,
    /// for <paramref name="misplaced"/> of them, one stack another chest's rule accepts. Nearer chests have lower numbers.
    /// </summary>
    /// <param name="priorities">Every fifth chest at priority 1, so nearby chests of its category re-sort into it.</param>
    private static List<Box> Base(int count, double misplaced, int seed = 7, int stacks = 12, bool priorities = true)
    {
        var random = new Random(seed); var boxes = new List<Box>();
        for (var i = 0; i < count; i++)
        {
            var leaf = Leaves[i % Leaves.Length];
            var box = new Box { Id = "c" + i.ToString("D3"), Payload = i % 17 == 16 ? "" : Rule(priorities && i % 5 == 0 ? 1 : 0, leaf), Distance = i };
            var own = Items.Where(x => LeafOf(x) == leaf).ToArray();
            for (var s = 0; s < stacks; s++) box.Put(own[random.Next(own.Length)], 1 + random.Next(20));
            if (random.NextDouble() < misplaced) box.Put(Items[random.Next(Items.Length)], 1 + random.Next(20));
            boxes.Add(box);
        }
        return boxes;
    }

    [Fact]
    public void Input_ClampsTheLimitsToThePlannersBounds()
    {
        var input = new CleanupInput(Taxonomy, new Answers(), Array.Empty<CleanupChest>(), maximumChests: 4000, maximumSources: 4000);
        Assert.Equal(512, input.MaximumChests); Assert.Equal(256, input.MaximumSources);
        Assert.Equal(64, new CleanupInput(Taxonomy, new Answers(), Array.Empty<CleanupChest>()).MaximumSources);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CleanupInput(Taxonomy, new Answers(), Array.Empty<CleanupChest>(), maximumSources: 0));
        Assert.True(TransferLimits.MaximumRunInventories > StashPlanner.MaximumChests);
        Assert.Equal(512, StashPlanner.MaximumChests);
    }

    // When every chest with items fits in the source limit, the pick changes nothing: the same moves as before X67.
    [Fact]
    public void EverythingFits_SameMovesWhateverTheSourceLimit()
    {
        var boxes = Base(60, 0.5);
        var at64 = Plan(boxes, 64); var at256 = Plan(boxes, 256);
        Assert.NotEmpty(at64.Moves);
        Assert.Equal(Describe(at64), Describe(at256));
        Assert.Equal(0, at64.SkippedChests);
    }

    // D156: a far chest with a misplaced stack is reached before nearer chests that only need re-sorts.
    [Fact]
    public void WorkFirst_AFarChestWithAMisplacedStack_IsPickedBeforeNearerSortedChests()
    {
        var boxes = new List<Box>();
        for (var i = 0; i < 30; i++) boxes.Add(new Box { Id = "near" + i.ToString("D2"), Payload = Rule(Leaves[0]), Distance = i }.Put(Items[0], 5));
        var metal = new Box { Id = "target", Payload = Rule(Leaves[1]), Distance = 1 };
        var far = new Box { Id = "far", Payload = Rule(Leaves[0]), Distance = 40 }.Put(Items[ItemsPerLeaf], 7); // An item of Leaves[1].
        var loose = new Box { Id = "loose", Payload = "", Distance = 45 }.Put(Items[ItemsPerLeaf + 1], 3);
        boxes.Add(metal); boxes.Add(far); boxes.Add(loose);
        var plan = Plan(boxes, sources: 16);
        Assert.Contains(plan.Moves, x => x.SourceId == "far" && x.DestinationId == "target" && x.Kind == CleanupKind.Rejected);
        Assert.Contains(plan.Moves, x => x.SourceId == "loose" && x.DestinationId == "target" && x.Kind == CleanupKind.Unruled);
        // 32 chests hold items, 16 give: the two work chests and the 14 nearest others.
        Assert.Equal(16, plan.SkippedChests);
        // Before X67 (nearest first), the farthest chests were never reached from here.
        var nearestOnly = boxes.OrderBy(x => x.Distance).Take(16).Select(x => x.Id).ToList();
        Assert.DoesNotContain("far", nearestOnly);
    }

    // A chest whose accepting chests are full keeps its slot, and the report says "no room".
    [Fact]
    public void WorkFirst_FullDestinations_KeepTheChestOnTheList_AndSayNoRoom()
    {
        var full = new Box { Id = "full", Payload = Rule(Leaves[1]), Distance = 0, Width = 1, Height = 1 }.Put(Items[ItemsPerLeaf + 2], 50);
        var boxes = new List<Box> { full };
        for (var i = 0; i < 20; i++) boxes.Add(new Box { Id = "near" + i.ToString("D2"), Payload = Rule(Leaves[0]), Distance = 1 + i }.Put(Items[0], 5));
        boxes.Add(new Box { Id = "far", Payload = Rule(Leaves[0]), Distance = 50 }.Put(Items[ItemsPerLeaf], 7));
        for (var press = 0; press < 3; press++)
        {
            var plan = Plan(boxes, sources: 16);
            Assert.Empty(plan.Moves);
            Assert.Contains(plan.Remainders, x => x.SourceId == "far" && x.Reason == RunReason.NoCapacity);
        }
    }

    // Each press moves misplaced stacks out, those chests drop off the list, and the next press reaches further, until done.
    // Re-sorts (a better-priority chest for an accepted stack) are left out: they need the ranking, so only the nearest
    // chests without cheap work get them (D156); from one spot, re-sorts beyond those wait until the player moves.
    [Theory]
    [InlineData(120, 16)]
    [InlineData(300, 64)]
    public void RepeatedPresses_Settle_AndConserveEveryItem(int chests, int sources)
    {
        var boxes = Base(chests, 0.6, seed: chests, priorities: false);
        var before = Totals(boxes);
        var presses = 0;
        while (true)
        {
            var plan = Plan(boxes, sources);
            if (plan.Moves.Count == 0) break;
            Apply(boxes, plan);
            Assert.Equal(before, Totals(boxes));
            Assert.True(++presses <= chests / sources + 3, "cleanup did not settle");
        }
        Assert.True(presses >= 1);
        // Settled: every stack in a chest with a rule is one that rule accepts, or no other chest accepts it.
        var evaluator = new ChestRuleEvaluator();
        foreach (var box in boxes.Where(b => b.Payload.Length != 0))
            foreach (var stack in box.Stacks)
                if (evaluator.Evaluate(box.Payload, stack.Prefab, Taxonomy, new Answers()) != ChestRuleDecision.Accept)
                    Assert.DoesNotContain(boxes, b => b.Payload.Length != 0 && evaluator.Evaluate(b.Payload, stack.Prefab, Taxonomy, new Answers()) == ChestRuleDecision.Accept);
    }

    /// <summary>
    /// X67 desk measurement (D156: measure first). Off unless YAVSOS_BENCH=1; it then writes local/x67-bench.txt. Planning cost
    /// at 256 and 512 chests and 64, 128 and 256 sources, cold (a new rule evaluator, as after a rule or category change) and
    /// warm (the same evaluator, as on a second press); each plan builds its own route cache. "pick" is the work-first source
    /// pick alone, from the planner's own clock.
    /// </summary>
    [Fact]
    public void Benchmark_PlanningCostAtTheNewLimits()
    {
        if (Environment.GetEnvironmentVariable("YAVSOS_BENCH") != "1") return;
        var lines = new List<string> { $"X67 desk benchmark {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z; {Environment.ProcessorCount} cores; .NET {Environment.Version}; " +
            $"{Items.Length} items in {Leaves.Length} categories; chests 8x4 with 12 stacks, 60% with one misplaced stack; median of 5 runs, ms" };
        // The runtime's one-time compile, so "cold" means a cold rule cache (P10-28 warms the game the same way).
        for (var i = 0; i < 3; i++) Plan(Base(256, 0.6, seed: 99), 256, evaluator: new ChestRuleEvaluator());
        foreach (var count in new[] { 256, 512 })
        {
            var boxes = Base(count, 0.6, seed: count);
            var stacks = boxes.Sum(x => x.Stacks.Count);
            foreach (var sources in new[] { 64, 128, 256 })
            {
                double Median(List<double> rows) { rows.Sort(); return rows[rows.Count / 2]; }
                var cold = new List<double>(); var warm = new List<double>(); var pick = new List<double>(); var moves = 0;
                for (var run = 0; run < 5; run++)
                {
                    var evaluator = new ChestRuleEvaluator();
                    var clock = Stopwatch.StartNew();
                    var plan = Plan(boxes, sources, evaluator: evaluator);
                    cold.Add(clock.Elapsed.TotalMilliseconds); moves = plan.Moves.Count;
                    clock.Restart();
                    Plan(boxes, sources, evaluator: evaluator);
                    warm.Add(clock.Elapsed.TotalMilliseconds);
                    double picked = 0;
                    Plan(boxes, sources, evaluator: evaluator, timed: (step, ms) => { if (step == "sources") picked = ms; });
                    pick.Add(picked);
                }
                lines.Add($"chests={count} stacks={stacks} sources={sources} moves={moves} cold={Median(cold):0.0} warm={Median(warm):0.0} pick={Median(pick):0.00}");
            }
        }
        foreach (var line in lines) output.WriteLine(line);
        var path = Path.Combine(EnglishText.Root(), "local", "x67-bench.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
