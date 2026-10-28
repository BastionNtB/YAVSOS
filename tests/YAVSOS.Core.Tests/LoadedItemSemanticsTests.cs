using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

public sealed class LoadedItemSemanticsTests
{
    private static CatalogSnapshot Catalog(params CatalogFact[] facts) => CatalogSnapshot.Build(facts);
    private static LoadedSourceFact Use(string method, string item = "UnknownResource", bool eligible = true, int amount = 1,
        string id = "use", string consumer = "consumer") => new(LoadedSourceFactKind.ItemUse, id, output: item,
            route: consumer, method: method, eligible: eligible, amount: amount);
    private static ClassificationResult Classify(CatalogSnapshot catalog, params LoadedSourceFact[] uses) =>
        ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), LoadedSourceReceipt.Build(catalog, uses).Snapshot).Index.Items["UnknownResource"];

    [Theory]
    [InlineData("building", "materials.building", "building")]
    [InlineData("planting", "farming", "farming")]
    [InlineData("fuel", "materials.fuel", "fuel")]
    [InlineData("creature-food", "animal.feed", "feed")]
    [InlineData("saddle", "animal.saddles", "equipment")]
    [InlineData("door-key", "special.keys", null)]
    [InlineData("trader-handover", "special.keys", null)]
    [InlineData("offering", "special.offerings", null)]
    [InlineData("fireworks", "special.fireworks", null)]
    public void ExplicitConsumer_AddsSpecificMembershipAndUsageWithoutOrigin(string method, string category, string? usage)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        var item = Classify(catalog, Use(method));
        Assert.Equal(category, item.Primary);
        var member = Assert.Single(item.Memberships, x => !CategoryClosure.IsDerived(x));
        Assert.Equal(category, member.Category); Assert.StartsWith("loaded-use:", member.Source);
        Assert.Contains("consumer", member.Reason);
        Assert.DoesNotContain(item.Facets, x => x.Dimension is "biome" or "mod" or "related-mod" or "rarity");
        if (usage == null) Assert.Empty(item.Facets);
        else { var facet = Assert.Single(item.Facets); Assert.Equal("usage", facet.Dimension); Assert.Equal(usage, facet.Value); }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void UnavailableConsumer_AddsNoMembershipOrFacet(bool eligible, int amount)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        var item = Classify(catalog, Use("fuel", eligible: eligible, amount: amount));
        Assert.Equal("misc", item.Primary); Assert.Empty(item.Facets);
        Assert.Equal("misc", Assert.Single(item.Memberships).Category);
    }

    [Fact]
    public void MissingItemUse_DoesNotInventCatalogOrGraphRegistration()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        var receipt = LoadedSourceReceipt.Build(catalog, new[] { Use("planting", "AbsentSeed") });
        Assert.Empty(receipt.Snapshot.SemanticEvidence); Assert.Empty(receipt.Snapshot.Routes);
        var result = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), receipt.Snapshot);
        Assert.Equal(new[] { "UnknownResource" }, result.Index.Items.Keys);
    }

    [Fact]
    public void ConsumerEvidence_IsOrdinalCompactedImmutableAndIncludesDisabledChangesInDigest()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        var rows = new[] { Use("fuel", id: "z", consumer: "later"), Use("fuel", id: "a", consumer: "earlier"), Use("building", id: "b") };
        var a = LoadedSourceReceipt.Build(catalog, rows); var b = LoadedSourceReceipt.Build(catalog, rows.Reverse());
        Assert.Equal(a.Snapshot.Fingerprint, b.Snapshot.Fingerprint);
        var evidence = Assert.Single(a.Snapshot.SemanticEvidence);
        Assert.Equal(2, evidence.Memberships.Count); Assert.Equal(2, evidence.Facets.Count);
        Assert.Contains("earlier", evidence.Memberships.Single(x => x.Category == "materials.fuel").Reason);
        Assert.Throws<NotSupportedException>(() => ((IList<ItemClassificationEvidence>)a.Snapshot.SemanticEvidence).Clear());
        Assert.NotEqual(a.Snapshot.Fingerprint, LoadedSourceReceipt.Build(catalog, rows.Append(Use("fuel", id: "disabled", eligible: false))).Snapshot.Fingerprint);
        Assert.True(a.Snapshot.ScalarBytes > LoadedSourceReceipt.Build(catalog, Array.Empty<LoadedSourceFact>()).Snapshot.ScalarBytes);
    }

    [Fact]
    public void UnsupportedOrIncompleteUse_RejectsWholeReceiptEvenWhenDisabled()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { Use("unknown", eligible: false) }));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { Use("fuel", consumer: "") }));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { Use("fuel", item: "") }));
    }

    [Theory]
    [InlineData("smelt")]
    [InlineData("cook")]
    [InlineData("ferment")]
    public void MachineUse_ClassifiesProcessingWithoutAssumingOreOrMetalComposition(string method)
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"), ClassificationContractFixture.Item("Product"));
        var item = Classify(catalog, LoadedSourceReceiptTests.Input(input: "UnknownResource", output: "Product", method: method));
        Assert.Equal("materials.processing", item.Primary);
        Assert.Equal("materials.processing", Assert.Single(item.Memberships, x => !CategoryClosure.IsDerived(x)).Category);
        Assert.Equal("processing", Assert.Single(item.Facets).Value);
    }

    [Fact]
    public void CookingInput_GetsFoodIngredientWhileEdibleOutputKeepsFoodCategory()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"),
            ClassificationContractFixture.Item("Meal", "Consumable", health: 20, duration: 60));
        var snapshot = LoadedSourceReceipt.Build(catalog, new[] { LoadedSourceReceiptTests.Input(input: "UnknownResource", output: "Meal", method: "cook") }).Snapshot;
        var index = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), snapshot).Index;
        Assert.Equal("ingredients.food", index.Items["UnknownResource"].Primary);
        Assert.Contains(index.Items["UnknownResource"].Facets, x => x.Dimension == "usage" && x.Value == "food");
        Assert.Equal("food.health", index.Items["Meal"].Primary);
        Assert.DoesNotContain(index.Items["Meal"].Memberships, x => x.Category == "materials.processing");
    }

    [Fact]
    public void Fermentation_IdentifiesBaseAndItsRecipeIngredientsWithoutPrefixGuessing()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"), ClassificationContractFixture.Item("Berry"),
            ClassificationContractFixture.Item("Potion", "Consumable"), ClassificationContractFixture.Recipe(output: "UnknownResource"),
            ClassificationContractFixture.Requirement(input: "Berry"));
        var snapshot = LoadedSourceReceipt.Build(catalog, new[] { LoadedSourceReceiptTests.Input(input: "UnknownResource", output: "Potion", method: "ferment") }).Snapshot;
        var index = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), snapshot).Index;
        Assert.Equal("brewing.bases", index.Items["UnknownResource"].Primary);
        Assert.Equal("meads", index.Items["Potion"].Primary);
        Assert.Equal("ingredients.brewing", index.Items["Berry"].Primary);
        Assert.Contains(index.Items["Berry"].Memberships, x => x.Category == "materials.crafting");
        var withoutSources = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules()).Index;
        Assert.Equal("misc", withoutSources.Items["UnknownResource"].Primary);
        Assert.DoesNotContain(withoutSources.Items["Berry"].Memberships, x => x.Category == "ingredients.brewing");
    }

    [Fact]
    public void IncompleteOrUnavailableConversion_CannotSupplyProcessingOrBrewingMembership()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"), ClassificationContractFixture.Item("Potion", "Consumable"));
        foreach (var rows in new[] {
            new[] { LoadedSourceReceiptTests.Input(input: "UnknownResource", output: "Potion", method: "ferment", eligible: false) },
            new[] { LoadedSourceReceiptTests.Input("one", input: "UnknownResource", output: "Potion", method: "ferment"),
                LoadedSourceReceiptTests.Input("two", input: "Absent", output: "Potion", method: "ferment") } })
        {
            var item = Classify(catalog, rows); Assert.Equal("misc", item.Primary); Assert.Empty(item.Facets);
        }
    }

    [Fact]
    public void ReplacementOverride_ReplacesConsumerMembershipsButPreservesUseEvidence()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource", quest: true));
        var snapshot = LoadedSourceReceipt.Build(catalog, new[] { Use("fuel") }).Snapshot;
        var taxonomy = DefaultClassificationTaxonomy.Create();
        var rules = new ClassificationRuleSet(taxonomy, new[] { new ClassificationOverride("exact", "UnknownResource", OverrideOperation.Replace, new[] { "special.runes" }) });
        var item = ClassificationPipeline.Build(catalog, rules, snapshot).Index.Items["UnknownResource"];
        Assert.Equal("special.runes", item.Primary); Assert.Equal("special.runes", Assert.Single(item.Memberships, x => !CategoryClosure.IsDerived(x)).Category);
        Assert.True(item.Quest); Assert.Contains(item.Facets, x => x.Dimension == "usage" && x.Value == "fuel");
    }

    [Fact]
    public void RepeatedPipeline_RetainsSemanticEvidenceAndSourceWithdrawalRemovesIt()
    {
        var catalog = Catalog(ClassificationContractFixture.Item("UnknownResource"));
        var snapshot = LoadedSourceReceipt.Build(catalog, new[] { Use("saddle") }).Snapshot;
        var full = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), snapshot);
        var reused = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), snapshot);
        Assert.Equal(full.Index.Fingerprint, reused.Index.Fingerprint);
        Assert.Equal("animal.saddles", reused.Index.Items["UnknownResource"].Primary);
        var withdrawn = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules());
        Assert.Equal("misc", withdrawn.Index.Items["UnknownResource"].Primary); Assert.Empty(withdrawn.Index.Items["UnknownResource"].Facets);
    }
    [Fact]
    public void AssignmentRoundTrip_PreservesNamedAndConsumerCategoriesAndLiteralProvenance()
    {
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out var error), error);
        var catalog = Catalog(ClassificationContractFixture.Item("Wood"), ClassificationContractFixture.Item("UnknownResource"));
        var source = LoadedSourceReceipt.Build(catalog, new[] { Use("planting"), Use("building", "Wood", id: "build") }).Snapshot;
        var rules = ClassificationRuleSet.FromSchema1(policy!);
        var classified = ClassificationPipeline.Build(catalog, rules, source);
        var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy!);
        var restored = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy!);
        Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
        Assert.Equal("materials.wood", restored.Items["Wood"].Primary);
        Assert.Contains(restored.Items["Wood"].Memberships, x => x.Category == "materials.building");
        Assert.Equal("farming", restored.Items["UnknownResource"].Primary);
        Assert.Equal(classified.Index.Items["UnknownResource"].Memberships.Select(x => (x.Category, x.Source, x.Reason)),
            restored.Items["UnknownResource"].Memberships.Select(x => (x.Category, x.Source, x.Reason)));
        Assert.Equal(AcceptanceMatch.FilteredCategory, new ClassificationAcceptance(new[] {
            ClassificationContractFixture.Rule(new[] { "farming" }, ("usage", new[] { "farming" })) }).Match(restored.Items["UnknownResource"]));
    }

}
