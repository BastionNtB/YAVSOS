using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using YAVSOS.Core.Tests.Legacy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

/// <summary>X48 (D130): `&lt;add&gt;` and `&lt;remove&gt;` lines in the categories file, schema chosen by content.</summary>
public sealed class CategoryPolicyEditTests
{
    private const string Ns = CategoryPolicyCodec.Namespace;

    private const string Schema1 = """
        <policy xmlns="urn:com.bastionntb.yavsos:categories" schema="1" revision="7">
          <categories><category id="metal" displayName="Métal &amp; ore"/><category id="crafting" displayName="Crafting"/></categories>
          <assignments>
            <assignment match="glob" selector="Iron*"><member category="metal"/></assignment>
            <assignment match="exact" selector="Iron" primary="metal"><member category="metal"/><member category="crafting"/></assignment>
          </assignments>
        </policy>
        """;

    // The design note's example (§3), with built-in IDs.
    private const string Edits = """
        <policy xmlns="urn:com.bastionntb.yavsos:categories" schema="2" revision="3">
          <categories>
            <category id="materials.ore" displayName="Ore bay" />
            <category id="weapons.swords" displayName="Swords" />
            <category id="drinkware" displayName="Drinkware" />
          </categories>
          <assignments>
            <assignment match="exact" selector="CopperOre"><add category="materials.ore" /></assignment>
            <assignment match="glob" selector="Tankard*" primary="drinkware">
              <remove category="weapons.swords" />
              <add category="drinkware" />
            </assignment>
          </assignments>
        </policy>
        """;

    private const string EditsCanonical = "<policy schema=\"2\" revision=\"3\" xmlns=\"urn:com.bastionntb.yavsos:categories\"><categories>"
        + "<category id=\"drinkware\" displayName=\"Drinkware\" /><category id=\"materials.ore\" displayName=\"Ore bay\" />"
        + "<category id=\"weapons.swords\" displayName=\"Swords\" /></categories><assignments>"
        + "<assignment match=\"exact\" selector=\"CopperOre\"><add category=\"materials.ore\" /></assignment>"
        + "<assignment match=\"glob\" selector=\"Tankard*\" primary=\"drinkware\"><remove category=\"weapons.swords\" /><add category=\"drinkware\" /></assignment>"
        + "</assignments></policy>";

    private static string Doc(string categories, string assignments, string schema = "2", long revision = 1) =>
        $"<policy xmlns=\"{Ns}\" schema=\"{schema}\" revision=\"{revision}\"><categories>{categories}</categories><assignments>{assignments}</assignments></policy>";

