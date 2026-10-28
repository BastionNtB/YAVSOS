using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Classification;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;
using static YAVSOS.Core.Tests.ClassificationContractFixture;
using static YAVSOS.Core.Tests.ClassificationPipelineTests;

namespace YAVSOS.Core.Tests;

public sealed class ClassificationPerformanceTests
{
    private static string Assigned(string category, int revision) => CategoryPolicyCodecTests.Wrap(
        $"<category id=\"{category}\" displayName=\"{category}\"/>",
        $"<assignment match=\"exact\" selector=\"Output\"><member category=\"{category}\"/></assignment>", revision);
    private static CategoryPolicy Policy(string category, int revision)
    {
        Assert.True(CategoryPolicyCodec.TryParse(Assigned(category, revision), out var policy, out var diagnostic), diagnostic);
        return policy!;
    }
    private static CatalogSnapshot Loaded() => CatalogSnapshot.Build(new[] {
        Item(), Item("Output", "OneHandedWeapon"), Item("Unresolved"), Recipe(output: "Output"), Requirement() });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Host_QueuesImmutablePolicyAndRejectsLatePolicyWork(bool fault)
    {
        var lifecycle = new PolicyLifecycle(); lifecycle.BeginSession(PolicyMode.Standalone); lifecycle.ApplyLocal(Assigned("first", 1));
        var catalog = Loaded(); var jobs = new List<TaskCompletionSource<ClassifiedCatalog>>();
        var work = new List<Func<ClassifiedCatalog>>(); var tokens = new List<CancellationToken>();
        using var cache = new ClassificationCache(schedule: (operation,t) => {
            work.Add(operation); tokens.Add(t);
            var job = new TaskCompletionSource<ClassifiedCatalog>(); jobs.Add(job); return job.Task;
        });
        using var host = new ClassificationHost(_ => { }, _ => { }, lifecycle, () => catalog, cache);
        host.BeginSession(true); host.Update();
        Assert.Single(work);
        Assert.Equal(ClassificationState.Building, cache.State); Assert.Null(host.Current);
        Assert.True(host.TrySetSources(host.Revision, Sources(catalog, Use("Output", 1))));
        lifecycle.ApplyLocal(Assigned("middle", 2)); host.Update();
        lifecycle.ApplyLocal(Assigned("latest", 3)); host.Update();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Single(jobs); Assert.Null(host.Current);
        if (fault) jobs[0].SetException(new InvalidOperationException("stale rules"));
        else { Assert.Throws<OperationCanceledException>(() => work[0]()); jobs[0].SetResult(ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(Policy("first", 1)))); }
        host.Update(); Assert.Equal(2, jobs.Count); Assert.Equal(2, work.Count); Assert.Null(host.Current);
        jobs[1].SetResult(work[1]());
        host.Update();
        Assert.Equal("latest", host.Current!.Index.Items["Output"].Primary);
        Assert.Equal(UsageOf(1), Assert.Single(host.Current.Index.Items["Output"].Facets).Value);
        Assert.False(lifecycle.CanAutomate);
    }

    [Fact]
    public void Host_PolicyRebuildKeepsSourceFacetsAndResetsAcrossSourcesAndSessions()
    {
        var lifecycle = new PolicyLifecycle(); lifecycle.BeginSession(PolicyMode.Standalone);
        lifecycle.ApplyLocal(CategoryPolicyCodec.EmptyDocument);
        var catalog = Loaded(); var calls = 0;
        using var cache = new ClassificationCache(schedule: (work,t) => { calls++; return Task.FromResult(work()); });
        using var host = new ClassificationHost(_ => { }, _ => { }, lifecycle, () => catalog, cache);
        host.BeginSession(true); host.Update(); host.Update();
        var source = Sources(catalog, Use("Output", 1));
        Assert.True(host.TrySetSources(host.Revision, source)); host.Update(); host.Update();
        var original = host.Current!;
        lifecycle.ApplyLocal(Assigned("custom", 2)); Assert.Null(host.Current);
        host.Update(); Assert.Null(host.Current); host.Update();
        Assert.NotSame(original, host.Current);
        Assert.Equal(new[] { "custom" }, Categories(host.Current!.Index.Items["Output"]));
        Assert.Equal(UsageOf(1), Assert.Single(host.Current.Index.Items["Output"].Facets).Value);
        for (var i=0; i<100; i++) host.Update(); Assert.Equal(3, calls);
        Assert.True(host.TrySetSources(host.Revision, Sources(catalog, Use("Output", 16))));
        host.Update(); host.Update();
        Assert.Equal(UsageOf(16), Assert.Single(host.Current!.Index.Items["Output"].Facets).Value);
        host.BeginSession(false); Assert.Null(host.Current);
        host.BeginSession(true); host.Update(); host.Update();
        Assert.Empty(host.Current!.Index.Items["Output"].Facets);
        Assert.True(host.TrySetSources(host.Revision, Sources(catalog, Use("Output", 16))));
        host.Update(); host.Update();
        Assert.Equal(UsageOf(16), Assert.Single(host.Current!.Index.Items["Output"].Facets).Value);
        catalog = CatalogSnapshot.Build(catalog.Facts.Reverse()); Assert.Null(host.Current);
        host.Update(); host.Update(); Assert.Empty(host.Current!.Index.Items["Output"].Facets);
        Assert.False(lifecycle.CanAutomate);
    }

    [Fact]
    public void Host_ReadyPolicyIdleDoesNotAllocateOnDetachedRuntime()
    {
        var lifecycle = new PolicyLifecycle(); lifecycle.BeginSession(PolicyMode.Standalone);
        lifecycle.ApplyLocal(CategoryPolicyCodec.EmptyDocument); var catalog = Loaded();
        using var cache = new ClassificationCache(schedule: (work,t) => Task.FromResult(work()));
        using var host = new ClassificationHost(_ => { }, _ => { }, lifecycle, () => catalog, cache);
        host.BeginSession(true); host.Update(); host.Update();
        for (var i=0; i<100; i++) host.Update();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i=0; i<1000; i++) host.Update();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(ClassificationState.Ready, cache.State);
    }

    [Theory]
    [InlineData("synchronous")]
    [InlineData("worker")]
    [InlineData("null-task")]
    public void Cache_PolicyWorkerFailureRemainsClosedUntilExplicitRecovery(string kind)
    {
        var calls = 0; var catalog = Loaded(); var policy = Policy("custom", 1);
        using var cache = new ClassificationCache(schedule: (work,t) => {
            if (++calls > 1) return Task.FromResult(work());
            if (kind == "synchronous") throw new InvalidOperationException("rule setup");
            return kind == "worker" ? Task.FromException<ClassifiedCatalog>(new InvalidOperationException("rules")) : null!;
        });
        cache.BeginSession(true); cache.SetPolicyInputs(catalog, policy); cache.Tick(); cache.Tick();
        Assert.Equal(ClassificationState.Failed, cache.State); Assert.Null(cache.Current);
        cache.SetPolicyInputs(catalog, policy); cache.Tick(); Assert.Equal(1, calls);
        cache.Invalidate(); cache.Tick(); cache.Tick();
        Assert.Equal(ClassificationState.Ready, cache.State); Assert.Equal(2, calls);
        Assert.Equal("custom", cache.Current!.Index.Items["Output"].Primary);
    }

    [Fact]
    public void Cache_PolicySourceBindingAndDisconnectRejectOldGeneration()
    {
        var catalog = Loaded(); var policy = Policy("custom", 1);
        var job = new TaskCompletionSource<ClassifiedCatalog>(); CancellationToken token = default;
        using var cache = new ClassificationCache(schedule: (work,t) => { token=t; return job.Task; });
        cache.BeginSession(true); cache.SetPolicyInputs(catalog, policy); var revision = cache.Revision;
        var source = Sources(catalog, Use("Wood", 1));
        Assert.True(cache.TrySetSources(revision, source)); Assert.False(cache.TrySetSources(revision, source));
        cache.Tick(); cache.BeginSession(false);
        Assert.True(token.IsCancellationRequested); Assert.False(cache.TrySetSources(cache.Revision, source));
        job.SetResult(ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy), source));
        cache.Tick(); Assert.Null(cache.Current); Assert.Equal(ClassificationState.Offline, cache.State);
        cache.BeginSession(true); cache.SetPolicyInputs(catalog, policy);
        Assert.False(cache.TrySetSources(revision, source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_PolicyWorkerDeadlineRejectsRunningAndCompletedWork(bool completeFirst)
    {
        var catalog = Loaded(); var policy = Policy("custom", 1); double now=0; var calls=0;
        var job = new TaskCompletionSource<ClassifiedCatalog>(); CancellationToken token=default;
        using var cache = new ClassificationCache(milliseconds: () => now, deadlineMilliseconds: 10,
            schedule: (work,t) => { calls++; token=t; return job.Task; });
        cache.BeginSession(true); cache.SetPolicyInputs(catalog, policy); cache.Tick(); now=9;
        cache.Tick(); Assert.Equal(ClassificationState.Building, cache.State); Assert.False(token.IsCancellationRequested); now=10;
        var result = ClassificationPipeline.Build(catalog, ClassificationRuleSet.FromSchema1(policy));
        if (completeFirst) job.SetResult(result);
        cache.Tick(); Assert.Equal(ClassificationState.Failed, cache.State); Assert.Null(cache.Current); Assert.True(token.IsCancellationRequested);
        if (!completeFirst) job.SetResult(result);
        cache.Tick(); cache.Tick(); Assert.Equal(1, calls); Assert.Null(cache.Current);
    }
}
