using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

// X65 (D156, D163, #78): chests standing in one column are one stack; made-up layouts, the flat distance, and the names.
public sealed class ChestStacksTests
{
    private readonly ITestOutputHelper output;
    public ChestStacksTests(ITestOutputHelper output) { this.output = output; }

    private const float Rise = 0.9f; // About a reinforced chest's height; the exact figure comes from the lab dump.
    private static ChestSpot At(float x, float y, float z = 0) => new ChestSpot(x, y, z);
    private static string Places(StackPlace[] places) => string.Join(" ", places.Select(p => $"{p.Stack}:{p.Place}/{p.Size}"));

    [Fact]
    public void OneStack_CountsFromTheBottom_InAnyInputOrder()
    {
        var chests = new[] { At(5, 2 * Rise), At(5, 0), At(5, 3 * Rise), At(5, Rise) };
        Assert.Equal("0:3/4 0:1/4 0:4/4 0:2/4", Places(ChestStacks.Group(chests)));
    }

    [Fact]
    public void AChestAlone_IsAStackOfOne()
    {
        var place = Assert.Single(ChestStacks.Group(new[] { At(1, 1, 1) }));
        Assert.Equal(new StackPlace(0, 1, 1), place);
        Assert.False(place.Stacked);
        Assert.Empty(ChestStacks.Group(Array.Empty<ChestSpot>()));
    }

    [Fact]
    public void TwoStacksSideBySideInAWall_StayApart()
    {
        // Narrow chests 0.6 m apart along the wall, three high each.
        var chests = new List<ChestSpot>();
        for (var level = 0; level < 3; level++) { chests.Add(At(0, level * Rise)); chests.Add(At(0.6f, level * Rise)); }
        var places = ChestStacks.Group(chests);
        Assert.Equal("0:1/3 1:1/3 0:2/3 1:2/3 0:3/3 1:3/3", Places(places));
    }

    [Fact]
    public void BackToBackWalls_AreSeparateStacks()
    {
        var chests = new[] { At(0, 0, 0), At(0, Rise, 0), At(0, 0, 0.5f), At(0, Rise, 0.5f) };
        Assert.Equal("0:1/2 0:2/2 1:1/2 1:2/2", Places(ChestStacks.Group(chests)));
    }

    [Fact]
    public void ChestsAFewCentimetersOffTheColumn_StillStack()
    {
        var chests = new[] { At(2, 0, 3), At(2.04f, Rise, 2.97f), At(1.93f, 2 * Rise, 3.05f), At(2.1f, 3 * Rise, 3.1f) };
        Assert.Equal("0:1/4 0:2/4 0:3/4 0:4/4", Places(ChestStacks.Group(chests)));
        // Past the tolerance, measured from the chest below, a chest starts its own column.
        var off = new[] { At(2, 0, 3), At(2.4f, Rise, 3) };
        Assert.Equal("0:1/1 1:1/1", Places(ChestStacks.Group(off)));
        Assert.Equal("0:1/2 0:2/2", Places(ChestStacks.Group(off, tolerance: 0.45f)));
    }

    // #120: each chest is measured from the one below it, so a stack that leans a little stays one stack.
    [Fact]
    public void AStackThatLeans_StaysOneStack()
    {
        // Three high, each 0.2 m off the one below: the top chest is 0.4 m from the bottom one.
        var leaning = new[] { At(0, 0), At(0.2f, Rise), At(0.4f, 2 * Rise) };
        Assert.Equal("0:1/3 0:2/3 0:3/3", Places(ChestStacks.Group(leaning)));
        // The lab's capture wall, column R1 (hand-placed black metal chests, stack-dump2 2026-11-08): 0.13 m, then 0.27 m.
        var r1 = new[] { At(-360.79f, 48.62f, -454.65f), At(-360.81f, 49.62f, -454.52f), At(-360.84f, 50.62f, -454.25f) };
        Assert.Equal("0:1/3 0:2/3 0:3/3", Places(ChestStacks.Group(r1)));
    }

    [Fact]
    public void LeaningStacksSideBySide_StayApart()
    {
        // Two walls back to back (0.5 m) and neighbours along each (0.6 m), every column leaning 0.15 m a level the same way.
        var chests = new List<ChestSpot>();
        for (var level = 0; level < 4; level++)
            for (var column = 0; column < 2; column++)
                for (var wall = 0; wall < 2; wall++)
                    chests.Add(At(column * 0.6f + level * 0.15f, level * Rise, wall * 0.5f));
        var places = ChestStacks.Group(chests);
        Assert.Equal(4, places.Select(x => x.Stack).Distinct().Count());
        Assert.All(places, x => Assert.Equal(4, x.Size));
        for (var i = 0; i < 4; i++) Assert.Equal(places[i].Stack, places[i + 12].Stack); // Bottom and top of each column.
    }

