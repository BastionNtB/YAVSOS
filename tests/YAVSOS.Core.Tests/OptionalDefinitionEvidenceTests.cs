using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class OptionalDefinitionEvidenceTests
{
    private static readonly OptionalCapability Supported = new("jotunn", OptionalCapabilityState.Supported, "2.30.2", "test probe");
    private static CatalogSnapshot Catalog() => CatalogSnapshot.Build(new[] {
        ClassificationContractFixture.Item("OwnedHide"), ClassificationContractFixture.Item("Wood") });
    private static OptionalDefinitionObservation Owns(string item, string owner, int index = 0) =>
        new(OptionalDefinitionKind.ItemRegistration, item, owner, item, index);
    private static OptionalDefinitionObservation Builds(string item, string owner = "com.example.builder", int index = 0,
        int amount = 2, bool eligible = true, string piece = "ExampleWall") =>
        new(OptionalDefinitionKind.PieceResource, piece, owner, item, index, amount, eligible);
    private static OptionalDefinitionObservation Drops(string item, string owner = "com.example.monsters", int index = 0) =>
        new(OptionalDefinitionKind.CreatureDrop, "ExampleWolf", owner, item, index, 1, true);
    private static IReadOnlyDictionary<string, ClassificationResult> Classify(CatalogSnapshot catalog, IEnumerable<LoadedSourceFact> rows) =>
        ClassificationPipeline.Build(catalog, ClassificationPipelineTests.Rules(), LoadedSourceReceipt.Build(catalog, rows).Snapshot).Index.Items;
    private static string[] Facets(ClassificationResult item, string dimension) =>
        item.Facets.Where(x => x.Dimension == dimension).Select(x => x.Value).ToArray();

    [Fact]
    public void SupportedOwnership_KeepsOwnerBuildingUseAndCreatureDropAsSeparateFacets()
    {
        var catalog = Catalog();
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] {
            Owns("OwnedHide", "com.example.armory"), Builds("Wood"), Drops("OwnedHide") });
        var items = Classify(catalog, rows);

        Assert.Equal(new[] { "com.example.armory" }, Facets(items["OwnedHide"], "mod"));
        Assert.Equal(new[] { "com.example.monsters" }, Facets(items["OwnedHide"], "related-mod"));
        Assert.Empty(Facets(items["OwnedHide"], "construction-mod"));
        // A building use never relabels the resource owner.
        Assert.Equal(new[] { "com.example.builder" }, Facets(items["Wood"], "construction-mod"));
        Assert.Empty(Facets(items["Wood"], "mod"));
        var owner = items["OwnedHide"].Facets.Single(x => x.Dimension == "mod");
        Assert.StartsWith("loaded-use:", owner.Source);
        Assert.Contains("registered item: OwnedHide", owner.Reason);
        Assert.Contains(rows, x => x.Kind == LoadedSourceFactKind.Coverage && x.Identity == "optional:jotunn:capability" && x.Detail.Contains("2.30.2"));
    }

    [Theory]
    [InlineData(OptionalCapabilityState.Absent)]
    [InlineData(OptionalCapabilityState.Incompatible)]
    public void UnsupportedCapability_PublishesOnlyCoverageAndLeavesEveryOwnershipDimensionUnknown(OptionalCapabilityState state)
    {
        var catalog = Catalog();
        var capability = state == OptionalCapabilityState.Absent ? OptionalCapability.Absent("jotunn")
            : OptionalCapability.Incompatible("jotunn", "2.31.0", "supported Jotunn version is 2.30.2");
        var rows = OptionalDefinitionEvidence.Build(catalog, capability, new[] { Owns("OwnedHide", "com.example.armory"), Builds("Wood"), Drops("OwnedHide") });

        var coverage = Assert.Single(rows);
        Assert.Equal(LoadedSourceFactKind.Coverage, coverage.Kind);
        Assert.Contains("jotunn=" + state, coverage.Detail);
        Assert.All(Classify(catalog, rows).Values, item => Assert.DoesNotContain(item.Facets, x => x.Dimension is "mod" or "related-mod" or "construction-mod"));
    }

    [Theory]
    [InlineData(OptionalDefinitionEvidence.JotunnFallbackOwner)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("bad\nowner")]
    public void FallbackOrInvalidOwner_IsNotAdmittedAsOwnership(string owner)
    {
        var catalog = Catalog();
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Owns("OwnedHide", owner), Builds("Wood", owner), Drops("OwnedHide", owner) });

        Assert.DoesNotContain(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
        Assert.Contains("invalid-owner=3", rows.Single(x => x.Identity == "optional:jotunn:gaps").Detail);
    }

    [Fact]
    public void UnregisteredOrUnresolvedReference_DoesNotInventARegistration()
    {
        var catalog = Catalog();
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] {
            Owns("AbsentItem", "com.example.armory"), Builds("", index: 0), Drops("AbsentDrop"),
            new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, "OwnedHideClone", "com.example.armory", "OwnedHide") });
        var receipt = LoadedSourceReceipt.Build(catalog, rows);

        Assert.DoesNotContain(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
        Assert.Contains("unresolved=4", rows.Single(x => x.Identity == "optional:jotunn:gaps").Detail);
    }

    [Fact]
    public void ConflictingOwnersForOneItem_StayUnknownWithoutRejectingOtherEvidence()
    {
        var catalog = Catalog();
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] {
            Owns("OwnedHide", "com.example.armory"), Owns("OwnedHide", "com.example.other", 1), Builds("Wood") });
        var items = Classify(catalog, rows);

        Assert.Empty(Facets(items["OwnedHide"], "mod"));
        Assert.Equal(new[] { "com.example.builder" }, Facets(items["Wood"], "construction-mod"));
        Assert.Contains("ambiguous-owner=1", rows.Single(x => x.Identity == "optional:jotunn:gaps").Detail);
    }

    [Fact]
    public void DuplicateSlotOrOverBound_DisablesOnlyThisAdapter()
    {
        var catalog = Catalog();
        var duplicate = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Builds("Wood"), Builds("OwnedHide") });
        var oversized = OptionalDefinitionEvidence.Build(catalog, Supported,
            Enumerable.Range(0, OptionalDefinitionEvidence.MaximumObservations + 1).Select(i => Builds("Wood", index: i)));

        foreach (var rows in new[] { duplicate, oversized })
        {
            Assert.DoesNotContain(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
            Assert.Contains("facets Unknown", rows.Single(x => x.Identity == "optional:jotunn:gap").Detail);
            Assert.All(Classify(catalog, rows).Values, item => Assert.DoesNotContain(item.Facets, x => x.Dimension == "construction-mod"));
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void DisabledOrNonpositiveSlot_ChangesReceiptIdentityButAddsNoFacet(bool eligible, int amount)
    {
        var catalog = Catalog();
        var baseline = LoadedSourceReceipt.Build(catalog, OptionalDefinitionEvidence.Build(catalog, Supported, Array.Empty<OptionalDefinitionObservation>()));
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Builds("Wood", amount: amount, eligible: eligible) });
        var receipt = LoadedSourceReceipt.Build(catalog, rows);

        Assert.Single(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
        Assert.NotEqual(baseline.Snapshot.Fingerprint, receipt.Snapshot.Fingerprint);
        Assert.Empty(Facets(Classify(catalog, rows)["Wood"], "construction-mod"));
    }

    [Fact]
    public void Build_IsOrderIndependentAndLeavesObservationsUnchanged()
    {
        var catalog = Catalog();
        var input = new[] { Drops("OwnedHide"), Builds("Wood", index: 1), Owns("OwnedHide", "com.example.armory"), Builds("OwnedHide") };
        var before = input.Select(x => (x.Kind, x.Definition, x.Owner, x.Item, x.Index, x.Amount, x.Eligible)).ToArray();

        var forward = LoadedSourceReceipt.Build(catalog, OptionalDefinitionEvidence.Build(catalog, Supported, input));
        var reverse = LoadedSourceReceipt.Build(catalog, OptionalDefinitionEvidence.Build(catalog, Supported, input.Reverse()));

        Assert.Equal(forward.Snapshot.Fingerprint, reverse.Snapshot.Fingerprint);
        Assert.Equal(before, input.Select(x => (x.Kind, x.Definition, x.Owner, x.Item, x.Index, x.Amount, x.Eligible)).ToArray());
    }

    [Fact]
    public void RejectedClaims_ChangeReceiptIdentityWithoutAddingPermissiveEvidence()
    {
        var catalog = Catalog();
        LoadedSourceReceipt Receipt(string owner) => LoadedSourceReceipt.Build(catalog,
            OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Owns("OwnedHide", "com.example.one"), Owns("OwnedHide", owner, 1) }));
        var first = Receipt("com.example.two"); var changed = Receipt("com.example.three");
        Assert.NotEqual(first.Snapshot.Fingerprint, changed.Snapshot.Fingerprint);
        Assert.Empty(first.Snapshot.SemanticEvidence); Assert.Empty(changed.Snapshot.SemanticEvidence);
        var invalid = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Builds("Wood", "bad\ud800") });
        Assert.DoesNotContain(invalid, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void DisabledOrNonpositiveRegistration_IsRawEvidenceOnly(bool eligible, int amount)
    {
        var catalog = Catalog();
        var observation = new OptionalDefinitionObservation(OptionalDefinitionKind.ItemRegistration, "OwnedHide", "com.example.one", "OwnedHide",
            amount: amount, eligible: eligible);
        var rows = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { observation });
        Assert.DoesNotContain(rows, x => x.Kind == LoadedSourceFactKind.FacetEvidence);
        Assert.Empty(Classify(catalog, rows)["OwnedHide"].Facets);
    }
}
