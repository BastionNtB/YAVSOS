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

public sealed class ClassificationIndexTests
{
    [Fact]
    public void Build_ContextualPropertiesRetainUncertainItemsAndExcludeCustomization()
    {
        var result = Build(new[] { Item(), Item("Coins", value: 1), Item("Amber", value: 10), Item("Hair", "Customization", icon: false),
            Item("Unknown", quest: true, icon: false), Item("EffectMaterial", effect: "effect") });
        Assert.Equal(new[] { "Amber", "Coins", "EffectMaterial", "Unknown", "Wood" }, result.Items.Keys);
        Assert.Equal(new[] { "valuables" }, Categories(result.Items["Coins"])); // Currency requires current payment evidence.
        Assert.Equal(new[] { "valuables" }, Categories(result.Items["Amber"]));
        Assert.Equal(new[] { "materials.wood" }, Categories(result.Items["Wood"])); // Default Swords skill is not a weapon fact.
        Assert.Equal(new[] { "misc" }, Categories(result.Items["EffectMaterial"])); // Effect alone is not a potion.
        Assert.DoesNotContain("Hair", result.Items.Keys);
        Assert.True(result.Items["Unknown"].Quest); Assert.False(result.Items["Unknown"].HasIcon);
        Assert.Equal("special.quest", result.Items["Unknown"].Primary);
        Assert.All(result.Items.Values, item => Assert.DoesNotContain(item.Facets, f => f.Dimension == "biome"));
    }

    [Theory]
    [InlineData("OneHandedWeapon", "Swords", "weapons.swords")]
    [InlineData("TwoHandedWeapon", "Axes", "weapons.axes")]
    [InlineData("TwoHandedWeaponLeft", "ElementalMagic", "weapons.magic")]
    [InlineData("Bow", "Crossbows", "weapons.crossbows")]
    public void Build_WeaponContextAddsParentAndSpecificMembership(string type, string skill, string subgroup)
    {
        var result = Build(new[] { Item("weapon", type, skill) }).Items["weapon"];
        Assert.Equal(new[] { "weapons", subgroup }, Categories(result));
        Assert.Equal(subgroup, result.Primary); Assert.True(result.InstanceRarity);
    }

    // D125 (P9-26): the game makes a tankard a one-handed weapon with the default Swords skill and ammo type "mead";
    // its attack drinks a Consumable mead. Shapes below are the game's own (catalog dump 2026-10-13).
    private static CatalogFact Tankard(string id = "Tankard", string ammo = "mead") => Item(id, "OneHandedWeapon", "Swords", ammo: ammo);
    private static CatalogFact Mead(string id = "MeadHealthMinor", string ammo = "mead") => Item(id, "Consumable", effect: "Potion_health_minor", ammo: ammo);

