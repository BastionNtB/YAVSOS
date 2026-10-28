using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class DefaultItemSemanticsTests
{
    private const string DefaultSource = "default-item-semantics";

    [Theory]
    [InlineData("Wood", "materials.wood")]
    [InlineData("Frostwood", "materials.wood")]
    [InlineData("IronOre", "materials.ore")]
    [InlineData("CopperScrap", "materials.ore")]
    [InlineData("Gold", "materials.metal")]
    [InlineData("Steel_TW", "materials.metal")]
    [InlineData("Stone", "materials.stone")]
    [InlineData("Diamond_TW", "materials.stone")]
    [InlineData("ScaleHide", "materials.hides")]
    [InlineData("HareScaleHide", "materials.hides")]
    [InlineData("LinenThread", "materials.textile")]
    [InlineData("WoolYarn_Teal_TW", "materials.textile")]
    [InlineData("FabricBundle_Blackforest_TW", "materials.textile")]
    [InlineData("MorgenSinew", "materials.creature")]
    [InlineData("AsksvinCarrionRibcage", "materials.creature")]
    [InlineData("BronzeNails", "materials.components")]
    [InlineData("MechanicalSpring", "materials.components")]
    [InlineData("BlackCore", "materials.cores")]
    [InlineData("Staffcore_Mistlands_TW", "materials.cores")]
    [InlineData("MoldArmormediumChest", "materials.moulds")]
    [InlineData("MoldSmallParts", "materials.moulds")]
    [InlineData("DyrnwynHiltFragment", "materials.upgrades")]
    [InlineData("GenericMoldUncooked", "materials.processing")]
    [InlineData("HildirKey_mountaincave", "special.keys")]
    [InlineData("RuneThurisaz_TW", "special.runes")]
    [InlineData("LastBossGate_RuneTile", "special.runes")]
    public void Build_ExactReviewedDefinitionAddsItsSemanticGroup(string prefab, string expected)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        var evidence = Assert.Single(result.Memberships, x => x.Source == DefaultSource && x.Category == expected);
        Assert.Contains(prefab, evidence.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("misc", Categories(result));
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("Material")]
    [InlineData("Misc")]
    public void Build_ResourceDefinitionsAllowOnlyTheTwoResourceContexts(string type)
    {
        var results = Build(new[] { Item("YagluthDrop", type), Item("ScytheHandle", type), Item("FrozenKingDrop", type) }).Items;
        Assert.Equal(new[] { "materials.cores" }, Categories(results["YagluthDrop"]));
        Assert.Equal(new[] { "materials.components" }, Categories(results["ScytheHandle"]));
        Assert.Equal(new[] { "special.offerings" }, Categories(results["FrozenKingDrop"]));
    }

    [Theory]
    [InlineData("Wood", "OneHandedWeapon")]
    [InlineData("Gold", "Chest")]
    [InlineData("FrozenKingDrop", "Consumable")]
    [InlineData("FireworksRocket_Blue", "Ammo")]
    [InlineData("DustMagic", "Helmet")]
    [InlineData("MoldSmallParts", "Tool")]
    [InlineData("Upgrader1Weapon", "UnknownModType")]
    [InlineData("ShardQueen_TW", "Trophy")]
    [InlineData("Wood", "material")]
    public void Build_KnownNameWithMismatchedTypeAddsNoDefaultEvidence(string prefab, string type)
    {
        var index = Build(new[] { Item(prefab, type) });
        if (type == "Customization") { Assert.Empty(index.Items); return; }
        Assert.DoesNotContain(index.Items[prefab].Memberships, x => x.Source == DefaultSource);
    }

    [Theory]
    [InlineData("wood")]
    [InlineData("Wood(Clone)")]
    [InlineData("Wood_TW")]
    [InlineData("NewGold")]
    [InlineData("MoldArmorMediumChest")]
    [InlineData("MoldFuture")]
    [InlineData("Upgrader8Weapon")]
    [InlineData("UnrelatedUncooked")]
    [InlineData("ShardFuture_TW")]
    [InlineData("UnknownShardMagic")]
    [InlineData("Dustmagic")]
    [InlineData("BlueGoldKey")]
    [InlineData("FireworksRocket_Orange")]
    [InlineData("UnknownRegisteredItem")]
    [InlineData("flax")]
    [InlineData("Flax_TW")]
    [InlineData("CrownJewel_TW")]
    [InlineData("DyeBlue_TW")]
    [InlineData("GoblinScraps")]
    [InlineData("CharredSkull")]
    public void Build_UnknownCaseAndLookalikeNamesStayAtFallback(string prefab)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        Assert.Equal(new[] { "misc" }, Categories(result));
        Assert.Equal("fallback", Assert.Single(result.Memberships).Source);
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("BarkaBranch", "materials.wood", "Valheim 1.0.17 item description")]
    [InlineData("CrownJewel", "materials.stone", "Valheim 1.0.17 item description")]
    [InlineData("Charredskull", "materials.creature", "Valheim 1.0.17 item description")]
    [InlineData("WrithanRoots", "materials.creature", "Valheim 1.0.17 item description")]
    [InlineData("CuredSquirrelHamstring", "materials.creature", "Valheim 1.0.17 item description")]
    [InlineData("Flax", "materials.textile", "Valheim 1.0.17 item description")]
    [InlineData("BlobVial", "materials.components", "Valheim 1.0.17 item description")]
    [InlineData("GoblinScraps_TW", "materials.textile", "Warfare 1.9.5 English item description")]
    public void Build_ResidualDescribedDefinitionAddsOnlyItsMaterialFamily(string prefab, string expected, string receipt)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        Assert.Equal(new[] { expected }, Categories(result));
        var evidence = Assert.Single(result.Memberships, x => !CategoryClosure.IsDerived(x));
        Assert.Equal(DefaultSource, evidence.Source);
        Assert.Equal("Exact definition: " + prefab + "; " + receipt + ".", evidence.Reason);
        Assert.Equal(expected, result.Primary);
        Assert.Empty(result.Facets);
        Assert.False(result.InstanceRarity);
    }

    [Theory]
    [InlineData("CrownJewel", "Misc", "materials.stone")]
    [InlineData("GoblinScraps_TW", "Misc", "materials.textile")]
    public void Build_ResidualDescribedDefinitionAcceptsTheMiscResourceContext(string prefab, string type, string expected)
    {
        var result = Build(new[] { Item(prefab, type) }).Items[prefab];
        Assert.Equal(new[] { expected }, Categories(result));
    }

    [Theory]
    [InlineData("Flax", "Consumable")]
    [InlineData("CrownJewel", "Helmet")]
    [InlineData("BlobVial", "Ammo")]
    [InlineData("Charredskull", "Trophy")]
    [InlineData("GoblinScraps_TW", "Customization")]
    public void Build_ResidualDescribedDefinitionWithNonResourceTypeAddsNoDefaultEvidence(string prefab, string type)
    {
        var index = Build(new[] { Item(prefab, type) });
        if (type == "Customization") { Assert.Empty(index.Items); return; }
        Assert.DoesNotContain(index.Items[prefab].Memberships, x => x.Source == DefaultSource);
    }

    // Retained residuals: their supported meaning is a use (fuel, feast/brewing/cooking input,
    // building, offering) or is undefined, so only fresh source routes may classify them.
    // Feast and catapult payload consumers are root-owned and deliberately not asserted here.
    [Theory]
    [InlineData("SpiceForests")]
    [InlineData("SpiceAshlands")]
    [InlineData("Coal")]
    [InlineData("Resin")]
    [InlineData("Tar")]
    [InlineData("ProustitePowder")]
    [InlineData("CharcoalResin")]
    [InlineData("FrozenFuel")]
    [InlineData("MemorialCoal")]
    [InlineData("OozeMork")]
    [InlineData("PowderedDragonEgg")]
    [InlineData("PungentPebbles")]
    [InlineData("FragrantBundle")]
    [InlineData("FreshSeaweed")]
    [InlineData("Dandelion")]
    [InlineData("BoneMawSerpentMeat")]
    [InlineData("UncookedBerryCakePlains_TW")]
    [InlineData("QueenBee")]
    [InlineData("Ironpit")]
    [InlineData("Bell", "Misc")]
    [InlineData("BarberKit", "Misc")]
    [InlineData("HatefulBlood", "Misc")]
    [InlineData("VegvisirShard_Bonemass")]
    public void Build_UnsupportedResidualDefinitionsReceiveNoDefaultEvidence(string prefab, string type = "Material")
    {
        var result = Build(new[] { Item(prefab, type) }).Items[prefab];
        Assert.Equal(new[] { "misc" }, Categories(result));
        Assert.Equal("fallback", Assert.Single(result.Memberships).Source);
        Assert.Empty(result.Facets);
    }

    [Fact]
    public void Build_WarfareDescribedResidualsClaimNoModOwnershipOrAssociation()
    {
        var results = Build(new[] { Item("GoblinScraps_TW"), Item("DyeBlack_TW") }).Items;
        Assert.All(results.Values, result =>
        {
            Assert.DoesNotContain(Categories(result), x => x.StartsWith("mod.", StringComparison.Ordinal));
            Assert.Empty(result.Facets);
            Assert.True(ClassificationFilters.Matches(result, Rule(filters: new[] { ("mod", new[] { "unknown" }) })));
            Assert.False(ClassificationFilters.Matches(result, Rule(filters: new[] { ("mod", new[] { "warfare" }) })));
        });
    }




    [Theory]
    [InlineData("Fader_Legendary_ShardStone")]
    [InlineData("Eikthyr_Magic_ShardStone")]
    [InlineData("Firewalker_Rare_ShardStone")]
    [InlineData("Unknown_Ancient_ShardStone")]
    [InlineData("Red_Impossible_ShardStone")]
    [InlineData("Red_magic_ShardStone")]
    public void Build_UndefinedSocketFamilyRarityCombinationsStayUnknown(string prefab)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        Assert.Equal(new[] { "misc" }, Categories(result));
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("ShardElder_TW", "Material", "magic.wizardry.shards")]
    [InlineData("ShardQueen_TW", "Material", "magic.wizardry.shards")]
    [InlineData("EnchantedWoodBlackForest_TW", "Material", "magic.wizardry.enchanted-wood")]
    [InlineData("EnchantedWoodMistlands_TW", "Material", "magic.wizardry.enchanted-wood")]
    [InlineData("ArcaneScroll_BlackForest_TW", "Material", "magic.wizardry.scrolls")]
    [InlineData("ArcaneScroll_Mistlands_TW", "Material", "magic.wizardry.scrolls")]
    [InlineData("ArcaneScroll_DamageBuff_TW", "Consumable", "magic.wizardry.scrolls")]
    [InlineData("ArcaneScroll_SpeedBuff_TW", "Consumable", "magic.wizardry.scrolls")]
    [InlineData("ArcaneScroll_JumpBuff_TW", "Consumable", "magic.wizardry.scrolls")]
    [InlineData("ArcaneScroll_SlowfallBuff_TW", "Consumable", "magic.wizardry.scrolls")]
    public void Build_ExactWizardryDefinitionsUseTheirCapturedTypeContext(string prefab, string type, string category)
    {
        var result = Build(new[] { Item(prefab, type) }).Items[prefab];
        Assert.Equal(new[] { "magic.materials", category }, Categories(result));
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("ArcaneScroll_DamageBuff_TW", "Material")]
    [InlineData("ArcaneScroll_BlackForest_TW", "Consumable")]
    public void Build_WizardryChangedTypeCannotReuseTheOtherScrollContext(string prefab, string type)
    {
        var index = Build(new[] { Item(prefab, type) });
        if (type == "Customization") { Assert.Empty(index.Items); return; }
        Assert.DoesNotContain(index.Items[prefab].Memberships, x => x.Source == DefaultSource);
    }

    [Theory]
    [InlineData("FoxPelt_TW", "materials.hides")]
    [InlineData("ProwlerFang_TW", "materials.creature")]
    [InlineData("RazorbackLeather_TW", "materials.hides")]
    public void Build_MonstrumAssociationDoesNotClaimResourceRegistrationOwnership(string prefab, string category)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        var expected = new List<string> { category, "mod.monstrum" };
        if (prefab == "FoxPelt_TW") expected.Add("materials.crafting"); // D042 E07 compatibility patch
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), Categories(result).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(result.Facets);
        Assert.True(ClassificationFilters.Matches(result, Rule(filters: new[] { ("mod", new[] { "unknown" }) })));
        Assert.False(ClassificationFilters.Matches(result, Rule(filters: new[] { ("mod", new[] { "monstrum" }) })));
    }

    [Fact]
    public void Build_MonstrumTrophyAssociationRequiresTrophyTypeAndExactIdentity()
    {
        var results = Build(new[] { Item("TrophyProwler_TW", "Trophy"), Item("TrophyFox_TW"), Item("TrophyOther_TW", "Trophy") }).Items;
        Assert.Equal(new[] { "mod.monstrum", "trophies" }, Categories(results["TrophyProwler_TW"]));
        Assert.Equal(new[] { "misc" }, Categories(results["TrophyFox_TW"]));
        Assert.Equal(new[] { "trophies" }, Categories(results["TrophyOther_TW"]));
        Assert.All(results.Values, x => Assert.Empty(x.Facets));
    }

    [Fact]
    public void Build_DefaultsNeverInventRegistrationsOrStaticEquipmentRarity()
    {
        var result = Build(new[] { Item("SwordDustAncient", "OneHandedWeapon"), Item("DustAncient") });
        Assert.Equal(new[] { "DustAncient", "SwordDustAncient" }, result.Items.Keys);
        Assert.True(result.Items["SwordDustAncient"].InstanceRarity);
        Assert.DoesNotContain(result.Items["SwordDustAncient"].Memberships, x => x.Source == DefaultSource);
        Assert.All(result.Items.Values, x => Assert.Empty(x.Facets));
        Assert.True(ClassificationFilters.Matches(result.Items["SwordDustAncient"], Rule(filters: new[] { ("rarity", new[] { "unknown" }) })));
        Assert.False(ClassificationFilters.Matches(result.Items["DustAncient"], Rule(filters: new[] { ("rarity", new[] { "ancient" }) })));
    }

    [Fact]
    public void Build_DefaultEvidenceIsDeterministicUniqueAndBoundedAcrossRepeatedBuilds()
    {
        var first = Build(new[] { Item("FoxPelt_TW"), Item("ShardQueen_TW"), Item("Wood") });
        var second = Build(new[] { Item("Wood"), Item("ShardQueen_TW"), Item("FoxPelt_TW") });
        Assert.Equal(first.InputFingerprint, second.InputFingerprint);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        foreach (var prefab in first.Items.Keys)
        {
            var members = first.Items[prefab].Memberships;
            Assert.Equal(members.Count, members.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(members.Select(x => (x.Category, x.Source, x.Reason)), second.Items[prefab].Memberships.Select(x => (x.Category, x.Source, x.Reason)));
            Assert.All(members, x => { Assert.InRange(x.Source.Length, 1, 64); Assert.InRange(x.Reason.Length, 1, 256); });
        }
        var member = Assert.Single(first.Items["Wood"].Memberships, x => !CategoryClosure.IsDerived(x));
        var single = Build(new[] { Item("Wood") }, evidence: new[] { new ItemClassificationEvidence("Wood", new[] { member }) });
        var duplicate = Build(new[] { Item("Wood") }, evidence: new[] { new ItemClassificationEvidence("Wood", new[] { member, member }) });
        Assert.Equal(single.InputFingerprint, duplicate.InputFingerprint);
        Assert.Equal(single.Fingerprint, duplicate.Fingerprint);
        Assert.Single(duplicate.Items["Wood"].Memberships, x => !CategoryClosure.IsDerived(x));
    }

    [Theory]
    [InlineData("Diamond_TW")]
    [InlineData("Onyx_TW")]
    [InlineData("FoxPelt_TW")]
    public void Build_E07SpawnRecipeCompatibilityPatch_AddsCraftingMaterialOnDedicatedServers(string prefab)
    {
        var result = Build(new[] { Item(prefab) }).Items[prefab];
        var patch = Assert.Single(result.Memberships, x => x.Category == "materials.crafting" && x.Source == DefaultSource);
        Assert.Contains("E07", patch.Reason, StringComparison.Ordinal);
        Assert.NotEqual("materials.crafting", result.Primary);
    }
}
