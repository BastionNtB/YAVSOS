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

public sealed class ItemProfileScopeTests
{
    private static LoadedSourceFact Scope(string method, string item = "Npc", string type = "OneHandedWeapon", int icons = 0,
        string id = "", bool eligible = true) => new(LoadedSourceFactKind.ProfileScope,
            id.Length == 0 ? "scope:" + method : id, method == "complete" ? "" : item,
            method == "definition" ? type : "", method: method, amount: icons, eligible: eligible,
            detail: method == "complete" ? new string('a', 64) : "");
    private static CatalogSnapshot Catalog(params CatalogFact[] extra) => CatalogSnapshot.Build(new[] {
        Item("Npc", "OneHandedWeapon", icon: false), Item("Uncertain", icon: false), Item("Hair", "Customization"), Item("Wood") }.Concat(extra));
    private static LoadedSourceFact[] Proof() => new[] { Scope("npc-consumer"), Scope("definition"), Scope("complete") };

    [Fact]
    public void Scope_TypedCustomizationIsExcludedWithoutNegativeProofAndUncertainIconlessItemsRemain()
    {
        var catalog = Catalog(); var scope = ItemProfileScope.Build(catalog);
        Assert.Equal("typed-customization", Assert.Single(scope.Excluded).Value);
        Assert.True(catalog.Items.ContainsKey("Hair"));
        Assert.DoesNotContain("Npc", scope.Excluded.Keys); Assert.DoesNotContain("Uncertain", scope.Excluded.Keys);
        Assert.False(scope.AcquisitionScanComplete);
    }

    [Fact]
    public void Scope_CompleteReviewedObservationExcludesNewNpcIdentityWithoutAnyItemNameRule()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NovelAttack", "Bow", icon: false) });
        var observations = new[] { Scope("npc-consumer", "NovelAttack"), Scope("definition", "NovelAttack", "Bow"), Scope("complete") };
        var scope = ItemProfileScope.Build(catalog, observations);
        Assert.Equal("proven-npc-internal", scope.Excluded["NovelAttack"]); Assert.True(scope.AcquisitionScanComplete);
        Assert.Equal(scope.Fingerprint, ItemProfileScope.Build(catalog, observations.Reverse()).Fingerprint);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing-consumer")]
    [InlineData("missing-definition")]
    [InlineData("icon-conflict")]
    [InlineData("type-conflict")]
    [InlineData("preserving-reference")]
    public void Scope_MissingOrContradictoryEvidenceRetainsNpc(string caseId)
    {
        var rows = Proof().Where(r => caseId != "incomplete" || r.Method != "complete")
            .Where(r => caseId != "missing-consumer" || r.Method != "npc-consumer")
            .Where(r => caseId != "missing-definition" || r.Method != "definition").ToList();
        if (caseId == "icon-conflict") rows.Add(Scope("definition", icons: 1, id: "conflict"));
        if (caseId == "type-conflict") rows.Add(Scope("definition", type: "Bow", id: "conflict"));
        if (caseId == "preserving-reference") rows.Add(Scope("preserve"));
        Assert.DoesNotContain("Npc", ItemProfileScope.Build(Catalog(), rows).Excluded.Keys);
    }

    [Theory]
    [InlineData("disabled-recipe-output")]
    [InlineData("disabled-recipe-input")]
    [InlineData("ineligible-route-input")]
    [InlineData("ineligible-acquisition")]
    [InlineData("semantic-use")]
    public void Scope_EverySourceAndRecipeReferenceVetoesNegativeProof(string caseId)
    {
        var catalog = caseId.StartsWith("disabled-recipe", StringComparison.Ordinal)
            ? Catalog(Recipe(output: caseId.EndsWith("output", StringComparison.Ordinal) ? "Npc" : "Wood", enabled: false),
                Requirement(input: caseId.EndsWith("input", StringComparison.Ordinal) ? "Npc" : "Wood")) : Catalog();
        var rows = Proof().ToList();
        if (caseId == "ineligible-route-input") rows.Add(new(LoadedSourceFactKind.RouteInput, "input", "Wood", "Npc", eligible: false));
        if (caseId == "ineligible-acquisition") rows.Add(new(LoadedSourceFactKind.Acquisition, "acquisition", "Npc", eligible: false));
        if (caseId == "semantic-use") rows.Add(new(LoadedSourceFactKind.ItemUse, "use", "Npc"));
        Assert.DoesNotContain("Npc", ItemProfileScope.Build(catalog, rows).Excluded.Keys);
    }

    [Fact]
    public void Scope_CompleteProofExcludesNpcAndVisibleIconVetoesExclusion()
    {
        Assert.Contains("Npc", ItemProfileScope.Build(Catalog(), Proof()).Excluded.Keys);
        var visible = CatalogSnapshot.Build(new[] { Item("Npc", "OneHandedWeapon", icon: true) });
        Assert.Empty(ItemProfileScope.Build(visible, Proof()).Excluded);
    }

    [Theory]
    [InlineData("duplicate-complete")]
    [InlineData("unknown-item")]
    [InlineData("unknown-method")]
    [InlineData("negative-icons")]
    [InlineData("ineligible")]
    public void Scope_MalformedEvidenceIsRejected(string caseId)
    {
        var rows = Proof().ToList();
        rows.Add(caseId switch {
            "duplicate-complete" => Scope("complete", id: "duplicate"),
            "unknown-item" => Scope("definition", item: "Absent", id: "bad"),
            "unknown-method" => Scope("unrecognized", id: "bad"),
            "negative-icons" => Scope("definition", icons: -1, id: "bad"),
            _ => Scope("definition", id: "bad", eligible: false) });
        Assert.Throws<InvalidDataException>(() => ItemProfileScope.Build(Catalog(), rows));
    }

    [Fact]
    public void Scope_CancellationAndCatalogBindingAreEnforced()
    {
        var catalog = Catalog();
        Assert.Throws<OperationCanceledException>(() => ItemProfileScope.Build(catalog, Proof(), new CancellationToken(true)));
        var other = CatalogSnapshot.Build(new[] { Item("Other") });
        Assert.Throws<InvalidDataException>(() => ClassificationIndex.Build(other, ClassificationPipelineTests.Rules(), profileScope: ItemProfileScope.Build(catalog)));
    }

    [Fact]
    public void AuthoritativeCodec_PartitionsProfilesAndExclusions()
    {
        var catalog = Catalog();
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out var error), error);
        var classified = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy!), LoadedSourceReceipt.Build(catalog, Array.Empty<LoadedSourceFact>()).Snapshot);
        var assignment = AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy!);
        var decoded = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(assignment), policy!);
        Assert.Equal(assignment.Fingerprint, decoded.Fingerprint);
        Assert.Equal(new[] { "Hair" }, decoded.ProfileScope.Excluded.Keys);
        Assert.Equal(new[] { "Npc", "Uncertain", "Wood" }, decoded.Items.Keys);
        Assert.Equal(catalog.Items.Count, decoded.Items.Count + decoded.ProfileScope.Excluded.Count);
    }
}
