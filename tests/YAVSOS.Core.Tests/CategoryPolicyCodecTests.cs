using System;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

public sealed class CategoryPolicyCodecTests
{
    private const string Full = """
        <policy xmlns="urn:com.bastionntb.yavsos:categories" schema="1" revision="7">
          <categories><category id="metal" displayName="Métal &amp; ore"/><category id="crafting" displayName="Crafting"/></categories>
          <assignments>
            <assignment match="glob" selector="Iron*"><member category="metal"/></assignment>
            <assignment match="exact" selector="Iron" primary="metal"><member category="metal"/><member category="crafting"/></assignment>
          </assignments>
        </policy>
        """;

    [Fact]
    public void GoldenCanonical_OutputMatchesPublishedSchema1()
    {
        const string canonical = "<policy schema=\"1\" revision=\"7\" xmlns=\"urn:com.bastionntb.yavsos:categories\"><categories><category id=\"crafting\" displayName=\"Crafting\" /><category id=\"metal\" displayName=\"Métal &amp; ore\" /></categories><assignments><assignment match=\"exact\" selector=\"Iron\" primary=\"metal\"><member category=\"crafting\" /><member category=\"metal\" /></assignment><assignment match=\"glob\" selector=\"Iron*\"><member category=\"metal\" /></assignment></assignments></policy>";

        Assert.True(CategoryPolicyCodec.TryParse(Full, out var policy, out var diagnostic), diagnostic);

        Assert.Equal(canonical, policy!.CanonicalXml);
        Assert.Equal("8a214aec0fb329ee50995a5d748b3bf5cb9a8e4d49f220e90950ffeb96bfe9cb", policy.Fingerprint);
    }

