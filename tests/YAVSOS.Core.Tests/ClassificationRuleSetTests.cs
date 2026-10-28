using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationRuleSetTests
{
    private static ClassificationOverride Assign(string match, string selector, string category) =>
        new ClassificationOverride(match, selector, OverrideOperation.Replace, new[] { category });

    [Fact]
    public void FromSchema1_ExactAssignmentReplacesAutomaticMembershipsWithoutChangingDocument()
    {
        var xml = CategoryPolicyCodecTests.Wrap("<category id=\"chosen\" displayName=\"Chosen\"/>",
            "<assignment match=\"exact\" selector=\"Sword\"><member category=\"chosen\"/></assignment>");
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var error), error);
        var canonical = policy!.CanonicalXml; var fingerprint = policy.Fingerprint;
        var rules = ClassificationRuleSet.FromSchema1(policy);
        var item = Build(new[] { Item("Sword", "OneHandedWeapon", quest: true) }, rules).Items["Sword"];
        Assert.Equal(OverrideOperation.Replace, Assert.Single(rules.Overrides).Operation);
        Assert.Equal(new[] { "chosen" }, Categories(item)); Assert.Equal("chosen", item.Primary);
        Assert.True(item.Quest); Assert.True(item.InstanceRarity);
        Assert.Equal(fingerprint, rules.PolicyFingerprint);
        Assert.Equal(canonical, policy.CanonicalXml); Assert.Equal(fingerprint, policy.Fingerprint);
        Assert.True(CategoryPolicyCodec.TryParse(canonical, out var reparsed, out error), error);
        Assert.Equal(fingerprint, reparsed!.Fingerprint);
    }

    [Fact]
    public void Select_ExactWinsAndGlobsNeverCascadeIntoIt()
    {
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            Assign("glob", "Sword*", "valuables"), Assign("exact", "Sword", "currency"), Assign("glob", "*", "food") });
        var result = Build(new[] { Item("Sword", "OneHandedWeapon"), Item("SwordLong"), Item("Stone") }, rules);
        Assert.Equal(new[] { "currency" }, Categories(result.Items["Sword"]));
        Assert.Equal(new[] { "valuables" }, Categories(result.Items["SwordLong"]));
        Assert.Equal(new[] { "food" }, Categories(result.Items["Stone"]));
        Assert.Equal("exact:Sword", result.Items["Sword"].SelectedOverride);
    }

    [Theory]
    [InlineData("more-literals", "Wo*d*", "W*")]
    [InlineData("fewer-stars", "Wo*d", "Wo**d")]
    [InlineData("fewer-questions", "Wo*d", "Wo?*d")]
    [InlineData("ordinal-tie", "*Wood", "Wood*")]
    public void Select_GlobPrecedenceIsStableAcrossInputOrder(string caseId, string winner, string loser)
    {
        _ = caseId;
        var rows = new[] { Assign("glob", loser, "food"), Assign("glob", winner, "valuables") };
        foreach (var ordered in new[] { rows, rows.Reverse().ToArray() })
        {
            var result = Build(new[] { Item() }, new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), ordered)).Items["Wood"];
            Assert.Equal(new[] { "valuables" }, Categories(result)); Assert.Equal("glob:" + winner, result.SelectedOverride);
        }
    }

    [Theory]
    [InlineData("star-empty", "Wo*od", "Wood", true)]
    [InlineData("star-retry", "W*d", "Wood", true)]
    [InlineData("whole-name", "Wo*", "xWood", false)]
    [InlineData("case-sensitive", "wo*", "Wood", false)]
    [InlineData("question-one", "Wo?d", "Wood", true)]
    [InlineData("question-short", "Wo?d", "Wod", false)]
    [InlineData("question-long", "Wo?d", "Woxxd", false)]
    [InlineData("operator-not-literal", "*x", "*ax", true)]
    [InlineData("literal-punctuation", "A.+*", "A.+tail", true)]
    [InlineData("no-regex", "A.+*", "Axxtail", false)]
    public void Select_GlobUsesOrdinalWholeNameStarAndQuestionSemantics(string caseId, string selector, string prefab, bool matches)
    {
        _ = caseId;
        var rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] { Assign("glob", selector, "valuables") });
        var result = Build(new[] { Item(prefab) }, rules).Items[prefab];
        Assert.Equal(matches ? new[] { "valuables" } : prefab == "Wood" ? new[] { "materials.wood" } : new[] { "misc" }, Categories(result));
        Assert.Equal(matches ? "glob:" + selector : null, result.SelectedOverride);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("space")]
    [InlineData("non-ascii")]
    [InlineData("slash")]
    [InlineData("brackets")]
    [InlineData("exact-star")]
    [InlineData("glob-no-operator")]
    [InlineData("unknown-kind")]
    public void Override_InvalidSelectorsReject(string caseId)
    {
        var selector = caseId switch { "empty" => "", "oversize" => new string('x', 129), "space" => "a b",
            "non-ascii" => "é", "slash" => "a/b", "brackets" => "a[b]", "exact-star" => "a*", _ => "Wood" };
        var match = caseId == "glob-no-operator" ? "glob" : caseId == "unknown-kind" ? "regex" : "exact";
        Assert.Throws<ArgumentException>(() => Assign(match, selector, "misc"));
    }

    [Fact]
    public void RuleSet_DuplicatesRetiredReferencesAndCountOverflowReject()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create(); var one = Assign("exact", "Wood", "misc");
        Assert.Throws<ArgumentException>(() => new ClassificationRuleSet(taxonomy, new[] { one, one }));
        Assert.Throws<ArgumentException>(() => new ClassificationRuleSet(taxonomy, new[] { Assign("exact", "Wood", "retired") }));
        var boundary = Enumerable.Range(0, 2048).Select(i => Assign("exact", "I" + i, "misc")).ToArray();
        Assert.Equal(2048, new ClassificationRuleSet(taxonomy, boundary).Overrides.Count);
        Assert.Throws<ArgumentException>(() => new ClassificationRuleSet(taxonomy, boundary.Append(Assign("exact", "Extra", "misc"))));
        Assert.Equal(128, Assign("exact", new string('x', 128), "misc").Selector.Length);
    }

    [Fact]
    public void Fingerprint_NormalizesOrderAndIncludesEditsPrimaryAndPolicy()
    {
        var taxonomy = DefaultClassificationTaxonomy.Create();
        var rows = new[] { Assign("exact", "Wood", "misc"), Assign("glob", "Stone*", "valuables") };
        var original = new ClassificationRuleSet(taxonomy, rows);
        Assert.Equal(original.Fingerprint, new ClassificationRuleSet(taxonomy, rows.Reverse()).Fingerprint);
        Assert.NotEqual(original.Fingerprint, new ClassificationRuleSet(taxonomy, rows, "other-policy").Fingerprint);
        var baseline = new ClassificationRuleSet(taxonomy, new[] { Assign("exact", "Wood", "misc") });
        foreach (var changed in new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "misc" }),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "misc" }, "misc"),
            new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "misc" }, facets: new[] {
                new FacetOverride("usage", OverrideOperation.Replace, new[] { "fuel" }) }) })
            Assert.NotEqual(baseline.Fingerprint, new ClassificationRuleSet(taxonomy, new[] { changed }).Fingerprint);
    }
}
