using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class AuthoritativeAssignmentsTests
{
    private static CategoryPolicy Policy(long revision = 1)
    {
        var xml = CategoryPolicyCodec.EmptyDocument.Replace("revision=\"1\"", "revision=\"" + revision + "\"");
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var error), error); return policy!;
    }
    private static CatalogSnapshot Catalog(params CatalogFact[] items) => CatalogSnapshot.Build(items.Length == 0 ? new[] { Item() } : items);
    private static AuthoritativeAssignmentSnapshot Snapshot(CatalogSnapshot catalog, CategoryPolicy policy) =>
        AuthoritativeAssignmentSnapshot.Create(catalog, ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy)), policy);
    private static void Text(BinaryWriter writer, string text) { var bytes = new UTF8Encoding(false, true).GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Wire(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, true)) compressor.Write(bytes, 0, bytes.Length);
        return "yas3:" + Convert.ToBase64String(output.ToArray());
    }
    private static byte[] WithDigest(byte[] body)
    {
        using var output = new MemoryStream(); output.Write(body, 0, body.Length); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        using var sha = SHA256.Create(); Text(writer, Convert.ToHexString(sha.ComputeHash(body)).ToLowerInvariant()); writer.Flush(); return output.ToArray();
    }
    private static byte[] Body(CatalogSnapshot catalog, CategoryPolicy policy, Action<BinaryWriter>? rows = null, int schema = 3,
        string? classifier = null, string? pipeline = null, string? catalogFingerprint = null, string? policyFingerprint = null, string? taxonomyFingerprint = null, int count = 1,
        bool scopeComplete = false, string source = "none", (string Item, string Reason)[]? exclusions = null)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(schema); Text(writer, classifier ?? ClassificationIndex.Version); Text(writer, pipeline ?? ClassificationPipeline.Version);
        Text(writer, catalogFingerprint ?? catalog.Fingerprint); Text(writer, policyFingerprint ?? policy.Fingerprint);
        Text(writer, taxonomyFingerprint ?? ClassificationRuleSet.FromSchema1(policy).Taxonomy.Fingerprint); Text(writer, source); writer.Write((int)(AssignmentCapabilities.Identity | AssignmentCapabilities.Categories));
        writer.Write(scopeComplete); writer.Write(exclusions?.Length ?? 0);
        foreach (var exclusion in exclusions ?? Array.Empty<(string Item, string Reason)>())
        { Text(writer, exclusion.Item); Text(writer, exclusion.Reason); }
        writer.Write(count);
        if (rows == null) Row(writer); else rows(writer);
        writer.Flush(); return output.ToArray();
    }
    private static void Row(BinaryWriter writer, string prefab = "Wood", string primary = "misc", string category = "misc",
        bool instance = false, bool quest = false, bool icon = true, bool catalogOnly = false, string? biome = "fuel", string? rarity = null, int memberCount = 1)
    {
        Text(writer, prefab); Text(writer, primary); writer.Write(quest); writer.Write(icon); writer.Write(catalogOnly); writer.Write(instance); writer.Write(false);
        writer.Write(memberCount);
        for (var i = 0; i < memberCount; i++) { Text(writer, category); Text(writer, "server-observation"); Text(writer, "Observed in world."); }
        writer.Write((biome == null ? 0 : 1) + (rarity == null ? 0 : 1));
        if (biome != null) { Text(writer, "usage"); Text(writer, biome); Text(writer, "server-source"); Text(writer, "Complete source proof."); }
        if (rarity != null) { Text(writer, "rarity"); Text(writer, rarity); Text(writer, "server-rarity"); Text(writer, "Observed rarity."); }
    }
    private static AuthoritativeAssignmentSnapshot Parse(byte[] body, CatalogSnapshot catalog, CategoryPolicy policy) =>
        AuthoritativeAssignmentsCodec.ParseWire(Wire(WithDigest(body)), policy);

    [Fact]
    public void Codec_RejectsReceivedClosureRows()
    {
        // P4-00b (D051): closure rows are rebuilt by the receiver and never accepted from the wire.
        var catalog = Catalog(); var policy = Policy();
        var body = Body(catalog, policy, writer => {
            Text(writer, "Wood"); Text(writer, "misc"); writer.Write(false); writer.Write(true); writer.Write(false); writer.Write(false); writer.Write(false);
            writer.Write(1); Text(writer, "misc"); Text(writer, CategoryClosure.Source); Text(writer, "Forged."); writer.Write(0);
        });
        Assert.Contains("rebuilt locally", Assert.Throws<InvalidDataException>(() => Parse(body, catalog, policy)).Message);
    }

    [Fact]
    public void CreateAndCodec_PreserveWholeAssignmentsFlagsBindingsAndLiteralProvenance()
    {
        var catalog = Catalog(Item("Wood", quest: true, icon: false), Item("Sword", type: "OneHandedWeapon"), Item("Hair", type: "Customization"));
        var policy = Policy(); var snapshot = Snapshot(catalog, policy);
        var wire = AuthoritativeAssignmentsCodec.ToWire(snapshot);
        var decoded = AuthoritativeAssignmentsCodec.ParseWire(wire, policy);
        Assert.Equal(3, decoded.Schema); Assert.Equal(catalog.Fingerprint, decoded.CatalogFingerprint); Assert.Equal(policy.Fingerprint, decoded.PolicyFingerprint);
        Assert.Equal(ClassificationIndex.Version, decoded.ClassifierVersion); Assert.Equal(ClassificationPipeline.Version, decoded.PipelineVersion);
        Assert.Equal(snapshot.Fingerprint, decoded.Fingerprint); Assert.Equal(2, decoded.Items.Count);
        Assert.True(decoded.Items["Wood"].Quest); Assert.False(decoded.Items["Wood"].HasIcon);
        Assert.Equal("typed-customization", decoded.ProfileScope.Excluded["Hair"]); Assert.True(decoded.Items["Sword"].InstanceRarity);
        Assert.Equal("weapons.swords", decoded.Items["Sword"].Primary);
        var evidence = Assert.Single(decoded.Items["Sword"].Memberships, x => x.Category == "weapons.swords");
        Assert.Equal("loaded-item", evidence.Source); Assert.Equal("Weapon-context skill: Swords", evidence.Reason);
        Assert.DoesNotContain(decoded.Items["Sword"].Facets, x => x.Dimension == "rarity");
        Assert.Equal(wire, AuthoritativeAssignmentsCodec.ToWire(decoded));
    }

    [Fact]
    public void Parse_ServerAssignmentsReplaceLocalInferencesWithoutLocalSourceFingerprintRequirement()
    {
        var catalog = Catalog(); var policy = Policy(); var local = Snapshot(catalog, policy);
        Assert.Empty(local.Items["Wood"].Facets);
        var authoritative = Parse(Body(catalog, policy), catalog, policy);
        Assert.Equal("misc", authoritative.Items["Wood"].Primary);
        Assert.Equal("server-observation", Assert.Single(authoritative.Items["Wood"].Memberships).Source);
        var biome = Assert.Single(authoritative.Items["Wood"].Facets);
        Assert.Equal("usage", biome.Dimension); Assert.Equal("fuel", biome.Value);
        Assert.Equal("server-source", biome.Source); Assert.Equal("Complete source proof.", biome.Reason);
        Assert.NotEqual(local.Fingerprint, authoritative.Fingerprint);
    }

    [Theory]
    [InlineData("unknown-item")]
    [InlineData("unknown-reason")]
    [InlineData("npc-without-complete-scan")]
    [InlineData("npc-without-source")]
    [InlineData("duplicate-exclusion")]
    [InlineData("unsorted-exclusions")]
    [InlineData("profile-exclusion-overlap")]
    public void Parse_RejectsStructurallyInvalidProfileExclusions(string invalid)
    {
        var catalog = Catalog(Item("Hair", "Customization"), Item("Npc", "OneHandedWeapon", icon: invalid == "visible-npc"), Item("Wood"));
        var policy = Policy();
        var exclusions = new[] { ("Hair", "typed-customization"), ("Npc", "proven-npc-internal") };
        if (invalid == "unknown-item") exclusions[1] = ("Absent", "proven-npc-internal");
        if (invalid == "unknown-reason") exclusions[1] = ("Npc", "probably-internal");
        if (invalid == "wrong-customization-type") exclusions[1] = ("Npc", "typed-customization");
        if (invalid == "missing-customization") exclusions = new[] { exclusions[1] };
        if (invalid == "duplicate-exclusion") exclusions = new[] { exclusions[0], exclusions[0] };
        if (invalid == "unsorted-exclusions") Array.Reverse(exclusions);
        var body = Body(catalog, policy,
            rows: w => { if (invalid != "missing-profile") Row(w, prefab: invalid == "profile-exclusion-overlap" ? "Hair" : "Wood", catalogOnly: invalid == "profile-exclusion-overlap"); },
            scopeComplete: invalid != "npc-without-complete-scan",
            source: invalid == "npc-without-source" ? "none" : new string('a', 64),
            count: invalid == "missing-profile" ? 0 : 1, exclusions: exclusions);
        AuthoritativeAssignmentSnapshot? accepted = null;
        Assert.Throws<InvalidDataException>(() => accepted = Parse(body, catalog, policy));
        Assert.Null(accepted);
    }

    [Fact]
    public void Create_RejectsWrongCatalogPolicyAndRecipeOrigin()
    {
        var catalog = Catalog(); var policy = Policy(); var classified = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentSnapshot.Create(catalog, classified, Policy(2)));
        var different = CatalogSnapshot.Build(new[] { Item(), Recipe(output: "Wood"), Requirement() });
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentSnapshot.Create(different, classified, policy));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("taxonomy")]
    public void Parse_RefusesEveryMismatchedBindingBeforeAcceptingItems(string binding)
    {
        var catalog = Catalog(); var policy = Policy(); var other = new string('0', 64);
        var body = Body(catalog, policy, catalogFingerprint: binding == "catalog" ? other : null,
            policyFingerprint: binding == "policy" ? other : null, taxonomyFingerprint: binding == "taxonomy" ? other : null,
            classifier: null, pipeline: null);
        Assert.Throws<InvalidDataException>(() => Parse(body, catalog, policy));
    }

    // D078 B02: a server one classifier or pipeline version ahead (or behind) is still accepted; the versions are kept.
    [Fact]
    public void Parse_AcceptsAnotherClassifierOrPipelineVersionAndKeepsIt()
    {
        var catalog = Catalog(); var policy = Policy();
        var parsed = Parse(Body(catalog, policy, classifier: "yavsos-classifier-99", pipeline: "yavsos-classification-pipeline-99"), catalog, policy);
        Assert.Equal("yavsos-classifier-99", parsed.ClassifierVersion);
        Assert.Equal("yavsos-classification-pipeline-99", parsed.PipelineVersion);
        Assert.NotEmpty(parsed.Items);
    }

    [Fact]
    public void Parse_OptionalTaxonomyCannotAuthorizeCategoriesOutsideEffectivePolicy()
    {
        var catalog = Catalog(); var policy = Policy();
        var effectiveTaxonomy = ClassificationRuleSet.FromSchema1(policy).Taxonomy;
        var customTaxonomy = new ClassificationTaxonomy(effectiveTaxonomy.Categories.Values.Concat(
            new[] { new ClassificationCategory("spoofed", "Spoofed", 1) }));
        var wire = Wire(WithDigest(Body(catalog, policy, w => Row(w, primary: "spoofed", category: "spoofed"),
            taxonomyFingerprint: customTaxonomy.Fingerprint)));
        AuthoritativeAssignmentSnapshot? accepted = null;
        var error = Assert.Throws<InvalidDataException>(() => accepted = AuthoritativeAssignmentsCodec.ParseWire(wire, policy, customTaxonomy));
        Assert.Null(accepted); Assert.Contains("effective policy", error.Message);
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(wire, policy));
        var ordinaryWire = AuthoritativeAssignmentsCodec.ToWire(Snapshot(catalog, policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(ordinaryWire, policy, customTaxonomy));
        var valid = AuthoritativeAssignmentsCodec.ParseWire(ordinaryWire, policy, effectiveTaxonomy);
        Assert.Equal("materials.wood", valid.Items["Wood"].Primary);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("glob")]
    public void Codec_MaximumLengthPolicySelectorsPreserveSelectedOverrideAndProvenance(string match)
    {
        var prefab = new string('a', 128);
        var selector = match == "exact" ? prefab : new string('a', 127) + "*";
        var xml = "<policy xmlns=\"" + CategoryPolicyCodec.Namespace + "\" schema=\"1\" revision=\"1\"><categories>" +
            "<category id=\"showcase\" displayName=\"Showcase\"/></categories><assignments><assignment match=\"" + match +
            "\" selector=\"" + selector + "\" primary=\"showcase\"><member category=\"showcase\"/></assignment></assignments></policy>";
        Assert.True(CategoryPolicyCodec.TryParse(xml, out var policy, out var error), error);
        var catalog = Catalog(Item(prefab)); var snapshot = Snapshot(catalog, policy!);
        var decoded = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy!);
        Assert.Equal(128, selector.Length); Assert.Equal(match + ":" + selector, decoded.Items[prefab].SelectedOverride);
        Assert.Equal("showcase", decoded.Items[prefab].Primary);
        var membership = Assert.Single(decoded.Items[prefab].Memberships);
        Assert.Equal("policy:" + match + ":" + selector, membership.Source);
        Assert.Equal("Explicit Replace membership.", membership.Reason);
    }

    [Fact]
    public void Parse_D042_MissingAndExtraItemsResolvePerItem_DuplicatesStillRefuse()
    {
        var catalog = Catalog(); var policy = Policy();
        var none = Parse(Body(catalog, policy, _ => { }, count: 0), catalog, policy);
        var fallback = none.Resolve("Wood", catalog)!;
        Assert.Equal(AuthoritativeAssignmentSnapshot.FallbackCategory, fallback.Primary);
        Assert.Equal(new[] { "misc" }, fallback.Memberships.Select(x => x.Category));
        var extra = Parse(Body(catalog, policy, w => { Row(w); Row(w, "ZExtra"); }, count: 2), catalog, policy);
        Assert.Equal(2, extra.Items.Count); Assert.Null(extra.Resolve("Absent", catalog));
        var two = Catalog(Item(), Item("Other"));
        Assert.Throws<InvalidDataException>(() => Parse(Body(two, policy, w => { Row(w); Row(w); }, count: 2), two, policy));
        Assert.Equal("Missing", Parse(Body(catalog, policy, w => Row(w, "Missing")), catalog, policy).Items["Missing"].Prefab);
    }

    // D081: a later server's facet vocabulary (a new value on a known dimension, a new dimension) is kept, not refused.
    private static void FacetRow(BinaryWriter writer, params (string Dimension, string Value)[] facets)
    {
        Text(writer, "Wood"); Text(writer, "misc"); writer.Write(false); writer.Write(true); writer.Write(false); writer.Write(false); writer.Write(false);
        writer.Write(1); Text(writer, "misc"); Text(writer, "server-observation"); Text(writer, "Observed in world.");
        writer.Write(facets.Length);
        foreach (var (dimension, value) in facets) { Text(writer, dimension); Text(writer, value); Text(writer, "server-source"); Text(writer, "Later vocabulary."); }
    }

    [Fact]
    public void Parse_D081_KeepsALaterServersFacetVocabulary_AndItTakesLessNeverMore()
    {
        var catalog = Catalog(); var policy = Policy();
        // Rows sorted as the server writes them: by dimension, then value.
        var parsed = Parse(Body(catalog, policy, w => FacetRow(w, ("future-dimension", "x"), ("usage", "fuel"), ("usage", "smelting"))), catalog, policy);
        var wood = parsed.Items["Wood"];
        Assert.Equal(new[] { "future-dimension:x:False", "usage:fuel:True", "usage:smelting:False" },
            wood.Facets.Select(f => f.Dimension + ":" + f.Value + ":" + f.Recognized));
        // The snapshot round-trips unchanged, so its digest still matches.
        Assert.Equal(parsed.Fingerprint, AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(parsed), policy).Fingerprint);
        // Matching: the known value still matches; the new one is present but no rule names it.
        Assert.True(ClassificationFilters.Matches(wood, ClassificationContractFixture.Rule(filters: new[] { ("usage", new[] { "fuel" }) })));
        var onlyNew = Parse(Body(catalog, policy, w => FacetRow(w, ("usage", "smelting"))), catalog, policy).Items["Wood"];
        Assert.False(ClassificationFilters.Matches(onlyNew, ClassificationContractFixture.Rule(filters: new[] { ("usage", new[] { "fuel" }) })));
        Assert.False(ClassificationFilters.Matches(onlyNew, ClassificationContractFixture.Rule(filters: new[] { ("usage", new[] { "unknown" }) })));
        Assert.True(ClassificationFilters.Matches(onlyNew, ClassificationContractFixture.Rule(new[] { "misc" })));
    }

    [Theory]
    [InlineData("Usage!", "x")]
    [InlineData("usage", "unknown")]
    [InlineData("future-dimension", "unknown")]
    [InlineData("future-dimension", "unreadable")]
    [InlineData("future-dimension", "bad\tvalue")]
    [InlineData("quality", "6")]
    public void Parse_D081_StillRefusesMalformedOrForbiddenFacets(string dimension, string value)
    {
        var catalog = Catalog(); var policy = Policy();
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => FacetRow(w, (dimension, value.Replace("\\t", "\t")))), catalog, policy));
    }

    [Theory]
    [InlineData("unknown-category")]
    [InlineData("primary-not-member")]
    [InlineData("duplicate-evidence")]
    [InlineData("unknown-biome")]
    [InlineData("empty-membership")]
    public void Parse_RejectsMalformedCategoryPrimaryFacetAndEvidenceSemantics(string error)
    {
        var catalog = Catalog(); var policy = Policy();
        var body = Body(catalog, policy, w => Row(w, primary: error == "primary-not-member" ? "weapons" : "misc",
            category: error == "unknown-category" ? "retired-category" : "misc", biome: error == "unknown-biome" ? "unknown" : "fuel",
            memberCount: error == "duplicate-evidence" ? 2 : error == "empty-membership" ? 0 : 1));
        Assert.Throws<InvalidDataException>(() => Parse(body, catalog, policy));
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("missing-customization")]
    [InlineData("visible-npc")]
    public void Parse_D042_ServerExclusionsNeedNotMatchTheClientCatalog(string variant)
    {
        var catalog = Catalog(Item("Hair", "Customization"), Item("Npc", "OneHandedWeapon", icon: variant == "visible-npc"), Item("Wood"));
        var policy = Policy();
        var exclusions = variant == "missing-customization" ? new[] { ("Npc", "proven-npc-internal") }
            : new[] { ("Hair", "typed-customization"), ("Npc", "proven-npc-internal") };
        var parsed = Parse(Body(catalog, policy, rows: w => { if (variant != "missing-profile") Row(w); },
            scopeComplete: true, source: new string('a', 64), count: variant == "missing-profile" ? 0 : 1, exclusions: exclusions), catalog, policy);
        Assert.Null(parsed.Resolve("Npc", catalog)); Assert.Null(parsed.Resolve("Hair", catalog));
        Assert.Equal("misc", parsed.Resolve("Wood", catalog)!.Primary);
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("icon")]
    [InlineData("catalog")]
    [InlineData("instance")]
    public void Parse_D042_ServerFlagsAreAcceptedPerItem(string flag)
    {
        var catalog = Catalog(); var policy = Policy();
        var parsed = Parse(Body(catalog, policy, w => Row(w, quest: flag == "quest",
            icon: flag != "icon", catalogOnly: flag == "catalog", instance: flag == "instance")), catalog, policy);
        Assert.Same(parsed.Items["Wood"], parsed.Resolve("Wood", catalog));
    }

    [Fact]
    public void Parse_EquipmentRarityRemainsFreshAndCannotArriveAsStaticFacet()
    {
        var catalog = Catalog(Item("Wood", type: "OneHandedWeapon")); var policy = Policy();
        var valid = Parse(Body(catalog, policy, w => Row(w, instance: true, biome: null)), catalog, policy);
        Assert.True(valid.Items["Wood"].InstanceRarity); Assert.Empty(valid.Items["Wood"].Facets);
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => Row(w, instance: true, rarity: "rare")), catalog, policy));
    }

    [Fact]
    public void Parse_RecomputesDigestRatherThanTrustingSuppliedClaim()
    {
        var catalog = Catalog(); var policy = Policy(); var bytes = WithDigest(Body(catalog, policy));
        bytes[bytes.Length - 1] = bytes[bytes.Length - 1] == (byte)'a' ? (byte)'b' : (byte)'a';
        var ex = Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(Wire(bytes), policy));
        Assert.Contains("digest mismatch", ex.Message);
    }

    [Fact]
    public void Parse_RejectsUnsupportedSchemaTruncationAndTrailingDecodedOrCompressedData()
    {
        var catalog = Catalog(); var policy = Policy();
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, schema: 1), catalog, policy));
        var bytes = WithDigest(Body(catalog, policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(Wire(bytes.Take(bytes.Length - 1).ToArray()), policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(Wire(bytes.Concat(new byte[] { 0 }).ToArray()), policy));
        var compressed = Convert.FromBase64String(Wire(bytes).Substring(5));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire("yas3:" + Convert.ToBase64String(compressed.Concat(new byte[] { 0 }).ToArray()), policy));
    }

    [Fact]
    public void Parse_RejectsInvalidUtf8UnboundedScalarAndNoncanonicalBooleans()
    {
        var catalog = Catalog(); var policy = Policy();
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => { w.Write(1); w.Write((byte)0xff); }), catalog, policy));
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => w.Write(8193)), catalog, policy));
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => { Text(w, "Wood"); Text(w, "misc"); w.Write((byte)2); }), catalog, policy));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Parse_RejectsInvalidMembershipAndFacetCountsBeforeAllocatingEvidence(int count)
    {
        var catalog = Catalog(); var policy = Policy();
        void Header(BinaryWriter w) { Text(w, "Wood"); Text(w, "misc"); w.Write(false); w.Write(true); w.Write(false); w.Write(false); w.Write(false); }
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => { Header(w); w.Write(count); }), catalog, policy));
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, w => { Header(w); w.Write(1); Text(w, "misc"); Text(w, "server"); Text(w, "Observed."); w.Write(count); }), catalog, policy));
    }

    [Fact]
    public void Parse_RejectsDuplicateFacetRows()
    {
        var catalog = Catalog(); var policy = Policy();
        foreach (var secondBiome in new[] { "fuel" })
        {
            var body = Body(catalog, policy, w => {
                Text(w, "Wood"); Text(w, "misc"); w.Write(false); w.Write(true); w.Write(false); w.Write(false); w.Write(false);
                w.Write(1); Text(w, "misc"); Text(w, "server"); Text(w, "Observed."); w.Write(2);
                foreach (var biome in new[] { "fuel", secondBiome }.OrderBy(x => x, StringComparer.Ordinal))
                { Text(w, "usage"); Text(w, biome); Text(w, "server"); Text(w, "Observed."); }
            });
            Assert.Throws<InvalidDataException>(() => Parse(body, catalog, policy));
        }
    }

    [Fact]
    public void EmptyCatalog_AcceptsOnlyTheCompleteEmptySnapshot()
    {
        var catalog = CatalogSnapshot.Build(Array.Empty<CatalogFact>()); var policy = Policy();
        var snapshot = Snapshot(catalog, policy);
        var decoded = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy);
        Assert.Empty(decoded.Items); Assert.Equal(snapshot.Fingerprint, decoded.Fingerprint);
        // D042: a server row for an item this client lacks is retained; the server's answer still wins for that prefab.
        var parsed = Parse(Body(catalog, policy), catalog, policy);
        Assert.Same(parsed.Items["Wood"], parsed.Resolve("Wood", catalog));
    }

    [Fact]
    public void Parse_D042_DifferentServerCatalogFingerprintIsAccepted()
    {
        var catalog = Catalog(); var policy = Policy();
        var parsed = Parse(Body(catalog, policy, catalogFingerprint: new string('0', 64)), catalog, policy);
        Assert.Equal(new string('0', 64), parsed.CatalogFingerprint); Assert.NotNull(parsed.Resolve("Wood", catalog));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16385)]
    public void Parse_RejectsInvalidItemCountBeforeAllocatingItemCollections(int count)
    {
        var catalog = Catalog(); var policy = Policy();
        Assert.Throws<InvalidDataException>(() => Parse(Body(catalog, policy, _ => { }, count: count), catalog, policy));
    }

    [Fact]
    public void Parse_RefusesCompressedAndExpandedByteBounds()
    {
        var catalog = Catalog(); var policy = Policy();
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire("yas3:" + Convert.ToBase64String(new byte[AuthoritativeAssignmentsCodec.MaximumCompressedBytes + 1]), policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(Wire(new byte[AuthoritativeAssignmentsCodec.MaximumDecodedBytes + 1]), policy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yas2:AAAA")]
    [InlineData("yas2:invalid!")]
    [InlineData("yas2:AAAA \n")]
    public void Parse_RejectsMissingUnsupportedMalformedOrNoncanonicalWire(string wire)
    {
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(wire, Policy()));
    }

    [Fact]
    public void Cancellation_CreateEncodeAndDecodeRefuseCancelledWork()
    {
        var catalog = Catalog(); var policy = Policy(); var snapshot = Snapshot(catalog, policy);
        var classified = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy)); var cancelled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy, cancelled));
        Assert.Throws<OperationCanceledException>(() => AuthoritativeAssignmentsCodec.ToWire(snapshot, cancelled));
        Assert.Throws<OperationCanceledException>(() => AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy, cancellation: cancelled));
    }

    [Fact]
    public void Snapshot_IsImmutableAndCanonicalAcrossCatalogRegistrationOrder()
    {
        var policy = Policy(); var first = Snapshot(Catalog(Item("Stone"), Item("Wood")), policy);
        var second = Snapshot(Catalog(Item("Wood"), Item("Stone")), policy);
        Assert.Equal(first.Fingerprint, second.Fingerprint); Assert.Equal(AuthoritativeAssignmentsCodec.ToWire(first), AuthoritativeAssignmentsCodec.ToWire(second));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, ClassificationResult>)first.Items).Remove("Wood"));
    }
}
