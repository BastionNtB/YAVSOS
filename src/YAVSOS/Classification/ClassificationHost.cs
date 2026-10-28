using System;
using System.Collections.Generic;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Classification;

/// <summary>No Unity object scanning or worker waits; never opens PolicyLifecycle catalog authority.</summary>
internal sealed class ClassificationHost : IDisposable
{
    private readonly ClassificationCache cache;
    private readonly Action<string> info, warning;
    private readonly PolicyLifecycle policy;
    private readonly Func<CatalogSnapshot?> getCatalog;
    private readonly Func<ClassificationSourceSnapshot?>? getSources;
    private readonly bool requireSources;
    private CatalogSnapshot? lastCatalog;
    private CategoryPolicy? lastPolicy;
    private ClassificationSourceSnapshot? sources;
    private ClassificationState reported;
    private readonly HashSet<string> skippedReported = new(StringComparer.Ordinal);
    internal ClassifiedCatalog? Current => InputsAreCurrent() ? cache.Current : null;
    internal long Revision => cache.Revision;
    /// <summary>The classification build failed for its current inputs (D087): the reason, else null.</summary>
    internal string? GaveUp => cache.State == ClassificationState.Failed ? cache.Diagnostic : null;
    internal ClassificationHost(Action<string> info, Action<string> warning, PolicyLifecycle policy, Func<CatalogSnapshot?> getCatalog, ClassificationCache? cache = null,
        Func<ClassificationSourceSnapshot?>? getSources = null, bool requireSources = false)
    { this.requireSources = requireSources; this.info = info; this.warning = warning; this.policy = policy; this.getCatalog = getCatalog; this.getSources = getSources; this.cache = cache ?? new ClassificationCache(); }
    internal void BeginSession(bool active)
    {
        lastCatalog = null; lastPolicy = null; sources = null;
        cache.BeginSession(active); reported = cache.State;
    }
    internal void InvalidateCatalog()
    {
        lastCatalog = null; sources = null; cache.SetInputs(null, null);
    }
    internal void InvalidateSources() { sources = null; cache.SetInputs(null, null); }
    /// <summary>Publishes sources when no live source host owns them (the detached tests' path).</summary>
    internal bool TrySetSources(long expectedRevision, ClassificationSourceSnapshot next)
    {
        if (getSources != null) return false; // Live source host owns freshness and withdrawal.
        if (!InputsAreCurrent()) return false;
        if (!cache.TrySetSources(expectedRevision, next)) return false;
        sources = next; return true;
    }
    private bool InputsAreCurrent() => policy.PolicyReady && lastPolicy != null && lastCatalog != null &&
        ReferenceEquals(policy.Effective, lastPolicy) && ReferenceEquals(getCatalog(), lastCatalog) &&
        (getSources == null || ReferenceEquals(getSources(), sources)) && (!requireSources || sources != null);
    internal void Update()
    {
        var catalog = getCatalog();
        if (!ReferenceEquals(catalog, lastCatalog)) { lastCatalog = catalog; sources = null; }
        if (getSources != null) sources = getSources();
        if (!policy.PolicyReady || catalog == null || requireSources && sources == null)
        {
            cache.SetInputs(null, null); cache.Tick(); return;
        }
        var effective = policy.Effective;
        lastPolicy = effective;
        // Effective policies are immutable: rule construction belongs to the serial worker.
        cache.SetPolicyInputs(catalog, effective, sources); cache.Tick();
        if (reported == cache.State) return;
        reported = cache.State;
        if (reported == ClassificationState.Ready)
        {
            var result = cache.Current!;
            info($"Read-only classification: {result.Index.Items.Count} player-item profiles, {result.ProfileScope.Excluded.Count} excluded definitions, " +
                $"SHA-256 {result.Index.Fingerprint}.");
            // D087: rows that failed only themselves (D086 D3), named once per session.
            foreach (var row in result.Index.Skipped)
                if (skippedReported.Add(row)) warning("Classification skipped " + row + ". Only this row is affected; report it to the mod that adds the item.");
        }
        else if (reported == ClassificationState.Failed) warning("Classification rejected: " + cache.Diagnostic);
    }
    public void Dispose() => cache.Dispose();
}
