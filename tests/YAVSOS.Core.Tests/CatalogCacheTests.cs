using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

public sealed class CatalogCacheTests
{
    private static CatalogFact Fact(CatalogFactKind kind, string id, params string[] pairs) => new CatalogFact(kind, id,
        Enumerable.Range(0, pairs.Length / 2).Select(i => new KeyValuePair<string, string>(pairs[i * 2], pairs[i * 2 + 1])));
    private static CatalogFact Item(string id = "Wood") => Fact(CatalogFactKind.Item, id, "type", "Material");
    private static CatalogCache Immediate(CatalogLimits? limits = null, Func<double>? clock = null) => new CatalogCache(limits,
        (rows, token) => Task.FromResult(CatalogSnapshot.Build(rows, limits, token)), clock ?? (() => 0));
    private static void Complete(CatalogCache cache, params CatalogFact[] facts)
    {
        cache.Tick(() => facts);
        cache.Tick(() => throw new Exception("Accepted snapshot must not reopen the registry."));
        Assert.Equal(CatalogState.FactsReady, cache.State);
    }

    // D086 B5.36: the catalog gets the same few retries as loaded sources, then gives up until its inputs change.
    [Fact]
    public void Cache_RetriesAFailedCapture_ThenGivesUpUntilInputsChange()
    {
        var now = 0d;
        using var cache = new CatalogCache(null, (rows, token) => Task.FromResult(CatalogSnapshot.Build(rows, null, token)), () => now, retries: 2, retrySeconds: 3);
        cache.BeginSession(true);
        IEnumerable<CatalogFact?> Broken() { yield return Item(); throw new InvalidDataException("registry changed"); }
        cache.Tick(Broken);
        Assert.Equal(CatalogState.Pending, cache.State); Assert.Equal(1, cache.Retries); Assert.False(cache.GaveUp);
        Assert.Contains("Trying again in 3 s (1 of 2)", cache.Diagnostic);
        now = 2999; cache.Tick(() => throw new Exception("Must wait for the pause."));
        now = 3000; cache.Tick(Broken); now = 6000; cache.Tick(Broken);
        Assert.Equal(CatalogState.Failed, cache.State); Assert.True(cache.GaveUp); Assert.Equal(2, cache.Retries);
        now = 9000; cache.Tick(() => throw new Exception("A cache that gave up must not reopen the registry."));
        cache.Invalidate(); Assert.Equal(0, cache.Retries);
        Complete(cache, Item());
        using var once = Immediate(); once.BeginSession(true); once.Tick(Broken);
        Assert.Equal(CatalogState.Failed, once.State); // By default, no retries.
    }

