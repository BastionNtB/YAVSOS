using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using YAVSOS.Classification;
using static YAVSOS.Core.Tests.ClassificationContractFixture;
using static YAVSOS.Core.Tests.ClassificationPipelineTests;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationCacheTests
{
    [Fact]
    public void Host_FreshSourceWithdrawalClosesPublicResultBeforeNextUpdateAndRecovers()
    {
        var policy = new PolicyLifecycle(); policy.BeginSession(PolicyMode.Standalone); policy.ApplyLocal(CategoryPolicyCodec.EmptyDocument);
        var catalog = CatalogSnapshot.Build(new[] { Item() });
        ClassificationSourceSnapshot? source = Sources(catalog, Use("Wood", 1));
        using var cache = new ClassificationCache(schedule: (work, token) => Task.FromResult(work()));
        using var host = new ClassificationHost(_ => { }, _ => { }, policy, () => catalog, cache, () => source);
        host.BeginSession(true); host.Update(); host.Update();
        Assert.Equal(UsageOf(1), host.Current!.Index.Items["Wood"].Facets.Single(x => x.Dimension == "usage").Value);
        Assert.False(host.TrySetSources(host.Revision, Sources(catalog, Use("Wood", 16))));
        Assert.Equal(UsageOf(1), host.Current!.Index.Items["Wood"].Facets.Single(x => x.Dimension == "usage").Value);
        source = null;
        Assert.Null(host.Current);
        host.InvalidateSources(); host.Update(); host.Update();
        Assert.Empty(host.Current!.Index.Items["Wood"].Facets);
        source = Sources(catalog, Use("Wood", 2));
        Assert.Null(host.Current);
        host.Update(); host.Update();
        Assert.Equal(UsageOf(2), host.Current!.Index.Items["Wood"].Facets.Single(x => x.Dimension == "usage").Value);
        Assert.False(policy.CanAutomate);
    }

    [Fact]
    public void Cache_ReusesIdenticalInputsAndIdleTicksWithoutRebuilding()
    {
        var calls = 0; var catalog = CatalogSnapshot.Build(new[] { Item() });
        using var cache = new ClassificationCache((c,r,s,t) => { calls++; return Task.FromResult(ClassificationPipeline.Build(c,r,s,t)); });
        cache.BeginSession(true); cache.SetInputs(catalog, Rules()); cache.Tick(); cache.Tick();
        Assert.Equal(ClassificationState.Ready, cache.State); var result = cache.Current;
        for (var i = 0; i < 100; i++) { cache.SetInputs(catalog, Rules()); cache.Tick(); }
        Assert.Equal(1, calls); Assert.Same(result, cache.Current);
        cache.SetInputs(null, null); Assert.Null(cache.Current); cache.Tick(); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("catalog")]
    [InlineData("source")]
    public void Cache_InputChangesWithdrawCurrentImmediatelyAndRebuild(string change)
    {
        var calls = 0; var catalog = CatalogSnapshot.Build(new[] { Item() }); var rules = Rules();
        using var cache = new ClassificationCache((c,r,s,t) => { calls++; return Task.FromResult(ClassificationPipeline.Build(c,r,s,t)); });
        cache.BeginSession(true); cache.SetInputs(catalog,rules); cache.Tick(); cache.Tick();
        var fingerprint = cache.Current!.Index.Fingerprint; var revision = cache.Revision;
        if (change == "policy") rules = new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(), policyFingerprint: "changed");
        if (change == "catalog") catalog = CatalogSnapshot.Build(new[] { Item(value: 10) });
        cache.SetInputs(catalog, rules, change == "source" ? Sources(catalog, Use("Wood", 1)) : null);
        Assert.Null(cache.Current); Assert.True(cache.Revision > revision); Assert.Equal(ClassificationState.Pending, cache.State);
        cache.Tick(); cache.Tick(); Assert.Equal(2, calls); Assert.NotEqual(fingerprint, cache.Current!.Index.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_CancelsStaleWorkerAndSerializesLatestGeneration(bool fault)
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() }); var jobs = new List<TaskCompletionSource<ClassifiedCatalog>>(); var tokens = new List<CancellationToken>();
        using var cache = new ClassificationCache((c,r,s,t) => { var job = new TaskCompletionSource<ClassifiedCatalog>(); jobs.Add(job); tokens.Add(t); return job.Task; });
        cache.BeginSession(true); cache.SetInputs(catalog,Rules()); cache.Tick();
        cache.SetInputs(catalog,new ClassificationRuleSet(DefaultClassificationTaxonomy.Create(),policyFingerprint:"next")); cache.Tick();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Single(jobs); Assert.Null(cache.Current);
        if (fault) jobs[0].SetException(new InvalidOperationException("late")); else jobs[0].SetResult(ClassificationPipeline.Build(catalog,Rules()));
        cache.Tick(); Assert.Equal(2,jobs.Count); Assert.Null(cache.Current); Assert.Equal(ClassificationState.Building,cache.State);
        jobs[1].SetResult(ClassificationPipeline.Build(catalog,Rules())); cache.Tick(); Assert.Equal(ClassificationState.Ready,cache.State);
    }

    [Fact]
    public void Cache_SourcePublicationNeedsCurrentSessionRevisionAndCatalog()
    {
        var catalog = CatalogSnapshot.Build(new[] { Item() }); var source = Sources(catalog,Use("Wood",1));
        using var cache = new ClassificationCache((c,r,s,t) => Task.FromResult(ClassificationPipeline.Build(c,r,s,t)));
        cache.BeginSession(true); cache.SetInputs(catalog,Rules()); var original = cache.Revision;
        Assert.False(cache.TrySetSources(original-1,source)); Assert.True(cache.TrySetSources(original,source));
        Assert.False(cache.TrySetSources(original,source));
        var nextCatalog = CatalogSnapshot.Build(new[] { Item(value:3) });
        Assert.False(cache.TrySetSources(cache.Revision,Sources(nextCatalog,Use("Wood",1))));
        cache.Tick(); cache.Tick(); Assert.Equal(UsageOf(1),cache.Current!.Index.Items["Wood"].Facets.Single(x=>x.Dimension=="usage").Value);
        cache.BeginSession(false); Assert.Null(cache.Current); Assert.False(cache.TrySetSources(cache.Revision,source));
        cache.BeginSession(true); cache.SetInputs(catalog,Rules()); Assert.False(cache.TrySetSources(original,source));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("completed")]
    public void Cache_DeadlineRejectsLateWorkWithoutRetryStorm(string schedule)
    {
        double now=0; var calls=0; var job=new TaskCompletionSource<ClassifiedCatalog>(); CancellationToken token=default;
        var catalog=CatalogSnapshot.Build(new[]{Item()});
        using var cache=new ClassificationCache((c,r,s,t)=>{calls++;token=t;return job.Task;},()=>now,10);
        cache.BeginSession(true);cache.SetInputs(catalog,Rules());cache.Tick(); now=10;
        if(schedule=="completed") job.SetResult(ClassificationPipeline.Build(catalog,Rules()));
        cache.Tick();Assert.Equal(ClassificationState.Failed,cache.State);Assert.Null(cache.Current);Assert.True(token.IsCancellationRequested);
        if(schedule=="running") job.SetResult(ClassificationPipeline.Build(catalog,Rules()));
        for(var i=0;i<10;i++)cache.Tick();Assert.Equal(1,calls);Assert.Null(cache.Current);
    }

    [Theory]
    [InlineData("synchronous")]
    [InlineData("worker")]
    [InlineData("null-task")]
    public void Cache_FailuresRemainClosedAndExplicitInvalidationRecovers(string kind)
    {
        var calls=0;var catalog=CatalogSnapshot.Build(new[]{Item()});
        using var cache=new ClassificationCache((c,r,s,t)=>{
            if(++calls>1)return Task.FromResult(ClassificationPipeline.Build(c,r,s,t));
            if(kind=="synchronous")throw new InvalidOperationException("reader");
            return kind=="worker"?Task.FromException<ClassifiedCatalog>(new InvalidOperationException("worker")):null!;
        });
        cache.BeginSession(true);cache.SetInputs(catalog,Rules());cache.Tick();cache.Tick();
        Assert.Equal(ClassificationState.Failed,cache.State);Assert.Null(cache.Current);cache.Tick();Assert.Equal(1,calls);
        cache.Invalidate();cache.Tick();cache.Tick();Assert.Equal(ClassificationState.Ready,cache.State);Assert.Equal(2,calls);
    }

    [Fact]
    public void Cache_DisconnectAndDisposalDoNotWaitForOutstandingWorker()
    {
        var job=new TaskCompletionSource<ClassifiedCatalog>();CancellationToken token=default;
        var cache=new ClassificationCache((c,r,s,t)=>{token=t;return job.Task;});
        cache.BeginSession(true);cache.SetInputs(CatalogSnapshot.Build(new[]{Item()}),Rules());cache.Tick();
        cache.Dispose();Assert.True(token.IsCancellationRequested);Assert.False(job.Task.IsCompleted);Assert.Null(cache.Current);
        job.SetException(new InvalidOperationException("late disposal"));cache.Tick();cache.Dispose();Assert.Equal(ClassificationState.Offline,cache.State);
        Assert.Throws<ObjectDisposedException>(()=>cache.BeginSession(true));
    }

    [Fact]
    public void Host_RebuildsPolicyWithoutRecapturingCatalogAndNeverOpensAuthority()
    {
        var policy=new PolicyLifecycle();policy.BeginSession(PolicyMode.Standalone);policy.ApplyLocal(CategoryPolicyCodec.EmptyDocument);
        var catalog=CatalogSnapshot.Build(new[]{Item()});var builds=0;var logs=new List<string>();
        using var cache=new ClassificationCache(schedule: (work,t)=>{builds++;return Task.FromResult(work());});
        using var host=new ClassificationHost(logs.Add,logs.Add,policy,()=>catalog,cache);
        host.BeginSession(true);host.Update();host.Update();Assert.NotNull(host.Current);Assert.False(policy.CanAutomate);
        var xml=CategoryPolicyCodecTests.Wrap("<category id=\"custom\" displayName=\"Custom\"/>","<assignment match=\"exact\" selector=\"Wood\"><member category=\"custom\"/></assignment>",2);
        policy.ApplyLocal(xml);host.Update();Assert.Null(host.Current);host.Update();Assert.Equal("custom",host.Current!.Index.Items["Wood"].Primary);
        Assert.Equal(2,builds);Assert.False(policy.CanAutomate);
        Assert.True(host.TrySetSources(host.Revision,Sources(catalog,Use("Wood",1))));host.Update();host.Update();
        Assert.Equal(UsageOf(1),host.Current!.Index.Items["Wood"].Facets.Single(x=>x.Dimension=="usage").Value);
        host.InvalidateCatalog();Assert.Null(host.Current);host.Update();host.Update();
        Assert.Empty(host.Current!.Index.Items["Wood"].Facets);
        policy.BeginSession(PolicyMode.AwaitingServer);host.Update();Assert.Null(host.Current);Assert.False(policy.CanAutomate);
    }

    [Theory]
    [InlineData("withdrawal")]
    [InlineData("policy")]
    [InlineData("catalog")]
    public void Host_PublicBoundariesImmediatelyRejectChangedInputsBeforeUpdate(string change)
    {
        var policy = new PolicyLifecycle(); policy.BeginSession(PolicyMode.UnmanagedLocal); policy.ApplyLocal(CategoryPolicyCodec.EmptyDocument);
        var catalog = CatalogSnapshot.Build(new[] { Item() }); var source = Sources(catalog, Use("Wood", 1));
        using var cache = new ClassificationCache(schedule: (work,t) => Task.FromResult(work()));
        using var host = new ClassificationHost(_ => { }, _ => { }, policy, () => catalog, cache);
        host.BeginSession(true); host.Update(); host.Update(); Assert.NotNull(host.Current); var revision = host.Revision;
        if (change == "withdrawal") policy.AwaitServer();
        if (change == "policy") policy.ApplyLocal(CategoryPolicyCodecTests.Wrap("<category id=\"custom\" displayName=\"Custom\"/>", "", 2));
        if (change == "catalog") catalog = CatalogSnapshot.Build(new[] { Item(value: 3) });
        Assert.Null(host.Current);
        Assert.False(host.TrySetSources(revision, source));
        Assert.False(policy.CanAutomate);
    }
}
