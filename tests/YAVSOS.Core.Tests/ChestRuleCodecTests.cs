using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class ChestRuleCodecTests
{
    private static readonly ClassificationTaxonomy Taxonomy = DefaultClassificationTaxonomy.Create();
    private static string Document(string contents = "", string state = "configured", string priority = "0", string overflow = "false", string fallback = "false") =>
        "<chestRules schema=\"1\" state=\"" + state + "\" priority=\"" + priority + "\" overflow=\"" + overflow + "\" existingItemFallback=\"" + fallback + "\">" + contents + "</chestRules>";

    [Fact]
    public void RoundTrip_PreservesEveryRuleFieldAndExactExclusionPrecedence()
    {
        var source = new ChestRules(Taxonomy, ChestRuleState.Configured,
            new[] { Rule(new[] { "materials.wood", "food" }, ("usage", new[] { "fuel", "unknown" }), ("mod", new[] { "pack<&\"" })), Rule() },
            new[] { "Wood", "A<&\"" }, new[] { "Wood", "Stone" }, int.MinValue, true);
        var xml = ChestRuleCodec.Serialize(source); var parsed = ChestRuleCodec.Parse(xml, Taxonomy);
        Assert.Equal(xml, ChestRuleCodec.Serialize(parsed)); Assert.Equal(source.Fingerprint, parsed.Fingerprint);
        Assert.Equal(ChestRuleState.Configured, parsed.State); Assert.Equal(int.MinValue, parsed.Priority); Assert.True(parsed.Overflow); Assert.False(parsed.ExistingItemFallback);
        Assert.Equal(new[] { "A<&\"", "Wood" }, parsed.Includes); Assert.Equal(new[] { "Stone", "Wood" }, parsed.Excludes);
        Assert.Equal(2, parsed.Acceptance.Rules.Count);
        var filtered = Assert.Single(parsed.Acceptance.Rules, x => x.Categories.Count != 0);
        Assert.Equal(new[] { "food", "materials.wood" }, filtered.Categories);
        Assert.Equal(new[] { "fuel", "unknown" }, filtered.Filters["usage"]); Assert.Equal("pack<&\"", Assert.Single(filtered.Filters["mod"]));
        Assert.Equal(AcceptanceMatch.Excluded, parsed.Acceptance.Match(Build(new[] { Item() }).Items["Wood"]));
    }

    [Fact]
    public void Parse_ExplicitEmptyAndUnconfiguredRemainDifferentAndFallbackIsExplicit()
    {
        var empty = ChestRuleCodec.Parse(Document(), Taxonomy);
        var unconfigured = ChestRuleCodec.Parse(Document(state: "unconfigured"), Taxonomy);
        var optIn = ChestRuleCodec.Parse(Document("<exclude prefab=\"Stone\"/>", state: "unconfigured", fallback: "true"), Taxonomy);
        Assert.Equal(ChestRuleState.Configured, empty.State); Assert.Equal(ChestRuleState.Unconfigured, unconfigured.State);
        Assert.NotEqual(empty.Fingerprint, unconfigured.Fingerprint); Assert.False(unconfigured.ExistingItemFallback); Assert.True(optIn.ExistingItemFallback);
        Assert.Equal(AcceptanceMatch.None, empty.Acceptance.Match(Build(new[] { Item() }).Items["Wood"]));
        Assert.Equal(AcceptanceMatch.Excluded, optIn.Acceptance.Match(Build(new[] { Item("Stone") }).Items["Stone"]));
    }

    [Fact]
    public void Identity_NormalizesRuleAndSelectionOrderWhitespaceDuplicatesAndEmptyDimensions()
    {
        var first = ChestRuleCodec.Parse(Document("<rule><category id=\"materials.wood\"/><filter dimension=\"biome\"><value id=\"ocean\"/><value id=\"unknown\"/></filter></rule><include prefab=\"Wood\"/><include prefab=\"Stone\"/>"), Taxonomy);
        var second = ChestRuleCodec.Parse(Document("\n<include prefab=\"Stone\"/><rule><filter dimension=\"mod\"/><filter dimension=\"biome\"><value id=\"unknown\"/><value id=\"ocean\"/></filter><category id=\"materials.wood\"/></rule><include prefab=\"Wood\"/>\n"), Taxonomy);
        Assert.Equal(first.Fingerprint, second.Fingerprint); Assert.Equal(ChestRuleCodec.Serialize(first), ChestRuleCodec.Serialize(second));
        var duplicateRules = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(), Rule() }, new[] { "Wood", "Wood" });
        Assert.Single(duplicateRules.Acceptance.Rules); Assert.Single(duplicateRules.Includes);
        Assert.Equal(64, first.Fingerprint.Length);
    }

    [Fact]
    public void Identity_UsesStableCategoryIdsAndChangesForEveryBehaviorField()
    {
        var baseline = new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "materials.wood" }) });
        var renamed = new ClassificationTaxonomy(Taxonomy.Categories.Values.Select(x => new ClassificationCategory(x.Id, "New " + x.Id, 999)));
        Assert.Equal(baseline.Fingerprint, ChestRuleCodec.Parse(ChestRuleCodec.Serialize(baseline), renamed).Fingerprint);
        var variants = new[] { new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "food" }) }),
            new ChestRules(Taxonomy, ChestRuleState.Configured, baseline.Acceptance.Rules, priority: 1),
            new ChestRules(Taxonomy, ChestRuleState.Configured, baseline.Acceptance.Rules, overflow: true),
            new ChestRules(Taxonomy, ChestRuleState.Configured, baseline.Acceptance.Rules, includes: new[] { "Wood" }),
            new ChestRules(Taxonomy, ChestRuleState.Configured, baseline.Acceptance.Rules, excludes: new[] { "Wood" }),
            new ChestRules(Taxonomy, ChestRuleState.Configured, new[] { Rule(new[] { "materials.wood" }, ("usage", new[] { "fuel" })) }) };
        Assert.All(variants, x => Assert.NotEqual(baseline.Fingerprint, x.Fingerprint));
        Assert.NotEqual(new ChestRules(Taxonomy, ChestRuleState.Unconfigured).Fingerprint,
            new ChestRules(Taxonomy, ChestRuleState.Unconfigured, existingItemFallback: true).Fingerprint);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("state")]
    [InlineData("priority")]
    [InlineData("overflow")]
    [InlineData("existingItemFallback")]
    public void Parse_RequiresEveryRootAttribute(string attribute)
    {
        var xml = Document(); var start = xml.IndexOf(" " + attribute + "=", StringComparison.Ordinal); var end = xml.IndexOf('"', xml.IndexOf('"', start) + 1);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml.Remove(start, end - start + 1), Taxonomy));
    }

    [Fact]
    public void Parse_RejectsUnsupportedSchemaAttributesElementsAndFilters()
    {
        var invalid = new[] { Document().Replace("schema=\"1\"", "schema=\"2\""), Document().Replace("<chestRules ", "<chestRules extension=\"1\" "),
            Document("<unknown/>"), Document("<rule attribute=\"x\"/>"), Document("<rule><category id=\"Not A Valid ID\"/></rule>"),
            Document("<rule><filter dimension=\"Tier\"/></rule>"),
            Document("<rule><category id=\"food\" extra=\"x\"/></rule>"), Document("<include prefab=\"Wood\"><rule/></include>"),
            Document("<rule><filter dimension=\"mod\"><unknown id=\"pack\"/></filter></rule>"),
            Document().Replace("<chestRules ", "<chestRules xmlns=\"urn:unsupported\" ") };
        Assert.All(invalid, xml => Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml, Taxonomy)));
        // D086 D6: a value a known dimension doesn't have is kept as stored (it matches nothing).
        var later = Document("<rule><filter dimension=\"usage\"><value id=\"Fuel\"/></filter></rule>");
        Assert.Equal(new[] { "Fuel" }, ChestRuleCodec.Parse(later, Taxonomy).FullRules.Single().Filters["usage"]);
    }

    [Fact]
    public void Parse_KeepsAWellFormedUnknownCategoryAsIgnored_D050()
    {
        var rules = ChestRuleCodec.Parse(Document("<rule><category id=\"retired\"/></rule>"), Taxonomy);
        Assert.Equal(new[] { "retired" }, rules.UnknownCategories);
        Assert.Contains("<category id=\"retired\" />", ChestRuleCodec.Serialize(rules).Replace("\"/>", "\" />"));
    }

    [Fact]
    public void Parse_RejectsDuplicateSelectionsAndDimensionsRatherThanSilentlyDroppingErrors()
    {
        var invalid = new[] { Document("<include prefab=\"Wood\"/><include prefab=\"Wood\"/>"), Document("<exclude prefab=\"Wood\"/><exclude prefab=\"Wood\"/>"),
            Document("<rule><category id=\"food\"/><category id=\"food\"/></rule>"),
            Document("<rule><filter dimension=\"mod\"/><filter dimension=\"mod\"/></rule>"),
            Document("<rule><filter dimension=\"mod\"><value id=\"pack\"/><value id=\"pack\"/></filter></rule>") };
        Assert.All(invalid, xml => Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml, Taxonomy)));
    }

    [Fact]
    public void Parse_RejectsMalformedPartialTrailingDtdCommentsInstructionsAndText()
    {
        var invalid = new[] { "", "<chestRules", Document() + "<other/>", Document() + "trailing", "<!DOCTYPE chestRules [<!ENTITY x 'value'>]>" + Document(),
            "<!--comment-->" + Document(), "<?unknown instruction?>" + Document(), Document("<!--comment-->"), Document("unexpected text"),
            Document("<rule><![CDATA[unexpected]]></rule>") };
        Assert.All(invalid, xml => Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml, Taxonomy)));
    }

    [Fact]
    public void Parse_RejectsInvalidScalarsStatesAndFallbackCombinations()
    {
        var invalid = new[] { Document(priority: "2147483648"), Document(priority: "1.0"), Document(overflow: "True"), Document(state: "empty"), Document(fallback: "true"),
            Document("<rule/>", state: "unconfigured"), Document("<include prefab=\"Wood\"/>", state: "unconfigured"), Document(state: "unconfigured", overflow: "true"),
            Document("<include prefab=\" \"/>"), Document("<rule><filter dimension=\"mod\"><value id=\" \"/></filter></rule>"), Document("<include prefab=\"W&#10;ood\"/>") };
        Assert.All(invalid, xml => Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml, Taxonomy)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChestRules(Taxonomy, (ChestRuleState)3));
    }

    [Fact]
    public void Parse_Utf8ByteEnvelopeAcceptsBoundaryAndRejectsAdjacentOverrun()
    {
        var shell = Document(); var remaining = ChestRuleCodec.MaximumUtf8Bytes - Encoding.UTF8.GetByteCount(shell);
        var exact = Document(new string(' ', remaining));
        Assert.Equal(ChestRuleCodec.MaximumUtf8Bytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Equal(ChestRuleState.Configured, ChestRuleCodec.Parse(exact, Taxonomy).State);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document(new string(' ', remaining + 1)), Taxonomy));
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<include prefab=\"" + new string('\u00e9', ChestRuleCodec.MaximumUtf8Bytes / 2) + "\"/>"), Taxonomy));
    }

    [Fact]
    public void Parse_AggregateUtf8EnvelopeRejectsOtherwiseValidBoundedSelectors()
    {
        var selectors = Enumerable.Range(0, 800).Select(i => i + new string('\u00e9', 200)).ToArray();
        Assert.Equal(800, selectors.Length);
        Assert.All(selectors, selector => Assert.InRange(selector.Length, 201, 203));
        var xml = Document(string.Concat(selectors.Select(selector => "<include prefab=\"" + selector + "\"/>")));
        Assert.True(xml.Length < ChestRuleCodec.MaximumUtf8Bytes);
        Assert.True(Encoding.UTF8.GetByteCount(xml) > ChestRuleCodec.MaximumUtf8Bytes);
        var error = Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(xml, Taxonomy));
        Assert.Equal("Chest XML byte bound exceeded.", error.Message);
        // A representative subset verifies the same Unicode selectors satisfy scalar/count/schema constraints.
        var accepted = ChestRuleCodec.Parse(Document(string.Concat(selectors.Take(100).Select(selector => "<include prefab=\"" + selector + "\"/>"))), Taxonomy);
        Assert.Equal(100, accepted.Includes.Count);
        Assert.Contains("0" + new string('\u00e9', 200), accepted.Includes);
    }

    [Fact]
    public void Parse_CountEnvelopesAcceptMaximumAndRejectAdjacentOverrun()
    {
        var rules = string.Concat(Enumerable.Repeat("<rule/>", 64));
        Assert.Single(ChestRuleCodec.Parse(Document(rules), Taxonomy).Acceptance.Rules);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document(rules + "<rule/>"), Taxonomy));
        var includes = string.Concat(Enumerable.Range(0, 2048).Select(i => "<include prefab=\"I" + i + "\"/>"));
        Assert.Equal(2048, ChestRuleCodec.Parse(Document(includes), Taxonomy).Includes.Count);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document(includes + "<include prefab=\"extra\"/>"), Taxonomy));
        var excludes = includes.Replace("<include", "<exclude");
        Assert.Equal(2048, ChestRuleCodec.Parse(Document(excludes), Taxonomy).Excludes.Count);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document(excludes + "<exclude prefab=\"extra\"/>"), Taxonomy));
        var values = string.Concat(Enumerable.Range(0, 64).Select(i => "<value id=\"pack" + i + "\"/>"));
        Assert.Equal(64, Assert.Single(ChestRuleCodec.Parse(Document("<rule><filter dimension=\"mod\">" + values + "</filter></rule>"), Taxonomy).Acceptance.Rules).Filters["mod"].Count);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<rule><filter dimension=\"mod\">" + values + "<value id=\"extra\"/></filter></rule>"), Taxonomy));
    }

    [Fact]
    public void Parse_CategoryDimensionAndScalarBoundsAcceptMaximumAndRejectAdjacentOverrun()
    {
        var categories = Enumerable.Range(0, 257).Select(i => new ClassificationCategory("category-" + i, "Category " + i, 0)).ToArray();
        var taxonomy = new ClassificationTaxonomy(categories.Append(new ClassificationCategory("misc", "Misc", 999)));
        var links = string.Concat(categories.Take(256).Select(x => "<category id=\"" + x.Id + "\"/>"));
        Assert.Equal(256, Assert.Single(ChestRuleCodec.Parse(Document("<rule>" + links + "</rule>"), taxonomy).Acceptance.Rules).Categories.Count);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<rule>" + links + "<category id=\"category-256\"/></rule>"), taxonomy));
        var dimensions = string.Concat(ClassificationFilters.Dimensions.Select(d => "<filter dimension=\"" + d + "\"><value id=\"unknown\"/></filter>"));
        Assert.Equal(ClassificationFilters.Dimensions.Count, Assert.Single(ChestRuleCodec.Parse(Document("<rule>" + dimensions + "</rule>"), Taxonomy).Acceptance.Rules).Filters.Count);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<rule>" + dimensions + "<filter dimension=\"mod\"/></rule>"), Taxonomy));
        Assert.Equal(2048, Assert.Single(ChestRuleCodec.Parse(Document("<include prefab=\"" + new string('p', 2048) + "\"/>"), Taxonomy).Includes).Length);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<include prefab=\"" + new string('p', 2049) + "\"/>"), Taxonomy));
        Assert.Equal(128, Assert.Single(Assert.Single(ChestRuleCodec.Parse(Document("<rule><filter dimension=\"mod\"><value id=\"" + new string('m', 128) + "\"/></filter></rule>"), Taxonomy).Acceptance.Rules).Filters["mod"]).Length);
        Assert.Throws<ArgumentException>(() => ChestRuleCodec.Parse(Document("<rule><filter dimension=\"mod\"><value id=\"" + new string('m', 129) + "\"/></filter></rule>"), Taxonomy));
    }

    [Fact]
    public void Constructor_RevalidatesCurrentCategoryLinksOwnsInputsAndBoundsCanonicalOutput()
    {
        var includes = new[] { "Wood" }; var rules = new[] { Rule(new[] { "food" }) };
        var source = new ChestRules(Taxonomy, ChestRuleState.Configured, rules, includes);
        includes[0] = "Stone"; rules[0] = Rule();
        Assert.Equal("Wood", Assert.Single(source.Includes)); Assert.Equal("food", Assert.Single(Assert.Single(source.Acceptance.Rules).Categories));
        var retired = new ClassificationTaxonomy(new[] { new ClassificationCategory("misc", "Misc", 0) });
        Assert.Throws<ArgumentException>(() => new ChestRules(retired, ChestRuleState.Configured, source.Acceptance.Rules));
        Assert.Throws<ArgumentException>(() => new ChestRules(Taxonomy, ChestRuleState.Configured,
            includes: Enumerable.Range(0, 2048).Select(i => i + new string('x', 200))));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)source.Includes)[0] = "Changed");
    }
}
