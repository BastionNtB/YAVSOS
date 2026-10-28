using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationPipelineTests
{
    // Ordinary loaded-consumer witnesses. Each variant names a different consumer method so
    // receipts built from different variants have different fingerprints and usage facets.
    internal static LoadedSourceFact Use(string item, int variant) => new(LoadedSourceFactKind.ItemUse, "use:" + variant + ":" + item,
        item, route: "consumer:" + variant, method: variant switch { 2 => "building", 4 => "planting", 16 => "creature-food", _ => "fuel" });
    internal static string UsageOf(int variant) => variant switch { 2 => "building", 4 => "farming", 16 => "feed", _ => "fuel" };
    internal static ClassificationSourceSnapshot Sources(CatalogSnapshot catalog, params LoadedSourceFact[] rows) =>
        LoadedSourceReceipt.Build(catalog, rows).Snapshot;
    internal static ClassificationRuleSet Rules() => new ClassificationRuleSet(DefaultClassificationTaxonomy.Create());

    [Fact]
    public void Pipeline_SourceReceiptBindsFingerprintAndRejectsDifferentCatalog()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() });
        var sources = Sources(catalog, Use("Wood", 1));
        var result = ClassificationPipeline.Build(catalog, Rules(), sources);
        Assert.Equal(sources.Fingerprint, result.SourceFingerprint);
        Assert.Equal(UsageOf(1), result.Index.Items["Wood"].Facets.Single(x => x.Dimension == "usage").Value);
        Assert.Equal("none", ClassificationPipeline.Build(catalog, Rules()).SourceFingerprint);
        Assert.Throws<InvalidDataException>(() => ClassificationPipeline.Build(CatalogSnapshot.Build(new[] { Item(value: 3) }), Rules(), sources));
    }

    [Fact]
    public void Pipeline_NoSourcesKeepsFacetsEmptyAndPolicyReplacementRemainsAuthoritative()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("Sword", "OneHandedWeapon") });
        Assert.Empty(ClassificationPipeline.Build(catalog, Rules()).Index.Items["Sword"].Facets);
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Sword", OverrideOperation.Replace, new[] { "valuables" }) });
        var result = ClassificationPipeline.Build(catalog, rules, Sources(catalog, Use("Sword", 1))).Index.Items["Sword"];
        Assert.Equal(new[] { "valuables" }, Categories(result));
        Assert.DoesNotContain(result.Facets, f => f.Dimension is "biome" or "creature-biome");
    }

    [Fact]
    public void Pipeline_CancellationAndClassificationCapRejectWholeBuild()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ClassificationPipeline.Build(catalog, Rules(), cancellation: cancelled.Token));
        Assert.Throws<InvalidOperationException>(() => ClassificationPipeline.Build(catalog, Rules(), Sources(catalog, Use("Wood", 1)), maxClassificationWork: 1));
        var sources = Sources(catalog, Use("Wood", 1));
        Assert.Equal(ClassificationPipeline.Build(catalog, Rules(), sources).Index.Fingerprint,
            ClassificationPipeline.Build(catalog, Rules(), sources).Index.Fingerprint);
    }
}
