using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationContractTests
{
    [Fact]
    public void Taxonomy_LabelRenamePreservesIdentityAndRankWhileCustomIdsAreExplicit()
    {
        var defaults = DefaultClassificationTaxonomy.Create();
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodecTests.Wrap(
            "<category id=\"weapons\" displayName=\"Arms\"/><category id=\"custom\" displayName=\"Custom\"/>", ""), out var policy, out var error), error);
        var changed = defaults.WithPolicy(policy!);
        Assert.Equal("Arms", changed.Categories["weapons"].Name);
        Assert.Equal(defaults.Categories["weapons"].DisplayRank, changed.Categories["weapons"].DisplayRank);
        Assert.Equal(500, changed.Categories["custom"].DisplayRank);
        Assert.Equal("Weapons", defaults.Categories["weapons"].Name);
        Assert.NotEqual(defaults.Fingerprint, changed.Fingerprint);
        Assert.Throws<ArgumentException>(() => new ClassificationRuleSet(defaults, new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "custom" }) }));
    }

    [Fact]
    public void Taxonomy_NormalizesOwnsAndRequiresUnambiguousFallbackWithinBounds()
    {
        var misc = new ClassificationCategory("misc", "Misc", 999);
        var rows = new[] { new ClassificationCategory("a", "A", 0), misc };
        var taxonomy = new ClassificationTaxonomy(rows);
        Assert.Equal(taxonomy.Fingerprint, new ClassificationTaxonomy(rows.Reverse()).Fingerprint);
        rows[0] = new ClassificationCategory("b", "B", 1);
        Assert.Equal(new[] { "a", "misc" }, taxonomy.Categories.Keys);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ClassificationCategory>)taxonomy.Categories).Clear());
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy(Array.Empty<ClassificationCategory>()));
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy(new[] { misc, misc }));
        var boundary = Enumerable.Range(0, 511).Select(i => new ClassificationCategory("c" + i, "C", 500)).Append(misc).ToArray();
        Assert.Equal(512, new ClassificationTaxonomy(boundary).Categories.Count);
        Assert.Throws<ArgumentException>(() => new ClassificationTaxonomy(boundary.Append(new ClassificationCategory("extra", "Extra", 1))));
        Assert.NotEqual(taxonomy.Fingerprint, new ClassificationTaxonomy(new[] { new ClassificationCategory("a", "A", 1), misc }).Fingerprint);
    }

    [Theory]
    [InlineData("uppercase-id")]
    [InlineData("empty-label")]
    [InlineData("long-id")]
    [InlineData("long-label")]
    [InlineData("negative-rank")]
    [InlineData("large-rank")]
    [InlineData("invalid-utf16")]
    public void Category_InvalidScalarAndRankReject(string caseId)
    {
        var id = caseId == "uppercase-id" ? "Upper" : caseId == "long-id" ? new string('a', 65) : "a";
        var label = caseId == "empty-label" ? " " : caseId == "long-label" ? new string('a', 129) : caseId == "invalid-utf16" ? new string((char)0xd800, 1) : "A";
        var rank = caseId == "negative-rank" ? -1 : caseId == "large-rank" ? 1000 : 1;
        Assert.ThrowsAny<ArgumentException>(() => new ClassificationCategory(id, label, rank));
    }

    [Fact]
    public void Evidence_NormalizesDuplicatesPreservesDistinctProvenanceAndOwnsInputs()
    {
        var one = new MembershipEvidence("materials.wood", "one", "Observed.");
        var two = new MembershipEvidence("materials.wood", "two", "Observed.");
        var facet = new FacetEvidence("mod", "vanilla", "one", "Receipt.");
        var members = new[] { two, one, one }; var facets = new[] { facet, facet };
        var result = new ItemClassificationEvidence("Wood", members, facets);
        members[0] = new MembershipEvidence("food", "changed", "Changed.");
        Assert.Equal(new[] { "one", "two" }, result.Memberships.Select(x => x.Source));
        Assert.Single(result.Facets); Assert.Equal("vanilla", result.Facets[0].Value);
        Assert.Throws<NotSupportedException>(() => ((IList<MembershipEvidence>)result.Memberships).Clear());
        Assert.Equal(256, new MembershipEvidence("misc", new string('s', 256), new string('r', 2048)).Source.Length);
        Assert.Throws<ArgumentException>(() => new MembershipEvidence("misc", new string('s', 257), "Reason."));
        Assert.Throws<ArgumentException>(() => new FacetEvidence("mod", "a", "source", new string('r', 2049)));
        Assert.Throws<ArgumentException>(() => new ItemClassificationEvidence("Wood", Enumerable.Repeat(one, 129)));
        Assert.Throws<ArgumentException>(() => new ItemClassificationEvidence("Wood", facets: Enumerable.Repeat(facet, 65)));
    }

    [Fact]
    public void Overrides_ValidateOperationsPrimaryAndUniqueFacetDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClassificationOverride("exact", "Wood", (OverrideOperation)3, new[] { "misc" }));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "misc" }, "food"));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Remove, new[] { "misc" }, "misc"));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Add, Array.Empty<string>()));
        var edit = new FacetOverride("usage", OverrideOperation.Replace, Array.Empty<string>());
        Assert.Empty(edit.Values);
        Assert.Throws<ArgumentException>(() => new FacetOverride("usage", OverrideOperation.Add, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new FacetOverride("usage", OverrideOperation.Remove, Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "misc" }, facets: new[] { edit, edit }));
        var memberships = new[] { "valuables", "misc", "misc" };
        var row = new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, memberships);
        memberships[0] = "food";
        Assert.Equal(new[] { "misc", "valuables" }, row.Memberships);
    }

    [Fact]
    public void Build_PrimaryDisplayUsesRankThenOrdinalAndReplacementEmptyFallsBack()
    {
        var taxonomy = new ClassificationTaxonomy(DefaultClassificationTaxonomy.Create().Categories.Values.Concat(new[] {
            new ClassificationCategory("custom.z", "Z", 0), new ClassificationCategory("custom.a", "A", 0) }));
        var rules = new ClassificationRuleSet(taxonomy, new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "custom.z", "custom.a" }) });
        Assert.Equal("custom.a", Build(new[] { Item() }, rules).Items["Wood"].Primary);
        var empty = new ClassificationRuleSet(taxonomy, new[] {
            new ClassificationOverride("exact", "Sword", OverrideOperation.Replace, Array.Empty<string>()) });
        Assert.Equal(new[] { "misc" }, Categories(Build(new[] { Item("Sword", "OneHandedWeapon") }, empty).Items["Sword"]));
    }
}
