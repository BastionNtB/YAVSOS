using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Tests.OptionalFakes;
using YAVSOS.Integrations;

namespace YAVSOS.Core.Tests;

public sealed class OptionalReflectionTests
{
    private static readonly FakeGameObject Sword = new(), Wall = new(), Wolf = new(), Fallback = new();

    static OptionalReflectionTests()
    {
        FakeItemManager.Instance.Items["OwnedSword"] = new FakeItem(Sword, new FakePlugin("com.example.armory"));
        FakeItemManager.Instance.Items["Unattributed"] = new FakeItem(Fallback, null);
        FakePieceManager.Instance.Pieces["ExampleWall"] = new FakePiece(Wall, new FakePlugin("com.example.builder"));
        FakeCreatureManager.Instance.Creatures.Add(("ExampleWolf", new FakeCreature(Wolf, new FakePlugin("com.example.monsters"))));
    }

    private static OptionalTypeResolver Jotunn(params (string Name, Type? Type)[] overrides)
    {
        var map = new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["UnityEngine.GameObject"] = typeof(FakeGameObject), ["BepInEx.BepInPlugin"] = typeof(FakePlugin),
            ["Jotunn.Entities.CustomEntity"] = typeof(FakeEntity), ["Jotunn.Entities.CustomItem"] = typeof(FakeItem),
            ["Jotunn.Entities.CustomPiece"] = typeof(FakePiece), ["Jotunn.Entities.CustomCreature"] = typeof(FakeCreature),
            ["Jotunn.Managers.ItemManager"] = typeof(FakeItemManager), ["Jotunn.Managers.PieceManager"] = typeof(FakePieceManager),
            ["Jotunn.Managers.CreatureManager"] = typeof(FakeCreatureManager)
        };
        foreach (var (name, type) in overrides) map[name] = type;
        return name => map.TryGetValue(name, out var type) ? type : null;
    }
    internal static OptionalTypeResolver EpicLoot(params (string Name, Type? Type)[] overrides)
    {
        var map = new Dictionary<string, Type?>(StringComparer.Ordinal)
        {
            ["EpicLoot.MagicItemComponent"] = typeof(global::EpicLoot.MagicItemComponent),
            ["EpicLoot.Data.CustomItemData"] = typeof(global::EpicLoot.Data.CustomItemData),
            ["EpicLoot.Data.ItemInfo"] = typeof(global::EpicLoot.Data.ItemInfo), ["EpicLoot.MagicItem"] = typeof(global::EpicLoot.MagicItem),
            ["EpicLoot.ItemRarity"] = typeof(global::EpicLoot.ItemRarity),
            ["EpicLoot.Crafting.CraftingItemExtensions"] = typeof(global::EpicLoot.Crafting.CraftingItemExtensions),
            ["ItemDrop+ItemData"] = typeof(FakeItemData),
            ["EpicLoot.ShardStones.ShardType"] = typeof(global::EpicLoot.ShardStones.ShardType),
            ["EpicLoot.ShardStones.ShardDefinition"] = typeof(global::EpicLoot.ShardStones.ShardDefinition),
            ["EpicLoot.ShardStones.Shards+ShardDefinitions"] = typeof(global::EpicLoot.ShardStones.Shards.ShardDefinitions)
        };
        foreach (var (name, type) in overrides) map[name] = type;
        return name => map.TryGetValue(name, out var type) ? type : null;
    }

    [Fact]
    public void JotunnProbe_ReportsOwnerOnlyForTheExactRegisteredLoadedObject()
    {
        var capability = OptionalJotunnApi.Probe("2.30.2", Jotunn(), out var api);
        var registrations = (FakeItemManager.Instance.Items.Count, FakePieceManager.Instance.Pieces.Count, FakeCreatureManager.Instance.Creatures.Count);

        Assert.Equal(OptionalCapabilityState.Supported, capability.State);
        Assert.NotNull(api);
        Assert.Equal("com.example.armory", api!.ItemOwner("OwnedSword", Sword));
        Assert.Equal("com.example.builder", api.PieceOwner("ExampleWall", Wall));
        Assert.Equal("com.example.monsters", api.CreatureOwner("ExampleWolf", Wolf));
        // Same name with another object, an absent registration or missing metadata is not ownership.
        Assert.Null(api.ItemOwner("OwnedSword", new FakeGameObject()));
        Assert.Null(api.ItemOwner("OwnedSword(Clone)", Sword));
        Assert.Null(api.ItemOwner("Unattributed", Fallback));
        Assert.Null(api.PieceOwner("OwnedSword", Sword));
        Assert.False(api.Faulted);
        Assert.Equal(registrations, (FakeItemManager.Instance.Items.Count, FakePieceManager.Instance.Pieces.Count, FakeCreatureManager.Instance.Creatures.Count));
    }

    [Fact]
    public void JotunnProbe_AbsentPublishesNoApi()
    {
        var capability = OptionalJotunnApi.Probe(null, Jotunn(), out var api);

        Assert.Equal(OptionalCapabilityState.Absent, capability.State);
        Assert.Null(api);
    }

    // D058: another version with the verified shape is supported; only the shape decides.
    [Theory]
    [InlineData("2.30.1")]
    [InlineData("2.31.0")]
    public void JotunnProbe_OtherVersionWithMatchingShapeIsSupported(string version)
    {
        var capability = OptionalJotunnApi.Probe(version, Jotunn(), out var api);

        Assert.Equal(OptionalCapabilityState.Supported, capability.State);
        Assert.Equal(version, capability.ObservedVersion);
        Assert.NotNull(api);
    }

    [Theory]
    [InlineData("Jotunn.Managers.CreatureManager", null)]
    [InlineData("Jotunn.Managers.ItemManager", typeof(FakeUntypedItemManager))]
    [InlineData("Jotunn.Entities.CustomItem", typeof(FakePiece))]
    [InlineData("BepInEx.BepInPlugin", typeof(FakeGameObject))]
    public void JotunnProbe_ChangedApiShapeIsIncompatible(string name, Type? replacement)
    {
        var capability = OptionalJotunnApi.Probe("2.30.2", Jotunn((name, replacement)), out var api);

        Assert.Equal(OptionalCapabilityState.Incompatible, capability.State);
        Assert.StartsWith("API shape:", capability.Diagnostic);
        Assert.Null(api);
    }

    [Fact]
    public void JotunnLookupFault_DisablesTheAdapterWithoutThrowing()
    {
        var capability = OptionalJotunnApi.Probe("2.30.2", Jotunn(("Jotunn.Managers.CreatureManager", typeof(FakeThrowingCreatureManager))), out var api);
        Assert.True(capability.Supported);

        Assert.Null(api!.CreatureOwner("ExampleWolf", Wolf));
        Assert.True(api.Faulted);
        Assert.Equal(nameof(InvalidOperationException), api.Fault);
        // Later lookups stay closed so a capture cannot mix pre- and post-fault ownership.
        Assert.Null(api.ItemOwner("OwnedSword", Sword));
    }

    [Fact]
    public void OptionalProbe_MissingTransitiveAssemblyIsIncompatibleWithoutThrowing()
    {
        OptionalTypeResolver unavailable = _ => throw new FileNotFoundException("optional dependency");
        Assert.Equal(OptionalCapabilityState.Incompatible, OptionalJotunnApi.Probe("2.30.2", unavailable, out var api).State);
        Assert.Null(api);
        Assert.Equal(OptionalCapabilityState.Incompatible, OptionalEpicLootApi.Probe("0.14.13", unavailable).State);
    }

    [Fact]
    public void MalformedOptionalDefinition_DiscardsPartialOwnershipAndPreservesOrdinaryEvidence()
    {
        var initial = OptionalJotunnApi.Probe("2.30.2", Jotunn(), out var api);
        var catalog = LoadedSourceReceiptTests.MakeCatalog("OwnedSword");
        var observations = new List<OptionalDefinitionObservation>();
        api!.ReadDefinition(() => { }, () =>
        {
            observations.Add(new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, "OwnedSword",
                api.ItemOwner("OwnedSword", Sword)!, "OwnedSword"));
        });
        Assert.Single(observations);
        api.ReadDefinition(() => { }, () => throw new InvalidDataException("Null character drop."));
        api.ReadDefinition(() => { }, () => throw new Exception("Must not run after the fault."));
        var capability = api.CaptureCapability(initial);
        Assert.Equal(OptionalCapabilityState.Incompatible, capability.State);
        Assert.Equal(nameof(InvalidDataException), api.Fault);
        var rows = OptionalDefinitionEvidence.Build(catalog, capability, observations)
            .Concat(new[] { new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "ordinary", output: "OwnedSword", route: "piece", method: "building") });
        var receipt = LoadedSourceReceipt.Build(catalog, rows);
        var result = ClassificationPipeline.Build(catalog, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), receipt.Snapshot).Index.Items["OwnedSword"];
        Assert.Contains(result.Memberships, m => m.Category == "materials.building");
        Assert.Contains(result.Facets, f => f.Dimension == "usage" && f.Value == "building");
        Assert.DoesNotContain(result.Facets, f => f.Dimension is "mod" or "related-mod" or "construction-mod");
    }

    [Fact]
    public void OptionalDefinitionBoundary_DoesNotSwallowExternalFreshnessFailure()
    {
        OptionalJotunnApi.Probe("2.30.2", Jotunn(), out var api);
        var visited = false;
        Assert.Throws<InvalidDataException>(() => api!.ReadDefinition(
            () => throw new InvalidDataException("registry changed"), () => visited = true));
        Assert.False(visited);
        Assert.False(api!.Faulted);
    }

    [Fact]
    public void EpicLootProbe_SupportedShapeNamesTheReadOnlyStorageContract()
    {
        var capability = OptionalEpicLootApi.Probe("0.14.13", EpicLoot());

        Assert.Equal(OptionalCapabilityState.Supported, capability.State);
        Assert.Equal("0.14.13", capability.ObservedVersion);
        Assert.Contains(OptionalEpicLootRarity.InstanceDataKey, capability.Diagnostic);
    }

    [Theory]
    [InlineData(null, null, null, OptionalCapabilityState.Absent)]
    [InlineData("0.14.13", "EpicLoot.Crafting.CraftingItemExtensions", null, OptionalCapabilityState.Incompatible)]
    [InlineData("0.14.13", "EpicLoot.ItemRarity", typeof(FakeOtherRarity), OptionalCapabilityState.Incompatible)]
    [InlineData("0.14.13", "EpicLoot.Data.CustomItemData", typeof(FakeEntity), OptionalCapabilityState.Incompatible)]
    [InlineData("0.14.13", "ItemDrop+ItemData", typeof(FakeGameObject), OptionalCapabilityState.Incompatible)]
    public void EpicLootProbe_AbsentVersionOrShapeMismatchStaysUnknown(string? version, string? name, Type? replacement, OptionalCapabilityState expected)
    {
        var resolve = name == null ? EpicLoot() : EpicLoot((name, replacement));

        var capability = OptionalEpicLootApi.Probe(version, resolve);

        Assert.Equal(expected, capability.State);
        Assert.False(capability.Supported);
    }

    [Fact]
    public void EpicLootProbe_PublishedKeyShapeIsSupportedWithoutTheOldBaseTypes()
    {
        // P8-20: EpicLoot 0.15.4 dropped CustomItemData and ItemInfo; the key is published as MagicItemData.Key.
        var resolve = EpicLoot(("EpicLoot.MagicItemData", typeof(FakeMagicItemData)), ("EpicLoot.MagicItemComponent", typeof(FakeStandaloneMagicItemComponent)),
            ("EpicLoot.Data.CustomItemData", null), ("EpicLoot.Data.ItemInfo", null));

        var capability = OptionalEpicLootApi.Probe("0.15.4", resolve);

        Assert.Equal(OptionalCapabilityState.Supported, capability.State);
        Assert.Contains(OptionalEpicLootRarity.InstanceDataKey, capability.Diagnostic);
    }

    [Fact]
    public void EpicLootProbe_PublishedKeyThatDiffersIsIncompatible()
    {
        var resolve = EpicLoot(("EpicLoot.MagicItemData", typeof(FakeOtherMagicItemData)), ("EpicLoot.MagicItemComponent", typeof(FakeStandaloneMagicItemComponent)),
            ("EpicLoot.Data.CustomItemData", null), ("EpicLoot.Data.ItemInfo", null));

        var capability = OptionalEpicLootApi.Probe("0.15.4", resolve);

        Assert.Equal(OptionalCapabilityState.Incompatible, capability.State);
        Assert.Contains("MagicItemData.Key", capability.Diagnostic);
    }

    [Fact]
    public void EpicLootProbe_NeitherKeyShapeIsIncompatible()
    {
        var resolve = EpicLoot(("EpicLoot.MagicItemComponent", typeof(FakeStandaloneMagicItemComponent)), ("EpicLoot.Data.ItemInfo", null));

        Assert.Equal(OptionalCapabilityState.Incompatible, OptionalEpicLootApi.Probe("0.15.4", resolve).State);
    }

    [Theory]
    [InlineData("0.14.12")]
    [InlineData("0.15.0")]
    public void EpicLootProbe_OtherVersionWithMatchingShapeIsSupported(string version)
    {
        var capability = OptionalEpicLootApi.Probe(version, EpicLoot());

        Assert.True(capability.Supported);
        Assert.Equal(version, capability.ObservedVersion);
    }
}