    [Fact]
    public void Build_WeaponWhoseAmmoOnlyConsumablesSupplyIsADrinkingVesselInTheEquipmentRoot()
    {
        var index = Build(new[] { Tankard(), Tankard("TankardOdin"), Mead(), Mead("MeadStaminaMinor"), Item("SwordIron", "OneHandedWeapon", "Swords") });
        foreach (var id in new[] { "Tankard", "TankardOdin" })
        {
            var tankard = index.Items[id];
            Assert.Equal(new[] { "equipment" }, Categories(tankard)); Assert.Equal("equipment", tankard.Primary);
            // The picker shows it under Equipment · Other (D051 closure), not under any named Equipment subcategory.
            Assert.Equal(new[] { "equipment", "equipment.other" }, AllCategories(tankard));
            Assert.Contains("mead", Assert.Single(CategoryClosure.Underived(tankard.Memberships)).Reason);
            Assert.True(tankard.InstanceRarity); // Still per-instance gear: rarity and condition read the stack as before.
            Assert.False(ClassificationFilters.Matches(tankard, Rule(new[] { "weapons.swords" })));
            Assert.False(ClassificationFilters.Matches(tankard, Rule(new[] { "weapons" })));
            Assert.False(ClassificationFilters.Matches(tankard, Rule(new[] { "equipment.tools" })));
            Assert.True(ClassificationFilters.Matches(tankard, Rule(new[] { "equipment" })));
        }
        Assert.Equal(new[] { "meads" }, Categories(index.Items["MeadHealthMinor"])); // The drinks themselves are unchanged.
        Assert.Equal(new[] { "weapons", "weapons.swords" }, Categories(index.Items["SwordIron"]));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("mixed-ammo")]
    [InlineData("mixed-ammo-nonequipable")]
    [InlineData("empty-ammo")]
    [InlineData("only-material")]
    [InlineData("malformed-consumable")]
    public void Build_WeaponStaysAWeaponUnlessOnlyConsumablesSupplyItsAmmo(string caseId)
    {
        var facts = new List<CatalogFact>
        {
            caseId == "empty-ammo" ? Tankard(ammo: "") : Tankard(),
            caseId == "empty-ammo" ? Mead(ammo: "") : caseId == "malformed-consumable" ? Item("MeadHealthMinor", "Consumable", health: double.NaN, ammo: "mead") : Item("Wood"),
        };
        if (caseId == "mixed-ammo") { facts.Add(Mead()); facts.Add(Item("Flagon", "Ammo", ammo: "mead")); }
        if (caseId == "mixed-ammo-nonequipable") { facts.Add(Mead()); facts.Add(Item("Flagon", "AmmoNonEquipable", ammo: "mead")); }
        // Only the kinds the game's inventory takes as ammo supply it; a Material with the same ammo type does not.
        if (caseId == "only-material") facts.Add(Item("Barley", ammo: "mead"));
        var index = Build(facts);
        Assert.Equal(new[] { "weapons", "weapons.swords" }, Categories(index.Items["Tankard"]));
        if (caseId == "malformed-consumable") Assert.Equal("misc", index.Items["MeadHealthMinor"].Primary);
    }

    [Theory]
    // Every ammo-using weapon shape in the game's catalog other than the tankards, with its real suppliers.
    [InlineData("Bow", "Bow", "Bows", "$ammo_arrows", "ArrowWood", "Ammo", "weapons.bows")]
    [InlineData("CrossbowArbalest", "Bow", "Crossbows", "$ammo_bolts", "BoltBone", "Ammo", "weapons.crossbows")]
    [InlineData("FishingRod", "TwoHandedWeapon", "None", "$item_fishingbait", "FishingBait", "Ammo", "weapons.other")]
    [InlineData("SwordIron", "OneHandedWeapon", "Swords", "", "MeadHealthMinor", "Consumable", "weapons.swords")]
    [InlineData("StaffFireball", "TwoHandedWeapon", "ElementalMagic", "", "MeadEitrMinor", "Consumable", "weapons.magic")]
    public void Build_RealWeaponShapesKeepTheirWeaponCategory(string id, string type, string skill, string ammo, string supplier, string supplierType, string expected)
    {
        var index = Build(new[] { Item(id, type, skill, ammo: ammo), Item(supplier, supplierType, ammo: ammo), Tankard(), Mead("MeadTasty") });
        Assert.Contains(expected, AllCategories(index.Items[id])); Assert.Contains("weapons", Categories(index.Items[id]));
        Assert.DoesNotContain("equipment", AllCategories(index.Items[id]));
        Assert.Equal("equipment", index.Items["Tankard"].Primary);
    }

    [Fact]
    public void Build_DrinkingVesselRuleIsInTheAnswerIdentityAndTheClassifierVersion()
    {
        // D125 changed answers for unchanged inputs, so cached and published answers from version 12 must rebuild.
        Assert.Equal("yavsos-classifier-13", ClassificationIndex.Version);
        var vessel = Build(new[] { Tankard(), Mead() });
        var weapon = Build(new[] { Tankard(), Item("MeadHealthMinor", "Ammo", ammo: "mead") });
        Assert.NotEqual(vessel.Fingerprint, weapon.Fingerprint);
        Assert.Equal(vessel.Fingerprint, Build(new[] { Mead(), Tankard() }).Fingerprint); // Supplier order does not matter.
    }