    [Fact]
    public void Snapshot_IsOwnedOrdinalAndDeterministicAcrossRegistrationOrder()
    {
        var values = new Dictionary<string, string> { ["type"] = "Material", ["nameKey"] = "$wood" };
        var wood = new CatalogFact(CatalogFactKind.Item, "Wood", values);
        values["type"] = "Changed";
        var recipe = Fact(CatalogFactKind.Recipe, "recipe", "output", "Wood", "enabled", "False");
        var requirement = Fact(CatalogFactKind.Requirement, "slot0", "recipe", "recipe", "input", "Missing", "amount", "3");
        var a = CatalogSnapshot.Build(new[] { wood, recipe, requirement, Item("wood") });
        var b = CatalogSnapshot.Build(new[] { Item("wood"), requirement, recipe, wood });
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal("Material", a.Items["Wood"].Value("type"));
        Assert.Equal(2, a.Items.Count); // Case-sensitive prefab identity.
        Assert.Equal("False", a.Recipes["recipe"].Value("enabled"));
        Assert.Equal("3", Assert.Single(a.Requirements["recipe"]).Value("amount"));
        Assert.Equal(new[] { "item:Missing" }, a.MissingReferences);
        Assert.Equal(64, a.Fingerprint.Length);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, CatalogFact>)a.Items).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)wood.Values).Clear());
    }

    [Fact]
    public void Fingerprint_CoversValuesKindsFieldBoundariesAndEmptyMissingEndpoints()
    {
        var baseline = CatalogSnapshot.Build(new[] { Item(), Fact(CatalogFactKind.Recipe, "r", "output", "") });
        Assert.Equal(new[] { "empty-item-reference:r" }, baseline.MissingReferences);
        var changed = CatalogSnapshot.Build(new[] { Item(), Fact(CatalogFactKind.Recipe, "r", "output", "Wood") });
        Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
        var joined = CatalogSnapshot.Build(new[] { Fact(CatalogFactKind.Item, "ab", "c", "d") });
        var split = CatalogSnapshot.Build(new[] { Fact(CatalogFactKind.Item, "a", "bc", "d") });
        Assert.NotEqual(joined.Fingerprint, split.Fingerprint);
        Assert.NotEqual(joined.Fingerprint, CatalogSnapshot.Build(new[] { Fact(CatalogFactKind.Warning, "ab", "c", "d") }).Fingerprint);
    }

    [Theory]
    [InlineData(CatalogFactKind.Item)]
    [InlineData(CatalogFactKind.Recipe)]
    [InlineData(CatalogFactKind.Requirement)]
    public void Snapshot_RejectsAmbiguousIdentitiesAndOrphanRequirements(CatalogFactKind kind)
    {
        var fact = Fact(kind, "duplicate", "recipe", "parent", "output", "Wood", "input", "Wood");
        Assert.Throws<InvalidDataException>(() => CatalogSnapshot.Build(new[] {
            Item(), Fact(CatalogFactKind.Recipe, "parent", "output", "Wood"), fact, fact }));
        Assert.Throws<InvalidDataException>(() => CatalogSnapshot.Build(new[] { Fact(CatalogFactKind.Requirement, "orphan", "recipe", "absent") }));
    }

    [Theory]
    [InlineData(0)] // Item count.
    [InlineData(1)] // Recipe count.
    [InlineData(2)] // Total row count.
    [InlineData(3)] // UTF-8 scalar data.
    public void SnapshotAndCapture_EnforceExactCountAndScalarBoundaries(int boundary)
    {
        var rows = boundary == 1 ? new[] { Fact(CatalogFactKind.Recipe, "a"), Fact(CatalogFactKind.Recipe, "b") }
            : new[] { Item("a"), Item("b") };
        var limits = boundary switch {
            0 => new CatalogLimits(maxItems: 1), 1 => new CatalogLimits(maxRecipes: 1),
            2 => new CatalogLimits(maxRows: 1), _ => new CatalogLimits(maxScalarBytes: rows[0].ScalarBytes) };
        Assert.Single(CatalogSnapshot.Build(rows.Take(1), limits).Facts);
        Assert.Throws<InvalidDataException>(() => CatalogSnapshot.Build(rows, limits));
        using var cache = Immediate(limits); cache.BeginSession(true);
        cache.Tick(() => rows);
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Null(cache.Current);
        cache.Tick(() => throw new Exception("Failure must not automatically retry."));
    }

    [Fact]
    public void Scalars_RejectMalformedUtf16AndOversizeButRetainUnicodeAndEmptyEvidence()
    {
        Assert.Throws<ArgumentException>(() => Fact(CatalogFactKind.Item, new string('a', 2049)));
        Assert.Throws<ArgumentException>(() => Fact(CatalogFactKind.Item, " "));
        Assert.Throws<ArgumentException>(() => Fact(CatalogFactKind.Item, "invalid\ud800"));
        Assert.Throws<ArgumentException>(() => Fact(CatalogFactKind.Item, "valid", "key", "\udc00"));
        var unicode = Fact(CatalogFactKind.Item, "木\ud83d\ude00", "note", "");
        Assert.Equal(11, unicode.ScalarBytes); // 3 + 4 UTF-8 identity bytes, 4 key bytes.
        Assert.Equal("", unicode.Value("note"));
        Assert.Throws<ArgumentException>(() => new CatalogFact(CatalogFactKind.Item, "too-many",
            Enumerable.Range(0, 65).Select(i => new KeyValuePair<string, string>(i.ToString(), ""))));
    }

    [Fact]
    public void Cache_RespectsStepBudgetCoalescesInvalidationAndNeverReadsReadyCache()
    {
        using var cache = Immediate(new CatalogLimits(stepsPerTick: 2));
        cache.BeginSession(true); cache.Invalidate(); cache.Invalidate();
        var reads = 0; var closes = 0;
        IEnumerable<CatalogFact?> Capture()
        {
            try { for (var i = 0; i < 3; i++) { reads++; yield return Item(i.ToString()); } }
            finally { closes++; }
        }
        cache.Tick(Capture);
        Assert.Equal(2, reads); Assert.Equal(CatalogState.Capturing, cache.State);
        cache.Tick(() => throw new Exception("Capture must reuse its iterator."));
        Assert.Equal(3, reads); Assert.Equal(1, closes); Assert.Equal(CatalogState.Building, cache.State);
        cache.Tick(() => throw new Exception("Ready cache must not enumerate again."));
        Assert.Equal(3, cache.Current!.Items.Count);
        var snapshot = cache.Current;
        cache.Tick(() => throw new Exception("Idle must not enumerate."));
        Assert.Same(snapshot, cache.Current);
    }

    [Fact]
    public void Cache_SliceOverrunStopsBeforeNextStepAndMeasuresActualCost()
    {
        double now = 0; var reads = 0;
        using var cache = Immediate(clock: () => now); cache.BeginSession(true);
        IEnumerable<CatalogFact?> Capture()
        {
            reads++; now += 7; yield return Item();
            reads++; yield return Item("Stone");
        }
        cache.Tick(Capture);
        Assert.Equal(1, reads); Assert.Equal(7, cache.MaximumStepMilliseconds);
        Assert.Equal(CatalogState.Capturing, cache.State);
    }

    [Fact]
    public void Cache_ConnectionChangeRejectsLateSuccessAndSerializesWorkers()
    {
        var jobs = new List<TaskCompletionSource<CatalogSnapshot>>(); var tokens = new List<CancellationToken>(); var opens = 0;
        using var cache = new CatalogCache(build: (rows, token) => {
            tokens.Add(token); var job = new TaskCompletionSource<CatalogSnapshot>(); jobs.Add(job); return job.Task;
        }, milliseconds: () => 0);
        cache.BeginSession(true); cache.Tick(() => new[] { Item("old") });
        cache.BeginSession(true); cache.Invalidate();
        Assert.True(tokens[0].IsCancellationRequested); Assert.Null(cache.Current);
        cache.Tick(() => { opens++; return new[] { Item("new") }; });
        Assert.Equal(0, opens); Assert.Single(jobs);
        jobs[0].SetResult(CatalogSnapshot.Build(new[] { Item("old") }));
        cache.Tick(() => { opens++; return new[] { Item("new") }; });
        Assert.Equal(1, opens); Assert.Equal(2, jobs.Count); Assert.Null(cache.Current);
        jobs[1].SetResult(CatalogSnapshot.Build(new[] { Item("new") }));
        cache.Tick(() => throw new Exception("Completion must not recapture."));
        Assert.Equal(new[] { "new" }, cache.Current!.Items.Keys);
    }

    [Fact]
    public void Cache_OfflineCancelsCaptureReleasesIteratorAndDiscardsLateWorkerFault()
    {
        var closed = false;
        IEnumerable<CatalogFact?> Capture()
        {
            try { yield return Item(); yield return Item("Stone"); }
            finally { closed = true; }
        }
        using var cache = Immediate(new CatalogLimits(stepsPerTick: 1)); cache.BeginSession(true);
        cache.Tick(Capture); cache.BeginSession(false);
        Assert.True(closed); Assert.Null(cache.Current); Assert.Equal(CatalogState.Offline, cache.State);
        cache.Tick(() => throw new Exception("Offline must not capture."));
        var job = new TaskCompletionSource<CatalogSnapshot>();
        using var workerCache = new CatalogCache(build: (_, _) => job.Task, milliseconds: () => 0);
        workerCache.BeginSession(true); workerCache.Tick(() => new[] { Item() }); workerCache.BeginSession(false);
        job.SetException(new IOException("late fault"));
        workerCache.Tick(() => throw new Exception("Offline must not capture."));
        Assert.Equal(CatalogState.Offline, workerCache.State); Assert.Null(workerCache.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cache_DeadlineRejectsCaptureAndLateCompletedWorker(bool worker)
    {
        double now = 0;
        var job = new TaskCompletionSource<CatalogSnapshot>(); CancellationToken token = default;
        using var cache = new CatalogCache(new CatalogLimits(stepsPerTick: worker ? 64 : 1, buildSeconds: 1),
            (_, t) => { token = t; return job.Task; }, () => now);
        cache.BeginSession(true); cache.Tick(() => new[] { Item(), Item("Stone") });
        now = 1000;
        if (worker) job.SetResult(CatalogSnapshot.Build(new[] { Item() }));
        cache.Tick(() => throw new Exception("Deadline must not reopen the registry."));
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Null(cache.Current);
        Assert.Contains("deadline", cache.Diagnostic);
        if (worker) Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cache_FaultIsControlledAndExplicitInvalidationRecovers()
    {
        using var cache = Immediate(); cache.BeginSession(true);
        cache.Tick(() => throw new IOException("read failure"));
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Contains("read failure", cache.Diagnostic);
        cache.Invalidate(); Complete(cache, Item());
        cache.Invalidate(); Assert.Null(cache.Current); Assert.Equal(CatalogState.Pending, cache.State);
        Complete(cache, Item("Stone")); Assert.Equal(new[] { "Stone" }, cache.Current!.Items.Keys);
    }

    [Fact]
    public void Cache_WorkerFailureDoesNotPublishAndDoesNotRetry()
    {
        using var cache = new CatalogCache(build: (_, _) => Task.FromException<CatalogSnapshot>(new IOException("seal failed")), milliseconds: () => 0);
        cache.BeginSession(true); cache.Tick(() => new[] { Item() });
        cache.Tick(() => throw new Exception("Failure must not reopen the registry."));
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Contains("seal failed", cache.Diagnostic); Assert.Null(cache.Current);
    }

    [Fact]
    public void Cache_RunningWorkerDeadlineCancelsWithoutWaitingOrStartingAnotherJob()
    {
        double now = 0; CancellationToken token = default; var builds = 0; var opens = 0;
        var job = new TaskCompletionSource<CatalogSnapshot>();
        using var cache = new CatalogCache(new CatalogLimits(buildSeconds: 1), (_, t) => {
            builds++; token = t; return job.Task;
        }, () => now);
        cache.BeginSession(true); cache.Tick(() => new[] { Item() }); now = 1000;
        cache.Tick(() => { opens++; return new[] { Item("Stone") }; });
        Assert.True(token.IsCancellationRequested); Assert.False(job.Task.IsCompleted);
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Null(cache.Current);
        cache.Invalidate(); cache.Tick(() => { opens++; return new[] { Item("Stone") }; });
        Assert.Equal(0, opens); Assert.Equal(1, builds); Assert.Equal(CatalogState.Pending, cache.State);
        job.SetCanceled();
        cache.BeginSession(false); cache.Tick(() => throw new Exception("Offline must not retry."));
        Assert.Equal(CatalogState.Offline, cache.State);
    }

    [Fact]
    public void Cache_WaitsForRegistryAndCountsNullYieldsAgainstStepBudget()
    {
        var reads = 0;
        using var cache = Immediate(new CatalogLimits(stepsPerTick: 2)); cache.BeginSession(true);
        cache.Tick(() => null); Assert.Equal(CatalogState.Pending, cache.State);
        IEnumerable<CatalogFact?> Capture()
        {
            reads++; yield return null;
            reads++; yield return null;
            reads++; yield return Item();
        }
        cache.Tick(Capture); Assert.Equal(2, reads); Assert.Equal(CatalogState.Capturing, cache.State);
        cache.Tick(() => throw new Exception("Null yields must retain the reader."));
        cache.Tick(() => throw new Exception("Complete facts must not reopen."));
        Assert.Equal(3, reads); Assert.Equal(new[] { "Wood" }, cache.Current!.Items.Keys);
    }

    [Fact]
    public void Cache_DisposalFailureIsControlledAndBlocksPublication()
    {
        using var cache = Immediate(); cache.BeginSession(true);
        cache.Tick(() => new BadCleanup());
        Assert.Equal(CatalogState.Failed, cache.State); Assert.Contains("cleanup failed", cache.Diagnostic); Assert.Null(cache.Current);
    }
    private sealed class BadCleanup : IEnumerable<CatalogFact?>, IEnumerator<CatalogFact?>
    {
        public CatalogFact? Current => null;
        object? IEnumerator.Current => Current;
        public bool MoveNext() => false;
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => throw new IOException("cleanup failed");
        public IEnumerator<CatalogFact?> GetEnumerator() => this;
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void FactsReady_NeverOpensClassificationOrPolicyAutomationGate()
    {
        var policy = new PolicyLifecycle(); policy.BeginSession(PolicyMode.Standalone);
        Assert.Equal(PolicyUpdate.Accepted, policy.ApplyLocal(CategoryPolicyCodecTests.Wrap("", "")));
        using var cache = Immediate(); cache.BeginSession(true); Complete(cache, Item());
        Assert.True(policy.PolicyReady); Assert.False(policy.CanAutomate);
        Assert.Equal(CatalogState.FactsReady, cache.State);
    }

    [Fact]
    public void Snapshot_CancellationAndInvalidLimitsRejectWorkBeforePublication()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => CatalogSnapshot.Build(new[] { Item() }, cancellation: cancel.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(sliceMilliseconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(buildSeconds: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(stepsPerTick: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CatalogLimits(maxScalarBytes: 32 * 1024 * 1024 + 1));
    }
}