    [Theory]
    [InlineData("prefixed-reversed-whitespace", true)]
    [InlineData("leaf-cdata-text", false)]
    [InlineData("leaf-child", false)]
    [InlineData("foreign-member", false)]
    [InlineData("namespaced-attribute", false)]
    [InlineData("trailing-element", false)]
    [InlineData("truncated-root", false)]
    public void Parse_StreamingShapes_PreservesAcceptedAndRejectedCases(string caseId, bool accepted)
    {
        var valid = "<p:policy xmlns:p=\"urn:com.bastionntb.yavsos:categories\" schema=\"1\" revision=\"9\">"
            + "<p:assignments><p:assignment match=\"exact\" selector=\"Iron\" primary=\"metal\"><p:member category=\"metal\"><![CDATA[ \t ]]></p:member></p:assignment></p:assignments>"
            + "<!--ignored--><?ignored x?><p:categories><p:category id=\"metal\" displayName=\"Ore &amp; 🪨\">&#xA0;</p:category></p:categories></p:policy>";
        var payload = caseId switch
        {
            "prefixed-reversed-whitespace" => valid,
            "leaf-cdata-text" => valid.Replace("<![CDATA[ \t ]]>", "<![CDATA[unexpected]]>"),
            "leaf-child" => valid.Replace("&#xA0;", "<p:member category=\"metal\"/>"),
            "foreign-member" => valid.Replace("<p:member category", "<p:member xmlns:p=\"other\" category"),
            "namespaced-attribute" => valid.Replace("id=\"metal\"", "p:id=\"metal\""),
            "trailing-element" => valid + "<extra/>",
            "truncated-root" => valid.Replace("</p:policy>", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };

        Assert.Equal(accepted, CategoryPolicyCodec.TryParse(payload, out var policy, out var diagnostic));

        if (!accepted) { Assert.Null(policy); Assert.NotEmpty(diagnostic); return; }
        Assert.Equal("", diagnostic);
        Assert.Equal(9, policy!.Revision);
        Assert.Equal("Ore & 🪨", Assert.Single(policy.Categories).DisplayName);
        var assignment = Assert.Single(policy.Assignments);
        Assert.Equal("Iron", assignment.Selector);
        Assert.Equal("metal", assignment.Primary);
        Assert.Equal("metal", Assert.Single(assignment.Memberships));
    }

    [Fact]
    public void Parse_CompletePolicy_PreservesSemanticMembershipsAndDisplayChoice()
    {
        Assert.True(CategoryPolicyCodec.TryParse(Full, out var policy, out var error), error);
        Assert.Equal(7, policy!.Revision);
        Assert.Equal(new[] { "crafting", "metal" }, policy.Categories.Select(x => x.Id));
        Assert.Equal("Métal & ore", policy.Categories[1].DisplayName);
        Assert.Equal("exact", policy.Assignments[0].Match);
        Assert.Equal("Iron", policy.Assignments[0].Selector);
        Assert.Equal(new[] { "crafting", "metal" }, policy.Assignments[0].Memberships);
        Assert.Equal("metal", policy.Assignments[0].Primary);
        Assert.Equal("Iron*", policy.Assignments[1].Selector);
        Assert.Null(policy.Assignments[1].Primary);
    }

    [Fact]
    public void Parse_EmptyPolicy_IsValidWithoutInventingTaxonomy()
    {
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out var error), error);
        Assert.Equal(1, policy!.Revision);
        Assert.Empty(policy.Categories);
        Assert.Empty(policy.Assignments);
        Assert.Matches("^[0-9a-f]{64}$", policy.Fingerprint);
        Assert.Equal("c435c8b0d50d0e51c3a26afa58d62b2958210ffef1c2b9320877bb0965834865", policy.Fingerprint);
        Assert.Equal("<policy schema=\"1\" revision=\"1\" xmlns=\"urn:com.bastionntb.yavsos:categories\"><categories /><assignments /></policy>", policy.CanonicalXml);
    }

    [Fact]
    public void Fingerprint_OrderWhitespaceAndAttributeOrder_DoNotChangeSnapshot()
    {
        const string reordered = """
            <policy revision="7" schema="1" xmlns="urn:com.bastionntb.yavsos:categories"><assignments>
            <assignment primary="metal" selector="Iron" match="exact"><member category="crafting"/><member category="metal"/></assignment>
            <assignment selector="Iron*" match="glob"><member category="metal"/></assignment>
            </assignments><categories><category displayName="Crafting" id="crafting"/><category displayName="Métal &amp; ore" id="metal"/></categories></policy>
            """;
        Assert.True(CategoryPolicyCodec.TryParse(Full, out var first, out var error), error);
        Assert.True(CategoryPolicyCodec.TryParse(reordered, out var second, out error), error);
        Assert.Equal(first!.CanonicalXml, second!.CanonicalXml);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.True(CategoryPolicyCodec.TryParse(first.CanonicalXml, out var roundTrip, out error), error);
        Assert.Equal(first.Fingerprint, roundTrip!.Fingerprint);
    }

    [Fact]
    public void Fingerprint_RevisionMembershipAndPrimaryChanges_ChangeDigest()
    {
        Assert.True(CategoryPolicyCodec.TryParse(Full, out var first, out _));
        foreach (var changed in new[] { Full.Replace("revision=\"7\"", "revision=\"8\""),
            Full.Replace("primary=\"metal\"", "primary=\"crafting\""),
            Full.Replace("<member category=\"crafting\"/>", "") })
        {
            Assert.True(CategoryPolicyCodec.TryParse(changed, out var next, out var error), error);
            Assert.NotEqual(first!.Fingerprint, next!.Fingerprint);
        }
    }

    // Safe case IDs are metadata; malformed/large payloads are constructed inside the test.
    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("whitespace")]
    [InlineData("malformed")]
    [InlineData("namespace")]
    [InlineData("new-schema")]
    [InlineData("missing-schema")]
    [InlineData("zero-revision")]
    [InlineData("negative-revision")]
    [InlineData("overflow-revision")]
    [InlineData("unknown-attribute")]
    [InlineData("unknown-element")]
    [InlineData("unexpected-text")]
    [InlineData("duplicate-section")]
    [InlineData("duplicate-category")]
    [InlineData("invalid-id")]
    [InlineData("empty-label")]
    [InlineData("long-label")]
    [InlineData("unknown-member")]
    [InlineData("duplicate-member")]
    [InlineData("no-members")]
    [InlineData("invalid-primary")]
    [InlineData("duplicate-selector")]
    [InlineData("invalid-match")]
    [InlineData("exact-wildcard")]
    [InlineData("glob-without-wildcard")]
    [InlineData("long-selector")]
    [InlineData("dtd")]
    [InlineData("oversize-ascii")]
    [InlineData("oversize-utf8")]
    public void Parse_InvalidEnvelope_RejectsWholeSnapshot(string caseId)
    {
        var payload = caseId switch
        {
            "empty" => "",
            "null" => null,
            "whitespace" => " \t\r\n ",
            "malformed" => "<policy>",
            "namespace" => Full.Replace(CategoryPolicyCodec.Namespace, "other"),
            "new-schema" => Full.Replace("schema=\"1\"", "schema=\"2\""),
            "missing-schema" => Full.Replace("schema=\"1\"", ""),
            "zero-revision" => Full.Replace("revision=\"7\"", "revision=\"0\""),
            "negative-revision" => Full.Replace("revision=\"7\"", "revision=\"-1\""),
            "overflow-revision" => Full.Replace("revision=\"7\"", "revision=\"9223372036854775808\""),
            "unknown-attribute" => Full.Replace("schema=\"1\"", "schema=\"1\" surprise=\"yes\""),
            "unknown-element" => Full.Replace("</categories>", "<unknown/></categories>"),
            "unexpected-text" => Full.Replace("<categories>", "<categories>surprise"),
            "duplicate-section" => Full.Replace("</policy>", "<categories/></policy>"),
            "duplicate-category" => Full.Replace("</categories>", "<category id=\"metal\" displayName=\"Again\"/></categories>"),
            "invalid-id" => Full.Replace("id=\"metal\"", "id=\"Metal\""),
            "empty-label" => Full.Replace("displayName=\"Crafting\"", "displayName=\" \""),
            "long-label" => Full.Replace("Crafting", new string('a', 129)),
            "unknown-member" => Full.Replace("category=\"metal\"", "category=\"missing\""),
            "duplicate-member" => Full.Replace("<member category=\"crafting\"/>", "<member category=\"metal\"/>"),
            "no-members" => Full.Replace("<member category=\"metal\"/>", "").Replace("<member category=\"crafting\"/>", ""),
            "invalid-primary" => Full.Replace("primary=\"metal\"", "primary=\"missing\""),
            "duplicate-selector" => Full.Replace("match=\"glob\" selector=\"Iron*\"", "match=\"exact\" selector=\"Iron\""),
            "invalid-match" => Full.Replace("match=\"exact\"", "match=\"regex\""),
            "exact-wildcard" => Full.Replace("selector=\"Iron\"", "selector=\"Iron?\""),
            "glob-without-wildcard" => Full.Replace("Iron*", "Iron"),
            "long-selector" => Full.Replace("selector=\"Iron\"", "selector=\"" + new string('a', 129) + "\""),
            "dtd" => "<!DOCTYPE policy [<!ENTITY attack SYSTEM 'file:///unreadable'>]>" + Full,
            "oversize-ascii" => new string('a', CategoryPolicyCodec.MaxPayloadBytes + 1),
            "oversize-utf8" => new string('é', CategoryPolicyCodec.MaxPayloadBytes / 2 + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };
        Assert.False(CategoryPolicyCodec.TryParse(payload, out var policy, out var error));
        Assert.Null(policy);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Parse_CategoryAndAssignmentLimits_AcceptsBoundaryAndRejectsNext()
    {
        var categories = string.Concat(Enumerable.Range(0, CategoryPolicyCodec.MaxCategories)
            .Select(i => $"<category id=\"c{i}\" displayName=\"Category {i}\"/>"));
        var assignments = string.Concat(Enumerable.Range(0, CategoryPolicyCodec.MaxAssignments)
            .Select(i => $"<assignment match=\"exact\" selector=\"I{i}\"><member category=\"c0\"/></assignment>"));
        var bounded = Wrap(categories, "");
        Assert.True(CategoryPolicyCodec.TryParse(bounded, out var policy, out var error), error);
        Assert.Equal(CategoryPolicyCodec.MaxCategories, policy!.Categories.Count);
        Assert.False(CategoryPolicyCodec.TryParse(Wrap(categories + "<category id=\"extra\" displayName=\"Extra\"/>", ""), out _, out error));
        Assert.Contains("categories", error);
        var assignmentBoundary = Wrap("<category id=\"c0\" displayName=\"C\"/>", assignments);
        Assert.True(CategoryPolicyCodec.TryParse(assignmentBoundary, out policy, out error), error);
        Assert.Equal(CategoryPolicyCodec.MaxAssignments, policy!.Assignments.Count);
        Assert.False(CategoryPolicyCodec.TryParse(Wrap("<category id=\"c0\" displayName=\"C\"/>", assignments +
            "<assignment match=\"exact\" selector=\"Extra\"><member category=\"c0\"/></assignment>"), out _, out error));
        Assert.Contains("assignments", error);
    }

    [Fact]
    public void Parse_ByteLimit_AcceptsExactLimitAndRejectsOneMoreByte()
    {
        var payload = CategoryPolicyCodec.EmptyDocument;
        payload += new string(' ', CategoryPolicyCodec.MaxPayloadBytes - Encoding.UTF8.GetByteCount(payload));
        Assert.True(CategoryPolicyCodec.TryParse(payload, out _, out var error), error);
        Assert.False(CategoryPolicyCodec.TryParse(payload + " ", out _, out error));
        Assert.Contains("bytes", error);
    }

    [Fact]
    public void Parse_ScalarMaximums_AcceptsDocumentedBoundaryValues()
    {
        var id = new string('a', 64);
        var label = new string('b', 128);
        var selector = new string('c', 128);
        var payload = Wrap($"<category id=\"{id}\" displayName=\"{label}\"/>",
            $"<assignment match=\"exact\" selector=\"{selector}\"><member category=\"{id}\"/></assignment>", long.MaxValue);
        Assert.True(CategoryPolicyCodec.TryParse(payload, out var policy, out var error), error);
        Assert.Equal(long.MaxValue, policy!.Revision);
        Assert.Equal(id, policy.Categories[0].Id);
        Assert.Equal(label, policy.Categories[0].DisplayName);
        Assert.Equal(selector, policy.Assignments[0].Selector);
    }

    internal static string Wrap(string categories, string assignments, long revision = 1) =>
        $"<policy xmlns=\"{CategoryPolicyCodec.Namespace}\" schema=\"1\" revision=\"{revision}\"><categories>{categories}</categories><assignments>{assignments}</assignments></policy>";
}