    [Theory]
    [InlineData("Helmet", "equipment.armor.head")]
    [InlineData("Chest", "equipment.armor.chest")]
    [InlineData("Legs", "equipment.armor.legs")]
    [InlineData("Shoulder", "equipment.armor.capes")]
    public void Build_ArmorSlotAddsParentAndSpecificMembership(string type, string subgroup)
    {
        var result = Build(new[] { Item("armor", type) }).Items["armor"];
        Assert.Equal(new[] { "equipment.armor", subgroup }, Categories(result)); Assert.Equal(subgroup, result.Primary);
        Assert.Equal(new[] { "equipment", "equipment.armor", subgroup }, AllCategories(result));
    }

    [Theory]
    [InlineData("$ammo_arrows", "ammunition.arrows")]
    [InlineData("$ammo_bolts", "ammunition.bolts")]
    [InlineData("$ammo_turretbolt", "ammunition.turret")]
    [InlineData("$item_fishingbait", "fishing.bait")]
    public void Build_AmmoContextAddsSpecificGroupWithoutChangingOtherMaterial(string ammo, string group)
    {
        var result = Build(new[] { Item("ammo", "Ammo", ammo: ammo), Item("material", ammo: ammo) });
        // Fishing bait is its own root, outside ammunition (D054).
        Assert.Equal(group == "fishing.bait" ? new[] { group } : new[] { "ammunition", group }, Categories(result.Items["ammo"]));
        Assert.Equal(new[] { "misc" }, Categories(result.Items["material"]));
    }

    [Theory]
    [InlineData(50, 10, 0, "food.health")]
    [InlineData(10, 50, 0, "food.stamina")]
    [InlineData(10, 10, 50, "food.eitr")]
    [InlineData(50, 50, 0, "food.balanced")]
    public void Build_FoodUsesPositiveDurationAndLargestStat(double health, double stamina, double eitr, string group)
    {
        var result = Build(new[] { Item("RawMeat", health: health, stamina: stamina, eitr: eitr, duration: 600),
            Item("NoDuration", health: health, stamina: stamina, eitr: eitr) });
        Assert.Equal(new[] { "food", group }, Categories(result.Items["RawMeat"]));
        Assert.Equal(group, result.Items["RawMeat"].Primary);
        Assert.Equal(new[] { "misc" }, Categories(result.Items["NoDuration"]));
    }

    [Fact]
    public void Build_PositiveCreationUseKeepsFoodEquipmentAndMaterialsDistinct()
    {
        var result = Build(new[] { Item(), Item("Bread", "Consumable", health: 10, duration: 600),
            Item("RawMeat", health: 10, duration: 600), Item("Trophy", "Trophy"),
            Recipe(any: true), Requirement(), Requirement("slot1", input: "RawMeat"), Requirement("slot2", input: "Trophy") });
        Assert.Equal(new[] { "ingredients.food", "materials.crafting", "materials.wood" }, Categories(result.Items["Wood"]));
        Assert.Equal(new[] { "food", "food.health", "ingredients.food" }, Categories(result.Items["RawMeat"]));
        Assert.Equal(new[] { "ingredients.food", "trophies" }, Categories(result.Items["Trophy"]));
        Assert.All(new[] { "Wood", "RawMeat", "Trophy" }, id => Assert.Contains(result.Items[id].Facets,
            f => f.Dimension == "usage" && f.Value == "crafting" && f.Reason == "r"));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("upgrade-only")]
    [InlineData("refiner-station")]
    [InlineData("zero-output")]
    [InlineData("zero-input")]
    [InlineData("refinement-input")]
    [InlineData("missing-output")]
    public void Build_ExcludedRecipeRolesCannotCreateUsageOrCraftingMembership(string scenario)
    {
        var recipe = Recipe(enabled: scenario != "disabled", upgrade: scenario == "upgrade-only", refiner: scenario == "refiner-station",
            amount: scenario == "zero-output" ? 0 : 1, output: scenario == "missing-output" ? "Absent" : "Bread");
        var requirement = Requirement(amount: scenario == "zero-input" ? 0 : 1, refinement: scenario == "refinement-input");
        var result = Build(new[] { Item(), Item("Bread"), recipe, requirement }).Items["Wood"];
        Assert.Equal(new[] { "materials.wood" }, Categories(result)); Assert.Empty(result.Facets);
    }

