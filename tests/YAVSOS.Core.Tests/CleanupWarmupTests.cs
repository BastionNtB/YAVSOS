using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>P10-28: the warm-up's made-up base reaches each part of the planner a real cleanup uses.</summary>
public sealed class CleanupWarmupTests
{
    [Fact]
    public void Plan_ReachesEachKindOfMoveAndRoute()
    {
        var plan = CleanupWarmup.Plan();
        Assert.False(plan.CategoriesPending);
        Assert.Contains(plan.Moves, x => x.Kind == CleanupKind.Unruled && x.SourceId == "unruled");
        Assert.Contains(plan.Moves, x => x.Kind == CleanupKind.Rejected && x.SourceId == "wood" && x.Identity.Prefab == "WarmupCopper");
        Assert.Contains(plan.Moves, x => x.Kind == CleanupKind.Resort && x.SourceId == "metal" && x.DestinationId == "metal-first");
        Assert.Contains(plan.Moves, x => x.Route == MoveRoute.MucAdd && x.DestinationId == "food");
        Assert.DoesNotContain(plan.Moves, x => x.DestinationId == "metal-full");
        Assert.DoesNotContain(plan.Moves, x => x.SourceId == "held");
        Assert.Contains(plan.Moves, x => x.Identity.Prefab == "WarmupSword" && x.DestinationId == "swords");
    }

    [Fact]
    public async Task Plan_OffTheCallingThread_IsTheSamePlan()
    {
        var here = CleanupWarmup.Plan();
        var there = await Task.Run(CleanupWarmup.Plan);
        Assert.Equal(here.Moves.Select(x => (x.SourceId, x.DestinationId, x.Identity.Prefab, x.Units, x.Kind, x.Route)),
            there.Moves.Select(x => (x.SourceId, x.DestinationId, x.Identity.Prefab, x.Units, x.Kind, x.Route)));
    }
}
