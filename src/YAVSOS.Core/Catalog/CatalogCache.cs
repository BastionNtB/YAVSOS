using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace YAVSOS.Core.Catalog;

public enum CatalogState { Offline, Pending, Capturing, Building, FactsReady, Failed }

/// <summary>Main-thread owned. One capture and at most one detached worker; no stale snapshot lookup.</summary>
public sealed class CatalogCache : IDisposable
{
    private readonly CatalogLimits limits;
    private readonly Func<IReadOnlyList<CatalogFact>, CancellationToken, Task<CatalogSnapshot>> build;
    private readonly Func<double> clock;
    private IEnumerator<CatalogFact?>? reader;
    private List<CatalogFact>? rows;
    private Task<CatalogSnapshot>? worker;
    private CancellationTokenSource? cancellation;
    private long revision, workerRevision;
    private bool active;
    // D144: the capture's limit counts only the time spent capturing, so a host with a low frame rate isn't failed
    // for the frames in between; the build's limit starts with the build. A wall-clock backstop still ends a capture.
    private double started, working, buildStarted;
    public const double CaptureWallMilliseconds = 600_000;
    // D086 B5.36: like loaded sources, a failed capture or build is tried again a few times per change of inputs;
    // D144: then again with a growing pause.
    private readonly StageRetry retry;
    /// <summary>Failed attempts since the inputs last changed; the last one is in <see cref="Diagnostic"/>.</summary>
    public int Retries => retry.Attempts;
    /// <summary>The quick retries for the current inputs failed (shown, D087). A slow retry is due later (D144).</summary>
    public bool GaveUp => State == CatalogState.Failed;
    private int bytes, items, recipes;
    public CatalogState State { get; private set; }
    public CatalogSnapshot? Current { get; private set; }
    public string Diagnostic { get; private set; } = "No active catalog session.";
    public long Revision => revision;
    public double MaximumStepMilliseconds { get; private set; }
    // D144: where the time went for the last ready result, for the log line (capture work, capture elapsed, build).
    public double LastCaptureWorkMilliseconds { get; private set; }
    public double LastCaptureElapsedMilliseconds { get; private set; }
    public double LastBuildMilliseconds { get; private set; }
    private void RecordTiming()
    {
        LastCaptureWorkMilliseconds = working; LastCaptureElapsedMilliseconds = buildStarted - started;
        LastBuildMilliseconds = clock() - buildStarted;
    }