    [Fact]
    public void Build_MissingRequiredInputIsRetainedWithoutInventingRegistrationOrBiome()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("Bread", "Consumable", health: 10, duration: 600), Recipe(), Requirement(input: "UnknownToken") });
        var result = ClassificationIndex.Build(catalog, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()));
        Assert.Equal(new[] { "item:UnknownToken" }, catalog.MissingReferences);
        Assert.Equal(new[] { "Bread" }, result.Items.Keys);
        Assert.Equal(new[] { "food", "food.health" }, Categories(result.Items["Bread"]));
        Assert.Equal(new[] { "food-stat:health" }, result.Items["Bread"].Facets.Select(x => x.Dimension + ":" + x.Value));
    }

    [Fact]
    public void Build_PrimaryOnlyChangeDoesNotChangeMembershipsFacetsOrProtectionFlags()
    {
        var item = Item("Sword", "OneHandedWeapon", quest: true, icon: false);
        var baseline = Build(new[] { item }).Items["Sword"];
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Sword", OverrideOperation.Add, Array.Empty<string>(), primary: "weapons") });
        var changed = Build(new[] { item }, rules).Items["Sword"];
        Assert.Equal("special.quest", baseline.Primary); Assert.Equal("weapons", changed.Primary);
        Assert.Equal(Categories(baseline), Categories(changed)); Assert.Equal(baseline.Facets, changed.Facets);
        Assert.True(changed.Quest); Assert.False(changed.HasIcon);
        Assert.True(ClassificationFilters.Matches(changed, Rule(new[] { "weapons.swords" })));
    }

    [Theory]
    [InlineData(OverrideOperation.Replace, "valuables")]
    [InlineData(OverrideOperation.Add, "food,food.health,valuables")]
    [InlineData(OverrideOperation.Remove, "food.health")]
    public void Build_ExplicitOperationUsesAutomaticBaselineAndRetainsIndependentFacets(OverrideOperation operation, string expected)
    {
        var category = operation == OverrideOperation.Remove ? "food" : "valuables";
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] { new ClassificationOverride("exact", "Food", operation, new[] { category }) });
        var evidence = new[] { new ItemClassificationEvidence("Food", facets: new[] { new FacetEvidence("usage", "food", "material-tier", "Complete route.") }) };
        var result = Build(new[] { Item("Food", health: 10, duration: 600) }, rules, evidence).Items["Food"];
        Assert.Equal(expected.Split(','), Categories(result));
        Assert.Equal(new[] { "food-stat:health", "usage:food" }, result.Facets.Select(x => x.Dimension + ":" + x.Value));
        Assert.Equal("exact:Food", result.SelectedOverride);
    }

    [Fact]
    public void Build_RemovingLastMembershipFallsBackAndInvalidPrimaryRejectsWholeIndex()
    {
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Remove, new[] { "materials.wood" }) });
        var result = Build(new[] { Item("Wood") }, rules).Items["Wood"];
        Assert.Equal("misc", result.Primary); Assert.Equal(new[] { "misc" }, Categories(result));
        var invalid = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "valuables" }, "food") });
        Assert.Throws<ArgumentException>(() => Build(new[] { Item() }, invalid));
    }

    [Fact]
    public void Build_SourceAndManualEvidenceKeepSeparateReasonsAndFacetOverridesAreExplicit()
    {
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "materials.wood" }, facets: new[] {
                new FacetOverride("usage", OverrideOperation.Replace, new[] { "fuel" }),
                new FacetOverride("related-mod", OverrideOperation.Add, new[] { "monstrum" }),
                new FacetOverride("construction-mod", OverrideOperation.Remove, new[] { "old-builder" }) }) });
        var extra = new ItemClassificationEvidence("Wood", new[] { new MembershipEvidence("materials.wood", "source-adapter", "Wood definition.") }, new[] {
            new FacetEvidence("usage", "crafting", "material-tier", "Complete proof."),
            new FacetEvidence("construction-mod", "old-builder", "build-adapter", "Enabled piece resource."),
            new FacetEvidence("mod", "vanilla", "registration-adapter", "Origin receipt.") });
        var item = Build(new[] { Item() }, rules, new[] { extra }).Items["Wood"];
        Assert.Equal(new[] { "materials.wood" }, Categories(item));
        Assert.Equal(new[] { "default-item-semantics", "policy:exact:Wood", "source-adapter" }, item.Memberships.Where(x => !CategoryClosure.IsDerived(x)).Select(x => x.Source));
        Assert.Equal(new[] { "vanilla", "monstrum", "fuel" }, item.Facets.Select(x => x.Value));
    }

    [Fact]
    public void Build_ClearedFacetIsUnknown()
    {
        var extra = new[] { new ItemClassificationEvidence("Wood", facets: new[] { new FacetEvidence("usage", "crafting", "proof", "Complete.") }) };
        var clear = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Add, Array.Empty<string>(), facets: new[] { new FacetOverride("usage", OverrideOperation.Replace, Array.Empty<string>()) }) });
        var result = Build(new[] { Item() }, clear, extra).Items["Wood"];
        Assert.Empty(result.Facets); Assert.True(ClassificationFilters.Matches(result, Rule(filters: new[] { ("usage", new[] { "unknown" }) })));
    }

    [Fact]
    public void Build_IndexAndInputIdentityAreDeterministicAndCoverAllInputs()
    {
        var member = new MembershipEvidence("materials.wood", "adapter", "Proof.");
        var facet = new FacetEvidence("mod", "vanilla", "adapter", "Receipt.");
        var a = Build(new[] { Item(), Item("Stone") }, evidence: new[] { new ItemClassificationEvidence("Wood", new[] { member }, new[] { facet }) });
        var b = Build(new[] { Item("Stone"), Item() }, evidence: new[] { new ItemClassificationEvidence("Wood", new[] { member, member }, new[] { facet, facet }) });
        Assert.Equal(a.InputFingerprint, b.InputFingerprint); Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, Build(new[] { Item(), Item("Stone", value: 3) }).Fingerprint);
        Assert.NotEqual(a.InputFingerprint, Build(new[] { Item(), Item("Stone") }, sourceIdentity: "different-adapter-version").InputFingerprint);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ClassificationResult>)a.Items).Clear());
    }

    [Fact]
    public void Build_MalformedRowsFailOnlyThemselves_MissingReferencesLimitsAndCancellationRejectWholeGeneration()
    {
        // D086 D3: one item's malformed facts make only that item Misc, with the reason; a malformed recipe gives no evidence.
        var index = Build(new[] { Item(health: double.NaN, id: "Odd"), Fact(CatalogFactKind.Item, "bad", "type", "Material"), Item(),
            Item("Bread", "Consumable", health: 10, duration: 600), Recipe(), Requirement(),
            Fact(CatalogFactKind.Recipe, "broken", "output", "Bread", "enabled", "maybe"), Requirement("slot1", "broken", "Wood") });
        Assert.Equal("misc", index.Items["Odd"].Primary); Assert.Equal("misc", index.Items["bad"].Primary);
        Assert.Contains(index.Items["Odd"].Memberships, x => x.Reason.StartsWith("Malformed catalog facts: ", StringComparison.Ordinal));
        Assert.Contains("materials.crafting", Categories(index.Items["Wood"])); // The good recipe still counts.
        Assert.Equal(3, index.Skipped.Count);
        Assert.Contains(index.Skipped, x => x.StartsWith("recipe broken: ", StringComparison.Ordinal));
        Assert.Contains(index.Skipped, x => x.StartsWith("item Odd: ", StringComparison.Ordinal));
        Assert.Equal(index.Fingerprint, Build(new[] { Item(health: double.NaN, id: "Odd"), Fact(CatalogFactKind.Item, "bad", "type", "Material"), Item(),
            Item("Bread", "Consumable", health: 10, duration: 600), Recipe(), Requirement(),
            Fact(CatalogFactKind.Recipe, "broken", "output", "Bread", "enabled", "maybe"), Requirement("slot1", "broken", "Wood") }).Fingerprint);
        Assert.Throws<ArgumentException>(() => Build(new[] { Item() }, evidence: new[] { new ItemClassificationEvidence("Absent") }));
        Assert.Throws<ArgumentException>(() => Build(new[] { Item() }, evidence: new[] { new ItemClassificationEvidence("Wood"), new ItemClassificationEvidence("Wood") }));
        Assert.Throws<InvalidOperationException>(() => Build(new[] { Item(), Item("Stone") }, maxWork: 1));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => ClassificationIndex.Build(CatalogSnapshot.Build(new[] { Item() }),
            new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), cancellation: cancel.Token));
    }

    [Fact]
    public void Build_ClassifiedIndexDoesNotOpenExistingPolicyReadiness()
    {
        var policy = new PolicyLifecycle(); policy.BeginSession(PolicyMode.Standalone);
        Assert.Equal(PolicyUpdate.Accepted, policy.ApplyLocal(CategoryPolicyCodecTests.Wrap("", "")));
        var result = Build(new[] { Item() });
        Assert.Single(result.Items); Assert.True(policy.PolicyReady); Assert.False(policy.CanAutomate);
    }

    [Fact]
    public void Build_EquipmentRejectsStaticRarityEvidenceAndPrefabEdits()
    {
        var extra = new[] { new ItemClassificationEvidence("Sword", facets: new[] { new FacetEvidence("rarity", "rare", "adapter", "Static.") }) };
        Assert.Throws<ArgumentException>(() => Build(new[] { Item("Sword", "OneHandedWeapon") }, evidence: extra));
        foreach (var operation in new[] { OverrideOperation.Replace, OverrideOperation.Add, OverrideOperation.Remove })
        {
            var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
                new ClassificationOverride("exact", "Sword", OverrideOperation.Add, new[] { "weapons" }, facets: new[] {
                    new FacetOverride("rarity", operation, new[] { "rare" }) }) });
            Assert.Throws<ArgumentException>(() => Build(new[] { Item("Sword", "OneHandedWeapon") }, rules));
        }
    }

    [Fact]
    public void Build_BrewingUsesConsumableOutputAndToolSkillRequiresWeaponContext()
    {
        var result = Build(new[] { Item(), Item("Mead", "Consumable", effect: "buff"), Recipe(output: "Mead"), Requirement(),
            Item("Pick", "TwoHandedWeapon", "Pickaxes"), Item("DefaultSkill", skill: "Farming"), Item("Builder", table: "Hammer") });
        Assert.Equal(new[] { "ingredients.brewing", "materials.crafting", "materials.wood" }, Categories(result.Items["Wood"]));
        Assert.Equal(new[] { "meads" }, Categories(result.Items["Mead"]));
        Assert.Equal(new[] { "equipment.tools", "weapons" }, Categories(result.Items["Pick"]));
        Assert.Equal(new[] { "misc" }, Categories(result.Items["DefaultSkill"]));
        Assert.Equal(new[] { "equipment.tools" }, Categories(result.Items["Builder"]));
    }

    [Theory]
    [InlineData("Shield", "equipment.shields", true)]
    [InlineData("Utility", "equipment.accessories", true)]
    [InlineData("Trinket", "equipment.accessories", true)]
    [InlineData("Tool", "equipment.tools", true)]
    [InlineData("Torch", "equipment.tools", true)]
    [InlineData("Fish", "fish", false)]
    [InlineData("Trophy", "trophies", false)]
    [InlineData("FutureType", "misc", false)]
    public void Build_OtherItemContextsHaveSpecificMembershipAndRarityScope(string type, string category, bool instanceRarity)
    {
        var result = Build(new[] { Item("item", type) }).Items["item"];
        Assert.Equal(new[] { category }, Categories(result)); Assert.Equal(category, result.Primary);
        Assert.Equal(instanceRarity, result.InstanceRarity); Assert.Equal(type == "Customization", result.CatalogOnly);
    }

    [Theory]
    [InlineData("negative-output")]
    [InlineData("negative-input")]
    public void Build_NegativeCreationAmountsCannotCreateUsage(string caseId)
    {
        var result = Build(new[] { Item(), Item("Bread"), Recipe(amount: caseId == "negative-output" ? -1 : 1),
            Requirement(amount: caseId == "negative-input" ? -1 : 1) }).Items["Wood"];
        Assert.Equal(new[] { "materials.wood" }, Categories(result)); Assert.Empty(result.Facets);
    }

    [Fact]
    public void Build_EmptyCatalogAndExactWorkBoundaryAreValidButAdjacentExcessRejects()
    {
        Assert.Empty(Build(Array.Empty<CatalogFact>()).Items);
        var baseline = Build(new[] { Item() });
        Assert.Equal(baseline.Fingerprint, Build(new[] { Item() }, maxWork: baseline.Work).Fingerprint);
        Assert.Throws<InvalidOperationException>(() => Build(new[] { Item() }, maxWork: baseline.Work - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(new[] { Item() }, maxWork: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(new[] { Item() }, maxWork: 10000001));
    }

    [Fact]
    public void Build_CancellationDuringEmptySupplementEnumerationCannotReturnIndex()
    {
        using var cancel = new CancellationTokenSource();
        IEnumerable<ItemClassificationEvidence> Evidence()
        {
            cancel.Cancel();
            yield break;
        }
        Assert.Throws<OperationCanceledException>(() => ClassificationIndex.Build(CatalogSnapshot.Build(Array.Empty<CatalogFact>()),
            new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), Evidence(), cancellation: cancel.Token));
    }

    [Theory]
    [InlineData("membership-category")]
    [InlineData("membership-source")]
    [InlineData("membership-reason")]
    [InlineData("facet-dimension")]
    [InlineData("facet-value")]
    [InlineData("facet-source")]
    [InlineData("facet-reason")]
    public void Build_EachSupplementaryEvidenceFieldContributesToInputAndOutputIdentity(string caseId)
    {
        ItemClassificationEvidence Evidence(bool changed) => new ItemClassificationEvidence("Wood",
            new[] { new MembershipEvidence(changed && caseId == "membership-category" ? "materials.stone" : "materials.wood",
                changed && caseId == "membership-source" ? "other" : "adapter", changed && caseId == "membership-reason" ? "Revised proof." : "Proof.") },
            new[] { new FacetEvidence(changed && caseId == "facet-dimension" ? "related-mod" : "mod",
                changed && caseId == "facet-value" ? "other-pack" : "vanilla", changed && caseId == "facet-source" ? "other" : "adapter",
                changed && caseId == "facet-reason" ? "Revised receipt." : "Receipt.") });
        var baseline = Build(new[] { Item() }, evidence: new[] { Evidence(false) });
        var changed = Build(new[] { Item() }, evidence: new[] { Evidence(true) });
        Assert.NotEqual(baseline.InputFingerprint, changed.InputFingerprint);
        Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Build_EvidenceRowCapAcceptsBoundaryAndRejectsNextSupplement()
    {
        var shared = Enumerable.Range(0, 128).Select(i => new MembershipEvidence("misc", "s", "r" + i)).ToArray();
        var items = Enumerable.Range(0, 2049).Select(i => Item("I" + i)).ToArray();
        var evidence = items.Select(i => new ItemClassificationEvidence(i.Identity, shared)).ToArray();
        var boundary = Build(items.Take(2048), evidence: evidence.Take(2048));
        Assert.Equal(2048, boundary.Items.Count);
        Assert.Equal(128, boundary.Items["I0"].Memberships.Count);
        var rejected = Assert.Throws<InvalidOperationException>(() => Build(items, evidence: evidence));
        Assert.Contains("evidence bound", rejected.Message);
    }

    [Fact]
    public void Build_ScalarCapRejectsOversizeTextIndependentlyOfRowAndWorkCaps()
    {
        var shared = Enumerable.Range(0, 128).Select(i => new MembershipEvidence("misc", "s", i.ToString().PadRight(2048, 'r'))).ToArray();
        var items = Enumerable.Range(0, 129).Select(i => Item("I" + i)).ToArray();
        var evidence = items.Select(i => new ItemClassificationEvidence(i.Identity, shared)).ToArray();
        Assert.Equal(127, Build(items.Take(127), evidence: evidence.Take(127)).Items.Count);
        var rejected = Assert.Throws<InvalidOperationException>(() => Build(items, evidence: evidence));
        Assert.Contains("scalar bound", rejected.Message);
    }

    [Fact]
    public void Build_ScalarCapAcceptsExactUtf8BoundaryAndRejectsOneExtraByte()
    {
        const int itemCount = 128, rowsPerItem = 128, cap = 32 * 1024 * 1024;
        var items = Enumerable.Range(0, itemCount).Select(i => Item("I" + i)).ToArray();
        // Public scalar accounting includes each supplement/output prefab and evidence strings.
        // ASCII keeps UTF-8 bytes explicit; shared rows keep this fixture's retained memory small.
        var reasonBytes = cap - items.Sum(i => i.Identity.Length) * 2 - itemCount * rowsPerItem * ("misc".Length + "s".Length);
        var length = reasonBytes / (itemCount * rowsPerItem);
        var extra = reasonBytes % (itemCount * rowsPerItem);
        var shortRows = Enumerable.Range(0, rowsPerItem).Select(i => new MembershipEvidence("misc", "s", i.ToString().PadRight(length, 'r'))).ToArray();
        var longRows = Enumerable.Range(0, rowsPerItem).Select(i => new MembershipEvidence("misc", "s", i.ToString().PadRight(length + 1, 'r'))).ToArray();
        ItemClassificationEvidence[] Evidence(bool over) => items.Select((item, index) => new ItemClassificationEvidence(item.Identity,
            Enumerable.Range(0, rowsPerItem).Select(slot => over && index == 0 && slot == 0
                ? new MembershipEvidence("misc", "s", "0".PadRight(length + 2, 'r'))
                : index * rowsPerItem + slot < extra ? longRows[slot] : shortRows[slot]))).ToArray();
        var result = Build(items, evidence: Evidence(false));
        Assert.Equal(itemCount, result.Items.Count);
        Assert.Equal(rowsPerItem, result.Items["I0"].Memberships.Count);
        var rejected = Assert.Throws<InvalidOperationException>(() => Build(items, evidence: Evidence(true)));
        Assert.Contains("scalar bound", rejected.Message);
    }
}
