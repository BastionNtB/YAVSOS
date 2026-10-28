using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ResidualItemSemanticsTests
{
    [Theory]
    [InlineData("Material")]
    [InlineData("Misc")]
    public void Build_BlackDyeIsPigmentWithoutTextileOrOwnershipClaims(string type)
    {
        var result = Build(new[] { Item("DyeBlack_TW", type) }).Items["DyeBlack_TW"];
        Assert.Equal(new[] { "materials.pigments" }, Categories(result));
        Assert.Equal("materials.pigments", result.Primary);
        Assert.Equal("default-item-semantics", Assert.Single(result.Memberships, x => !CategoryClosure.IsDerived(x)).Source);
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("DyeBlack_TW", "Helmet")]
    [InlineData("DyeBlack_TW", "material")]
    [InlineData("dyeblack_TW", "Material")]
    [InlineData("DyeBlack_TW(Clone)", "Material")]
    public void Build_PigmentDefaultsRequireExactIdentityAndResourceType(string prefab, string type)
    {
        Assert.DoesNotContain(Build(new[] { Item(prefab, type) }).Items[prefab].Memberships,
            x => x.Category == "materials.pigments");
    }

    [Theory]
    [InlineData("OozeMork", "BombBlob_Morkhalla")]
    [InlineData("ProustitePowder", "BombBlob_Lava")]
    [InlineData("Tar", "BombBlob_Tar")]
    public void Build_ResidualBombResourcesFollowCurrentRecipesWithoutCompositionClaims(string input, string output)
    {
        var facts = new[] { Item(input), Item(output, "OneHandedWeapon"), Recipe(output: output), Requirement(input: input) };
        var result = Build(facts).Items[input];
        Assert.Equal(new[] { "ingredients.bombs", "materials.crafting" }, Categories(result));
        Assert.Equal("ingredients.bombs", result.Primary);
        var member = Assert.Single(result.Memberships, x => x.Category == "ingredients.bombs");
        Assert.Equal("loaded-recipe:creation", member.Source);
        Assert.Equal("r", member.Reason);
        var facet = Assert.Single(result.Facets);
        Assert.Equal(("usage", "crafting"), (facet.Dimension, facet.Value));
        Assert.Equal("misc", Build(facts.Take(2)).Items[input].Primary);
    }

    [Theory]
    [InlineData("BombBlob_Poison", "OneHandedWeapon")]
    [InlineData("BombBlob_PoisonElite", "OneHandedWeapon")]
    [InlineData("BombBlob_Tar", "OneHandedWeapon")]
    [InlineData("BombBlob_Lava", "OneHandedWeapon")]
    [InlineData("BombBlob_Frost", "OneHandedWeapon")]
    [InlineData("BombBlob_Morkhalla", "OneHandedWeapon")]
    public void Build_ReviewedCreatureBombOutputsAdmitNewResourceIngredients(string output, string type)
    {
        var facts = new[] { Item("NewModResource", "Misc"), Item(output, type), Recipe(output: output), Requirement(input: "NewModResource") };
        var index = Build(facts);
        Assert.Equal("ingredients.bombs", index.Items["NewModResource"].Primary);
        Assert.Equal(new[] { output, "NewModResource" }.OrderBy(x => x, StringComparer.Ordinal), index.Items.Keys);
        Assert.DoesNotContain(index.Items[output].Memberships, x => x.Category == "ingredients.bombs");
        Assert.Equal(index.Fingerprint, Build(facts.Reverse()).Fingerprint);
    }

    [Theory]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", false, false, false, 1, 1, false)]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", true, true, false, 1, 1, false)]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", true, false, true, 1, 1, false)]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", true, false, false, 0, 1, false)]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", true, false, false, 1, 0, false)]
    [InlineData("BombBlob_Tar", "OneHandedWeapon", true, false, false, 1, 1, true)]
    [InlineData("BombBlob_Tar", "Material", true, false, false, 1, 1, false)]
    [InlineData("BombSiege", "OneHandedWeapon", true, false, false, 1, 1, false)]
    [InlineData("bombBlob_Tar", "OneHandedWeapon", true, false, false, 1, 1, false)]
    [InlineData("BombBlob_Tar(Clone)", "OneHandedWeapon", true, false, false, 1, 1, false)]
    [InlineData("FutureBomb", "OneHandedWeapon", true, false, false, 1, 1, false)]
    [InlineData("StaffClusterbomb", "TwoHandedWeapon", true, false, false, 1, 1, false)]
    public void Build_UnsupportedBombDefinitionsAndNonCreationPathsSupplyNoBombMembership(string output, string type,
        bool enabled, bool upgrade, bool refiner, int outputAmount, int inputAmount, bool refinement)
    {
        var result = Build(new[] { Item("Tar"), Item(output, type),
            Recipe(output: output, enabled: enabled, upgrade: upgrade, refiner: refiner, amount: outputAmount),
            Requirement(input: "Tar", amount: inputAmount, refinement: refinement, upgrade: 2) }).Items["Tar"];
        Assert.DoesNotContain(result.Memberships, x => x.Category == "ingredients.bombs");
    }

    [Theory]
    [InlineData("Helmet", 0)]
    [InlineData("Material", 10)]
    public void Build_NonResourceOrEdibleInputsAreNotBombIngredients(string type, double food)
    {
        var result = Build(new[] { Item("Input", type, health: food, duration: food), Item("BombBlob_Tar", "OneHandedWeapon"),
            Recipe(output: "BombBlob_Tar"), Requirement(input: "Input") }).Items["Input"];
        Assert.DoesNotContain(result.Memberships, x => x.Category == "ingredients.bombs");
    }

    [Fact]
    public void Build_BombMembershipPreservesMaterialPriorityAndOtherLoadedUses()
    {
        var result = Build(new[] { Item("Wood"), Item("Tar"), Item("BombBlob_Tar", "OneHandedWeapon"),
            Recipe(output: "BombBlob_Tar"), Requirement(input: "Wood"), Requirement("slot1", input: "Tar") },
            evidence: new[] { new ItemClassificationEvidence("Tar", new[] { new MembershipEvidence("materials.building", "loaded-use:test", "building consumer") }) });
        Assert.Equal("materials.wood", result.Items["Wood"].Primary);
        Assert.Contains(result.Items["Wood"].Memberships, x => x.Category == "ingredients.bombs");
        Assert.Equal(new[] { "ingredients.bombs", "materials.building", "materials.crafting" }, Categories(result.Items["Tar"]));
    }

    [Fact]
    public void Build_NewCategoriesSurviveWireRoundTripAndSchema1Replacement()
    {
        const string xml = """
            <policy schema="1" revision="2" xmlns="urn:com.bastionntb.yavsos:categories">
              <categories><category id="valuables" displayName="Valuables"/></categories>
              <assignments><assignment match="exact" selector="DyeBlack_TW">
                <member category="valuables"/>
              </assignment></assignments>
            </policy>
            """;
        var catalog = CatalogSnapshot.Build(new[] { Item("DyeBlack_TW"), Item("Tar"), Item("BombBlob_Tar", "OneHandedWeapon"),
            Recipe(output: "BombBlob_Tar"), Requirement(input: "Tar") });
        foreach (var document in new[] { CategoryPolicyCodec.EmptyDocument, xml })
        {
            Assert.True(CategoryPolicyCodec.TryParse(document, out var policy, out var error), error);
            var index = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy!));
            var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, index, policy!);
            var decoded = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy!);
            Assert.Equal(snapshot.Fingerprint, decoded.Fingerprint);
            Assert.Equal(new[] { document == xml ? "valuables" : "materials.pigments" }, Categories(decoded.Items["DyeBlack_TW"]));
            Assert.Equal("ingredients.bombs", decoded.Items["Tar"].Primary);
        }
    }




    [Theory]
    [InlineData("ForestToken")]
    [InlineData("IronBountyToken")]
    [InlineData("GoldBountyToken")]
    public void Build_AdventureTokensAreCurrencyWithoutClaimingMetalCompositionOrAcquisition(string prefab)
    {
        var result = Build(new[] { Item(prefab, "Misc") }).Items[prefab];
        Assert.Equal(new[] { "currency" }, Categories(result));
        Assert.Equal("currency", result.Primary);
        Assert.Equal("default-item-semantics", Assert.Single(result.Memberships).Source);
        Assert.Empty(result.Facets);
    }

    [Theory]
    [InlineData("Meadows_Magic_Unidentified", "Helmet")]
    [InlineData("ShardSlotChiselAncient", "Tool")]
    [InlineData("ForestToken", "Consumable")]
    [InlineData("Meadows_Magic_Unidentified", "misc")]
    [InlineData("Meadows_Magic_Unidentified", "UnknownModType")]
    public void Build_ChangedDefinitionTypesCannotReuseResourceDefaults(string prefab, string type)
    {
        var result = Build(new[] { Item(prefab, type) }).Items[prefab];
        Assert.DoesNotContain(result.Memberships, x => x.Source == "default-item-semantics");
    }

    [Theory]
    [InlineData("_Unidentified")]
    [InlineData("_ShardStone")]
    [InlineData("EL_DummyPrefab")]
    [InlineData("VegvisirShard_Bonemass")]
    [InlineData("FutureBiome_Magic_Unidentified")]
    [InlineData("Meadows_Common_Unidentified")]
    [InlineData("Meadows_Magic_Unidentified(Clone)")]
    [InlineData("meadows_Magic_Unidentified")]
    [InlineData("Ashlands_Magic_Unidentified")]
    [InlineData("ShardSlotChiselRare")]
    [InlineData("ShardSlotChiselAncient_TW")]
    [InlineData("GoldBountyToken_TW")]
    public void Build_TemplatesInternalDefinitionsAndLookalikesRetainFallback(string prefab)
    {
        var result = Build(new[] { Item(prefab, "Misc") }).Items[prefab];
        Assert.Equal(new[] { "misc" }, Categories(result));
        Assert.Equal("fallback", Assert.Single(result.Memberships).Source);
        Assert.Empty(result.Facets);
    }


    [Fact]
    public void Build_ExtendedTaxonomyPreservesCurrencyIdentityAndRegisteredItemSet()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create();
        Assert.Equal("Coins & tokens", taxonomy.Categories["currency"].Name);
        var facts = new[] { Item("ForestToken", "Misc"), Item("Meadows_Rare_Unidentified", "Misc"), Item("Unknown", "Misc") };
        var forward = Build(facts);
        var reverse = Build(facts.Reverse());
        Assert.Equal(new[] { "ForestToken", "Meadows_Rare_Unidentified", "Unknown" }, forward.Items.Keys);
        Assert.Equal(forward.InputFingerprint, reverse.InputFingerprint);
        Assert.Equal(forward.Fingerprint, reverse.Fingerprint);
        Assert.Equal("misc", forward.Items["Unknown"].Primary);
        Assert.False(forward.Items.ContainsKey("ShardSlotChiselAncient"));
    }
}