    public CatalogCache(CatalogLimits? limits = null,
        Func<IReadOnlyList<CatalogFact>, CancellationToken, Task<CatalogSnapshot>>? build = null, Func<double>? milliseconds = null,
        int retries = 0, double retrySeconds = 3)
    {
        if (retries < 0 || retries > 10 || double.IsNaN(retrySeconds) || retrySeconds < 0 || retrySeconds > 60) throw new ArgumentOutOfRangeException(nameof(retries));
        retry = new StageRetry(retries, retrySeconds, 1000);
        this.limits = limits ?? new CatalogLimits();
        this.build = build ?? ((facts, token) => Task.Run(() => CatalogSnapshot.Build(facts, this.limits, token), token));
        clock = milliseconds ?? (() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    }

    public void BeginSession(bool isActive)
    {
        active = isActive;
        Invalidate();
        MaximumStepMilliseconds = 0;
    }

    public void Invalidate()
    {
        revision++; Current = null; retry.Reset();
        cancellation?.Cancel();
        var cleanupError = ReleaseCapture();
        State = active ? CatalogState.Pending : CatalogState.Offline;
        Diagnostic = active ? "Loaded catalog inputs changed; facts pending." : "No active catalog session.";
        if (cleanupError != null && active) { State = CatalogState.Failed; Diagnostic = "Capture cleanup failed: " + cleanupError.Message; }
    }

    // Null from open means ObjectDB is not ready yet; null from its iterator is a cooperative yield.
    public void Tick(Func<IEnumerable<CatalogFact?>?> open)
    {
        if (worker != null)
        {
            if (!worker.IsCompleted)
            {
                if (State == CatalogState.Building && clock() - buildStarted >= limits.BuildSeconds * 1000) Fail("Catalog build deadline exceeded.");
                return; // Cancelled workers finish before another generation is admitted.
            }
            try
            {
                var candidate = worker.GetAwaiter().GetResult();
                if (active && State == CatalogState.Building && workerRevision == revision)
                {
                    if (clock() - buildStarted >= limits.BuildSeconds * 1000) Fail("Catalog build deadline exceeded.");
                    else { RecordTiming(); Current = candidate; State = CatalogState.FactsReady; Diagnostic = "Loaded facts ready; classification remains pending."; }
                }
            }
            catch (Exception error) { if (State == CatalogState.Building && workerRevision == revision) Fail(error.GetType().Name + ": " + error.Message); }
            finally { worker = null; cancellation!.Dispose(); cancellation = null; }
        }
        if (!active || State == CatalogState.FactsReady) return;
        if (State == CatalogState.Failed)
        {
            if (!retry.Take(clock())) return;
            State = CatalogState.Pending; Diagnostic = "Trying again after a failure; facts pending.";
        }
        try
        {
            var sliceStart = clock();
            if (State == CatalogState.Pending)
            {
                if (retry.Scheduled && !retry.Take(sliceStart)) return;
                var source = open();
                if (source == null) return;
                started = sliceStart; working = 0; rows = new List<CatalogFact>(); bytes = items = recipes = 0;
                reader = source.GetEnumerator(); State = CatalogState.Capturing;
            }
            if (working >= limits.BuildSeconds * 1000) { Fail($"Catalog capture deadline exceeded ({limits.BuildSeconds:0.#} s of capture work)."); return; }
            if (sliceStart - started >= CaptureWallMilliseconds) { Fail("Catalog capture deadline exceeded (10 min since the capture started)."); return; }
            for (var step = 0; step < limits.StepsPerTick && clock() - sliceStart < limits.SliceMilliseconds; step++)
            {
                var stepStart = clock();
                var more = reader!.MoveNext();
                if (more && reader.Current != null)
                {
                    CatalogSnapshot.CheckBounds(reader.Current, limits, rows!.Count + 1, ref bytes, ref items, ref recipes);
                    rows.Add(reader.Current);
                }
                MaximumStepMilliseconds = Math.Max(MaximumStepMilliseconds, clock() - stepStart);
                if (!more)
                {
                    var detached = rows!.AsReadOnly();
                    var cleanupError = ReleaseCapture();
                    if (cleanupError != null) throw cleanupError;
                    cancellation = new CancellationTokenSource(); workerRevision = revision; State = CatalogState.Building;
                    buildStarted = clock();
                    worker = build(detached, cancellation.Token) ?? throw new InvalidOperationException("Catalog worker was not created.");
                    break;
                }
            }
            working += clock() - sliceStart;
        }
        catch (Exception error) { Fail(error.GetType().Name + ": " + error.Message); }
    }

    private Exception? ReleaseCapture()
    {
        var old = reader; reader = null; rows = null;
        try { old?.Dispose(); return null; }
        catch (Exception error) { return error; }
    }
    private void Fail(string message)
    {
        cancellation?.Cancel();
        if (worker == null) { cancellation?.Dispose(); cancellation = null; }
        var cleanupError = ReleaseCapture(); Current = null; State = CatalogState.Failed;
        Diagnostic = message + (cleanupError == null ? "" : " Cleanup: " + cleanupError.Message);
        if (!active || cleanupError != null) { retry.Hold(); return; }
        Diagnostic += retry.Schedule(clock());
        if (!retry.Slow) State = CatalogState.Pending; // Quick retry; a slow one waits in Failed, shown as given up (D087).
    }
    public void Dispose()
    {
        BeginSession(false);
        // A detached worker may still finish. Observe its fault and release its token without blocking Unity.
        if (worker != null)
        {
            var owned = cancellation!;
            _ = worker.ContinueWith(task => { _ = task.Exception; owned.Dispose(); }, TaskScheduler.Default);
            worker = null; cancellation = null;
        }
    }
}