    private static CategoryPolicy Parse(string xml)
    {
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var diagnostic), diagnostic);
        return policy!;
    }

    // ---- Schema 1 is unchanged -------------------------------------------------------------------------------------

    [Fact]
    public void Golden_Schema1RuleSetFingerprintUnchanged()
    {
        var policy = Parse(Schema1);
        Assert.Equal(1, policy.Schema);
        // Both fingerprints as 1.1 computed them (CategoryPolicyCodecTests has the canonical bytes).
        Assert.Equal("8a214aec0fb329ee50995a5d748b3bf5cb9a8e4d49f220e90950ffeb96bfe9cb", policy.Fingerprint);
        Assert.Equal("d62affb64927d3d4f2071d4d2aca449eece383d54ce8107f3c2f9bfa492b1435", ClassificationRuleSet.FromPolicy(policy).Fingerprint);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("empty")]
    [InlineData("declared-2-members-only")]
    [InlineData("prefixed")]
    public void Schema1_CanonicalBytesAndFingerprintMatchThe11Reader(string caseId)
    {
        var xml = caseId switch
        {
            "full" => Schema1,
            "empty" => CategoryPolicyCodec.EmptyDocument,
            // A file written with schema="2" but only member lines is served as schema 1 (X48 §4).
            "declared-2-members-only" => Schema1.Replace("schema=\"1\"", "schema=\"2\""),
            "prefixed" => "<p:policy xmlns:p=\"" + Ns + "\" schema=\"1\" revision=\"9\"><p:assignments><p:assignment match=\"exact\" selector=\"Iron\">"
                + "<p:member category=\"metal\"/></p:assignment></p:assignments><p:categories><p:category id=\"metal\" displayName=\"Ore\"/></p:categories></p:policy>",
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };
        var policy = Parse(xml);
        var legacyInput = caseId == "declared-2-members-only" ? policy.CanonicalXml : xml;

        Assert.True(CategoryPolicyCodecV110.TryParse(legacyInput, out var legacyCanonical, out var legacyFingerprint, out var diagnostic), diagnostic);

        Assert.Equal(1, policy.Schema);
        Assert.StartsWith("<policy schema=\"1\" ", policy.CanonicalXml);
        Assert.Equal(legacyCanonical, policy.CanonicalXml);
        Assert.Equal(legacyFingerprint, policy.Fingerprint);
        Assert.All(policy.Assignments, x => { Assert.True(x.Replaces); Assert.Empty(x.Removals); });
    }

    // ---- Parsing and the canonical form ----------------------------------------------------------------------------

    [Fact]
    public void Parse_AddAndRemoveLines_BuildEditAssignmentsAndSchema2()
    {
        var policy = Parse(Edits);

        Assert.Equal(2, policy.Schema);
        Assert.Equal(EditsCanonical, policy.CanonicalXml);
        var copper = policy.Assignments[0];
        Assert.Equal(("exact", "CopperOre", false), (copper.Match, copper.Selector, copper.Replaces));
        Assert.Equal(new[] { "materials.ore" }, copper.Memberships);
        Assert.Empty(copper.Removals);
        Assert.Null(copper.Primary);
        var tankards = policy.Assignments[1];
        Assert.Equal(("glob", "Tankard*", false), (tankards.Match, tankards.Selector, tankards.Replaces));
        Assert.Equal(new[] { "drinkware" }, tankards.Memberships);
        Assert.Equal(new[] { "weapons.swords" }, tankards.Removals);
        Assert.Equal("drinkware", tankards.Primary);
    }

    [Fact]
    public void Canonical_Schema2RoundTripsAndIgnoresSourceOrder()
    {
        var policy = Parse(Edits);
        var reparsed = Parse(policy.CanonicalXml);
        // Lines in another order, adds before removes, whitespace and a prefix: the same canonical form.
        var shuffled = Parse("<p:policy xmlns:p=\"" + Ns + "\" revision=\"3\" schema=\"2\"><p:assignments>"
            + "<p:assignment selector=\"Tankard*\" match=\"glob\" primary=\"drinkware\">\n <p:add category=\"drinkware\"/> <p:remove category=\"weapons.swords\"></p:remove></p:assignment>"
            + "<p:assignment match=\"exact\" selector=\"CopperOre\"><p:add category=\"materials.ore\"/></p:assignment></p:assignments>"
            + "<p:categories><p:category id=\"weapons.swords\" displayName=\"Swords\"/><p:category id=\"materials.ore\" displayName=\"Ore bay\"/>"
            + "<p:category id=\"drinkware\" displayName=\"Drinkware\"/></p:categories></p:policy>");

        Assert.Equal(policy.CanonicalXml, reparsed.CanonicalXml);
        Assert.Equal(policy.Fingerprint, reparsed.Fingerprint);
        Assert.Equal(policy.CanonicalXml, shuffled.CanonicalXml);
        Assert.Equal(policy.Fingerprint, shuffled.Fingerprint);
        Assert.Equal(ClassificationRuleSet.FromPolicy(policy).Fingerprint, ClassificationRuleSet.FromPolicy(reparsed).Fingerprint);
    }

    [Fact]
    public void Schema_ContentDecides()
    {
        const string categories = "<category id=\"metal\" displayName=\"Metal\"/><category id=\"ore\" displayName=\"Ore\"/>";
        var member = "<assignment match=\"exact\" selector=\"Iron\"><member category=\"metal\"/></assignment>";
        var add = "<assignment match=\"exact\" selector=\"Tin\"><add category=\"ore\"/></assignment>";
        var remove = "<assignment match=\"exact\" selector=\"Tin\"><remove category=\"ore\"/></assignment>";

        Assert.Equal(1, Parse(Doc(categories, member, "1")).Schema);
        Assert.Equal(1, Parse(Doc(categories, member, "2")).Schema);
        Assert.Equal(2, Parse(Doc(categories, member + add, "2")).Schema);
        Assert.Equal(2, Parse(Doc(categories, remove, "2")).Schema);
        // A file that declares schema 1 but uses the new lines is still schema 2 on the wire.
        var declared1 = Parse(Doc(categories, member + add, "1"));
        Assert.Equal(2, declared1.Schema);
        Assert.StartsWith("<policy schema=\"2\" ", declared1.CanonicalXml);
        // Unknown schemas stay refused.
        Assert.False(CategoryPolicyCodec.TryParse(Doc(categories, member, "3"), out _, out var error));
        Assert.Contains("expected 1 or 2", error);
    }

    [Fact]
    public void Fingerprints_DifferForEveryChangeOfMeaning()
    {
        const string categories = "<category id=\"metal\" displayName=\"Metal\"/><category id=\"ore\" displayName=\"Ore\"/>";
        string Row(string lines, string primary = "") => $"<assignment match=\"exact\" selector=\"Tin\"{primary}>{lines}</assignment>";
        var variants = new[]
        {
            Doc(categories, Row("<member category=\"ore\"/>")),
            Doc(categories, Row("<add category=\"ore\"/>")),
            Doc(categories, Row("<remove category=\"ore\"/>")),
            Doc(categories, Row("<add category=\"ore\"/><remove category=\"metal\"/>")),
            Doc(categories, Row("<add category=\"metal\"/><remove category=\"ore\"/>")),
            Doc(categories, Row("<add category=\"ore\"/><add category=\"metal\"/>")),
            Doc(categories, Row("<add category=\"ore\"/><add category=\"metal\"/>", " primary=\"ore\"")),
        };
        var policies = variants.Select(Parse).ToArray();

        Assert.Equal(policies.Length, policies.Select(x => x.Fingerprint).Distinct().Count());
        Assert.Equal(policies.Length, policies.Select(x => ClassificationRuleSet.FromPolicy(x).Fingerprint).Distinct().Count());
    }

    // ---- Rules the reader enforces ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("mixed-member-add", "mixes member lines")]
    [InlineData("mixed-remove-member", "mixes member lines")]
    [InlineData("add-and-remove-same", "appears twice")]
    [InlineData("add-twice", "appears twice")]
    [InlineData("remove-twice", "appears twice")]
    [InlineData("unknown-add", "Unknown membership category 'missing'")]
    [InlineData("unknown-remove", "Unknown membership category 'missing'")]
    [InlineData("primary-on-remove-only", "must be one of its add lines")]
    [InlineData("primary-is-removed", "must be one of its add lines")]
    [InlineData("primary-not-added", "must be one of its add lines")]
    [InlineData("empty-assignment", "at least one membership")]
    [InlineData("add-without-category", "add requires category")]
    [InlineData("unknown-attribute", "Unknown attribute on remove")]
    [InlineData("child-element", "Unknown element in add")]
    [InlineData("text", "Unexpected text in remove")]
    [InlineData("foreign-namespace", "Unknown element in assignment")]
    [InlineData("unknown-line", "Unknown element in assignment")]
    public void Parse_MalformedOrConflictingLines_RejectWholeFile(string caseId, string expected)
    {
        const string categories = "<category id=\"metal\" displayName=\"Metal\"/><category id=\"ore\" displayName=\"Ore\"/>";
        string Row(string lines, string primary = "") => $"<assignment match=\"exact\" selector=\"Tin\"{primary}>{lines}</assignment>";
        var assignment = caseId switch
        {
            "mixed-member-add" => Row("<member category=\"metal\"/><add category=\"ore\"/>"),
            "mixed-remove-member" => Row("<remove category=\"metal\"/><member category=\"ore\"/>"),
            "add-and-remove-same" => Row("<add category=\"ore\"/><remove category=\"ore\"/>"),
            "add-twice" => Row("<add category=\"ore\"/><add category=\"ore\"/>"),
            "remove-twice" => Row("<remove category=\"ore\"/><remove category=\"ore\"/>"),
            "unknown-add" => Row("<add category=\"missing\"/>"),
            "unknown-remove" => Row("<remove category=\"missing\"/>"),
            "primary-on-remove-only" => Row("<remove category=\"ore\"/>", " primary=\"metal\""),
            "primary-is-removed" => Row("<add category=\"metal\"/><remove category=\"ore\"/>", " primary=\"ore\""),
            "primary-not-added" => Row("<add category=\"metal\"/>", " primary=\"ore\""),
            "empty-assignment" => Row(""),
            "add-without-category" => Row("<add/>"),
            "unknown-attribute" => Row("<remove category=\"ore\" operation=\"add\"/>"),
            "child-element" => Row("<add category=\"ore\"><member category=\"metal\"/></add>"),
            "text" => Row("<remove category=\"ore\">surprise</remove>"),
            "foreign-namespace" => Row("<add xmlns=\"other\" category=\"ore\"/>"),
            "unknown-line" => Row("<replace category=\"ore\"/>"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId))
        };

        Assert.False(CategoryPolicyCodec.TryParse(Doc(categories, assignment), out var policy, out var error));
        Assert.Null(policy);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Lifecycle_MalformedEditKeepsTheLastAcceptedPolicy()
    {
        const string categories = "<category id=\"ore\" displayName=\"Ore\"/>";
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.ManagedServer);
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyLocal(Doc(categories, "<assignment match=\"exact\" selector=\"Tin\"><add category=\"ore\"/></assignment>", revision: 2)));
        var accepted = state.Local!.Fingerprint;

        Assert.Equal(PolicyUpdate.Rejected, state.ApplyLocal(Doc(categories,
            "<assignment match=\"exact\" selector=\"Tin\"><add category=\"ore\"/><remove category=\"ore\"/></assignment>", revision: 3)));

        Assert.Equal(accepted, state.Local!.Fingerprint);
        Assert.Contains("last accepted policy retained", state.Diagnostic);
    }

    // ---- Applied over automatic memberships ------------------------------------------------------------------------

    private static readonly CatalogFact[] Items = { Item("Sword", "OneHandedWeapon"), Item("SwordLong", "OneHandedWeapon"), Item("Stone") };

    private static ClassificationResult Classify(string categories, string assignments, string prefab = "Sword") =>
        Build(Items, ClassificationRuleSet.FromPolicy(Parse(Doc(categories, assignments)))).Items[prefab];

    private static string Listed(params string[] ids) => string.Concat(ids.Select(id => $"<category id=\"{id}\" displayName=\"{id}\"/>"));

    [Fact]
    public void Apply_AddKeepsAutomaticCategories()
    {
        var automatic = Categories(Build(Items).Items["Sword"]);
        Assert.Contains("weapons.swords", automatic);

        var result = Classify(Listed("drinkware"), "<assignment match=\"exact\" selector=\"Sword\"><add category=\"drinkware\"/></assignment>");

        Assert.Equal(automatic.Append("drinkware").OrderBy(x => x, StringComparer.Ordinal), Categories(result).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("exact:Sword", result.SelectedOverride);
    }

    [Fact]
    public void Apply_RemoveThenAddMovesOneCategoryAndKeepsTheRest()
    {
        var automatic = Categories(Build(Items).Items["Sword"]);
        var result = Classify(Listed("weapons.swords", "drinkware"),
            "<assignment match=\"glob\" selector=\"Sword*\" primary=\"drinkware\"><add category=\"drinkware\"/><remove category=\"weapons.swords\"/></assignment>");

        var expected = automatic.Where(x => x != "weapons.swords").Append("drinkware").OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expected, Categories(result).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("drinkware", result.Primary);
        Assert.DoesNotContain(result.Memberships, x => x.Category == "weapons.swords");
        // A glob edit reaches every item it matches, and none it doesn't.
        Assert.Contains("drinkware", Categories(Classify(Listed("weapons.swords", "drinkware"),
            "<assignment match=\"glob\" selector=\"Sword*\"><add category=\"drinkware\"/></assignment>", "SwordLong")));
        Assert.DoesNotContain("drinkware", Categories(Classify(Listed("weapons.swords", "drinkware"),
            "<assignment match=\"glob\" selector=\"Sword*\"><add category=\"drinkware\"/></assignment>", "Stone")));
    }

    [Fact]
    public void Apply_RemovingEverythingFallsBackToMisc_AndAMissingCategoryDoesNothing()
    {
        var automatic = Categories(Build(Items).Items["Sword"]);
        var removeAll = string.Concat(automatic.Select(id => $"<remove category=\"{id}\"/>"));

        var emptied = Classify(Listed(automatic), $"<assignment match=\"exact\" selector=\"Sword\">{removeAll}</assignment>");
        var untouched = Classify(Listed("drinkware"), "<assignment match=\"exact\" selector=\"Sword\"><remove category=\"drinkware\"/></assignment>");

        Assert.Equal(new[] { "misc" }, Categories(emptied));
        Assert.Equal("misc", emptied.Primary);
        Assert.Equal(automatic, Categories(untouched));
    }

    [Fact]
    public void Apply_ARemovedParentComesBackWhileAChildStays()
    {
        var result = Classify(Listed("weapons"), "<assignment match=\"exact\" selector=\"Sword\"><remove category=\"weapons\"/></assignment>");

        Assert.Contains("weapons.swords", AllCategories(result));
        Assert.Contains("weapons", AllCategories(result)); // Closure adds parents after the edit (X48 rule 6).
    }

    [Fact]
    public void Apply_SingleWinner_AnExactEditHidesAMatchingGlob()
    {
        var result = Classify(Listed("drinkware", "valuables"),
            "<assignment match=\"exact\" selector=\"Sword\"><add category=\"drinkware\"/></assignment>"
            + "<assignment match=\"glob\" selector=\"Sword*\"><add category=\"valuables\"/></assignment>");
        var other = Classify(Listed("drinkware", "valuables"),
            "<assignment match=\"exact\" selector=\"Sword\"><add category=\"drinkware\"/></assignment>"
            + "<assignment match=\"glob\" selector=\"Sword*\"><add category=\"valuables\"/></assignment>", "SwordLong");

        Assert.Contains("drinkware", Categories(result));
        Assert.DoesNotContain("valuables", Categories(result));
        Assert.Contains("valuables", Categories(other));
        Assert.DoesNotContain("drinkware", Categories(other));
    }

    [Fact]
    public void Apply_ReplaceStillClearsAutomaticCategories()
    {
        var result = Classify(Listed("drinkware"), "<assignment match=\"exact\" selector=\"Sword\"><member category=\"drinkware\"/></assignment>");

        Assert.Equal(new[] { "drinkware" }, Categories(result));
    }

    // ---- Core contract ---------------------------------------------------------------------------------------------

    [Fact]
    public void Override_RemovalsAreValidated()
    {
        var removeOnly = new ClassificationOverride("exact", "Wood", OverrideOperation.Add, Array.Empty<string>(), removals: new[] { "misc", "food", "misc" });
        Assert.Equal(new[] { "food", "misc" }, removeOnly.Removals);
        Assert.Empty(new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "food" }).Removals);

        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Replace, new[] { "food" }, removals: new[] { "misc" }));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Remove, new[] { "food" }, removals: new[] { "misc" }));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "food" }, removals: new[] { "food" }));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Add, new[] { "food" }, "misc", removals: new[] { "misc" }));
        Assert.Throws<ArgumentException>(() => new ClassificationOverride("exact", "Wood", OverrideOperation.Add, Array.Empty<string>(), removals: new[] { "Not valid" }));
        Assert.Throws<ArgumentException>(() => new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), new[] {
            new ClassificationOverride("exact", "Wood", OverrideOperation.Add, Array.Empty<string>(), removals: new[] { "unlisted" }) }));
    }

    // ---- A 1.1 reader and the server's warning ---------------------------------------------------------------------

    [Fact]
    public void OldReader_RefusesSchema2AndAcceptsSchema1()
    {
        var edited = Parse(Edits);
        var replaced = Parse(Schema1.Replace("schema=\"1\"", "schema=\"2\""));

        // What a 1.2 server publishes with an edit: a 1.1 client refuses the whole policy (no half-applied categories).
        Assert.False(CategoryPolicyCodecV110.TryParse(edited.CanonicalXml, out var canonical, out _, out var refused));
        Assert.Null(canonical);
        Assert.Contains("Unsupported policy schema", refused);
        // The same lines declared schema 1 still fail on the 1.1 reader (a 1.1 server can't be fed the new form).
        Assert.False(CategoryPolicyCodecV110.TryParse(Edits.Replace("schema=\"2\"", "schema=\"1\""), out _, out _, out refused));
        Assert.Contains("Unknown element in assignment", refused);
        // A replace-only file is schema 1 on the wire, so a 1.1 client accepts it with the same fingerprint.
        Assert.True(CategoryPolicyCodecV110.TryParse(replaced.CanonicalXml, out canonical, out var fingerprint, out var diagnostic), diagnostic);
        Assert.Equal(replaced.CanonicalXml, canonical);
        Assert.Equal(replaced.Fingerprint, fingerprint);
    }

    [Fact]
    public void Recovery_EditingBackToReplaceOnlyServesSchema1AgainToA11Client()
    {
        const string categories = "<category id=\"ore\" displayName=\"Ore\"/>";
        var server = new PolicyLifecycle();
        server.BeginSession(PolicyMode.ManagedServer);
        Assert.Equal(PolicyUpdate.Accepted, server.ApplyLocal(Doc(categories, "<assignment match=\"exact\" selector=\"Tin\"><add category=\"ore\"/></assignment>", revision: 4)));
        Assert.False(CategoryPolicyCodecV110.TryParse(server.Local!.CanonicalXml, out _, out _, out _));

        Assert.Equal(PolicyUpdate.Accepted, server.ApplyLocal(Doc(categories, "<assignment match=\"exact\" selector=\"Tin\"><member category=\"ore\"/></assignment>", revision: 5)));

        Assert.Equal(1, server.Local!.Schema);
        Assert.True(CategoryPolicyCodecV110.TryParse(server.Local.CanonicalXml, out _, out var fingerprint, out var diagnostic), diagnostic);
        Assert.Equal(server.Local.Fingerprint, fingerprint);
    }

    [Theory]
    [InlineData("1.1.0", 2, false)]
    [InlineData("1.0.2", 2, false)]
    [InlineData("1.2.0", 2, true)]
    [InlineData("1.3.1", 2, true)]
    [InlineData("2.0.0", 2, true)]
    [InlineData("1.1.0", 1, true)]
    [InlineData(null, 2, true)]
    [InlineData("not a version", 2, true)]
    public void Readers_OnlyAKnownOlderVersionMissesSchema2(string? version, int schema, bool reads)
    {
        Assert.Equal(reads, PolicyReaders.Reads(version, schema));
        Assert.Equal(reads, PolicyReaders.OlderClientWarning("Ragnhild", version, schema, true) == null);
    }

    [Fact]
    public void Warning_NamesThePlayerVersionAndReason()
    {
        var joined = PolicyReaders.OlderClientWarning("Ragnhild", "1.1.0", 2, true)!;
        var connected = PolicyReaders.OlderClientWarning("Ragnhild", "1.1.0", 2, false)!;

        Assert.StartsWith("Ragnhild joined with YAVSOS 1.1.0, which can't read this server's categories file", joined);
        Assert.StartsWith("Ragnhild is connected with YAVSOS 1.1.0", connected);
        Assert.Contains("schema 2", joined);
        Assert.Contains("YAVSOS " + CategoryPolicyCodec.Schema2ReaderVersion + " or later", joined);
        Assert.Equal("1.2.0", CategoryPolicyCodec.Schema2ReaderVersion);
    }
}
