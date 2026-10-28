using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Classification;

public enum ClassificationState { Offline, Pending, Building, Ready, Failed }

/// <summary>Main-thread owned, serial detached worker, immediate stale-result withdrawal.</summary>
public sealed class ClassificationCache : IDisposable
{
    private readonly Func<CatalogSnapshot, ClassificationRuleSet, ClassificationSourceSnapshot?, CancellationToken, Task<ClassifiedCatalog>>? build;
    private readonly Func<Func<ClassifiedCatalog>, CancellationToken, Task<ClassifiedCatalog>> schedule;
    private readonly Func<double> clock;
    private readonly double deadlineMilliseconds;
    private CatalogSnapshot? catalog;
    private ClassificationRuleSet? rules;
    private CategoryPolicy? policy;
    private ClassificationSourceSnapshot? sources;
    private Task<ClassifiedCatalog>? worker;
    private CancellationTokenSource? cancellation;
    private long revision, workerRevision;
    private bool active, disposed;
    private double started;
    public ClassificationState State { get; private set; }
    public ClassifiedCatalog? Current { get; private set; }
    public long Revision => revision;
    public string Diagnostic { get; private set; } = "No active classification session.";
    public ClassificationCache(Func<CatalogSnapshot, ClassificationRuleSet, ClassificationSourceSnapshot?, CancellationToken, Task<ClassifiedCatalog>>? build = null,
        Func<double>? milliseconds = null, double deadlineMilliseconds = 30000,
        Func<Func<ClassifiedCatalog>, CancellationToken, Task<ClassifiedCatalog>>? schedule = null)
    {
        if (double.IsNaN(deadlineMilliseconds) || double.IsInfinity(deadlineMilliseconds) || deadlineMilliseconds <= 0 || deadlineMilliseconds > 300000)
            throw new ArgumentOutOfRangeException(nameof(deadlineMilliseconds));
        this.deadlineMilliseconds = deadlineMilliseconds;
        this.build = build; this.schedule = schedule ?? ((work, token) => Task.Run(work, token));
        clock = milliseconds ?? (() => Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));
    }
    public void BeginSession(bool isActive)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ClassificationCache));
        active = isActive; catalog = null; rules = null; policy = null; sources = null; Invalidate();
    }
    public void SetInputs(CatalogSnapshot? nextCatalog, ClassificationRuleSet? nextRules, ClassificationSourceSnapshot? nextSources = null)
        => SetInputsCore(nextCatalog, nextRules, null, nextSources);
    /// <summary>Queues immutable policy data; rules are constructed only on the detached worker.</summary>
    public void SetPolicyInputs(CatalogSnapshot? nextCatalog, CategoryPolicy? nextPolicy, ClassificationSourceSnapshot? nextSources = null)
        => SetInputsCore(nextCatalog, null, nextPolicy, nextSources);
    private void SetInputsCore(CatalogSnapshot? nextCatalog, ClassificationRuleSet? nextRules, CategoryPolicy? nextPolicy, ClassificationSourceSnapshot? nextSources)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ClassificationCache));
        if (nextCatalog == null || nextRules == null && nextPolicy == null) { nextCatalog = null; nextRules = null; nextPolicy = null; nextSources = null; }
        if (nextSources != null && nextSources.CatalogFingerprint != nextCatalog!.Fingerprint)
            throw new ArgumentException("Source receipt does not match catalog.");
        if (catalog?.Fingerprint == nextCatalog?.Fingerprint && rules?.Fingerprint == nextRules?.Fingerprint &&
            policy?.Fingerprint == nextPolicy?.Fingerprint && sources?.Fingerprint == nextSources?.Fingerprint) return;
        catalog = nextCatalog; rules = nextRules; policy = nextPolicy; sources = nextSources; Invalidate();
    }
    public bool TrySetSources(long expectedRevision, ClassificationSourceSnapshot next)
    {
        if (disposed || !active || expectedRevision != revision || catalog == null || rules == null && policy == null || next == null || next.CatalogFingerprint != catalog.Fingerprint) return false;
        SetInputsCore(catalog, rules, policy, next); return true;
    }
    public void Invalidate()
    {
        revision++; Current = null; cancellation?.Cancel();
        State = active ? ClassificationState.Pending : ClassificationState.Offline;
        Diagnostic = active ? "Classification inputs pending." : "No active classification session.";
    }
    public void Tick()
    {
        if (disposed) return;
        if (worker != null)
        {
            if (!worker.IsCompleted)
            {
                if (State == ClassificationState.Building && clock() - started >= deadlineMilliseconds) Fail("Classification deadline exceeded.");
                return;
            }
            try
            {
                var result = worker.GetAwaiter().GetResult();
                if (active && State == ClassificationState.Building && workerRevision == revision)
                {
                    if (clock() - started >= deadlineMilliseconds) Fail("Classification deadline exceeded.");
                    else { Current = result ?? throw new InvalidOperationException("Empty classification worker result."); State = ClassificationState.Ready; Diagnostic = "Read-only classification ready; automation authority remains pending."; }
                }
            }
            catch (Exception error) { if (State == ClassificationState.Building && workerRevision == revision) Fail(error.GetType().Name + ": " + error.Message); }
            finally { worker = null; cancellation!.Dispose(); cancellation = null; }
        }
        if (!active || State != ClassificationState.Pending || catalog == null || rules == null && policy == null) return;
        cancellation = new CancellationTokenSource(); workerRevision = revision; started = clock(); State = ClassificationState.Building;
        try
        {
            worker = StartBuild(catalog, rules, policy, sources, cancellation.Token);
            if (worker == null) throw new InvalidOperationException("Classification worker not created.");
        }
        catch (Exception error) { Fail(error.GetType().Name + ": " + error.Message); cancellation.Dispose(); cancellation = null; }
    }
    // Keep closure creation outside Tick so even cancelled/running/ready idle ticks allocate nothing.
    // Parameters capture this generation, never mutable main-thread inputs inside the worker.
    private Task<ClassifiedCatalog> StartBuild(CatalogSnapshot capturedCatalog, ClassificationRuleSet? capturedRules,
        CategoryPolicy? capturedPolicy, ClassificationSourceSnapshot? capturedSources, CancellationToken token)
        => capturedPolicy != null
            ? schedule(() => {
                token.ThrowIfCancellationRequested();
                var resolved = ClassificationRuleSet.FromSchema1(capturedPolicy);
                return ClassificationPipeline.Build(capturedCatalog, resolved, capturedSources, token);
            }, token)
            : build != null ? build(capturedCatalog, capturedRules!, capturedSources, token)
                : schedule(() => ClassificationPipeline.Build(capturedCatalog, capturedRules!, capturedSources, token), token);
    private void Fail(string message) { cancellation?.Cancel(); Current = null; State = ClassificationState.Failed; Diagnostic = message; }
    public void Dispose()
    {
        if (disposed) return;
        BeginSession(false); disposed = true;
        if (worker != null)
        {
            var owned = cancellation!;
            _ = worker.ContinueWith(task => { _ = task.Exception; owned.Dispose(); }, TaskScheduler.Default);
            worker = null; cancellation = null;
        }
    }
}
