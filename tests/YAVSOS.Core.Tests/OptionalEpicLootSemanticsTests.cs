using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class OptionalEpicLootSemanticsTests
{
    private static readonly OptionalCapability Supported = new(OptionalEpicLootSemantics.Adapter,
        OptionalCapabilityState.Supported, "0.14.13", "supported test shape");
    private static HashSet<string> Pairs(params string[] values) => new(values, StringComparer.Ordinal);
    private static CatalogFact Definition(string name, string type, string tag, string key = "")
    {
        var item = Item(name, type, ammo: tag);
        return new CatalogFact(item.Kind, item.Identity, item.Values.Concat(new[] { new KeyValuePair<string, string>("nameKey", key) }));
    }
    private static ClassificationResult Classify(CatalogFact item, string[]? pairs = null, string? payload = null,
        OptionalCapability? capability = null)
    {
        var catalog = CatalogSnapshot.Build(new[] { item });
        var row = OptionalEpicLootSemantics.Observe(item, capability ?? Supported, Pairs(pairs ?? Array.Empty<string>()), payload);
        var receipt = LoadedSourceReceipt.Build(catalog, row == null ? Array.Empty<LoadedSourceFact>() : new[] { row });
        return ClassificationPipeline.Build(catalog, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), receipt.Snapshot).Index.Items[item.Identity];
    }

    public static IEnumerable<object[]> CraftingFamilies()
    {
        foreach (var rarity in new[] { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient" })
            foreach (var family in new[] { ("dust", "magic.epicloot.dust"), ("shard", "magic.epicloot.shards"),
                ("reagent", "magic.epicloot.reagents"), ("essence", "magic.epicloot.essence") })
                yield return new object[] { rarity, family.Item1, family.Item2 };
    }

    [Theory]
    [MemberData(nameof(CraftingFamilies))]
    public void Observe_NewIdentityUsesStructuredFamilyAndRarityKeys(string rarity, string family, string category)
    {
        var result = Classify(Definition("CompletelyNewResource", "Material", rarity + "|MagicCraftingMaterial",
            "$mod_epicloot_" + rarity + " $mod_epicloot_assets_" + family));
        Assert.Equal(new[] { category, "magic.materials" }, Categories(result));
        Assert.Equal(category, result.Primary);
        Assert.All(result.Memberships.Where(x => !CategoryClosure.IsDerived(x)), m => Assert.StartsWith("loaded-use:", m.Source));
        Assert.Empty(result.Facets); // Semantic role does not supply rarity/biome/ownership facets.
    }

    [Theory]
    [InlineData("Material", "Red|Magic|ShardStone", "Red|Magic", "magic.epicloot.socket-materials", "magic.materials")]
    [InlineData("Material", "Firewalker|Magic|ShardStone", "Firewalker|Magic", "magic.epicloot.socket-materials", "magic.materials")]
    [InlineData("Material", "Ancient|Runestone", "", "magic.epicloot.stones", "magic.materials")]
    [InlineData("Misc", "Rare|ShardSlotChisel", "", "equipment.tools", "magic.epicloot.socket-tools")]
    public void Observe_NewRegisteredVariantsFollowTagsAndCurrentConfiguredPairs(string type, string tag, string pair, string first, string second)
    {
        var result = Classify(Definition("NeverListedPrefab", type, tag), pair.Length == 0 ? null : new[] { pair });
        Assert.Equal(new[] { first, second }, Categories(result));
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("Misc", "Red|Magic|ShardStone", "")]
    [InlineData("Material", "red|Magic|ShardStone", "")]
    [InlineData("Material", "Red|magic|ShardStone", "")]
    [InlineData("Material", "Red|Magic|ShardStone|extra", "")]
    [InlineData("Material", "Red|Common|ShardStone", "")]
    [InlineData("Material", "_|ShardStone", "")]
    [InlineData("Material", "Epic|MagicCraftingMaterial", "$mod_epicloot_Rare $mod_epicloot_assets_dust")]
    [InlineData("Material", "Epic|MagicCraftingMaterial", "Epic Dust")]
    [InlineData("Material", "Epic|MagicCraftingMaterial", "$mod_epicloot_Epic $mod_epicloot_assets_future")]
    [InlineData("Misc", "Epic|MagicCraftingMaterial", "$mod_epicloot_Epic $mod_epicloot_assets_dust")]
    [InlineData("Tool", "Ancient|ShardSlotChisel", "")]
    [InlineData("Material", "Ancient|EtchedRunestone", "")]
    public void Observe_MalformedOrWrongContextEvidenceDoesNotGrantMembership(string type, string tag, string key)
    {
        var result = Classify(Definition("DustMagic", type, tag, key), new[] { "Red|Magic" });
        Assert.DoesNotContain(result.Memberships, m => m.Category.StartsWith("magic.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Memberships, m => m.Source == "default-item-semantics");
        Assert.Empty(result.Facets);
    }

    [Fact]
    public void Observe_RemovedPairAndAbsentCapabilityWithdrawMembershipWithoutExactFallback()
    {
        var item = Definition("Red_Magic_ShardStone", "Material", "Red|Magic|ShardStone");
        Assert.Equal("magic.epicloot.socket-materials", Classify(item, new[] { "Red|Magic" }).Primary);
        Assert.Equal("misc", Classify(item).Primary);
        foreach (var state in new[] { OptionalCapabilityState.Absent, OptionalCapabilityState.Incompatible })
            Assert.Equal("misc", Classify(item, new[] { "Red|Magic" }, capability:
                new OptionalCapability(OptionalEpicLootSemantics.Adapter, state, "", "unavailable")).Primary);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "red|Magic" };
        Assert.Null(OptionalEpicLootSemantics.Observe(item, Supported, insensitive));
    }

    [Theory]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":true}")]
    [InlineData("{\"IsUnidentified\":true,\"Rarity\":\"Ancient\",\"Version\":3,\"Effects\":[]}")]
    [InlineData("{\"Version\":3,\"Rarity\":2,\"IsUnident\\u0069fied\":true}")]
    public void Observe_UnidentifiedDefinitionFlagSupportsNewBiomeIdentityWithoutInventedFacets(string payload)
    {
        var result = Classify(Definition("CustomBiome_NewLoot", "Misc", "Epic|MagicCraftingMaterial"), payload: payload);
        Assert.Equal(new[] { "magic.epicloot.unidentified" }, Categories(result));
        Assert.Equal("magic.epicloot.unidentified", result.Primary);
        Assert.False(result.InstanceRarity);
        Assert.Empty(result.Facets);
        Assert.Equal("misc", Classify(Definition("CustomBiome_NewLoot", "Misc", "Epic|MagicCraftingMaterial")).Primary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{\"Version\":3,\"Rarity\":0}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":false}")]
    [InlineData("{\"Version\":4,\"Rarity\":0,\"IsUnidentified\":true}")]
    [InlineData("{\"Version\":3,\"Rarity\":6,\"IsUnidentified\":true}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":\"true\"}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":1}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"isunidentified\":true}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":true,\"IsUnidentified\":false}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":true,}")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":true}tail")]
    [InlineData("{\"Version\":3,\"Rarity\":0,\"Nested\":{\"IsUnidentified\":true}}")]
    public void DefinitionIsUnidentified_InvalidOrAmbiguousDataStaysUnknown(string? payload)
    {
        Assert.False(OptionalEpicLootSemantics.DefinitionIsUnidentified(payload));
        Assert.Equal("misc", Classify(Definition("Meadows_Magic_Unidentified", "Misc", "Epic|MagicCraftingMaterial"), payload: payload).Primary);
    }

    [Fact]
    public void DefinitionIsUnidentified_BoundsAndEquipmentContextPreventStaticRoleLeakage()
    {
        const string payload = "{\"Version\":3,\"Rarity\":0,\"IsUnidentified\":true}";
        Assert.True(OptionalEpicLootSemantics.DefinitionIsUnidentified(payload.PadRight(65535)));
        Assert.True(OptionalEpicLootSemantics.DefinitionIsUnidentified(payload.PadRight(65536)));
        Assert.False(OptionalEpicLootSemantics.DefinitionIsUnidentified(payload.PadRight(65537)));
        var result = Classify(Definition("Sword", "OneHandedWeapon", ""), payload: payload);
        Assert.Equal(new[] { "weapons", "weapons.swords" }, Categories(result));
        Assert.Empty(result.Facets);
        var excessive = Pairs(Enumerable.Range(0, 1024).Select(i => "Family" + i + "|Magic").ToArray());
        var atLimit = OptionalEpicLootSemantics.Observe(Definition("New", "Material", "Family1023|Magic|ShardStone"), Supported, excessive);
        Assert.NotNull(atLimit); Assert.Equal("epicloot-socket-materials", atLimit!.Method);
        excessive.Add("Family1024|Magic");
        Assert.Throws<ArgumentException>(() => OptionalEpicLootSemantics.Observe(Definition("New", "Material", ""), Supported, excessive));
    }

    [Fact]
    public void Pipeline_AutomaticEvidencePreservesOverridesCodecAndDeterminism()
    {
        var item = Definition("NewSocketTool", "Misc", "Ancient|ShardSlotChisel");
        var catalog = CatalogSnapshot.Build(new[] { item });
        var observation = OptionalEpicLootSemantics.Observe(item, Supported, Pairs())!;
        var rows = new[] { observation, new LoadedSourceFact(LoadedSourceFactKind.Coverage, "capability", detail: "supported") };
        var forward = LoadedSourceReceipt.Build(catalog, rows);
        Assert.Equal(forward.Snapshot.Fingerprint, LoadedSourceReceipt.Build(catalog, rows.Reverse()).Snapshot.Fingerprint);
        const string xml = """
            <policy schema="1" revision="2" xmlns="urn:com.bastionntb.yavsos:categories">
              <categories><category id="valuables" displayName="Valuables"/></categories>
              <assignments><assignment match="exact" selector="NewSocketTool"><member category="valuables"/></assignment></assignments>
            </policy>
            """;
        foreach (var document in new[] { CategoryPolicyCodec.EmptyDocument, xml })
        {
            Assert.True(CategoryPolicyCodec.TryParse(document, out var policy, out var error), error);
            var classified = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy!), forward.Snapshot);
            var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy!);
            var decoded = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy!);
            Assert.Equal(snapshot.Fingerprint, decoded.Fingerprint);
            Assert.Equal(document == xml ? new[] { "valuables" } : new[] { "equipment.tools", "magic.epicloot.socket-tools" }, Categories(decoded.Items[item.Identity]));
            Assert.Empty(decoded.Items[item.Identity].Facets);
        }
        Assert.Equal("misc", ClassificationPipeline.Build(catalog, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()),
            LoadedSourceReceipt.Build(catalog, Array.Empty<LoadedSourceFact>()).Snapshot).Index.Items[item.Identity].Primary);
        Assert.Throws<OperationCanceledException>(() => LoadedSourceReceipt.Build(catalog, rows, cancellation: new CancellationToken(true)));
    }

    [Theory]
    [InlineData("ArmorGoldMageChestUncooked", "cook", "materials.processing")]
    [InlineData("CryptKey", "door-key", "special.keys")]
    public void Pipeline_FormerUseDefaultsRequireCurrentConsumerEvidence(string prefab, string method, string expected)
    {
        var item = Item(prefab); var catalog = CatalogSnapshot.Build(new[] { item, Item("Output", "OneHandedWeapon") });
        var rows = method == "cook" ? new[] { new LoadedSourceFact(LoadedSourceFactKind.RouteInput, "conversion", output: "Output",
            input: prefab, route: "machine", method: "cook") } : new[] { new LoadedSourceFact(LoadedSourceFactKind.ItemUse,
            "door", output: prefab, route: "actual-door", method: method) };
        Assert.Equal(expected, ClassificationPipeline.Build(catalog, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()),
            LoadedSourceReceipt.Build(catalog, rows).Snapshot).Index.Items[prefab].Primary);
        Assert.Equal("misc", Build(new[] { item }).Items[prefab].Primary);
    }
}
