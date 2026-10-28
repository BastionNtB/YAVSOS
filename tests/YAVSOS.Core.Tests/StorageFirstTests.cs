using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Classification;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class StorageFirstTests
{
    private static CategoryPolicy Policy(string? xml = null)
    {
        Assert.True(CategoryPolicyCodec.TryParse(xml ?? CategoryPolicyCodec.EmptyDocument, out var value, out var error), error);
        return value!;
    }
    private static LoadedSourceReceipt Ordinary(CatalogSnapshot catalog, params LoadedSourceFact[] rows) =>
        LoadedSourceReceipt.Build(catalog, rows);
    private static ClassifiedCatalog Classify(CatalogSnapshot catalog, LoadedSourceReceipt receipt, CategoryPolicy? policy = null) =>
        ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy ?? Policy()), receipt.Snapshot);
    private static LoadedSourceFact Fuel(bool enabled = true) => new(LoadedSourceFactKind.ItemUse, "new-burner:fuel",
        "NewMaterial", route: "new-burner", method: "fuel", eligible: enabled);

    [Fact]
    public void OrdinaryReceipt_RejectsCensusClaims()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        Assert.Throws<InvalidDataException>(() => Ordinary(catalog, new LoadedSourceFact(LoadedSourceFactKind.ProfileScope, "invalid", "NewMaterial")));
    }

    [Fact]
    public void OrdinaryPipeline_PreservesNewIdentityConsumersRequiredInputsAndUncertainDefinitionsWithoutBiomeInference()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial", icon: false), Item("NewFood", type: "Consumable", health: 20, duration: 100),
            Item("UncertainNpc", type: "OneHandedWeapon", icon: false), Item("Hair", type: "Customization"),
            Recipe(output: "NewFood"), Requirement(input: "NewMaterial"), Requirement("missing", input: "AbsentInput") });
        var receipt = Ordinary(catalog, Fuel()); var result = Classify(catalog, receipt);
        Assert.False(result.ProfileScope.AcquisitionScanComplete);
        Assert.Equal("typed-customization", Assert.Single(result.ProfileScope.Excluded).Value);
        Assert.Contains("UncertainNpc", result.Index.Items.Keys);
        Assert.Contains(catalog.Requirements["r"], x => x.Value("input") == "AbsentInput");
        Assert.Contains("materials.fuel", Categories(result.Index.Items["NewMaterial"]));
        Assert.Contains("ingredients.food", Categories(result.Index.Items["NewMaterial"]));
        Assert.DoesNotContain(result.Index.Items.Values.SelectMany(x => x.Facets), x => x.Dimension is "biome" or "creature-biome");
    }

    [Fact]
    public void OrdinaryPipeline_ChangedAndRemovedWitnessesWithdrawMembershipAndChangeFingerprint()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        var enabled = Classify(catalog, Ordinary(catalog, Fuel()));
        var disabled = Classify(catalog, Ordinary(catalog, Fuel(false)));
        var removed = Classify(catalog, Ordinary(catalog));
        Assert.Contains("materials.fuel", Categories(enabled.Index.Items["NewMaterial"]));
        Assert.DoesNotContain("materials.fuel", Categories(disabled.Index.Items["NewMaterial"]));
        Assert.DoesNotContain("materials.fuel", Categories(removed.Index.Items["NewMaterial"]));
        Assert.Equal("misc", removed.Index.Items["NewMaterial"].Primary);
        Assert.NotEqual(enabled.SourceFingerprint, disabled.SourceFingerprint);
        Assert.NotEqual(disabled.SourceFingerprint, removed.SourceFingerprint);
    }

    [Fact]
    public void OrdinaryReceipt_InputOrderIsBoundIntoDeterministicIdentity()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        var coverage = new LoadedSourceFact(LoadedSourceFactKind.Coverage, "adapter", detail: "supported");
        var first = Ordinary(catalog, Fuel(), coverage); var reordered = Ordinary(catalog, coverage, Fuel());
        Assert.Equal(first.Snapshot.Fingerprint, reordered.Snapshot.Fingerprint);
        Assert.Equal(Classify(catalog, first).Index.Fingerprint, Classify(catalog, reordered).Index.Fingerprint);
    }

    [Fact]
    public void OrdinaryPipeline_ReplacementOverridesRemainAuthoritativeAndMissingBiomeCannotMatchRestriction()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        var policy = Policy("""
            <policy xmlns="urn:com.bastionntb.yavsos:categories" schema="1" revision="1">
              <categories><category id="valuables" displayName="Valuables"/></categories>
              <assignments><assignment match="exact" selector="NewMaterial"><member category="valuables"/></assignment></assignments>
            </policy>
            """);
        var result = Classify(catalog, Ordinary(catalog, Fuel()), policy).Index.Items["NewMaterial"];
        Assert.Equal(new[] { "valuables" }, Categories(result));
        Assert.True(ClassificationFilters.Matches(result, Rule(new[] { "valuables" })));
        Assert.False(ClassificationFilters.Matches(result, Rule(new[] { "valuables" }, ("food-stat", new[] { "health" }))));
        Assert.True(ClassificationFilters.Matches(result, Rule(new[] { "valuables" }, ("food-stat", new[] { "unknown" }))));
    }

    [Fact]
    public void IdentityOnlyCodec_BindsPolicyWithoutClaimingCategoriesOrFacets()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial"), Item("Hair", type: "Customization") }); var policy = Policy();
        var snapshot = AuthoritativeAssignmentSnapshot.CreateIdentity(catalog, policy);
        var wire = AuthoritativeAssignmentsCodec.ToWire(snapshot);
        var parsed = AuthoritativeAssignmentsCodec.ParseWire(wire, policy);
        Assert.StartsWith("yas3:", wire); Assert.Equal(3, parsed.Schema);
        Assert.True(parsed.IdentityReady); Assert.False(parsed.CategoriesReady); Assert.Empty(parsed.Items);
        Assert.False(parsed.HasFacetEvidence("NewMaterial", "biome"));
        Assert.Equal(snapshot.Fingerprint, parsed.Fingerprint); Assert.Equal(wire, AuthoritativeAssignmentsCodec.ToWire(parsed));
        // D042: a different client catalog is accepted; the server's answer wins per item.
        Assert.Null(parsed.Resolve("Other", CatalogSnapshot.Build(new[] { Item("Other") })));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(wire,
            Policy(CategoryPolicyCodec.EmptyDocument.Replace("revision=\"1\"", "revision=\"2\""))));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire("yas2:" + wire.Substring(5), policy));
    }

    [Fact]
    public void CategoryCodec_OrdinaryEvidenceHasWholePartitionAndPerItemFacetsOnly()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial"), Item("Other") }); var policy = Policy();
        var receipt = Ordinary(catalog, Fuel()); var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, Classify(catalog, receipt), policy);
        var parsed = AuthoritativeAssignmentsCodec.ParseWire(AuthoritativeAssignmentsCodec.ToWire(snapshot), policy);
        Assert.True(parsed.IdentityReady); Assert.True(parsed.CategoriesReady); Assert.Equal(2, parsed.Items.Count);
        Assert.True(parsed.HasFacetEvidence("NewMaterial", "usage")); Assert.False(parsed.HasFacetEvidence("Other", "usage"));
        Assert.False(parsed.HasFacetEvidence("NewMaterial", "biome"));
        Assert.Contains("materials.fuel", Categories(parsed.Items["NewMaterial"]));
        Assert.False(parsed.ProfileScope.AcquisitionScanComplete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Codec_RejectsUnknownOrInconsistentCapabilityFlags(int flags)
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() }); var policy = Policy();
        var wire = AuthoritativeAssignmentsCodec.ToWire(AuthoritativeAssignmentSnapshot.CreateIdentity(catalog, policy));
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(ChangeCapabilities(wire, flags), policy));
    }

    [Fact]
    public void Codec_RejectsCategoryRowsDisguisedAsIdentityWithAValidDigest()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") }); var policy = Policy();
        var snapshot = AuthoritativeAssignmentSnapshot.Create(catalog, Classify(catalog, Ordinary(catalog, Fuel())), policy);
        var wire = AuthoritativeAssignmentsCodec.ToWire(snapshot);
        Assert.Throws<InvalidDataException>(() => AuthoritativeAssignmentsCodec.ParseWire(ChangeCapabilities(wire, 1), policy));
    }

    private static string ChangeCapabilities(string wire, int flags)
    {
        using var input = new MemoryStream(Convert.FromBase64String(wire.Substring(5)));
        using var decoded = new MemoryStream();
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress)) deflate.CopyTo(decoded);
        decoded.Position = 0;
        using (var reader = new BinaryReader(decoded, Encoding.UTF8, true))
        {
            reader.ReadInt32();
            for (var i = 0; i < 6; i++) { var length = reader.ReadInt32(); decoded.Position += length; }
        }
        using (var writer = new BinaryWriter(decoded, Encoding.UTF8, true)) writer.Write(flags);
        // Recompute a valid digest so rejection proves the capability/content contract,
        // rather than merely detecting that the original digest no longer matches.
        var body = decoded.ToArray().Take((int)decoded.Length - 68).ToArray();
        using var final = new MemoryStream(); final.Write(body);
        using (var writer = new BinaryWriter(final, Encoding.UTF8, true))
        {
            var digest = Encoding.UTF8.GetBytes(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant());
            writer.Write(digest.Length); writer.Write(digest);
        }
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
        { final.Position = 0; final.CopyTo(deflate); }
        return "yas3:" + Convert.ToBase64String(output.ToArray());
    }

    [Fact]
    public void OrdinaryReceipt_HonorsCancellation()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LoadedSourceReceipt.Build(catalog, new[] { Fuel() }, cancellation: cancelled.Token));
    }

    [Fact]
    public void Exchange_LateIdentityWorkerCannotReplaceNewCategoryGeneration()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") }); var policy = Policy();
        var jobs = new List<TaskCompletionSource<AssignmentPublication>>(); var tokens = new List<CancellationToken>();
        var snapshot = AuthoritativeAssignmentSnapshot.CreateIdentity(catalog, policy);
        using var exchange = new AssignmentExchange((work, token) =>
        { tokens.Add(token); var job = new TaskCompletionSource<AssignmentPublication>(); jobs.Add(job); return job.Task; });
        exchange.BeginSession(true); exchange.SetInputs(true, catalog, policy); exchange.Tick();
        var classified = Classify(catalog, Ordinary(catalog, Fuel()));
        exchange.SetInputs(true, catalog, policy, classified); exchange.Tick();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Single(jobs);
        jobs[0].SetResult(new AssignmentPublication(snapshot, "obsolete-identity")); exchange.Tick();
        Assert.Null(exchange.Current); Assert.Equal(2, jobs.Count);
        var categories = AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy);
        jobs[1].SetResult(new AssignmentPublication(categories, "current-categories")); exchange.Tick();
        Assert.True(exchange.Current!.Snapshot.CategoriesReady); Assert.Equal("current-categories", exchange.Current.Wire);
        exchange.BeginSession(false); Assert.Null(exchange.Current);
    }

    [Fact]
    public void Exchange_IdentityCategoriesWithdrawalAndRecoveryRemainDistinctAndCurrent()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") }); var policy = Policy();
        var classified = Classify(catalog, Ordinary(catalog, Fuel()));
        using var server = new AssignmentExchange((work, token) => Task.FromResult(work()));
        using var client = new AssignmentExchange((work, token) => Task.FromResult(work()));
        server.BeginSession(true); client.BeginSession(true);
        foreach (var categories in new[] { false, true, false, true })
        {
            server.SetInputs(true, catalog, policy, categories ? classified : null); server.Tick(); server.Tick();
            Assert.NotNull(server.Current); Assert.True(server.Current.Snapshot.IdentityReady);
            Assert.Equal(categories, server.Current.Snapshot.CategoriesReady);
            client.SetInputs(false, catalog, policy, payload: server.Current.Wire); Assert.Null(client.Current);
            client.Tick(); client.Tick(); Assert.NotNull(client.Current);
            Assert.Equal(server.Current.Snapshot.Fingerprint, client.Current.Snapshot.Fingerprint);
            Assert.Equal(categories, client.Current.Snapshot.CategoriesReady);
        }
        client.BeginSession(false); Assert.Null(client.Current);
    }

    [Fact]
    public void RequiredSourcesHost_WaitsForOrdinaryReceiptAndWithdrawsBeforeUpdate()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item("NewMaterial") });
        var lifecycle = new PolicyLifecycle(); lifecycle.BeginSession(PolicyMode.Standalone); lifecycle.ApplyLocal(CategoryPolicyCodec.EmptyDocument);
        ClassificationSourceSnapshot? source = null;
        using var cache = new ClassificationCache(schedule: (work, token) => Task.FromResult(work()));
        using var host = new ClassificationHost(_ => { }, _ => { }, lifecycle, () => catalog, cache, () => source, requireSources: true);
        host.BeginSession(true); host.Update(); host.Update(); Assert.Null(host.Current);
        source = Ordinary(catalog, Fuel()).Snapshot; host.Update(); host.Update();
        Assert.Contains("materials.fuel", Categories(host.Current!.Index.Items["NewMaterial"]));
        source = null; Assert.Null(host.Current); host.Update(); host.Update(); Assert.Null(host.Current);
        Assert.False(lifecycle.CanAutomate);
        source = Ordinary(catalog).Snapshot; host.Update(); host.Update();
        Assert.Equal("misc", host.Current!.Index.Items["NewMaterial"].Primary);
    }
}
