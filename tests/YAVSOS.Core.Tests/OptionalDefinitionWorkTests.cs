using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Integrations;
using static YAVSOS.Core.Tests.LoadedSourceReceiptTests;

namespace YAVSOS.Core.Tests;

public sealed class OptionalDefinitionWorkTests
{
    private static readonly OptionalCapability Supported = new("jotunn", OptionalCapabilityState.Supported, "2.30.2", "test probe");
    private static OptionalDefinitionObservation Observation(string item = "Wood") =>
        new(OptionalDefinitionKind.ItemRegistration, item, "com.example.owner", item);
    private sealed class Jobs
    {
        internal readonly List<TaskCompletionSource<IReadOnlyList<LoadedSourceFact>>> Pending = new();
        internal readonly List<CancellationToken> Tokens = new();
        internal readonly List<IReadOnlyList<OptionalDefinitionObservation>> Inputs = new();
        internal Task<IReadOnlyList<LoadedSourceFact>> Build(CatalogSnapshot catalog, OptionalCapability capability,
            IReadOnlyList<OptionalDefinitionObservation> input, CancellationToken token)
        {
            Tokens.Add(token); Inputs.Add(input);
            var result = new TaskCompletionSource<IReadOnlyList<LoadedSourceFact>>(); Pending.Add(result); return result.Task;
        }
    }
    private static List<LoadedSourceFact> Drain(IEnumerator<LoadedSourceFact?> reader)
    {
        var rows = new List<LoadedSourceFact>();
        // These callers have completed their controlled tasks; another null would be a defect.
        while (reader.MoveNext()) rows.Add(Assert.IsType<LoadedSourceFact>(reader.Current));
        return rows;
    }

