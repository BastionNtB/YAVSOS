using System;
using System.Collections.Generic;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

// X14: AzuAutoStore's store key, judged by what arrives where it says it stored.
public sealed class StoreKeyWatchTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private sealed class World
    {
        internal readonly Dictionary<(string, string), int> Counts = new();
        internal int? Count(string chest, string item) => Counts.TryGetValue((chest, item), out var n) ? n : null;
    }

    [Fact]
    public void LocalChest_ArrivedAtOnce_IsStored()
    {
        var world = new World(); world.Counts[("metal", "Copper")] = 10;
        var watch = new StoreKeyWatch();
        watch.Stored("metal", "Copper", 40, 10, viaMuc: false, A, now: 0);
        world.Counts[("metal", "Copper")] = 50;
        Assert.True(watch.Observe(0, world.Count));
        Assert.Equal((1, 40, 0), (watch.Report.MovedStacks, watch.Report.MovedUnits, watch.Report.Unconfirmed));
        Assert.Equal("Stashed 1 stack (40 items).", EnglishText.Of(watch.Report.Summary()));
    }

    [Fact]
    public void OtherPlayersChest_WaitsForArrival_ThenStored()
    {
        var world = new World(); world.Counts[("bob", "Copper")] = 0;
        var watch = new StoreKeyWatch();
        watch.Stored("bob", "Copper", 40, 0, viaMuc: true, A, now: 0);
        Assert.False(watch.Observe(0.25, world.Count)); // Bob's game hasn't answered yet: nothing is said.
        Assert.Equal(0, watch.Report.MovedUnits + watch.Report.Unconfirmed);
        world.Counts[("bob", "Copper")] = 40;
        Assert.True(watch.Observe(0.5, world.Count));
        Assert.Equal("Stashed 1 stack (40 items), 1 via MultiUserChest.", EnglishText.Of(watch.Report.Summary()));
    }

    // R1's shape: Azu said "Stored", nothing arrived. Unconfirmed after the window, and not before.
    [Fact]
    public void NothingArrives_IsUnconfirmed_AfterTheWindow()
    {
        var world = new World(); world.Counts[("drawer", "DeerHide")] = 820;
        var watch = new StoreKeyWatch();
        watch.Stored("drawer", "DeerHide", 20, 820, viaMuc: false, A, now: 0);
        Assert.False(watch.Observe(StoreKeyWatch.WindowSeconds - 1, world.Count));
        Assert.True(watch.Observe(StoreKeyWatch.WindowSeconds, world.Count));
        Assert.Equal((0, 1), (watch.Report.MovedUnits, watch.Report.Unconfirmed));
        Assert.Equal("Nothing stashed: 1 unconfirmed (not retried).", EnglishText.Of(watch.Report.Summary()));
        Assert.Equal(RunKind.StoreKey, watch.Report.Kind);
    }

    [Fact]
    public void PartArrives_ThatPartIsStored_TheRestUnconfirmed()
    {
        var world = new World(); world.Counts[("bob", "Wood")] = 5;
        var watch = new StoreKeyWatch();
        watch.Stored("bob", "Wood", 30, 5, viaMuc: true, A, now: 0);
        watch.Stored("bob", "Wood", 20, 5, viaMuc: true, B, now: 0); // A second stack into the same chest shares the baseline.
        world.Counts[("bob", "Wood")] = 40;
        Assert.True(watch.Observe(StoreKeyWatch.WindowSeconds, world.Count));
        // 35 arrived: all of the first stack's 30, 5 of the second's 20.
        Assert.Equal((2, 35, 1), (watch.Report.MovedStacks, watch.Report.MovedUnits, watch.Report.Unconfirmed));
    }

    [Fact]
    public void UnreadableOrGoneDestination_IsUnconfirmed()
    {
        var watch = new StoreKeyWatch();
        watch.Stored("backpack", "Resin", 5, before: null, viaMuc: false, A, now: 0);
        Assert.False(watch.Pending);
        watch.Stored("gone", "Stone", 10, before: 0, viaMuc: false, B, now: 0);
        Assert.True(watch.Observe(0, new World().Count)); // The chest was destroyed: no count, unconfirmed at once.
        Assert.Equal((0, 2), (watch.Report.MovedUnits, watch.Report.Unconfirmed));
        watch.Stored("chest", "Wood", 0, 0, viaMuc: false, A, now: 0); // Azu stored nothing: nothing to say.
        Assert.False(watch.Pending);
    }
}
