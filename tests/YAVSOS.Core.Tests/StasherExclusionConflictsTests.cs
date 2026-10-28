using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class StasherExclusionConflictsTests
{
    [Fact]
    public void Find_CountsOnlyAcceptedAndExcludedItems_InOrder()
    {
        var items = new[] { "SwordIron", "SwordBronze", "Wood", "SwordBronze", "AxeBronze" };
        var result = StasherExclusionConflicts.Find(items, x => x.StartsWith("Sword", StringComparison.Ordinal) || x == "AxeBronze",
            x => !x.StartsWith("Sword", StringComparison.Ordinal));
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "SwordBronze", "SwordIron" }, result.Examples);
    }

    [Fact]
    public void Find_LimitsExamplesButCountsAll()
    {
        var items = Enumerable.Range(0, 12).Select(x => "Item" + x.ToString("D2"));
        var result = StasherExclusionConflicts.Find(items, _ => true, _ => false);
        Assert.Equal(12, result.Count);
        Assert.Equal(StasherExclusionConflicts.MaximumExamples, result.Examples.Count);
        Assert.Equal("Item00", result.Examples[0]);
        Assert.Equal(12, result.Items.Count); // The item grid marks every one, not just the examples.
        Assert.Equal("Item11", result.Items[11]);
    }

    [Fact]
    public void Find_NoExclusionsMeansNoConflicts()
    {
        var result = StasherExclusionConflicts.Find(new[] { "SwordIron" }, _ => true, _ => true);
        Assert.Equal(0, result.Count); Assert.Empty(result.Examples);
    }
}