    [Fact]
    public void Capture_WaitsCooperativelyAndPublishesCompleteExactReceiptFromOwnedInputs()
    {
        var catalog = MakeCatalog("Wood", "Other"); var jobs = new Jobs();
        using var work = new OptionalDefinitionWork(jobs.Build);
        var observations = new[] { Observation() };
        using var reader = work.Capture(catalog, Supported, observations).GetEnumerator();
        Assert.True(reader.MoveNext()); Assert.Null(reader.Current);
        Assert.True(reader.MoveNext()); Assert.Null(reader.Current); Assert.Single(jobs.Pending);
        observations[0] = Observation("Other");
        Assert.Equal("Wood", Assert.Single(jobs.Inputs[0]).Item);
        var expected = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Observation() });
        jobs.Pending[0].SetResult(OptionalDefinitionEvidence.Build(catalog, Supported, jobs.Inputs[0]));
        var actual = Drain(reader);
        Assert.Equal(expected.Select(x => x.Identity), actual.Select(x => x.Identity));
        Assert.Equal(LoadedSourceReceipt.Build(catalog, expected).Snapshot.Fingerprint,
            LoadedSourceReceipt.Build(catalog, actual).Snapshot.Fingerprint);
        Assert.Contains(actual, x => x.Kind == LoadedSourceFactKind.FacetEvidence && x.Output == "Wood");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledGeneration_DiscardsLateSuccessOrFaultAndSerializesReplacement(bool fault)
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs();
        using var work = new OptionalDefinitionWork(jobs.Build);
        var first = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(first.MoveNext()); first.Dispose(); Assert.True(jobs.Tokens[0].IsCancellationRequested);
        using var next = work.Capture(catalog, Supported, Array.Empty<OptionalDefinitionObservation>()).GetEnumerator();
        Assert.True(next.MoveNext()); Assert.Null(next.Current); Assert.Single(jobs.Pending);
        if (fault) jobs.Pending[0].SetException(new InvalidOperationException("retired generation"));
        else jobs.Pending[0].SetResult(new[] { Use("stale") });
        Assert.True(next.MoveNext()); Assert.Null(next.Current); Assert.Equal(2, jobs.Pending.Count);
        jobs.Pending[1].SetResult(new[] { Coverage("fresh") });
        Assert.Equal("fresh", Assert.Single(Drain(next)).Detail);
    }

    [Fact]
    public void ActiveFault_PropagatesAndAllowsExplicitNewCapture()
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs();
        using var work = new OptionalDefinitionWork(jobs.Build);
        using var first = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(first.MoveNext()); jobs.Pending[0].SetException(new InvalidOperationException("worker failed"));
        Assert.Equal("worker failed", Assert.Throws<InvalidOperationException>(() => first.MoveNext()).Message);
        Assert.True(jobs.Tokens[0].IsCancellationRequested);
        using var next = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(next.MoveNext()); jobs.Pending[1].SetResult(new[] { Coverage("recovered") });
        Assert.Equal("recovered", Assert.Single(Drain(next)).Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetiredWorker_IsReapedAfterCompletionWithoutAnotherCapture(bool fault)
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs(); using var work = new OptionalDefinitionWork(jobs.Build);
        var reader = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(reader.MoveNext()); reader.Dispose();
        work.ReapRetired(); // Pending retired work still owns its slot and cancellation lifetime.
        Assert.NotNull(jobs.Tokens[0].WaitHandle);
        if (fault) jobs.Pending[0].SetException(new InvalidOperationException("retired fault"));
        else jobs.Pending[0].SetResult(new[] { Use() });
        work.ReapRetired(); work.ReapRetired();
        Assert.Throws<ObjectDisposedException>(() => jobs.Tokens[0].WaitHandle);
        Assert.Single(jobs.Pending);
    }

    [Fact]
    public void Disposal_CancelsLifetimeAndPreventsPendingOrFuturePublication()
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs(); var work = new OptionalDefinitionWork(jobs.Build);
        using var reader = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(reader.MoveNext()); work.Dispose(); work.Dispose();
        Assert.True(jobs.Tokens[0].IsCancellationRequested); jobs.Pending[0].SetResult(new[] { Use() });
        Assert.Throws<ObjectDisposedException>(() => reader.MoveNext());
        using var next = work.Capture(catalog, Supported, Array.Empty<OptionalDefinitionObservation>()).GetEnumerator();
        Assert.Throws<ObjectDisposedException>(() => next.MoveNext()); Assert.Single(jobs.Pending);
    }

    [Fact]
    public void ConcurrentCapture_IsRejectedWithoutCancellingOriginal()
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs(); using var work = new OptionalDefinitionWork(jobs.Build);
        using var first = work.Capture(catalog, Supported, new[] { Observation() }).GetEnumerator();
        Assert.True(first.MoveNext());
        using var other = work.Capture(catalog, Supported, Array.Empty<OptionalDefinitionObservation>()).GetEnumerator();
        Assert.Throws<InvalidOperationException>(() => other.MoveNext());
        Assert.False(jobs.Tokens[0].IsCancellationRequested); Assert.Single(jobs.Pending);
        jobs.Pending[0].SetResult(new[] { Use() }); Assert.Equal("wood", Assert.Single(Drain(first)).Identity);
    }

    private sealed class BoundedInput : IReadOnlyList<OptionalDefinitionObservation>
    {
        internal int Reads;
        public int Count => int.MaxValue;
        public OptionalDefinitionObservation this[int index]
        {
            get { Assert.InRange(index, 0, OptionalDefinitionEvidence.MaximumObservations); Reads++; return Observation(); }
        }
        public IEnumerator<OptionalDefinitionObservation> GetEnumerator() => throw new InvalidOperationException("Must use bounded copy.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capture_BoundsSnapshotAndPreservesWholeAdapterGapOrUnsupportedCoverage(bool supported)
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs(); using var work = new OptionalDefinitionWork(jobs.Build);
        var input = new BoundedInput(); var capability = supported ? Supported : OptionalCapability.Absent("jotunn");
        using var reader = work.Capture(catalog, capability, input).GetEnumerator();
        Assert.True(reader.MoveNext());
        Assert.Equal(supported ? OptionalDefinitionEvidence.MaximumObservations + 1 : 0, input.Reads);
        Assert.Equal(input.Reads, jobs.Inputs[0].Count);
        jobs.Pending[0].SetResult(OptionalDefinitionEvidence.Build(catalog, capability, jobs.Inputs[0]));
        var rows = Drain(reader); Assert.All(rows, x => Assert.Equal(LoadedSourceFactKind.Coverage, x.Kind));
        if (supported) Assert.Contains(rows, x => x.Detail.Contains("observation bound exceeded"));
        else Assert.Contains("Absent", Assert.Single(rows).Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceCache_InvalidationOrDeadlineCancelsPendingCompactionAndRecoversExactly(bool deadline)
    {
        var catalog = MakeCatalog("Wood"); var jobs = new Jobs(); using var work = new OptionalDefinitionWork(jobs.Build);
        var now = 0d;
        using var cache = new LoadedSourceCache(new LoadedSourceLimits(stepsPerTick: 1, buildSeconds: 1),
            build: (c, rows, _) => Task.FromResult(LoadedSourceReceipt.Build(c, rows)), milliseconds: () => now);
        cache.BeginSession(true); cache.SetCatalog(catalog);
        IEnumerable<LoadedSourceFact?> Open() => work.Capture(catalog, Supported, new[] { Observation() });
        cache.Tick(Open); Assert.Equal(LoadedSourceState.Capturing, cache.State);
        if (deadline)
        {
            now = 1000; cache.Tick(Open); Assert.Equal(LoadedSourceState.Failed, cache.State);
            Assert.Contains("deadline", cache.Diagnostic);
        }
        else cache.Invalidate();
        Assert.True(jobs.Tokens[0].IsCancellationRequested); Assert.Null(cache.Current);
        cache.Invalidate(); cache.Tick(Open); Assert.Single(jobs.Pending);
        jobs.Pending[0].SetResult(new[] { Use("stale") }); cache.Tick(Open); Assert.Equal(2, jobs.Pending.Count);
        var expected = OptionalDefinitionEvidence.Build(catalog, Supported, new[] { Observation() });
        jobs.Pending[1].SetResult(expected);
        for (var i = 0; i < expected.Count + 3; i++) cache.Tick(Open);
        Assert.Equal(LoadedSourceState.Ready, cache.State);
        Assert.Equal(LoadedSourceReceipt.Build(catalog, expected).Snapshot.Fingerprint, cache.Current!.Snapshot.Fingerprint);
        Assert.DoesNotContain(cache.Current.Facts, x => x.Identity == "stale");
    }

    [Fact]
    public async Task DefaultWorker_ProducesExactDetachedReceipt()
    {
        var catalog = MakeCatalog("Wood"); var input = new[] { Observation() };
        using var work = new OptionalDefinitionWork(); using var reader = work.Capture(catalog, Supported, input).GetEnumerator();
        var rows = new List<LoadedSourceFact>(); var finish = DateTime.UtcNow.AddSeconds(10);
        while (reader.MoveNext())
        {
            Assert.True(DateTime.UtcNow < finish, "Detached worker did not finish within ten seconds.");
            if (reader.Current != null) rows.Add(reader.Current);
            else await Task.Yield();
        }
        Assert.Equal(LoadedSourceReceipt.Build(catalog, OptionalDefinitionEvidence.Build(catalog, Supported, input)).Snapshot.Fingerprint,
            LoadedSourceReceipt.Build(catalog, rows).Snapshot.Fingerprint);
    }
}
