using System;
using System.Linq;
using System.IO;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

public sealed class LoadedSemanticExtensionTests
{
    private static ClassificationRuleSet Rules() => new(DefaultClassificationTaxonomy.Create());
    private static CatalogSnapshot Catalog(params CatalogFact[] facts) => CatalogSnapshot.Build(facts);
    private static LoadedSourceFact Use(string method, string output = "Resource", bool enabled = true) =>
        new(LoadedSourceFactKind.ItemUse, "consumer", output: output, route: "explicit-loaded-consumer", method: method, eligible: enabled);
    private static LoadedSourceFact Facet(string dimension, string value, string output = "Resource", string id = "facet", bool enabled = true) =>
        new(LoadedSourceFactKind.FacetEvidence, id, output: output, route: "supported-adapter:1:definition", method: dimension, detail: value, eligible: enabled);
    private static ClassifiedCatalog Build(CatalogSnapshot catalog, params LoadedSourceFact[] facts) =>
        ClassificationPipeline.Build(catalog, Rules(), LoadedSourceReceipt.Build(catalog, facts).Snapshot);

    [Theory]
    [InlineData("feast-ingredient", "ingredients.food")]
    [InlineData("siege-ammunition", "ammunition")]
    public void ExplicitFeastOrSiegeConsumer_ClassifiesMaterialWithoutInventingFoodStats(string method, string category)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        var item = Build(catalog, Use(method)).Index.Items["Resource"];
        Assert.Equal(category, item.Primary);
        Assert.DoesNotContain(item.Memberships, m => m.Category == "food");
        Assert.DoesNotContain(item.Facets, f => f.Dimension is "biome" or "mod");
        Assert.Equal("misc", Build(catalog, Use(method, enabled: false)).Index.Items["Resource"].Primary);
    }

    [Fact]
    public void FeastPreparationRecipe_AddsSpiceIngredientAndWithdrawsWithSource()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"), ClassificationContractFixture.Item("Spice"),
            ClassificationContractFixture.Recipe(output: "Resource"), ClassificationContractFixture.Requirement(input: "Spice"));
        var classified = Build(catalog, Use("feast-ingredient"));
        Assert.Equal("ingredients.food", classified.Index.Items["Resource"].Primary);
        var spice = classified.Index.Items["Spice"];
        Assert.Equal("ingredients.food", spice.Primary);
        Assert.Contains(spice.Memberships, m => m.Category == "materials.crafting");
        var withdrawn = ClassificationPipeline.Build(catalog, Rules());
        Assert.Equal("materials.crafting", withdrawn.Index.Items["Spice"].Primary);
        Assert.DoesNotContain(withdrawn.Index.Items["Spice"].Memberships, m => m.Category == "ingredients.food");
    }

    [Fact]
    public void NonFeastPreparationRecipe_DoesNotTurnCoalOrResinIntoFoodIngredients()
    {
        const string preparation = "XUncooked";
        var catalog = Catalog(ClassificationContractFixture.Item(preparation),
            ClassificationContractFixture.Item("X", "Consumable", health: 30, duration: 600),
            ClassificationContractFixture.Item("Fish"), ClassificationContractFixture.Item("Coal"), ClassificationContractFixture.Item("Resin"),
            ClassificationContractFixture.Recipe(output: preparation),
            ClassificationContractFixture.Requirement("fish", input: "Fish"),
            ClassificationContractFixture.Requirement("coal", input: "Coal"),
            ClassificationContractFixture.Requirement("resin", input: "Resin"));
        var facts = new[] { LoadedSourceReceiptTests.Input(input: preparation, output: "X", method: "cook") };
        var classified = Build(catalog, facts).Index;
        Assert.Contains(classified.Items[preparation].Memberships, m => m.Category == "ingredients.food");
        foreach (var resource in new[] { "Coal", "Resin" })
        {
            Assert.Equal("materials.crafting", classified.Items[resource].Primary);
            Assert.DoesNotContain(classified.Items[resource].Memberships, m => m.Category == "ingredients.food");
        }
    }

    [Theory]
    [InlineData(false, false, false, 1, 1, false)]
    [InlineData(true, true, false, 1, 1, false)]
    [InlineData(true, false, true, 1, 1, false)]
    [InlineData(true, false, false, 0, 1, false)]
    [InlineData(true, false, false, 1, 0, false)]
    [InlineData(true, false, false, 1, 1, true)]
    public void FeastPreparationRecipe_UnavailableCreationCannotClassifySpice(bool enabled, bool upgrade, bool refiner,
        int outputAmount, int inputAmount, bool refinement)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"), ClassificationContractFixture.Item("Spice"),
            ClassificationContractFixture.Recipe(output: "Resource", enabled: enabled, upgrade: upgrade, refiner: refiner, amount: outputAmount),
            ClassificationContractFixture.Requirement(input: "Spice", amount: inputAmount, refinement: refinement));
        Assert.DoesNotContain(Build(catalog, Use("feast-ingredient")).Index.Items["Spice"].Memberships, m => m.Category == "ingredients.food");
    }

    [Theory]
    [InlineData("mod", "actual.plugin.guid")]
    [InlineData("related-mod", "related.plugin.guid")]
    [InlineData("construction-mod", "piece.plugin.guid")]
    [InlineData("rarity", "legendary")]
    public void OptionalFacet_UsesRegisteredDetachedEvidenceWithoutMembership(string dimension, string value)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        var receipt = LoadedSourceReceipt.Build(catalog, new[] { Facet(dimension, value) });
        var item = ClassificationPipeline.Build(catalog, Rules(), receipt.Snapshot).Index.Items["Resource"];
        Assert.Equal("misc", item.Primary);
        var facet = Assert.Single(item.Facets);
        Assert.Equal(dimension, facet.Dimension); Assert.Equal(value, facet.Value);
        Assert.Equal("supported-adapter:1:definition", facet.Reason);
        Assert.Empty(receipt.Snapshot.Routes);
        Assert.Empty(Build(catalog, Facet(dimension, value, enabled: false)).Index.Items["Resource"].Facets);
    }

    [Theory]
    [InlineData("OneHandedWeapon")]
    [InlineData("Hands")]
    [InlineData("Utility")]
    [InlineData("Tool")]
    public void OptionalStaticRarity_CannotClassifyEquipment(string type)
    {
        // D086 D3: the row is skipped (named in the receipt's coverage); the item keeps its other evidence.
        var catalog = Catalog(ClassificationContractFixture.Item("Resource", type));
        Assert.DoesNotContain(Build(catalog, Facet("rarity", "epic")).Index.Items["Resource"].Facets, x => x.Dimension == "rarity");
        Assert.Contains(LoadedSourceReceipt.Build(catalog, new[] { Facet("rarity", "epic") }).Coverage, x => x.Contains("static rarity on equipment Resource"));
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("production")]
    [InlineData("breeding")]
    [InlineData("exchange")]
    public void AcquisitionMethod_PreservesConditionalDefinitionWithoutOrigin(string method)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        LoadedSourceFact Acquire(string detail, int amount = 1) => new(LoadedSourceFactKind.Acquisition, "defined-method", output: "Resource",
            route: "loaded-producer", method: method, detail: detail, amount: amount);
        var first = LoadedSourceReceipt.Build(catalog, new[] { Acquire("required world key=defeated-boss") });
        var second = LoadedSourceReceipt.Build(catalog, new[] { Acquire("required world key=other-boss") });
        var item = ClassificationPipeline.Build(catalog, Rules(), first.Snapshot).Index.Items["Resource"];
        Assert.Equal("misc", item.Primary);
        var facet = Assert.Single(item.Facets); Assert.Equal("acquisition", facet.Dimension); Assert.Equal(method, facet.Value);
        Assert.Empty(first.Snapshot.Routes);
        Assert.NotEqual(first.Snapshot.Fingerprint, second.Snapshot.Fingerprint);
        Assert.Empty(Build(catalog, Acquire("nonpositive", 0)).Index.Items["Resource"].Facets);
    }

    [Fact]
    public void UnsupportedFacetOrAcquisition_RejectsReceiptEvenWhenDisabled()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { Facet("biome", "meadows", enabled: false) }));
        // D086 D3: a value the filters can't hold is skipped and named, only when the row applies (B1.8).
        Assert.Empty(LoadedSourceReceipt.Build(catalog, new[] { Facet("rarity", "unknown", enabled: false) }).Coverage);
        Assert.Contains(LoadedSourceReceipt.Build(catalog, new[] { Facet("rarity", "unknown") }).Coverage, x => x.EndsWith("value for Resource is not usable; skipped"));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] {
            new LoadedSourceFact(LoadedSourceFactKind.Acquisition, "bad", output: "Resource", method: "purchase", eligible: false) }));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] {
            new LoadedSourceFact(LoadedSourceFactKind.Acquisition, "bad", output: "Resource", route: "producer", method: "guessed", eligible: false) }));
    }

    [Fact]
    public void FacetCompaction_KeepsDistinctDimensionsAndValuesAndDeterministicWitness()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        var rows = new[] { Facet("mod", "same.guid", id: "a"), Facet("mod", "same.guid", id: "z"),
            Facet("related-mod", "same.guid", id: "related"), Facet("related-mod", "other.guid", id: "other") };
        var first = LoadedSourceReceipt.Build(catalog, rows);
        var second = LoadedSourceReceipt.Build(catalog, rows.Reverse());
        Assert.Equal(first.Snapshot.Fingerprint, second.Snapshot.Fingerprint);
        var compact = Assert.Single(first.Snapshot.SemanticEvidence);
        Assert.Equal(3, compact.Facets.Count);
        Assert.Equal(2, compact.Facets.Count(f => f.Dimension == "related-mod"));
        Assert.Equal(Build(catalog, rows[0]).Index.Items["Resource"].Facets.Single().Source,
            compact.Facets.Single(f => f.Dimension == "mod").Source);
    }

    [Fact]
    public void MissingOptionalAndAcquisitionEndpoints_DoNotInventRegistration()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        var receipt = LoadedSourceReceipt.Build(catalog, new[] { Facet("mod", "plugin", "Absent"),
            new LoadedSourceFact(LoadedSourceFactKind.Acquisition, "absent-purchase", output: "Absent", route: "trader", method: "purchase") });
        Assert.Empty(receipt.Snapshot.SemanticEvidence);
    }

    [Fact]
    public void IncineratorCompleteConversion_AddsProcessingAndIncompleteRouteRefusesIt()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"), ClassificationContractFixture.Item("Product"));
        var route = LoadedSourceReceiptTests.Input(input: "Resource", output: "Product", method: "exchange");
        Assert.Equal("materials.processing", Build(catalog, route).Index.Items["Resource"].Primary);
        var missing = LoadedSourceReceiptTests.Input("missing", input: "Absent", output: "Product", method: "exchange");
        Assert.Equal("misc", Build(catalog, route, missing).Index.Items["Resource"].Primary);
    }

    [Fact]
    public void NewSemanticFacets_SurviveReplacementRebuildAndAssignmentRoundTrip()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("Resource"));
        var sources = LoadedSourceReceipt.Build(catalog, new[] { Use("siege-ammunition"), Facet("mod", "owner.guid"),
            Facet("rarity", "rare", id: "rarity"),
            new LoadedSourceFact(LoadedSourceFactKind.Acquisition, "acquisition", output: "Resource", route: "trader", method: "purchase") }).Snapshot;
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out var error), error);
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Resource", OverrideOperation.Replace, new[] { "special.runes" }) }, policyFingerprint: policy!.Fingerprint);
        var full = ClassificationPipeline.Build(catalog, rules, sources);
        var reused = ClassificationPipeline.Build(catalog, rules, sources);
        Assert.Equal(full.Index.Fingerprint, reused.Index.Fingerprint);
        Assert.Equal("special.runes", reused.Index.Items["Resource"].Primary);
        var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, full, policy!);
        var restored = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy!);
        Assert.Equal(3, restored.Items["Resource"].Facets.Count);
        Assert.Equal("rare", restored.Items["Resource"].Facets.Single(f => f.Dimension == "rarity").Value);
        Assert.Equal("owner.guid", restored.Items["Resource"].Facets.Single(f => f.Dimension == "mod").Value);
        var withdrawn = ClassificationPipeline.Build(catalog, Rules());
        Assert.Empty(withdrawn.Index.Items["Resource"].Facets); Assert.Equal("misc", withdrawn.Index.Items["Resource"].Primary);
    }
}
