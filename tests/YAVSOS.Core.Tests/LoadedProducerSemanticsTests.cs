using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class LoadedProducerSemanticsTests
{
    [Theory]
    [InlineData(100f, 1f, true)]
    [InlineData(2f, 0f, true)]
    [InlineData(1f, 1f, false)]
    [InlineData(0f, 1f, false)]
    [InlineData(100f, -1f, false)]
    [InlineData(float.NaN, 1f, false)]
    [InlineData(float.PositiveInfinity, 1f, false)]
    [InlineData(100f, float.NaN, false)]
    [InlineData(100f, float.PositiveInfinity, false)]
    [InlineData(2147483648f, 0f, false)]
    public void ResourceRoot_RequiresADrainableFiniteInitialLevelAndSafeRegeneration(float maximum, float regeneration, bool expected) =>
        Assert.Equal(expected, LoadedProducerSemantics.ResourceRootSupportsExtraction(maximum, regeneration));

    private static IncineratorConversionShape Shape(int priority, bool oneOf, params (string Key, int Amount)[] inputs) =>
        new(priority, oneOf, inputs.Select(x => new KeyValuePair<string, int>(x.Key, x.Amount)));
    private static Dictionary<string, int> Inventory(params (string Key, int Count)[] rows) => rows.ToDictionary(x => x.Key, x => x.Count);

    [Fact]
    public void IncineratorFires_UsesIntegerQuotientsForAllAndOneOf()
    {
        var all = Shape(0, false, ("$a", 2), ("$b", 1)); var any = Shape(0, true, ("$a", 2), ("$b", 3));
        Assert.True(LoadedProducerSemantics.IncineratorFires(all, Inventory(("$a", 2), ("$b", 1))));
        Assert.False(LoadedProducerSemantics.IncineratorFires(all, Inventory(("$a", 1), ("$b", 5))));
        Assert.False(LoadedProducerSemantics.IncineratorFires(all, Inventory(("$a", 9))));
        Assert.True(LoadedProducerSemantics.IncineratorFires(any, Inventory(("$b", 3))));
        Assert.False(LoadedProducerSemantics.IncineratorFires(any, Inventory(("$a", 1), ("$b", 2))));
        Assert.False(LoadedProducerSemantics.IncineratorFires(Shape(0, true), Inventory(("$a", 9))));
        Assert.Throws<ArgumentException>(() => LoadedProducerSemantics.IncineratorFires(Shape(0, false), Inventory()));
    }

    [Fact]
    public void IncineratorAll_IsReachableUnlessEqualOrHigherPriorityFiresOnMinimalInventory()
    {
        var target = Shape(5, false, ("$a", 1), ("$b", 1));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target }, 0, -1));
        // Lower priority overlap runs later; higher priority needing an absent item or a larger quotient does not fire.
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(4, false, ("$a", 1)) }, 0, -1));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { Shape(9, false, ("$a", 1), ("$c", 1)), target }, 1, -1));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { Shape(9, false, ("$a", 2)), target }, 1, -1));
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { Shape(9, false, ("$a", 1)), target }, 1, -1));
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { Shape(9, true, ("$z", 1), ("$b", 1)), target }, 1, -1));
        // Awake's unstable sort may run an equal priority first.
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(5, false, ("$b", 1)) }, 0, -1));
    }

    [Fact]
    public void IncineratorAll_DuplicateKeysNeedTheLargestAmount()
    {
        var target = Shape(0, false, ("$a", 1), ("$a", 3));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(1, false, ("$a", 4)) }, 0, -1));
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(1, false, ("$a", 3)) }, 0, -1));
    }

    [Fact]
    public void IncineratorOneOf_EvaluatesEachAlternativeSeparately()
    {
        var target = Shape(0, true, ("$a", 1), ("$b", 2));
        var conversions = new[] { Shape(1, false, ("$a", 1)), target };
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 1, 0));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(conversions, 2, -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IncineratorUnsafeConversion_DisqualifiesEveryAlternative(int amount)
    {
        var target = Shape(9, false, ("$a", 1));
        Assert.False(Shape(0, true, ("$b", amount)).Safe);
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(0, true, ("$b", amount)) }, 0, -1));
        // An empty AND set crafts int.MaxValue results; an empty one-of set never fires and is harmless.
        Assert.False(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(0, false) }, 0, -1));
        Assert.True(LoadedProducerSemantics.IncineratorAlternativeReachable(new[] { target, Shape(10, true) }, 0, -1));
    }

    [Fact]
    public void IncineratorShapes_AreBoundedAndRejectEmptyKeys()
    {
        Assert.Throws<ArgumentException>(() => Shape(0, false, ("", 1)));
        Assert.Throws<ArgumentException>(() => Shape(0, false, Enumerable.Range(0, 257).Select(i => ("$" + i, 1)).ToArray()));
        var wide = Enumerable.Range(0, 2).Select(_ => Shape(0, true, Enumerable.Range(0, 129).Select(i => ("$" + i, 1)).ToArray())).ToArray();
        Assert.Throws<ArgumentException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(wide, 0, 0));
        var many = Enumerable.Range(0, 257).Select(_ => Shape(0, true)).ToArray();
        Assert.Throws<ArgumentException>(() => LoadedProducerSemantics.IncineratorAlternativeReachable(many, 0, 0));
        var source = new List<KeyValuePair<string, int>> { new("$a", 1) };
        var detached = new IncineratorConversionShape(0, false, source); source.Clear();
        Assert.Single(detached.Requirements);
    }

    [Fact]
    public void IncineratorShape_StopsReadingAtTheRejectionBound()
    {
        var read = 0;
        IEnumerable<KeyValuePair<string, int>> Endless()
        {
            while (true) { read++; yield return new KeyValuePair<string, int>("$a", 1); }
        }
        Assert.Throws<ArgumentException>(() => new IncineratorConversionShape(0, false, Endless()));
        Assert.Equal(LoadedProducerSemantics.MaxIncineratorRequirements + 1, read);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(-1f, true)]
    [InlineData(0.5f, true)]
    [InlineData(0.9999f, true)]
    [InlineData(1f, false)]
    [InlineData(1.5f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void ProcreationInitiation_NeedsAChanceBelowOne(float chance, bool expected) =>
        Assert.Equal(expected, LoadedProducerSemantics.ProcreationInitiates(chance));

    [Theory]
    [InlineData(-1f, true)]
    [InlineData(0f, true)]
    [InlineData(600f, true)]
    [InlineData(float.MaxValue, true)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    [InlineData(float.NegativeInfinity, false)]
    public void ProcreationCompletion_RequiresAFiniteDuration(float duration, bool expected) =>
        Assert.Equal(expected, LoadedProducerSemantics.ProcreationCompletes(duration));

    [Theory]
    [InlineData(3, true, false, false, false, true)]   // parent + same-prefab partner
    [InlineData(2, true, false, false, false, false)]
    [InlineData(2, false, false, false, false, true)]  // no-partner output: parent alone
    [InlineData(1, false, false, false, false, false)]
    [InlineData(2, true, true, false, false, true)]    // separate partner prefab is not counted
    [InlineData(3, true, true, false, true, true)]     // ...unless it is also the offspring prefab
    [InlineData(2, true, true, false, true, false)]
    [InlineData(5, true, false, true, false, true)]    // offspring prefab equal to parent counts both again
    [InlineData(4, true, false, true, false, false)]
    public void ProcreationCapacity_CountsTheMinimalInitiatingPopulation(int max, bool partner, bool separate,
        bool offspringIsParent, bool offspringIsPartner, bool expected) =>
        Assert.Equal(expected, LoadedProducerSemantics.ProcreationCapacity(max, partner, separate, offspringIsParent, offspringIsPartner));

    [Theory]
    [InlineData(5, 50, 100, 5)]
    [InlineData(80, 50, 0, 50)]
    [InlineData(0, 50, 10, 0)]
    [InlineData(-3, 50, 10, 0)]
    [InlineData(5, 0, 10, 0)]
    [InlineData(5, 50, -1, 0)]
    public void PurchaseAmount_CapsByMaximumStackAndRejectsNegativePrice(int stack, int max, int price, int expected) =>
        Assert.Equal(expected, LoadedProducerSemantics.PurchaseAmount(stack, max, price));
}
