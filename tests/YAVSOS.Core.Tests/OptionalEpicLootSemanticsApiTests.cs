using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;
using EpicLoot;
using EpicLoot.ShardStones;
using Registry = EpicLoot.ShardStones.Shards.ShardDefinitions;

namespace YAVSOS.Core.Tests;

public sealed class OptionalEpicLootSemanticsApiTests
{
    private static OptionalCapability Probe(out OptionalEpicLootSemanticsApi api)
    {
        var capability = OptionalEpicLootSemanticsApi.Probe("0.14.13", OptionalReflectionTests.EpicLoot(), out var result);
        Assert.True(capability.Supported, capability.Diagnostic); api = result!; return capability;
    }

    [Fact]
    public void Probe_IsMetadataOnlyAndCaptureUsesActiveDefinitionsWithoutModifyingThem()
    {
        var first = new ShardDefinition { Rarities = new() { ItemRarity.Magic, ItemRarity.Ancient } };
        Registry.Current = new() { [ShardType.Custom] = first, [ShardType.Red] = new() { Rarities = new() { ItemRarity.Rare } } };
        var reads = Registry.Reads; Probe(out var api);
        Assert.Equal(reads, Registry.Reads);
        api.BeginCapture(); api.CheckDefinitions();
        Assert.Equal(new[] { "Custom|Ancient", "Custom|Magic", "Red|Rare" }, api.Pairs.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Same(first, Registry.Current[ShardType.Custom]);
        Assert.Equal(new[] { ItemRarity.Magic, ItemRarity.Ancient }, first.Rarities);
        Assert.False(api.Faulted);
        Registry.Current = new() { [ShardType.Custom] = new() { Rarities = new() { ItemRarity.Epic } } };
        api.BeginCapture();
        Assert.Equal(new[] { "Custom|Epic" }, api.Pairs);
    }

    [Fact]
    public void Probe_AbsentExposesNoReader()
    {
        var capability = OptionalEpicLootSemanticsApi.Probe(null, OptionalReflectionTests.EpicLoot(), out var api);
        Assert.False(capability.Supported); Assert.Null(api);
        Assert.Equal(OptionalCapabilityState.Absent, capability.State);
    }

    // D058: another version with the verified shape is supported.
    [Fact]
    public void Probe_OtherVersionWithMatchingShapeExposesReader()
    {
        var capability = OptionalEpicLootSemanticsApi.Probe("0.14.14", OptionalReflectionTests.EpicLoot(), out var api);
        Assert.True(capability.Supported); Assert.NotNull(api);
    }

    [Theory]
    [InlineData("EpicLoot.ShardStones.ShardType")]
    [InlineData("EpicLoot.ShardStones.ShardDefinition")]
    [InlineData("EpicLoot.ShardStones.Shards+ShardDefinitions")]
    public void Probe_MissingSemanticShapeLeavesExistingRarityCapabilityIndependent(string name)
    {
        var resolver = OptionalReflectionTests.EpicLoot((name, null));
        var semantics = OptionalEpicLootSemanticsApi.Probe("0.14.13", resolver, out var api);
        Assert.Equal(OptionalCapabilityState.Incompatible, semantics.State); Assert.Null(api);
        Assert.True(OptionalEpicLootApi.Probe("0.14.13", resolver).Supported);
    }

    [Fact]
    public void CaptureFault_DisablesSemanticEvidenceWithoutSwallowingSessionFreshnessErrors()
    {
        var initial = Probe(out var api);
        Registry.ThrowOnRead = true;
        try { api.ReadDefinition(() => { }, api.BeginCapture); }
        finally { Registry.ThrowOnRead = false; }
        Assert.True(api.Faulted);
        Assert.Equal(OptionalCapabilityState.Incompatible, api.CaptureCapability(initial).State);
        var called = false; api.ReadDefinition(() => { }, () => called = true);
        Assert.False(called);
        Assert.Throws<InvalidDataException>(() => api.ReadDefinition(() => throw new InvalidDataException("session changed"), () => called = true));
        Assert.False(called);
    }

    [Fact]
    public void StartCapture_RecoversNextGenerationButDoesNotReuseFaultedPairs()
    {
        var initial = Probe(out var api);
        Registry.Current = new() { [ShardType.Red] = new() { Rarities = new() { ItemRarity.Magic } } };
        api.StartCapture(() => { });
        Assert.Equal(new[] { "Red|Magic" }, api.Pairs);
        Registry.ThrowOnRead = true;
        try { api.StartCapture(() => { }); }
        finally { Registry.ThrowOnRead = false; }
        Assert.True(api.Faulted); Assert.Empty(api.Pairs);
        Registry.Current = new() { [ShardType.Custom] = new() { Rarities = new() { ItemRarity.Epic } } };
        api.StartCapture(() => { });
        Assert.False(api.Faulted); Assert.True(api.CaptureCapability(initial).Supported);
        Assert.Equal(new[] { "Custom|Epic" }, api.Pairs);
        Assert.Throws<InvalidDataException>(() => api.StartCapture(() => throw new InvalidDataException("stale session")));
    }

    [Fact]
    public void Capture_RegistryReplacementAndListChangesInvalidateTheSnapshot()
    {
        Registry.Current = new() { [ShardType.Red] = new() { Rarities = new() { ItemRarity.Magic } } };
        Probe(out var api); api.BeginCapture();
        Registry.Current[ShardType.Red].Rarities.Add(ItemRarity.Rare);
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
        api.BeginCapture();
        Registry.Current[ShardType.Red].Rarities[0] = ItemRarity.Ancient;
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
        api.BeginCapture();
        Registry.Current[ShardType.Red].Rarities = new() { ItemRarity.Magic, ItemRarity.Rare };
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
        api.BeginCapture();
        Registry.Current = new(Registry.Current);
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
    }

    [Fact]
    public void CheckDefinitions_RepeatedGuardAllocationDoesNotScaleWithRarityCount()
    {
        Registry.Current = new() { [ShardType.Red] = new() { Rarities = new() { ItemRarity.Magic } } };
        Probe(out var api); api.BeginCapture();
        static long Allocated(OptionalEpicLootSemanticsApi reader)
        {
            for (var i = 0; i < 8; i++) reader.CheckDefinitions();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 64; i++) reader.CheckDefinitions();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var singleton = Allocated(api);
        Registry.Current[ShardType.Red].Rarities = Enumerable.Repeat(ItemRarity.Magic, 64).ToList();
        api.BeginCapture();
        var full = Allocated(api);
        Assert.True(full <= singleton + 1024, $"Guard allocation scales with list values: singleton={singleton}, full={full}.");
        Assert.Equal(64, Registry.Current[ShardType.Red].Rarities.Count);
        Assert.Equal(new[] { "Red|Magic" }, api.Pairs);
    }

    [Fact]
    public void CheckDefinitions_DetectsLastValueAndDefinitionChangesWithUnchangedCounts()
    {
        Registry.Current = new() { [ShardType.Red] = new() { Rarities = Enumerable.Repeat(ItemRarity.Magic, 64).ToList() } };
        Probe(out var api); api.BeginCapture(); api.CheckDefinitions();
        Registry.Current[ShardType.Red].Rarities[63] = ItemRarity.Rare;
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
        api.BeginCapture(); api.CheckDefinitions();
        Registry.Current[ShardType.Red] = new() { Rarities = Registry.Current[ShardType.Red].Rarities };
        Assert.Throws<InvalidOperationException>(api.CheckDefinitions);
        api.StartCapture(() => { }); api.CheckDefinitions();
        Assert.Equal(new[] { "Red|Magic", "Red|Rare" }, api.Pairs.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Capture_EmptyAndMalformedDefinitionsStayBoundedAndDoNotInventPairs()
    {
        Registry.Current = new(); Probe(out var api); api.BeginCapture(); api.CheckDefinitions();
        Assert.Empty(api.Pairs);
        Registry.Current[ShardType.Red] = new() { Rarities = new() { (ItemRarity)99 } };
        Assert.Throws<InvalidOperationException>(api.BeginCapture);
        Registry.Current[ShardType.Red].Rarities = Enumerable.Repeat(ItemRarity.Magic, 64).ToList();
        api.BeginCapture(); Assert.Equal(new[] { "Red|Magic" }, api.Pairs);
        Registry.Current[ShardType.Red].Rarities.Add(ItemRarity.Rare);
        Assert.Throws<InvalidOperationException>(api.BeginCapture);
        Registry.Current = new() { [(ShardType)99] = new() };
        Assert.Throws<InvalidOperationException>(api.BeginCapture);
    }
}
