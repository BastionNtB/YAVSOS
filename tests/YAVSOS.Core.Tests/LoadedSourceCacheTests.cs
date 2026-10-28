using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using static YAVSOS.Core.Tests.LoadedSourceReceiptTests;

namespace YAVSOS.Core.Tests;

public sealed class LoadedSourceCacheTests
{
    private static LoadedSourceCache Immediate(LoadedSourceLimits? limits = null, Func<double>? clock = null) =>
        new LoadedSourceCache(limits, (catalog, rows, token) => Task.FromResult(LoadedSourceReceipt.Build(catalog, rows, limits, token)), clock ?? (() => 0));
    private static void Start(LoadedSourceCache cache, CatalogSnapshot? catalog = null)
    { cache.BeginSession(true); cache.SetCatalog(catalog ?? MakeCatalog("Wood")); }
    private static void Complete(LoadedSourceCache cache)
    {
        cache.Tick(() => new[] { Use(), Coverage() });
        cache.Tick(() => throw new Exception("Accepted receipt must not reopen definitions."));
        Assert.Equal(LoadedSourceState.Ready, cache.State); Assert.NotNull(cache.Current);
    }

    // A registry another mod is still changing (EpicLoot on join, runtime 2026-10-21) gets a few more tries after a pause.
    [Fact]
    public void Cache_RetriesAFailedCaptureAfterThePauseAndAcceptsOnlyAFreshOne()
    {
        var now = 0d;
        using var cache = new LoadedSourceCache(null, (catalog, rows, token) => Task.FromResult(LoadedSourceReceipt.Build(catalog, rows, null, token)),
            () => now, retries: 2, retrySeconds: 3);
        Start(cache);
        IEnumerable<LoadedSourceFact?> Changing() { yield return Use(); throw new InvalidOperationException("Shard registry changed during capture."); }
        cache.Tick(Changing);
        Assert.Equal(LoadedSourceState.Pending, cache.State); Assert.Equal(1, cache.Retries); Assert.Null(cache.Current);
        Assert.Contains("changed during capture", cache.Diagnostic);
        now = 2999; cache.Tick(() => throw new Exception("Must wait for the pause."));
        now = 3000; cache.Tick(Changing);
        Assert.Equal(2, cache.Retries); Assert.Equal(LoadedSourceState.Pending, cache.State);
        now = 6000; Complete(cache);
        Assert.Equal(2, cache.Retries);
        cache.Invalidate(); Assert.Equal(0, cache.Retries);
    }

    [Fact]
    public void Cache_StopsAfterItsRetriesAndByDefaultDoesNotRetry()
    {
        var now = 0d;
        using var cache = new LoadedSourceCache(null, (catalog, rows, token) => Task.FromResult(LoadedSourceReceipt.Build(catalog, rows, null, token)),
            () => now, retries: 1, retrySeconds: 1);
        Start(cache);
        IEnumerable<LoadedSourceFact?> Broken() { if (now >= 0) throw new InvalidDataException("broken"); yield break; }
        cache.Tick(Broken); now = 1000; cache.Tick(Broken);
        Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Equal(1, cache.Retries);
        now = 5000; cache.Tick(() => throw new Exception("A failed cache must not reopen definitions."));
        using var once = Immediate(); Start(once); once.Tick(Broken);
        Assert.Equal(LoadedSourceState.Failed, once.State);
    }

    [Fact]
    public void Cache_WaitsForCatalogAndLoadedRegistriesWithoutStartingWork()
    {
        using var cache = Immediate(); cache.BeginSession(true);
        cache.Tick(() => throw new Exception("Missing catalog must not open definitions."));
        Assert.Equal(LoadedSourceState.Pending, cache.State);
        cache.SetCatalog(MakeCatalog("Wood")); cache.Tick(() => null);
        Assert.Equal(LoadedSourceState.Pending, cache.State); Assert.Null(cache.Current);
        Complete(cache);
    }

    [Fact]
    public void Cache_WithdrawsImmediatelyOnInvalidationAndCatalogLossOrReplacement()
    {
        using var cache = Immediate(); var catalog = MakeCatalog("Wood"); Start(cache, catalog); Complete(cache);
        var revision = cache.Revision; var receipt = cache.Current;
        cache.SetCatalog(catalog); Assert.Equal(revision, cache.Revision); Assert.Same(receipt, cache.Current);
        cache.Invalidate(); Assert.True(cache.Revision > revision); Assert.Null(cache.Current);
        Complete(cache); cache.SetCatalog(null); Assert.Null(cache.Current);
        cache.Tick(() => throw new Exception("Lost catalog must not capture."));
        cache.SetCatalog(MakeCatalog("Wood")); Assert.Null(cache.Current); Complete(cache);
    }

