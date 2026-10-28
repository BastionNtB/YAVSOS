using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;

namespace YAVSOS.Core.Tests;

public sealed class AssignmentExchangeTests
{
    [Fact]
    public void Exchange_DisposalDuringRunningWorkerCancelsAndCannotAcceptLateFault()
    {
        var (catalog, policy, classified) = Inputs();
        var job = new TaskCompletionSource<AssignmentPublication>(); CancellationToken token = default;
        var cache = new AssignmentExchange((work, t) => { token = t; return job.Task; });
        cache.BeginSession(true); cache.SetInputs(true, catalog, policy, classified); cache.Tick();
        cache.Dispose(); Assert.True(token.IsCancellationRequested); Assert.False(job.Task.IsCompleted); Assert.Null(cache.Current);
        job.SetException(new InvalidOperationException("late disposed worker"));
        cache.Tick(); Assert.Null(cache.Current); cache.Dispose();
        Assert.Throws<ObjectDisposedException>(() => cache.BeginSession(true));
    }

    private static (CatalogSnapshot Catalog, CategoryPolicy Policy, ClassifiedCatalog Classified) Inputs()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() });
        Assert.True(CategoryPolicyCodec.TryParse(CategoryPolicyCodec.EmptyDocument, out var policy, out _));
        return (catalog, policy!, ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy!)));
    }
    [Fact]
    public void Exchange_ServerSnapshotAndClientAcceptanceShareAuthoritativeResultWithoutLocalInference()
    {
        var (catalog, policy, classified) = Inputs();
        using var server = new AssignmentExchange((work, token) => Task.FromResult(work()));
        server.BeginSession(true); server.SetInputs(true, catalog, policy, classified); server.Tick(); server.Tick();
        Assert.Equal("Wood", server.Current!.Snapshot.Items["Wood"].Prefab);
        using var client = new AssignmentExchange((work, token) => Task.FromResult(work()));
        client.BeginSession(true); client.SetInputs(false, catalog, policy, payload: server.Current.Wire); client.Tick(); client.Tick();
        Assert.Equal(server.Current.Snapshot.Fingerprint, client.Current!.Snapshot.Fingerprint);
        Assert.Equal("materials.wood", client.Current.Snapshot.Items["Wood"].Primary);
    }
    [Fact]
    public void Exchange_D042_DifferentClientCatalogAccepted_MalformedRecoveryNeverRetainsOldReadiness()
    {
        var (catalog, policy, classified) = Inputs();
        var wire = AuthoritativeAssignmentsCodec.ToWire(AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy));
        using var cache = new AssignmentExchange((work, token) => Task.Run(work, token));
        cache.BeginSession(true); cache.SetInputs(false, catalog, policy, payload: wire); Drain(cache);
        Assert.NotNull(cache.Current);
        cache.SetInputs(false, CatalogSnapshot.Build(new[] { Item(value: 10) }), policy, payload: wire);
        Assert.Null(cache.Current); Drain(cache);
        Assert.NotNull(cache.Current);
        cache.SetInputs(false, catalog, policy, payload: "malformed"); Drain(cache);
        Assert.Null(cache.Current); Assert.Contains("rejected", cache.Diagnostic);
        cache.SetInputs(false, catalog, policy, payload: wire); Drain(cache);
        Assert.Equal("Wood", cache.Current!.Snapshot.Items["Wood"].Prefab);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exchange_LateOrFaultedWorkerCannotPublishAcrossSessionAndLatestRunsSerially(bool fault)
    {
        var (catalog, policy, classified) = Inputs();
        var jobs = new List<TaskCompletionSource<AssignmentPublication>>(); var tokens = new List<CancellationToken>();
        using var cache = new AssignmentExchange((work, token) => { tokens.Add(token); var job = new TaskCompletionSource<AssignmentPublication>(); jobs.Add(job); return job.Task; });
        cache.BeginSession(true); cache.SetInputs(true, catalog, policy, classified); cache.Tick();
        cache.BeginSession(true); cache.SetInputs(true, catalog, policy, classified); cache.Tick();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Single(jobs); Assert.Null(cache.Current);
        var publication = new AssignmentPublication(AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy), "wire");
        if (fault) jobs[0].SetException(new InvalidOperationException("late")); else jobs[0].SetResult(publication);
        cache.Tick(); Assert.Equal(2, jobs.Count); Assert.Null(cache.Current);
        jobs[1].SetResult(publication); cache.Tick(); Assert.Same(publication, cache.Current);
        cache.BeginSession(false); Assert.Null(cache.Current);
    }
    [Fact]
    public void Exchange_DeadlineWithdrawsCompletedResultAndDoesNotRetryUntilInvalidated()
    {
        var (catalog, policy, classified) = Inputs(); double now = 0; var calls = 0;
        var job = new TaskCompletionSource<AssignmentPublication>(); CancellationToken token = default;
        using var cache = new AssignmentExchange((work, t) => { calls++; token = t; return job.Task; }, () => now, 10);
        cache.BeginSession(true); cache.SetInputs(true, catalog, policy, classified); cache.Tick();
        job.SetResult(new AssignmentPublication(AuthoritativeAssignmentSnapshot.Create(catalog, classified, policy), "wire"));
        now = 10; cache.Tick(); Assert.Null(cache.Current); Assert.True(token.IsCancellationRequested);
        Assert.Contains("deadline", cache.Diagnostic); cache.Tick(); Assert.Equal(1, calls);
        cache.Invalidate(); cache.Tick(); Assert.Equal(2, calls);
    }
    [Fact]
    public void Exchange_IdenticalInputsAndIdleTicksReuseOnePublication()
    {
        var (catalog, policy, classified) = Inputs(); var calls = 0;
        using var cache = new AssignmentExchange((work, token) => { calls++; return Task.FromResult(work()); });
        cache.BeginSession(true); cache.SetInputs(true, catalog, policy, classified); cache.Tick(); cache.Tick(); var result = cache.Current;
        for (var i = 0; i < 100; i++) { cache.SetInputs(true, catalog, policy, classified); cache.Tick(); }
        Assert.Same(result, cache.Current); Assert.Equal(1, calls);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { cache.SetInputs(true, catalog, policy, classified); cache.Tick(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        cache.SetInputs(false, catalog, policy); Assert.Null(cache.Current); cache.Tick(); Assert.Equal(1, calls);
    }
    private static void Drain(AssignmentExchange cache)
    {
        Assert.True(SpinWait.SpinUntil(() => { cache.Tick(); return cache.Current != null || cache.Diagnostic.Contains("rejected"); }, 10000));
    }
}
