using System;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class TransferLimitsTests
{
    // A03: a run captures the player and every chest a planner may consider, in either order.
    [Fact]
    public void RunInventoryLimit_CoversThePlayerAndEveryPlannedChest() =>
        Assert.True(TransferLimits.MaximumRunInventories >= Math.Max(CleanupPlanner.MaximumChests, StashPlanner.MaximumChests) + 1);
}
