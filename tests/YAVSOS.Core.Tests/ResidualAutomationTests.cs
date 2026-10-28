using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ResidualAutomationTests
{
    [Theory]
    [InlineData("UnfamiliarOffering")]
    [InlineData("AncientSeed")]
    [InlineData("WitheredBone")]
    [InlineData("DragonEgg")]
    [InlineData("GoblinTotem")]
    public void Offering_CurrentConsumerReplacesExactFallbackAndWithdraws(string name)
    {
        var catalog = CatalogSnapshot.Build(new[] { Item(name) });
        ClassificationResult Result(params LoadedSourceFact[] rows) => ClassificationPipeline.Build(catalog,
            new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), LoadedSourceReceipt.Build(catalog, rows).Snapshot).Index.Items[name];
        var use = new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "deferred:new", name, route: "new:bowl", method: "offering");
        Assert.Equal(new[] { "special.offerings" }, Categories(Result(use)));
        Assert.StartsWith("loaded-use:", Assert.Single(Result(use).Memberships, x => !CategoryClosure.IsDerived(x)).Source);
        Assert.Equal(new[] { "misc" }, Categories(Result()));
        Assert.Equal(new[] { "misc" }, Categories(Result(new LoadedSourceFact(LoadedSourceFactKind.ItemUse,
            "deferred:new", name, route: "new:bowl", method: "offering", eligible: false))));
        Assert.Empty(Result(use).Facets);
    }

    [Theory]
    [InlineData("Coins")]
    [InlineData("UnfamiliarPayment")]
    public void Currency_CurrentPaymentDefinitionReplacesIdentityAnchorAndWithdraws(string name)
    {
        var catalog = CatalogSnapshot.Build(new[] { Item(name, value: 1), Item("Sellable", value: 5) });
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create());
        var use = new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "payment:store", name, route: "store", method: "payment-currency");
        ClassificationIndex Result(ClassificationRuleSet policy, params LoadedSourceFact[] rows) =>
            ClassificationPipeline.Build(catalog, policy, LoadedSourceReceipt.Build(catalog, rows).Snapshot).Index;
        var current = Result(rules, use);
        Assert.Equal(new[] { "currency" }, Categories(current.Items[name]));
        Assert.Equal("currency", current.Items[name].Primary);
        Assert.StartsWith("loaded-use:", Assert.Single(current.Items[name].Memberships).Source);
        Assert.Equal(new[] { "valuables" }, Categories(current.Items["Sellable"]));
        Assert.Equal(new[] { "valuables" }, Categories(Result(rules).Items[name]));
        Assert.Equal(new[] { "valuables" }, Categories(Result(rules, new LoadedSourceFact(LoadedSourceFactKind.ItemUse,
            "payment:store", name, route: "store", method: "payment-currency", eligible: false)).Items[name]));
        var policy = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", name, OverrideOperation.Replace, new[] { "food" }) });
        Assert.Equal(new[] { "food" }, Categories(Result(policy, use).Items[name]));
    }

    private static CatalogFact Throwable(string name, string prefix = "primary", string change = "", string type = "OneHandedWeapon")
    {
        var values = new Dictionary<string, string> {
            [prefix + "AttackType"] = "Projectile", [prefix + "ConsumesItem"] = "True", [prefix + "RecoversItem"] = "False",
            [prefix + "ProjectileArea"] = "0", [prefix + "ImpactChance"] = "1", [prefix + "ImpactCount"] = "1",
            [prefix + "Projectiles"] = "1", [prefix + "ProjectileBursts"] = "1",
            [prefix + "ImpactTerrain"] = "True", [prefix + "ImpactCharacters"] = "True", [prefix + "ImpactObjects"] = "True",
            [prefix + "ImpactArea"] = "4", [prefix + "ImpactSmoke"] = "", [prefix + "SmokeInterval"] = "0" };
        switch (change)
        {
            case "melee": values[prefix + "AttackType"] = "Horizontal"; break;
            case "not-consumed": values[prefix + "ConsumesItem"] = "False"; break;
            case "zero-shots": values[prefix + "Projectiles"] = "0"; break;
            case "zero-bursts": values[prefix + "ProjectileBursts"] = "0"; break;
            case "recoverable": values[prefix + "RecoversItem"] = "True"; break;
            case "zero-chance": values[prefix + "ImpactChance"] = "0"; break;
            case "zero-count": values[prefix + "ImpactCount"] = "0"; break;
            case "no-surfaces": foreach (var key in new[] { "ImpactTerrain", "ImpactCharacters", "ImpactObjects" }) values[prefix + key] = "False"; break;
            case "snowball-or-creature": values[prefix + "ImpactArea"] = "0"; break;
            case "smoke": values[prefix + "ImpactArea"] = "0"; values[prefix + "ImpactSmoke"] = "UnfamiliarSmokePrefab"; values[prefix + "SmokeInterval"] = "0.5"; break;
            case "negative-interval-smoke": values[prefix + "ImpactArea"] = "0"; values[prefix + "ImpactSmoke"] = "UnfamiliarSmokePrefab"; values[prefix + "SmokeInterval"] = "-1"; break;
            case "zero-interval-smoke": values[prefix + "ImpactArea"] = "0"; values[prefix + "ImpactSmoke"] = "UnfamiliarSmokePrefab"; break;
            case "inactive-smoke": values[prefix + "ImpactArea"] = "0"; values[prefix + "SmokeInterval"] = "0.5"; break;
            case "projectile-area": values[prefix + "ProjectileArea"] = "2"; values[prefix + "ImpactChance"] = "0"; values[prefix + "ImpactArea"] = "0"; break;
        }
        return new CatalogFact(CatalogFactKind.Item, name, Item(name, type).Values.Concat(values));
    }

    [Theory]
    [InlineData("UnfamiliarThrowable", "primary", "", "OneHandedWeapon")]
    [InlineData("UnfamiliarSecondary", "secondary", "", "OneHandedWeapon")]
    [InlineData("BombOoze", "primary", "", "OneHandedWeapon")]
    [InlineData("BombBile", "primary", "", "OneHandedWeapon")]
    [InlineData("BombLava", "primary", "", "OneHandedWeapon")]
    [InlineData("BombDynamite", "primary", "", "OneHandedWeapon")]
    [InlineData("BombSpirit_TW", "primary", "", "OneHandedWeapon")]
    [InlineData("BombSiege", "primary", "", "Material")]
    [InlineData("BombSmoke", "primary", "smoke", "OneHandedWeapon")]
    [InlineData("UnfamiliarSmoke", "primary", "zero-interval-smoke", "OneHandedWeapon")]
    [InlineData("NegativeIntervalSmoke", "primary", "negative-interval-smoke", "OneHandedWeapon")]
    [InlineData("UnfamiliarSplash", "primary", "projectile-area", "Misc")]
    [InlineData("GenericColliderOnly", "primary", "no-surfaces", "OneHandedWeapon")]
    public void Bomb_ConsumedNonrecoverableAreaOrSmokeSupportsNewOutputs(string name, string prefix, string mode, string type)
    {
        var result = Build(new[] { Item("NewResource"), Throwable(name, prefix, mode, type), Recipe(output: name), Requirement(input: "NewResource") });
        Assert.Equal(new[] { "ingredients.bombs", "materials.crafting" }, Categories(result.Items["NewResource"]));
        Assert.DoesNotContain("ingredients.bombs", Categories(result.Items[name]));
        Assert.Equal("loaded-recipe:creation", Assert.Single(result.Items["NewResource"].Memberships, m => m.Category == "ingredients.bombs").Source);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("bad")]
    public void Bomb_ConfiguredSmokeRejectsMalformedOrNonfiniteInterval(string interval)
    {
        var bomb = Throwable("NewSmoke", change: "smoke");
        var changed = new CatalogFact(bomb.Kind, bomb.Identity, bomb.Values.Select(pair =>
            pair.Key == "primarySmokeInterval" ? new KeyValuePair<string, string>(pair.Key, interval) : pair));
        // D086 D3: only the malformed bomb falls back to Misc; the index stands.
        var index = Build(new[] { Item("NewResource"), changed, Recipe(output: "NewSmoke"), Requirement(input: "NewResource") });
        Assert.Equal("misc", index.Items["NewSmoke"].Primary); Assert.Contains(index.Skipped, x => x.StartsWith("item NewSmoke: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("melee")]
    [InlineData("not-consumed")]
    [InlineData("zero-shots")]
    [InlineData("zero-bursts")]
    [InlineData("recoverable")]
    [InlineData("zero-chance")]
    [InlineData("zero-count")]
    [InlineData("snowball-or-creature")]
    [InlineData("inactive-smoke")]
    public void Bomb_CounterexamplesDoNotImplyIngredientRoles(string scenario)
    {
        var result = Build(new[] { Item("NewResource"), Throwable("FutureBomb", change: scenario), Recipe(output: "FutureBomb"), Requirement(input: "NewResource") });
        Assert.Equal(new[] { "materials.crafting" }, Categories(result.Items["NewResource"]));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("zero-output")]
    [InlineData("upgrade-only")]
    [InlineData("refiner")]
    [InlineData("zero-input")]
    [InlineData("refinement-input")]
    [InlineData("edible-input")]
    [InlineData("equipment-input")]
    [InlineData("staff-output")]
    public void Bomb_AutomaticOutputStillRequiresEligibleCreationAndResourceInput(string scenario)
    {
        var result = Build(new[] { Item("NewResource", scenario == "equipment-input" ? "Helmet" : "Material",
                health: scenario == "edible-input" ? 10 : 0, duration: scenario == "edible-input" ? 600 : 0),
            Throwable("FutureBomb", type: scenario == "staff-output" ? "TwoHandedWeapon" : "OneHandedWeapon"),
            Recipe(output: "FutureBomb", enabled: scenario != "disabled", amount: scenario == "zero-output" ? 0 : 1,
                upgrade: scenario == "upgrade-only", refiner: scenario == "refiner"),
            Requirement(input: "NewResource", amount: scenario == "zero-input" ? 0 : 1, refinement: scenario == "refinement-input") });
        Assert.DoesNotContain("ingredients.bombs", Categories(result.Items["NewResource"]));
    }

    [Fact]
    public void Bomb_WithdrawnImpactHasNoIdentityFallbackAndOverrideWins()
    {
        var facts = new[] { Item("NewResource"), Throwable("BombOoze"), Recipe(output: "BombOoze"), Requirement(input: "NewResource") };
        Assert.Contains("ingredients.bombs", Categories(Build(facts).Items["NewResource"]));
        Assert.Equal(new[] { "materials.crafting" }, Categories(Build(facts.Select(f => f.Identity == "BombOoze" ? Item("BombOoze", "OneHandedWeapon") : f)).Items["NewResource"]));
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "NewResource", OverrideOperation.Replace, new[] { "misc" }) });
        Assert.Equal(new[] { "misc" }, Categories(Build(facts, rules).Items["NewResource"]));
        var malformed = new CatalogFact(CatalogFactKind.Item, "BombOoze", Throwable("BombOoze").Values.Select(p =>
            p.Key == "primaryImpactArea" ? new KeyValuePair<string, string>(p.Key, "NaN") : p));
        var withMalformed = Build(facts.Select(f => f.Identity == "BombOoze" ? malformed : f)); // D086 D3: only that item.
        Assert.Equal("misc", withMalformed.Items["BombOoze"].Primary); Assert.DoesNotContain("ingredients.bombs", Categories(withMalformed.Items["NewResource"]));
    }

    private static CatalogFact QualityItem(string id, int quality = 2) => new(CatalogFactKind.Item, id,
        Item(id, "OneHandedWeapon").Values.Concat(new[] { new KeyValuePair<string, string>("maxQuality", quality.ToString()) }));

    [Theory]
    [InlineData("UnfamiliarToken", false, false, 1, 0)]
    [InlineData("Upgrader0Armor", true, false, 0, 1)]
    [InlineData("Upgrader7Weapon", false, true, 1, 0)]
    public void UpgradeResource_ExplicitFlagSupportsNewIdentitiesAndUpgradeOnlyRecipes(string name, bool upgradeOnly, bool refiner, int amount, int perLevel)
    {
        var item = Build(new[] { Item(name), QualityItem("Target"), Recipe(output: "Target", upgrade: upgradeOnly, refiner: refiner),
            Requirement(input: name, amount: amount, refinement: true, upgrade: perLevel) }).Items[name];
        Assert.Equal(new[] { "materials.upgrades" }, Categories(item));
        Assert.Equal("loaded-recipe:upgrader", Assert.Single(item.Memberships, x => !CategoryClosure.IsDerived(x)).Source);
        Assert.Equal("r", Assert.Single(item.Memberships, x => !CategoryClosure.IsDerived(x)).Reason);
        Assert.Empty(item.Facets);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("zero-output")]
    [InlineData("negative-output")]
    [InlineData("zero-cost")]
    [InlineData("negative-base")]
    [InlineData("negative-level")]
    [InlineData("overflow-cost")]
    [InlineData("rounded-overflow-cost")]
    [InlineData("ordinary-quality-cost")]
    [InlineData("one-quality")]
    [InlineData("missing-output")]
    [InlineData("wrong-input-type")]
    [InlineData("edible-input")]
    public void UpgradeResource_InvalidOrOrdinaryRequirementsCannotImplyTokens(string scenario)
    {
        var input = Item("Unfamiliar", scenario == "wrong-input-type" ? "Trophy" : "Material",
            health: scenario == "edible-input" ? 10 : 0, duration: scenario == "edible-input" ? 600 : 0);
        var item = Build(new[] { input, QualityItem("Target", scenario == "one-quality" ? 1 : 2),
            Recipe(output: scenario == "missing-output" ? "Absent" : "Target", enabled: scenario != "disabled", amount: scenario == "zero-output" ? 0 : scenario == "negative-output" ? -1 : 1),
            Requirement(input: "Unfamiliar", refinement: scenario != "ordinary-quality-cost",
                amount: scenario == "zero-cost" ? 0 : scenario == "negative-base" ? -1 : scenario is "overflow-cost" or "rounded-overflow-cost" ? int.MaxValue : 1,
                upgrade: scenario == "negative-level" ? -1 : scenario == "overflow-cost" ? 1 : 0) }).Items["Unfamiliar"];
        Assert.DoesNotContain("materials.upgrades", Categories(item));
    }

    [Fact]
    public void UpgradeResource_WithdrawalHasNoExactFallbackAndPolicyRemainsAuthoritative()
    {
        var facts = new[] { Item("Upgrader0Armor"), QualityItem("Target"), Recipe(output: "Target"), Requirement(input: "Upgrader0Armor", refinement: true) };
        Assert.Contains("materials.upgrades", Categories(Build(facts).Items["Upgrader0Armor"]));
        Assert.Equal(new[] { "misc" }, Categories(Build(facts.Take(2)).Items["Upgrader0Armor"]));
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Upgrader0Armor", OverrideOperation.Replace, new[] { "misc" }) });
        Assert.Equal(new[] { "misc" }, Categories(Build(facts, rules).Items["Upgrader0Armor"]));
        Assert.Equal(new[] { "Target", "Upgrader0Armor" }, Build(facts.Where(f => f.Kind != CatalogFactKind.Requirement).Append(
            Requirement(input: "AbsentToken", refinement: true))).Items.Keys);
    }

    [Fact]
    public void UpgradeResource_MissingQualityIsUnknownAndMalformedQualityRejectsCatalogInterpretation()
    {
        var facts = new[] { Item("Unfamiliar", "Misc"), Item("Target"), Recipe(output: "Target"), Requirement(input: "Unfamiliar", refinement: true) };
        Assert.DoesNotContain("materials.upgrades", Categories(Build(facts).Items["Unfamiliar"]));
        foreach (var quality in new[] { "bad", "2147483648" })
        {
            var invalid = new CatalogFact(CatalogFactKind.Item, "Target", Item("Target").Values.Concat(
                new[] { new KeyValuePair<string, string>("maxQuality", quality) }));
            // D086 D3: the malformed item is Misc and gives no recipe evidence; the index stands.
            var index = Build(facts.Select(f => f.Identity == "Target" ? invalid : f));
            Assert.Equal("misc", index.Items["Target"].Primary); Assert.DoesNotContain("materials.upgrades", Categories(index.Items["Unfamiliar"]));
        }
        var valid = Build(facts.Select(f => f.Identity == "Target" ? QualityItem("Target") : f)).Items["Unfamiliar"];
        Assert.Equal(new[] { "materials.upgrades" }, Categories(valid));
    }

    [Theory]
    [InlineData("UnfamiliarCelebration")]
    [InlineData("FireworksRocket_Cyan")]
    public void Fireworks_CurrentConsumerSupportsNewIdentityAndWithdrawal(string name)
    {
        var catalog = CatalogSnapshot.Build(new[] { Item(name) });
        var use = new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "fire:0", output: name, route: "fire", method: "fireworks");
        ClassificationResult Result(params LoadedSourceFact[] rows) => ClassificationPipeline.Build(catalog,
            new ClassificationRuleSet(DefaultClassificationTaxonomy.Create()), LoadedSourceReceipt.Build(catalog, rows).Snapshot).Index.Items[name];
        Assert.Equal(new[] { "special.fireworks" }, Categories(Result(use)));
        Assert.StartsWith("loaded-use:", Assert.Single(Result(use).Memberships, x => !CategoryClosure.IsDerived(x)).Source);
        Assert.Empty(Result(use).Facets);
        Assert.Equal(new[] { "misc" }, Categories(Result()));
        foreach (var row in new[] {
            new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "fire:0", output: name, route: "fire", method: "fireworks", eligible: false),
            new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "fire:0", output: name, route: "fire", method: "fireworks", amount: 0),
            new LoadedSourceFact(LoadedSourceFactKind.ItemUse, "fire:0", output: name, route: "fire", method: "fireworks", amount: -1) })
            Assert.Equal(new[] { "misc" }, Categories(Result(row)));
    }
}
