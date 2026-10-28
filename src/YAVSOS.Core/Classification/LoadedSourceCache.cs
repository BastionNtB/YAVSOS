using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

public enum LoadedSourceState { Offline, Pending, Capturing, Building, Ready, Failed }

/// <summary>
/// Single main-thread capture and single detached worker; invalidation withdraws accepted evidence immediately. A failed
/// capture or build is tried again a bounded number of times after a pause: another mod can still be changing its
/// registries (EpicLoot applying the server's files on join, runtime 2026-10-21), and no evidence is accepted from the
/// failed attempt.
/// </summary>
public sealed class LoadedSourceCache : IDisposable
{
    private readonly LoadedSourceLimits limits;
    private readonly Func<CatalogSnapshot, IReadOnlyList<LoadedSourceFact>, CancellationToken, Task<LoadedSourceReceipt>> build;
    private readonly Func<double> clock;
    private CatalogSnapshot? catalog;
    private IEnumerator<LoadedSourceFact?>? reader;
    private List<LoadedSourceFact>? rows;
    private Task<LoadedSourceReceipt>? worker;
    private CancellationTokenSource? cancellation;
    private long revision, workerRevision;
    private CatalogSnapshot? workerCatalog;
    private bool active;
    private double started;
    private int bytes, attempts;
    private readonly int retries;
    private readonly double retryMilliseconds;
    private double retryAt;
    public LoadedSourceState State { get; private set; }
    public LoadedSourceReceipt? Current { get; private set; }
    public string Diagnostic { get; private set; } = "No active source session.";
    public long Revision => revision;
    public double MaximumStepMilliseconds { get; private set; }
    /// <summary>Failed attempts since the inputs last changed; the last one is in <see cref="Diagnostic"/>.</summary>
    public int Retries => attempts;
    public LoadedSourceCache(LoadedSourceLimits? limits = null,
        Func<CatalogSnapshot, IReadOnlyList<LoadedSourceFact>, CancellationToken, Task<LoadedSourceReceipt>>? build = null,
        Func<double>? milliseconds = null, int retries = 0, double retrySeconds = 3)
    {
        if (retries < 0 || retries > 10 || double.IsNaN(retrySeconds) || retrySeconds < 0 || retrySeconds > 60) throw new ArgumentOutOfRangeException(nameof(retries));
        this.retries = retries; retryMilliseconds = retrySeconds * 1000;
        this.limits = limits ?? new LoadedSourceLimits();
        this.build = build ?? ((loaded, facts, token) => Task.Run(() => LoadedSourceReceipt.Build(loaded, facts, this.limits, token), token));
        clock = milliseconds ?? (() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    }
    public void BeginSession(bool isActive)
    { active = isActive; catalog = null; Invalidate(); MaximumStepMilliseconds = 0; }
    public void SetCatalog(CatalogSnapshot? next)
    {
        if (ReferenceEquals(catalog, next)) return;
        catalog = next; Invalidate();
    }
    public void Invalidate()
    {
        revision++; Current = null; cancellation?.Cancel(); attempts = 0; retryAt = 0;
        var cleanup = ReleaseCapture(); State = active ? LoadedSourceState.Pending : LoadedSourceState.Offline;
        Diagnostic = active ? "Fresh loaded source inputs pending." : "No active source session.";
        if (cleanup != null && active) { State = LoadedSourceState.Failed; Diagnostic = "Source cleanup failed: " + cleanup.Message; }
    }
    // Open/iterator are game-thread owned. Null Open waits for registries; null rows yield cooperatively.
    public void Tick(Func<IEnumerable<LoadedSourceFact?>?> open)
    {
        if (worker != null)
        {
            if (!worker.IsCompleted)
            {
                if (State == LoadedSourceState.Building && clock() - started >= limits.BuildSeconds * 1000)
                    Fail("Loaded source build deadline exceeded.");
                return;
            }
            try
            {
                var candidate = worker.GetAwaiter().GetResult();
                if (active && State == LoadedSourceState.Building && workerRevision == revision && ReferenceEquals(workerCatalog, catalog))
                {
                    if (clock() - started >= limits.BuildSeconds * 1000) Fail("Loaded source build deadline exceeded.");
                    else if (candidate.Snapshot.CatalogFingerprint != catalog!.Fingerprint) Fail("Source worker returned a different catalog binding.");
                    else { Current = candidate; State = LoadedSourceState.Ready; Diagnostic = "Fresh bounded loaded-definition receipt ready; coverage is partial."; }
                }
            }
            catch (Exception error) { if (State == LoadedSourceState.Building && workerRevision == revision) Fail(error.GetType().Name + ": " + error.Message); }
            finally { worker = null; workerCatalog = null; cancellation!.Dispose(); cancellation = null; }
        }
        if (!active || catalog == null || State is LoadedSourceState.Ready or LoadedSourceState.Failed) return;
        try
        {
            var sliceStart = clock();
            if (State == LoadedSourceState.Pending)
            {
                if (sliceStart < retryAt) return;
                var source = open(); if (source == null) return;
                started = sliceStart; rows = new List<LoadedSourceFact>(); bytes = 0;
                reader = source.GetEnumerator(); State = LoadedSourceState.Capturing;
            }
            if (clock() - started >= limits.BuildSeconds * 1000) { Fail("Loaded source build deadline exceeded."); return; }
            for (var step = 0; step < limits.StepsPerTick && clock() - sliceStart < limits.SliceMilliseconds; step++)
            {
                var stepStart = clock(); var more = reader!.MoveNext();
                if (more && reader.Current != null)
                {
                    if (rows!.Count >= limits.MaxFacts) throw new InvalidOperationException("Loaded source fact limit exceeded.");
                    bytes = checked(bytes + reader.Current.ScalarBytes);
                    if (bytes > limits.MaxScalarBytes) throw new InvalidOperationException("Loaded source scalar limit exceeded.");
                    rows.Add(reader.Current);
                }
                MaximumStepMilliseconds = Math.Max(MaximumStepMilliseconds, clock() - stepStart);
                if (!more)
                {
                    var detached = rows!.AsReadOnly(); var cleanup = ReleaseCapture(); if (cleanup != null) throw cleanup;
                    cancellation = new CancellationTokenSource(); workerRevision = revision; workerCatalog = catalog;
                    State = LoadedSourceState.Building;
                    worker = build(catalog, detached, cancellation.Token) ?? throw new InvalidOperationException("Source worker was not created.");
                    break;
                }
            }
        }
        catch (Exception error) { Fail(error.GetType().Name + ": " + error.Message); }
    }
    private Exception? ReleaseCapture()
    {
        var old = reader; reader = null; rows = null;
        try { old?.Dispose(); return null; } catch (Exception error) { return error; }
    }
    private void Fail(string message)
    {
        cancellation?.Cancel(); if (worker == null) { cancellation?.Dispose(); cancellation = null; }
        var cleanup = ReleaseCapture(); Current = null; State = LoadedSourceState.Failed;
        Diagnostic = message + (cleanup == null ? "" : " Cleanup: " + cleanup.Message);
        if (!active || cleanup != null || attempts >= retries) return;
        attempts++; retryAt = clock() + retryMilliseconds; State = LoadedSourceState.Pending;
        Diagnostic += $" Trying again in {retryMilliseconds / 1000:0.#} s ({attempts} of {retries}).";
    }
    public void Dispose()
    {
        BeginSession(false);
        if (worker != null)
        {
            var owned = cancellation!;
            _ = worker.ContinueWith(task => { _ = task.Exception; owned.Dispose(); }, TaskScheduler.Default);
            worker = null; workerCatalog = null; cancellation = null;
        }
    }
}
