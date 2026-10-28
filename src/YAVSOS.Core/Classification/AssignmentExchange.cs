using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Classification;

public sealed class AssignmentPublication
{
    public AuthoritativeAssignmentSnapshot Snapshot { get; }
    public string Wire { get; }
    public AssignmentPublication(AuthoritativeAssignmentSnapshot snapshot, string wire)
    { Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot)); Wire = wire ?? throw new ArgumentNullException(nameof(wire)); }
}

/// <summary>One serial detached codec worker, one latest input, no network or mutation authority.</summary>
public sealed class AssignmentExchange : IDisposable
{
    private readonly Func<Func<AssignmentPublication>, CancellationToken, Task<AssignmentPublication>> schedule;
    private readonly Func<double> clock;
    private readonly double deadline;
    private CatalogSnapshot? catalog;
    private CategoryPolicy? policy;
    private ClassifiedCatalog? classified;
    private string wire = "";
    private bool server, active, pending, disposed;
    private long revision, workerRevision;
    private double started;
    private Task<AssignmentPublication>? worker;
    private CancellationTokenSource? cancellation;
    public AssignmentPublication? Current { get; private set; }
    public string Diagnostic { get; private set; } = "Authoritative assignments pending.";
    /// <summary>The latest input was rejected or exceeded its deadline; cleared by the next input.</summary>
    public bool Faulted { get; private set; }
    public long Revision => revision;
    public AssignmentExchange(Func<Func<AssignmentPublication>, CancellationToken, Task<AssignmentPublication>>? schedule = null,
        Func<double>? clock = null, double deadlineSeconds = 30)
    {
        if (deadlineSeconds <= 0 || deadlineSeconds > 300 || double.IsNaN(deadlineSeconds)) throw new ArgumentOutOfRangeException(nameof(deadlineSeconds));
        this.schedule = schedule ?? ((work, token) => Task.Run(work, token));
        this.clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        deadline = deadlineSeconds;
    }
    public void BeginSession(bool enabled)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AssignmentExchange));
        active = enabled; catalog = null; policy = null; classified = null; wire = ""; Invalidate();
    }
    public void Invalidate()
    {
        revision++; Current = null; Faulted = false; pending = active && catalog != null && policy != null && (server || wire.Length != 0); cancellation?.Cancel();
        Diagnostic = "Authoritative assignments pending.";
    }
    public void SetInputs(bool isServer, CatalogSnapshot? nextCatalog, CategoryPolicy? nextPolicy,
        ClassifiedCatalog? nextClassified = null, string payload = "")
    {
        if (disposed) return;
        if (server == isServer && ReferenceEquals(catalog, nextCatalog) && ReferenceEquals(policy, nextPolicy) &&
            ReferenceEquals(classified, nextClassified) && wire == payload) return;
        Invalidate(); server = isServer; catalog = nextCatalog; policy = nextPolicy; classified = nextClassified; wire = payload;
        pending = active && catalog != null && policy != null && (server || wire.Length != 0);
    }
    public void Tick()
    {
        if (worker != null)
        {
            if (clock() - started >= deadline && workerRevision == revision)
            { Invalidate(); pending = false; Faulted = true; Diagnostic = "Authoritative assignment worker deadline exceeded; readiness blocked."; }
            if (!worker.IsCompleted) return;
            var job = worker; worker = null;
            var accepted = !disposed && active && workerRevision == revision && !cancellation!.IsCancellationRequested;
            cancellation!.Dispose(); cancellation = null;
            try
            {
                var result = job.GetAwaiter().GetResult();
                if (accepted) { Current = result ?? throw new InvalidOperationException("Assignment worker returned no result."); Diagnostic = "Authoritative assignments validated."; }
            }
            catch (Exception error)
            { if (accepted) { Current = null; Faulted = true; Diagnostic = "Authoritative assignments rejected; readiness blocked: " + error.Message; } }
        }
        if (!disposed && active && pending && worker == null) StartWorker();
    }
    private void StartWorker()
    {
        pending = false; cancellation = new CancellationTokenSource(); workerRevision = revision; started = clock();
        var token = cancellation.Token; var capturedCatalog = catalog!; var capturedPolicy = policy!;
        var capturedClassified = classified; var capturedWire = wire; var capturedServer = server;
        try
        {
            worker = schedule(() => {
                token.ThrowIfCancellationRequested();
                var snapshot = capturedServer
                    ? capturedClassified != null ? AuthoritativeAssignmentSnapshot.Create(capturedCatalog, capturedClassified, capturedPolicy, token)
                        : AuthoritativeAssignmentSnapshot.CreateIdentity(capturedCatalog, capturedPolicy, token)
                    : AuthoritativeAssignmentsCodec.ParseWire(capturedWire, capturedPolicy, cancellation: token);
                var payload = capturedServer ? AuthoritativeAssignmentsCodec.ToWire(snapshot, token) : capturedWire;
                token.ThrowIfCancellationRequested(); return new AssignmentPublication(snapshot, payload);
            }, token) ?? throw new InvalidOperationException("Assignment scheduler returned no task.");
        }
        catch (Exception error)
        { cancellation.Dispose(); cancellation = null; Faulted = true; Diagnostic = "Authoritative assignments rejected; readiness blocked: " + error.Message; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; active = false; Invalidate();
        if (worker != null)
        {
            var owned = cancellation!;
            _ = worker.ContinueWith(task => { _ = task.Exception; owned.Dispose(); }, TaskScheduler.Default);
            worker = null; cancellation = null;
        }
    }
}
