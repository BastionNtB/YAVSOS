using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>Trusted adapter receipt bound to loaded facts.</summary>
public sealed class ClassificationSourceSnapshot
{
    public string CatalogFingerprint { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<MaterialRoute> Routes { get; }
    public IReadOnlyList<ItemClassificationEvidence> SemanticEvidence { get; }
    public ItemProfileScope ProfileScope { get; }
    public int ScalarBytes { get; }
    private ClassificationSourceSnapshot(CatalogSnapshot catalog, string fingerprint,
        MaterialRoute[] routes, ItemClassificationEvidence[] semanticEvidence, ItemProfileScope profileScope, CancellationToken cancellation)
    {
        ProfileScope = profileScope;
        ScalarBytes = ValidateCompact(routes, semanticEvidence, cancellation);
        CatalogFingerprint = catalog.Fingerprint; Fingerprint = fingerprint;
        Routes = Array.AsReadOnly(routes); SemanticEvidence = Array.AsReadOnly(semanticEvidence);
    }
    internal static ClassificationSourceSnapshot FromLoadedReceipt(CatalogSnapshot catalog, string fingerprint,
        IEnumerable<MaterialRoute> routes,
        IEnumerable<ItemClassificationEvidence> semanticEvidence, ItemProfileScope profileScope, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return new ClassificationSourceSnapshot(catalog, fingerprint,
            routes.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(), semanticEvidence.ToArray(), profileScope, cancellation);
    }
    private static int ValidateCompact(IReadOnlyList<MaterialRoute> routes,
        IReadOnlyList<ItemClassificationEvidence> semanticEvidence, CancellationToken cancellation)
    {
        if (routes.Count > 65536)
            throw new InvalidDataException("Compact classification source bounds exceeded.");
        if (semanticEvidence.Count > 16384) throw new InvalidDataException("Compact semantic evidence bounds exceeded.");
        var budget = new ScalarBudget(cancellation);
        foreach (var item in semanticEvidence)
        {
            budget.Charge(item.Prefab);
            foreach (var membership in item.Memberships)
            { budget.Charge(membership.Category); budget.Charge(membership.Source); budget.Charge(membership.Reason); }
            foreach (var facet in item.Facets)
            { budget.Charge(facet.Dimension); budget.Charge(facet.Value); budget.Charge(facet.Source); budget.Charge(facet.Reason); }
        }
        foreach (var route in routes)
        {
            budget.Charge(route.Id);
            foreach (var input in route.Inputs)
            { budget.Charge(input.Input); budget.Charge(input.Output); budget.Charge(input.Kind); budget.Charge(input.Producer); }
        }
        return budget.Bytes;
    }
    internal sealed class ScalarBudget
    {
        internal const int MaximumBytes = 32 * 1024 * 1024;
        private readonly CancellationToken cancellation;
        internal int Bytes { get; private set; }
        internal ScalarBudget(CancellationToken cancellation) { this.cancellation = cancellation; }
        internal void Charge(string value) => ChargeBytes(System.Text.Encoding.UTF8.GetByteCount(value));
        internal void ChargeBytes(int count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count < 0 || count > MaximumBytes - Bytes)
                throw new InvalidDataException("Compact classification source scalar bound exceeded.");
            Bytes += count;
        }
    }
}

public sealed class ClassifiedCatalog
{
    public string CatalogFingerprint { get; }
    public string PolicyFingerprint { get; }
    public ClassificationIndex Index { get; }
    public ItemProfileScope ProfileScope { get; }
    public string SourceFingerprint { get; }
    internal ClassifiedCatalog(ClassificationIndex index, string sourceFingerprint, string catalogFingerprint, string policyFingerprint, ItemProfileScope profileScope)
    { ProfileScope = profileScope; Index = index; SourceFingerprint = sourceFingerprint;
        CatalogFingerprint = catalogFingerprint; PolicyFingerprint = policyFingerprint; }
}

/// <summary>Pure worker pipeline. Loaded evidence never grants classification or inventory authority.</summary>
public static class ClassificationPipeline
{
    public const string Version = "yavsos-classification-pipeline-5";
    public static ClassifiedCatalog Build(CatalogSnapshot catalog, ClassificationRuleSet rules,
        ClassificationSourceSnapshot? sources = null, CancellationToken cancellation = default, int maxClassificationWork = 5000000)
    {
        if (catalog == null || rules == null) throw new ArgumentNullException(nameof(catalog));
        cancellation.ThrowIfCancellationRequested();
        if (sources != null && sources.CatalogFingerprint != catalog.Fingerprint)
            throw new InvalidDataException("Source receipt is bound to different loaded facts.");
        var sourceFingerprint = sources?.Fingerprint ?? "none";
        var scope = sources?.ProfileScope ?? ItemProfileScope.Build(catalog, cancellation: cancellation);
        var identity = ClassificationValidation.Hash(writer => { writer.Write(Version); writer.Write(sourceFingerprint); });
        var evidence = sources?.SemanticEvidence ?? Array.Empty<ItemClassificationEvidence>();
        var index = ClassificationIndex.Build(catalog, rules, evidence, identity, maxClassificationWork, cancellation, scope);
        cancellation.ThrowIfCancellationRequested();
        return new ClassifiedCatalog(index, sourceFingerprint, catalog.Fingerprint, rules.PolicyFingerprint, scope);
    }
}
