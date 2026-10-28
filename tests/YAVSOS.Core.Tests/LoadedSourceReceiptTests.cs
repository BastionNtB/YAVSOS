using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Core.Tests;

public sealed class LoadedSourceReceiptTests
{
    [Fact]
    public void Limits_ExtendedAssetCaptureDeadlineIsBoundedAndOrdinaryDefaultStaysThirtySeconds()
    {
        Assert.Equal(30, new LoadedSourceLimits().BuildSeconds);
        Assert.Equal(900, new LoadedSourceLimits(buildSeconds: 900).BuildSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(buildSeconds: 900.001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(buildSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(buildSeconds: double.NaN));
    }

    internal static CatalogSnapshot MakeCatalog(params string[] names) => CatalogSnapshot.Build(names.Select(name => ClassificationContractFixture.Item(name)));
    internal static LoadedSourceFact Use(string id = "wood", string output = "Wood", string method = "fuel", bool eligible = true, int amount = 1) =>
        new LoadedSourceFact(LoadedSourceFactKind.ItemUse, id, output, route: "consumer", method: method, amount: amount, eligible: eligible);
    internal static LoadedSourceFact Coverage(string detail = "gap: locations unsupported") =>
        new LoadedSourceFact(LoadedSourceFactKind.Coverage, "locations", detail: detail);
    internal static LoadedSourceFact Input(string id = "slot", string input = "Wood", string output = "Output", string route = "machine",
        string method = "smelt", bool eligible = true, int amount = 1) =>
        new LoadedSourceFact(LoadedSourceFactKind.RouteInput, id, output, input, route, method, amount: amount, eligible: eligible);
    internal static LoadedSourceReceipt Receipt(CatalogSnapshot catalog, params LoadedSourceFact[] rows) => LoadedSourceReceipt.Build(catalog, rows);

    [Fact]
    public void Receipt_IsDetachedOrdinalImmutableAndDeterministicAcrossObservationOrder()
    {
        var catalog = MakeCatalog("Wood", "Output"); var rows = new[] { Use(), Input(), Coverage() };
        var a = Receipt(catalog, rows); var b = Receipt(catalog, rows.Reverse().ToArray()); rows[0] = Use(method: "building");
        Assert.Equal(a.Snapshot.Fingerprint, b.Snapshot.Fingerprint);
        Assert.Equal(new[] { "locations", "slot", "wood" }, a.Facts.Select(x => x.Identity));
        Assert.Equal(new[] { "locations:gap: locations unsupported" }, a.Coverage);
        Assert.Throws<NotSupportedException>(() => ((IList<LoadedSourceFact>)a.Facts).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)a.Coverage).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MaterialRoute>)a.Snapshot.Routes).Clear());
    }

    [Fact]
    public void Receipt_FingerprintIncludesCatalogCoverageDisabledFactsAndEveryRouteScalar()
    {
        var catalog = MakeCatalog("Wood", "Output");
        var a = Receipt(catalog, Use(eligible: false), Input(), Coverage());
        foreach (var alternative in new[] {
            Receipt(catalog, Use(eligible: false, method: "building"), Input(), Coverage()),
            Receipt(catalog, Use(eligible: false), Input(amount: 2), Coverage()),
            Receipt(catalog, Use(eligible: false), Input(method: "cook"), Coverage()),
            Receipt(catalog, Use(eligible: false), Input(route: "other"), Coverage()),
            Receipt(catalog, Use(eligible: false), Input(), Coverage("gap: optional API incompatible")),
            Receipt(MakeCatalog("Wood", "Output", "Other"), Use(eligible: false), Input(), Coverage()) })
            Assert.NotEqual(a.Snapshot.Fingerprint, alternative.Snapshot.Fingerprint);
        Assert.Equal(3, a.Facts.Count);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public void Receipt_DisabledAndNonpositiveUsesAddNoSemanticEvidence(bool eligible, int amount)
    {
        var receipt = Receipt(MakeCatalog("Wood"), Use(eligible: eligible, amount: amount));
        Assert.Empty(receipt.Snapshot.SemanticEvidence); Assert.Single(receipt.Facts);
    }

    // P6-15 (D065): a tameable creature's food carries its name token as "eaten-by"; merged by token, filterable with Food.
    private static LoadedSourceFact Tame(string creature, string food, string token, int slot = 0, bool eligible = true) =>
        new LoadedSourceFact(LoadedSourceFactKind.FacetEvidence, creature + ":tame-food:" + slot, food, route: creature, method: "eaten-by",
            detail: token, amount: 1, eligible: eligible);

    [Fact]
    public void Receipt_TameFoodBecomesAnEatenByFilter_MergedByCreatureName()
    {
        var catalog = CatalogSnapshot.Build(new[]
        {
            ClassificationContractFixture.Item("Carrot", "Consumable", health: 10, stamina: 30, duration: 600),
            ClassificationContractFixture.Item("Mushroom", "Consumable", health: 15, stamina: 15, duration: 600),
            ClassificationContractFixture.Item("Wood"),
        });
        var receipt = Receipt(catalog,
            Use("boar:feed:0", "Carrot", "creature-food"), Tame("Boar", "Carrot", "$enemy_boar"),
            Tame("Boar_spiritcaller", "Carrot", "$enemy_boar"), // a variant with the same name: one value
            Tame("Prowler_TW", "Mushroom", "$enemy_prowler"),
            Tame("Lox", "Wood", "$enemy_lox", eligible: false)); // disabled: no evidence
        var index = ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), receipt.Snapshot).Index;
        var carrot = index.Items["Carrot"]; var mushroom = index.Items["Mushroom"];
        Assert.Equal(new[] { "$enemy_boar" }, carrot.Facets.Where(x => x.Dimension == "eaten-by").Select(x => x.Value));
        var boarFood = ClassificationContractFixture.Rule(new[] { "food" }, ("eaten-by", new[] { "$enemy_boar" }));
        Assert.True(ClassificationFilters.Matches(carrot, boarFood));
        Assert.False(ClassificationFilters.Matches(mushroom, boarFood));
        Assert.True(ClassificationFilters.Matches(mushroom, ClassificationContractFixture.Rule(null, ("eaten-by", new[] { "$enemy_prowler" }))));
        Assert.DoesNotContain(index.Items["Wood"].Facets, x => x.Dimension == "eaten-by");
        // Q8 item tooltip line: smallest categories, then the tames that eat it, in the player's language.
        var taxonomy = DefaultClassificationTaxonomy.Create();
        var line = RuleText.ItemLine(carrot, taxonomy, (_, token) => token == "$enemy_boar" ? "Boar" : token);
        Assert.Contains("eaten by Boar", line);
        Assert.DoesNotContain("Food & farming,", line); // no umbrella next to its own subcategory
        Assert.Equal("", RuleText.ItemLine(null, taxonomy));
        // The filter only shows where some item in scope is eaten by a tame.
        var choices = FilterChoices.Compute(index.Items.Values, new[] { "food" }, (_, _) => false, new Dictionary<string, IReadOnlyList<string>>());
        Assert.Equal(new[] { "$enemy_boar", "$enemy_prowler" }, choices.Single(x => x.Dimension == "eaten-by").Values);
    }

    [Fact]
    public void Receipt_CultivationKeepsCompleteAndInputs()
    {
        var catalog = MakeCatalog("Wood", "Iron", "Output");
        var receipt = Receipt(catalog, Input("one", method: "cultivate"), Input("two", "Iron", method: "cultivate"));
        var route = Assert.Single(receipt.Snapshot.Routes);
        Assert.False(route.AnyIngredient); Assert.Equal(new[] { "Iron", "Wood" }, route.Inputs.Select(x => x.Input));
        Assert.Equal("live:machine", route.Id); Assert.Equal("Output", route.Output);
    }

    [Fact]
    public void Receipt_MissingRequiredInputRemainsAnUnresolvedRouteIdentity()
    {
        var catalog = MakeCatalog("Wood", "Output");
        var receipt = Receipt(catalog, Input("one"), Input("missing", "yavsos:missing-source:slot"));
        Assert.Equal(2, Assert.Single(receipt.Snapshot.Routes).Inputs.Count);
        Assert.DoesNotContain("yavsos:missing-source:slot", ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), receipt.Snapshot).Index.Items.Keys);
    }

    [Fact]
    public void Receipt_MissingInputPlaceholderCollisionWithLoadedItemIsRejected()
    {
        var missing = "yavsos:missing-source:slot";
        Assert.Throws<InvalidDataException>(() => Receipt(MakeCatalog("Wood", "Output", missing), Input(input: missing)));
    }

    [Fact]
    public void Receipt_ReservedMissingInputCannotBeAnOutputEvenWhenAbsentFromCatalog()
    {
        var missing = "yavsos:missing-source:slot"; var catalog = MakeCatalog("Wood", "Output");
        Assert.False(catalog.Items.ContainsKey(missing));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Use(output: missing), Input(input: missing)));
    }

    [Fact]
    public void Receipt_ReservedMissingInputCannotBeProducedByAnotherRoute()
    {
        var missing = "yavsos:missing-source:slot"; var catalog = MakeCatalog("Wood", "Output");
        Assert.False(catalog.Items.ContainsKey(missing));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog,
            Input("spoof", output: missing, route: "spoof-producer"), Input("required", input: missing)));
    }

    [Fact]
    public void Receipt_OneUnavailableRequiredSlotDisablesWholeAlternative()
    {
        var receipt = Receipt(MakeCatalog("Wood", "Output"), Input("one"), Input("two", "Iron", eligible: false));
        Assert.Empty(receipt.Snapshot.Routes); Assert.Equal(2, receipt.Facts.Count);
    }

    [Fact]
    public void Receipt_RejectsAmbiguousAndInconsistentRouteFactsRatherThanPartialGraph()
    {
        var catalog = MakeCatalog("Wood", "Output");
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Use(), Use()));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Input("one"), Input("two", output: "Other")));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Input("one"), Input("two", method: "cook")));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Input(input: "")));
        Assert.Throws<InvalidDataException>(() => Receipt(catalog, Use(output: "")));
    }

    [Fact]
    public void Receipt_RejectsInventoryCensusObservations()
    {
        Assert.Throws<InvalidDataException>(() => Receipt(MakeCatalog("Wood"),
            new LoadedSourceFact(LoadedSourceFactKind.ProfileScope, "inventory-scope:complete", method: "complete", detail: new string('a', 64))));
    }

    [Fact]
    public void Receipt_EnforcesExactFactAndUtf8ScalarBoundaries()
    {
        var catalog = MakeCatalog("Wood"); var first = Use("木"); var second = Coverage();
        var limits = new LoadedSourceLimits(maxFacts: 1, maxScalarBytes: first.ScalarBytes);
        var receipt = LoadedSourceReceipt.Build(catalog, new[] { first }, limits);
        Assert.Equal(first.ScalarBytes, receipt.ScalarBytes);
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { first, second }, limits));
        Assert.Throws<InvalidDataException>(() => LoadedSourceReceipt.Build(catalog, new[] { first }, new LoadedSourceLimits(maxScalarBytes: first.ScalarBytes - 1)));
    }

    [Fact]
    public void Receipt_CompactScalarAccountingCountsTheRetainedRoutes()
    {
        var catalog = MakeCatalog("Wood");
        // live:machine route id (12), Wood/Output/smelt/machine link (4+6+5+7). Identities are no longer retained (D13).
        Assert.Equal(34, Receipt(catalog, Input()).Snapshot.ScalarBytes);
    }

    [Fact]
    public void Receipt_CancellationBeforeAndDuringEnumerationRejectsWholeReceipt()
    {
        var catalog = MakeCatalog("Wood"); using var cancellation = new CancellationTokenSource();
        IEnumerable<LoadedSourceFact> Rows() { yield return Use(); cancellation.Cancel(); yield return Coverage(); }
        Assert.Throws<OperationCanceledException>(() => LoadedSourceReceipt.Build(catalog, Rows(), cancellation: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LoadedSourceReceipt.Build(catalog, Array.Empty<LoadedSourceFact>(), cancellation: cancellation.Token));
    }

    [Fact]
    public void Facts_RejectInvalidKindsIdentityControlsMalformedUtf16AndOversize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceFact((LoadedSourceFactKind)99, "invalid"));
        Assert.Throws<ArgumentException>(() => Use(id: "bad\n"));
        Assert.ThrowsAny<ArgumentException>(() => Use(output: "bad\ud800"));
        Assert.ThrowsAny<ArgumentException>(() => Coverage("bad\udc00"));
        Assert.Throws<ArgumentException>(() => Use(output: new string('x', 1025)));
        Assert.Throws<ArgumentException>(() => Coverage(new string('x', 4097)));
    }

    [Fact]
    public void Limits_RejectInvalidAndNonfiniteBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(maxFacts: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(stepsPerTick: 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(sliceMilliseconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoadedSourceLimits(buildSeconds: double.PositiveInfinity));
    }
}