    [Fact]
    public void Cache_StepBudgetIncludesCooperativeNullYieldsAndDisposesFinishedCapture()
    {
        using var cache = Immediate(new LoadedSourceLimits(stepsPerTick: 2)); Start(cache);
        var reads = 0; var closes = 0;
        IEnumerable<LoadedSourceFact?> Capture()
        {
            try { reads++; yield return null; reads++; yield return Use(); reads++; yield return Coverage(); }
            finally { closes++; }
        }
        cache.Tick(Capture); Assert.Equal(2, reads); Assert.Equal(LoadedSourceState.Capturing, cache.State);
        cache.Tick(() => throw new Exception("Active capture must reuse its iterator."));
        Assert.Equal(3, reads); Assert.Equal(1, closes); Assert.Equal(LoadedSourceState.Building, cache.State);
        cache.Tick(() => throw new Exception("Completion must not recapture."));
        Assert.Equal(2, cache.Current!.Facts.Count);
    }

    [Fact]
    public void Cache_MeasuresActualStepAndStopsAfterSliceOverrun()
    {
        var now = 0d; var reads = 0;
        using var cache = Immediate(clock: () => now); Start(cache);
        IEnumerable<LoadedSourceFact?> Capture() { reads++; now += 5; yield return Use(); reads++; yield return Coverage(); }
        cache.Tick(Capture);
        Assert.Equal(1, reads); Assert.Equal(5, cache.MaximumStepMilliseconds); Assert.Equal(LoadedSourceState.Capturing, cache.State);
    }

    [Fact]
    public void Cache_SessionChangeCancelsAndRejectsLateSuccessBeforeAdmittingNextWorker()
    {
        var oldCatalog = MakeCatalog("Wood"); var newCatalog = MakeCatalog("Wood", "Other");
        var jobs = new List<TaskCompletionSource<LoadedSourceReceipt>>(); var tokens = new List<CancellationToken>(); var opens = 0;
        using var cache = new LoadedSourceCache(build: (catalog, rows, token) => {
            tokens.Add(token); var job = new TaskCompletionSource<LoadedSourceReceipt>(); jobs.Add(job); return job.Task;
        }, milliseconds: () => 0);
        Start(cache, oldCatalog); cache.Tick(() => new[] { Use() }); Start(cache, newCatalog);
        Assert.True(tokens[0].IsCancellationRequested); Assert.Null(cache.Current);
        cache.Tick(() => { opens++; return new[] { Use() }; }); Assert.Equal(0, opens); Assert.Single(jobs);
        jobs[0].SetResult(Receipt(oldCatalog, Use()));
        cache.Tick(() => { opens++; return new[] { Use() }; });
        Assert.Equal(1, opens); Assert.Equal(2, jobs.Count); Assert.Null(cache.Current);
        jobs[1].SetResult(Receipt(newCatalog, Use())); cache.Tick(() => throw new Exception("Completed worker must not recapture."));
        Assert.Equal(newCatalog.Fingerprint, cache.Current!.Snapshot.CatalogFingerprint);
    }

    [Fact]
    public void Cache_CatalogChangeWithinSessionRejectsLateWorkerEvenWhenFingerprintMatches()
    {
        var catalog = MakeCatalog("Wood"); var replacement = MakeCatalog("Wood"); var job = new TaskCompletionSource<LoadedSourceReceipt>();
        var builds = 0; var opens = 0; CancellationToken token = default;
        using var cache = new LoadedSourceCache(build: (_, _, nextToken) => { builds++; token = nextToken; return job.Task; }, milliseconds: () => 0);
        Start(cache, catalog); cache.Tick(() => new[] { Use() }); cache.SetCatalog(replacement);
        Assert.True(token.IsCancellationRequested); Assert.Null(cache.Current);
        job.SetResult(Receipt(catalog, Use()));
        cache.Tick(() => { opens++; return null; });
        Assert.Equal(1, builds); Assert.Equal(1, opens); Assert.Null(cache.Current); Assert.Equal(LoadedSourceState.Pending, cache.State);
    }