    [Fact]
    public void ChestsOnShelves_StackAcrossTheBoards_ButNotAcrossFloors()
    {
        // A shelf: boards every 1.4 m, a chest on each, three shelves wide; then the floor above, 4 m up, with its own shelf.
        var chests = new List<ChestSpot>();
        for (var column = 0; column < 3; column++)
            for (var board = 0; board < 3; board++) chests.Add(At(column * 1.2f, board * 1.4f));
        chests.Add(At(0, 2.8f + 4f)); chests.Add(At(0, 2.8f + 4f + 1.4f));
        var places = ChestStacks.Group(chests);
        Assert.Equal(new StackPlace(0, 1, 3), places[0]);
        Assert.Equal(new StackPlace(0, 3, 3), places[2]);
        Assert.Equal(new StackPlace(3, 2, 3), places[7]); // Stacks are numbered column by column, bottom up.
        Assert.Equal(new StackPlace(1, 1, 2), places[9]); // The upper floor: the same column, its own stack.
        Assert.Equal(new StackPlace(1, 2, 2), places[10]);
        Assert.Equal(4, places.Select(x => x.Stack).Distinct().Count());
        // With a larger gap allowed, the two floors are one stack of five.
        Assert.Equal(new StackPlace(0, 5, 5), ChestStacks.Group(chests, maximumGap: 5f)[10]);
    }

    [Fact]
    public void MixedHeights_ChestTypesAndRaisedFloors_KeepTheirOrder()
    {
        // A wood chest (0.7 m), a reinforced chest (0.9 m) and a black metal chest on top; beside it a stack on a step 0.5 m up.
        var chests = new[] { At(0, 0), At(0, 0.7f), At(0, 1.6f), At(1, 0.5f), At(1, 1.2f) };
        Assert.Equal("0:1/3 0:2/3 0:3/3 1:1/2 1:2/2", Places(ChestStacks.Group(chests)));
    }

    [Fact]
    public void TheSameLayout_GivesTheSameStacks_AndBadPositionsStandAlone()
    {
        var chests = new[] { At(0, 0), At(0, Rise), At(float.NaN, 0), At(0, 2 * Rise) };
        var once = ChestStacks.Group(chests);
        Assert.Equal(once, ChestStacks.Group(chests));
        Assert.Equal((1, 1), (once[2].Place, once[2].Size));
        Assert.Equal(3, once[3].Size);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChestStacks.Group(chests, tolerance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChestStacks.Group(chests, maximumGap: float.NaN));
        Assert.Throws<ArgumentNullException>(() => ChestStacks.Group(null!));
    }

    [Fact]
    public void Across_IsTheFlatDistance_SoAWholeStackReadsTheSame()
    {
        var player = At(0, 10, 0);
        Assert.Equal(6, ChestPlaceText.Across(player, At(6, 12.4f, 0.3f)));
        Assert.Equal(6, ChestPlaceText.Across(player, At(6, 7.1f, 0.3f))); // Below the player: the same.
        Assert.Equal(5, ChestPlaceText.Across(player, At(3, 30f, 4)));
        Assert.Equal(1, ChestPlaceText.Across(player, At(0.5f, 10, 0))); // Half away from zero.
        Assert.Equal(0, ChestPlaceText.Across(player, At(float.NaN, 10, 0)));
        // Each chest of a stack of four at one spot, 6 m off: one distance for all.
        var stack = Enumerable.Range(0, 4).Select(i => At(6, 10 + i * Rise, 0.02f * i)).ToList();
        Assert.Single(stack.Select(x => ChestPlaceText.Across(player, x)).Distinct());
    }

    [Fact]
    public void PlaceNames_TellEveryChestOfAWallApart()
    {
        // A wall of 4 stacks of 5 at one distance: within each stack no two chests share a name, where the 3-D distance
        // gave them all "Chest 6 m away" or "Chest 7 m away".
        var player = At(0, 0, -6);
        var chests = Enumerable.Range(0, 20).Select(i => At(i / 5 * 1.0f, i % 5 * Rise, 0)).ToList();
        var places = ChestStacks.Group(chests);
        foreach (var group in Enumerable.Range(0, 4))
        {
            var names = Enumerable.Range(group * 5, 5)
                .Select(i => EnglishText.Of(ChestName.Away("Chest", ChestPlaceText.Across(player, chests[i]), places[i]).Said)).ToList();
            Assert.Equal(5, names.Distinct().Count());
            Assert.EndsWith(" m away, bottom of a stack of 5", names[0]);
            Assert.EndsWith(" m away, 3 of 5 from the bottom", names[2]);
            Assert.EndsWith(" m away, top of a stack of 5", names[4]);
        }
    }

    // #80: timed at the raised chest limit. 512 chests: 8 walls of 8 stacks of 8, each chest a few centimeters off.
    [Fact]
    public void FiveHundredTwelveChests_GroupInWellUnderAFrame()
    {
        var random = new Random(65);
        float Jitter() => (float)(random.NextDouble() - 0.5) * 0.08f;
        var chests = new List<ChestSpot>();
        for (var wall = 0; wall < 8; wall++)
            for (var column = 0; column < 8; column++)
                for (var level = 0; level < 8; level++)
                    chests.Add(At(column * 1.0f + Jitter(), level * Rise, wall * 3f + Jitter()));
        var places = ChestStacks.Group(chests); // Warm up (JIT).
        Assert.Equal(64, places.Select(x => x.Stack).Distinct().Count());
        Assert.All(places, x => Assert.Equal(8, x.Size));
        const int runs = 50;
        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            ChestStacks.Group(chests);
            times[i] = clock.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        output.WriteLine($"512 chests: median {times[runs / 2]:0.000} ms, slowest {times[runs - 1]:0.000} ms over {runs} runs");
        Assert.True(times[runs / 2] < 5, $"median {times[runs / 2]:0.000} ms");
    }
}
