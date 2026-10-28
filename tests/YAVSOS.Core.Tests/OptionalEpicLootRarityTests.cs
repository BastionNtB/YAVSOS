using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class OptionalEpicLootRarityTests
{
    private static readonly OptionalCapability Supported = new("epicloot", OptionalCapabilityState.Supported, "0.14.13", "test probe");
    private const string Key = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
    private static CatalogSnapshot Catalog() => CatalogSnapshot.Build(new[] {
        ClassificationContractFixture.Item("DustEpic", ammo: "Epic|MagicCraftingMaterial"),
        ClassificationContractFixture.Item("MagicSword", type: "OneHandedWeapon", ammo: "Epic|MagicCraftingMaterial") });
    private static IReadOnlyDictionary<string, ClassificationResult> Classify(CatalogSnapshot catalog, OptionalCapability capability) =>
        ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(),
            LoadedSourceReceipt.Build(catalog, OptionalEpicLootRarity.MaterialFacts(catalog, capability)).Snapshot).Index.Items;
    private static IReadOnlyDictionary<string, string> Data(string payload) =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["unrelated"] = "kept", [Key] = payload });
    private static ClassificationAcceptance Rarity(params string[] values) => new(new[] {
        new ClassificationFilterRule(DefaultClassificationTaxonomy.Create(), Array.Empty<string>(),
            new[] { new KeyValuePair<string, IEnumerable<string>>("rarity", values) }) });

    [Theory]
    [InlineData("Material", "Epic|MagicCraftingMaterial", "epic")]
    [InlineData("Material", "Ancient|Runestone", "ancient")]
    [InlineData("Material", "Rare|EtchedRunestone", "rare")]
    [InlineData("Material", "MagicCraftingMaterial", null)]
    [InlineData("Material", "epic|MagicCraftingMaterial", null)]
    [InlineData("Material", "Epic||MagicCraftingMaterial", null)]
    [InlineData("Material", "Red|Epic|ShardStone", null)]
    [InlineData("Material", "Epic|Arrow", null)]
    [InlineData("Misc", "Epic|MagicCraftingMaterial", null)]
    public void MaterialRarity_AcceptsOnlyExactEpicLootMaterialTags(string type, string ammo, string? expected)
    {
        Assert.Equal(expected, OptionalEpicLootRarity.MaterialRarity(ClassificationContractFixture.Item("X", type: type, ammo: ammo)));
    }

    [Fact]
    public void MaterialFacts_AddStaticMaterialRarityButNeverPrefabEquipmentRarity()
    {
        var items = Classify(Catalog(), Supported);

        var rarity = Assert.Single(items["DustEpic"].Facets, x => x.Dimension == "rarity");
        Assert.Equal("epic", rarity.Value);
        Assert.Contains("Epic|MagicCraftingMaterial", rarity.Reason);
        Assert.True(items["MagicSword"].InstanceRarity);
        Assert.DoesNotContain(items["MagicSword"].Facets, x => x.Dimension == "rarity");
    }

    [Theory]
    [InlineData(OptionalCapabilityState.Absent)]
    [InlineData(OptionalCapabilityState.Incompatible)]
    public void UnsupportedCapability_KeepsMaterialAndInstanceRarityUnknown(OptionalCapabilityState state)
    {
        var capability = state == OptionalCapabilityState.Absent ? OptionalCapability.Absent("epicloot")
            : OptionalCapability.Incompatible("epicloot", "0.14.14", "supported EpicLoot version is 0.14.13");
        var catalog = Catalog();
        var rows = OptionalEpicLootRarity.MaterialFacts(catalog, capability);
        var items = Classify(catalog, capability);

        Assert.Equal(LoadedSourceFactKind.Coverage, Assert.Single(rows).Kind);
        Assert.DoesNotContain(items["DustEpic"].Facets, x => x.Dimension == "rarity");
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(capability, items["MagicSword"], Data("{\"Version\":3,\"Rarity\":2}")));
    }

    [Fact]
    public void ReadInstance_ReturnsTheCurrentRarityOnEveryCallForAcceptance()
    {
        var sword = Classify(Catalog(), Supported)["MagicSword"];
        var stored = new Dictionary<string, string> {
            [Key] = "{\"Version\":3,\"Rarity\":2,\"Effects\":[{\"EffectType\":\"ModifyDamage\",\"EffectValue\":1.5e0}]," +
                "\"DisplayName\":\"Edge \\\"\\u0041\\\"\",\"LegendaryID\":null,\"IsUnidentified\":false,\"Sockets\":[]}" };

        var epic = Assert.Single(OptionalEpicLootRarity.ReadInstance(Supported, sword, stored));
        Assert.Equal(("rarity", "epic"), (epic.Dimension, epic.Value));
        Assert.Equal(AcceptanceMatch.Filters, Rarity("epic").Match(sword, new[] { epic }));

        // A changed instance is observed by the next read; nothing from the previous call is retained.
        stored[Key] = "{ \"Rarity\" : \"Mythic\", \"Version\" : 3 }";
        var mythic = OptionalEpicLootRarity.ReadInstance(Supported, sword, stored);
        Assert.Equal("mythic", Assert.Single(mythic).Value);
        Assert.Equal(AcceptanceMatch.None, Rarity("epic").Match(sword, mythic));

        stored.Remove(Key);
        var none = OptionalEpicLootRarity.ReadInstance(Supported, sword, stored);
        Assert.Empty(none);
        Assert.Equal(AcceptanceMatch.Filters, Rarity("unknown").Match(sword, none));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"Version\":3,\"Rarity\":1}]")]
    [InlineData("{\"Version\":2,\"Rarity\":1}")]
    [InlineData("{\"Version\":3,\"Rarity\":6}")]
    [InlineData("{\"Version\":3,\"Rarity\":-1}")]
    [InlineData("{\"Version\":3,\"Rarity\":1.0}")]
    [InlineData("{\"Version\":3,\"Rarity\":\"epic\"}")]
    [InlineData("{\"Version\":3,\"rarity\":1}")]
    [InlineData("{\"Version\":3,\"Rarity\":1,\"Rarity\":2}")]
    [InlineData("{\"Version\":3,\"Rarity\":1")]
    [InlineData("{\"Version\":3,\"Rarity\":1} trailing")]
    [InlineData("{\"Version\":3,\"Rarity\":01}")]
    [InlineData("{\"Version\":3,\"Rarity\":1,\"Name\":\"bad\u0001\"}")]
    public void ReadInstance_MalformedOrUnsupportedPayloadStaysUnknownWithoutMutation(string payload)
    {
        var sword = Classify(Catalog(), Supported)["MagicSword"];
        var data = Data(payload);
        var before = data.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();

        Assert.Empty(OptionalEpicLootRarity.ReadInstance(Supported, sword, data));
        Assert.Equal(before, data.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ReadInstance_DepthAndPayloadBoundsStayUnknown()
    {
        var sword = Classify(Catalog(), Supported)["MagicSword"];
        var deep = "{\"Version\":3,\"Rarity\":1,\"Effects\":" + new string('[', 40) + new string(']', 40) + "}";
        var large = "{\"Version\":3,\"Rarity\":1,\"DisplayName\":\"" + new string('a', OptionalEpicLootRarity.MaximumInstancePayload) + "\"}";
        var nested = "{\"Version\":3,\"Rarity\":1,\"Effects\":" + new string('[', 20) + new string(']', 20) + "}";

        Assert.Empty(OptionalEpicLootRarity.ReadInstance(Supported, sword, Data(deep)));
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(Supported, sword, Data(large)));
        Assert.Equal("rare", Assert.Single(OptionalEpicLootRarity.ReadInstance(Supported, sword, Data(nested))).Value);
    }

    [Fact]
    public void ReadInstance_StaticItemWrongAdapterOrMissingDataReturnsNoFact()
    {
        var items = Classify(Catalog(), Supported);
        var payload = Data("{\"Version\":3,\"Rarity\":3}");
        var jotunn = new OptionalCapability("jotunn", OptionalCapabilityState.Supported, "2.30.2", "test probe");

        // An empty result keeps Match's instance-rarity contract valid for static items.
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(Supported, items["DustEpic"], payload));
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(jotunn, items["MagicSword"], payload));
        Assert.Empty(OptionalEpicLootRarity.ReadInstance(Supported, items["MagicSword"], null));
        Assert.Equal("legendary", Assert.Single(OptionalEpicLootRarity.ReadInstance(Supported, items["MagicSword"], payload)).Value);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ReadInstance_EnforcesTheExactPayloadBoundary(int offset, bool known)
    {
        const string prefix = "{\"Version\":3,\"Rarity\":1,\"DisplayName\":\"";
        var payload = prefix + new string('a', OptionalEpicLootRarity.MaximumInstancePayload + offset - prefix.Length - 2) + "\"}";
        var sword = Classify(Catalog(), Supported)["MagicSword"];
        Assert.Equal(known ? 1 : 0, OptionalEpicLootRarity.ReadInstance(Supported, sword, Data(payload)).Count);
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void ReadInstance_EnforcesTheExactNestingBoundary(int depth, bool known)
    {
        var payload = "{\"Version\":3,\"Rarity\":1,\"Effects\":" + new string('[', depth) + new string(']', depth) + "}";
        var sword = Classify(Catalog(), Supported)["MagicSword"];
        Assert.Equal(known ? 1 : 0, OptionalEpicLootRarity.ReadInstance(Supported, sword, Data(payload)).Count);
    }
}