    [Fact]
    public void Cache_OfflineClosesCaptureAndDiscardsLateWorkerFault()
    {
        var closed = false;
        IEnumerable<LoadedSourceFact?> Capture()
        { try { yield return Use(); yield return Coverage(); } finally { closed = true; } }
        using var cache = Immediate(new LoadedSourceLimits(stepsPerTick: 1)); Start(cache); cache.Tick(Capture); cache.BeginSession(false);
        Assert.True(closed); Assert.Null(cache.Current); Assert.Equal(LoadedSourceState.Offline, cache.State);
        cache.Tick(() => throw new Exception("Offline must not capture."));
        var job = new TaskCompletionSource<LoadedSourceReceipt>();
        using var workerCache = new LoadedSourceCache(build: (_, _, _) => job.Task, milliseconds: () => 0);
        Start(workerCache); workerCache.Tick(() => new[] { Use() }); workerCache.BeginSession(false);
        job.SetException(new IOException("late source failure")); workerCache.Tick(() => throw new Exception("Offline must not capture."));
        Assert.Equal(LoadedSourceState.Offline, workerCache.State); Assert.Null(workerCache.Current);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("running-worker")]
    [InlineData("completed-worker")]
    public void Cache_DeadlineRejectsWholeCaptureOrWorkerWithoutPublishingOrWaiting(string stage)
    {
        var now = 0d; var job = new TaskCompletionSource<LoadedSourceReceipt>(); CancellationToken token = default;
        var catalog = MakeCatalog("Wood");
        using var cache = new LoadedSourceCache(new LoadedSourceLimits(stepsPerTick: stage == "capture" ? 1 : 32, buildSeconds: 1),
            (_, _, nextToken) => { token = nextToken; return job.Task; }, () => now);
        Start(cache, catalog); cache.Tick(() => new[] { Use(), Coverage() }); now = 1000;
        if (stage == "completed-worker") job.SetResult(Receipt(catalog, Use()));
        cache.Tick(() => throw new Exception("Deadline must not reopen definitions."));
        Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Null(cache.Current); Assert.Contains("deadline", cache.Diagnostic);
        if (stage != "capture") Assert.True(token.IsCancellationRequested);
        if (stage == "running-worker") Assert.False(job.Task.IsCompleted);
    }

    [Fact]
    public void Cache_FaultDoesNotRetryAndExplicitInvalidationRecovers()
    {
        using var cache = Immediate(); Start(cache); cache.Tick(() => throw new IOException("registry unavailable"));
        Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Contains("registry unavailable", cache.Diagnostic); Assert.Null(cache.Current);
        cache.Tick(() => throw new Exception("Failure must await explicit invalidation."));
        cache.Invalidate(); Complete(cache);
    }

    [Fact]
    public void Cache_WorkerFaultOrWrongCatalogBindingNeverPublishes()
    {
        var wrong = Receipt(MakeCatalog("Wood", "Other"), Use());
        foreach (var result in new[] { Task.FromException<LoadedSourceReceipt>(new IOException("seal failed")), Task.FromResult(wrong) })
        {
            using var cache = new LoadedSourceCache(build: (_, _, _) => result, milliseconds: () => 0);
            Start(cache); cache.Tick(() => new[] { Use() }); cache.Tick(() => throw new Exception("Failed completion must not recapture."));
            Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Null(cache.Current);
            Assert.Contains(result.IsFaulted ? "seal failed" : "different catalog binding", cache.Diagnostic);
        }
    }

    [Fact]
    public void Cache_EnforcesCaptureFactAndScalarCapsBeforeWorkerStarts()
    {
        var first = Use(); var second = Coverage();
        foreach (var limits in new[] { new LoadedSourceLimits(maxFacts: 1), new LoadedSourceLimits(maxScalarBytes: first.ScalarBytes) })
        {
            var builds = 0;
            using var cache = new LoadedSourceCache(limits, (_, _, _) => { builds++; throw new Exception("Over-budget capture must never dispatch."); }, () => 0);
            Start(cache); cache.Tick(() => new[] { first, second });
            Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Null(cache.Current); Assert.Equal(0, builds);
        }
    }

    [Fact]
    public void Cache_IteratorCleanupFailureRejectsEntireReceipt()
    {
        using var cache = Immediate(); Start(cache); cache.Tick(() => new BadCleanup());
        Assert.Equal(LoadedSourceState.Failed, cache.State); Assert.Null(cache.Current); Assert.Contains("cleanup failure", cache.Diagnostic);
    }

    private sealed class BadCleanup : IEnumerable<LoadedSourceFact?>, IEnumerator<LoadedSourceFact?>
    {
        public LoadedSourceFact? Current => null; object? IEnumerator.Current => Current;
        public IEnumerator<LoadedSourceFact?> GetEnumerator() => this; IEnumerator IEnumerable.GetEnumerator() => this;
        public bool MoveNext() => false; public void Reset() => throw new NotSupportedException();
        public void Dispose() => throw new IOException("cleanup failure");
    }

    [Fact]
    public void Cache_IdleTicksAllocateNoBytesAndNeverReopenLoadedDefinitions()
    {
        using var cache = Immediate(); Start(cache); Complete(cache);
        Func<IEnumerable<LoadedSourceFact?>?> reject = () => throw new Exception("Idle must never reopen definitions.");
        for (var i = 0; i < 1000; i++) cache.Tick(reject);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) cache.Tick(reject);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
