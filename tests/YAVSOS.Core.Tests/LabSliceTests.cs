#if YAVSOS_LAB
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

/// <summary>
/// X63 (D145): the Lab build's slice limits above the production caps. Runs only with
/// <c>dotnet test --configuration Lab</c>; the Release build has no such factory.
/// </summary>
public sealed class LabSliceTests
{
    private static CatalogFact Item(int i) => new CatalogFact(CatalogFactKind.Item, "Item" + i,
        new[] { new KeyValuePair<string, string>("type", "Material") });

    [Fact]
    public void CatalogLabSlice_GoesPastTheCapsAndKeepsEveryOtherLimit()
    {
        var limits = new CatalogLimits(maxItems: 100, buildSeconds: 20).WithLabSlice(1024, 16);
        Assert.Equal((1024, 16d), (limits.StepsPerTick, limits.SliceMilliseconds));
        Assert.Equal((100, 32768, 131072, 32 * 1024 * 1024, 20d), (limits.MaxItems, limits.MaxRecipes, limits.MaxRows, limits.MaxScalarBytes, limits.BuildSeconds));
        // The production constructor keeps its caps in the Lab build too.
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(stepsPerTick: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(sliceMilliseconds: 2.5));
    }

    [Fact]
    public void SourceLabSlice_GoesPastTheCapsAndKeepsEveryOtherLimit()
    {
        var limits = new LoadedSourceLimits(maxFacts: 100, buildSeconds: 60).WithLabSlice(1024, 16);
        Assert.Equal((1024, 16d), (limits.StepsPerTick, limits.SliceMilliseconds));
        Assert.Equal((100, 8 * 1024 * 1024, 60d), (limits.MaxFacts, limits.MaxScalarBytes, limits.BuildSeconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(stepsPerTick: 257));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(4097, 8)]
    [InlineData(256, 0)]
    [InlineData(256, -1)]
    [InlineData(256, 50.5)]
    [InlineData(256, double.NaN)]
    [InlineData(256, double.PositiveInfinity)]
    public void LabSlice_RejectsValuesOutsideItsBounds(int steps, double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits().WithLabSlice(steps, milliseconds));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits().WithLabSlice(steps, milliseconds));
    }

    [Theory]
    [InlineData(64, 2, 64)]     // today's catalog slice: the step limit binds with a clock that stands still
    [InlineData(1024, 16, 1024)]
    public void CatalogCache_TakesTheLabStepsInOneFrame(int steps, double milliseconds, int expected)
    {
        var limits = new CatalogLimits().WithLabSlice(steps, milliseconds);
        using var cache = new CatalogCache(limits, (rows, token) => Task.FromResult(CatalogSnapshot.Build(rows, limits, token)), () => 0);
        cache.BeginSession(true);
        var reads = 0;
        IEnumerable<CatalogFact?> Capture() { for (var i = 0; i < 2000; i++) { reads++; yield return Item(i); } }
        cache.Tick(Capture);
        Assert.Equal(expected, reads);
        Assert.Equal(CatalogState.Capturing, cache.State);
    }

    [Fact]
    public void CatalogCache_StopsTheLabSliceAtItsMilliseconds()
    {
        var now = 0d;
        var limits = new CatalogLimits().WithLabSlice(1024, 16);
        using var cache = new CatalogCache(limits, (rows, token) => Task.FromResult(CatalogSnapshot.Build(rows, limits, token)), () => now);
        cache.BeginSession(true);
        var reads = 0;
        IEnumerable<CatalogFact?> Capture() { for (var i = 0; i < 2000; i++) { reads++; now += 1; yield return Item(i); } }
        cache.Tick(Capture);
        Assert.Equal(16, reads); // each step takes 1 ms; the 17th would start at 16 ms
        Assert.Equal(1, cache.MaximumStepMilliseconds);
    }
}
#endif
