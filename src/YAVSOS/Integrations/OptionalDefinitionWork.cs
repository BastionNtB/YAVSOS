using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Integrations;

/// <summary>
/// Game-thread-owned coordinator for detached optional-definition compaction. The owner outlives
/// capture adapters; disposing a capture cancels its work without admitting an overlapping worker.
/// Null rows let the source cache enforce its existing slice and whole-capture deadline.
/// </summary>
internal sealed class OptionalDefinitionWork : IDisposable
{
    private sealed class Job
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<IReadOnlyList<LoadedSourceFact>> Task = null!;
    }
    private readonly Func<CatalogSnapshot, OptionalCapability, IReadOnlyList<OptionalDefinitionObservation>,
        CancellationToken, Task<IReadOnlyList<LoadedSourceFact>>> build;
    private Job? job;
    private bool capturing, disposed;

    internal OptionalDefinitionWork(Func<CatalogSnapshot, OptionalCapability, IReadOnlyList<OptionalDefinitionObservation>,
        CancellationToken, Task<IReadOnlyList<LoadedSourceFact>>>? build = null) =>
        this.build = build ?? ((catalog, capability, input, token) =>
            Task.Run(() => OptionalDefinitionEvidence.Build(catalog, capability, input, token), token));

    internal IEnumerable<LoadedSourceFact?> Capture(CatalogSnapshot catalog, OptionalCapability capability,
        IReadOnlyList<OptionalDefinitionObservation> observations)
    {
        CheckDisposed();
        if (capturing) throw new InvalidOperationException("Optional definition capture is already active.");
        capturing = true;
        Job? owned = null;
        try
        {
            // A cancelled generation retains the slot until its task terminates, including faults.
            while (job != null)
            {
                if (!job.Task.IsCompleted) { yield return null; CheckDisposed(); continue; }
                ReleaseCompleted();
            }
            CheckDisposed();
            // Scalars are immutable. Copy only the bounded references on the caller thread, before
            // Task.Run; an over-bound input retains the builder's existing whole-adapter gap result.
            var count = capability.Supported ? Math.Min(observations.Count, OptionalDefinitionEvidence.MaximumObservations + 1) : 0;
            var input = new OptionalDefinitionObservation[count];
            for (var i = 0; i < count; i++) input[i] = observations[i];
            owned = new Job();
            try
            {
                owned.Task = build(catalog, capability, input, owned.Cancellation.Token) ??
                    throw new InvalidOperationException("Optional definition worker was not created.");
            }
            catch { owned.Cancellation.Dispose(); owned = null; throw; }
            job = owned;
            while (!owned.Task.IsCompleted) { yield return null; CheckDisposed(); }
            CheckDisposed();
            var rows = owned.Task.GetAwaiter().GetResult() ??
                throw new InvalidOperationException("Optional definition worker returned no facts.");
            ReleaseCompleted(); owned = null;
            foreach (var row in rows) { CheckDisposed(); yield return row; }
        }
        finally
        {
            if (owned != null && ReferenceEquals(job, owned)) owned.Cancellation.Cancel();
            capturing = false;
        }
    }

    private void ReleaseCompleted()
    {
        var old = job!; job = null;
        _ = old.Task.Exception; // Observe a retired fault without publishing that generation.
        old.Cancellation.Dispose();
    }
    private void CheckDisposed()
    { if (disposed) throw new ObjectDisposedException(nameof(OptionalDefinitionWork)); }

    // Called each game update, including offline/failed sessions. An abandoned worker may
    // finish without another capture; release its result/fault and CTS on the owning thread.
    internal void ReapRetired()
    {
        CheckDisposed();
        if (!capturing && job != null && job.Task.IsCompleted) ReleaseCompleted();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        var old = job; job = null;
        if (old == null) return;
        old.Cancellation.Cancel();
        _ = old.Task.ContinueWith(task => { _ = task.Exception; old.Cancellation.Dispose(); }, TaskScheduler.Default);
    }
}
