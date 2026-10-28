using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>D034 output boundary. Excluded definitions remain in the evidence catalog.</summary>
public sealed class ItemProfileScope
{
    public const string Version = "yavsos-item-profile-scope-1";
    public IReadOnlyDictionary<string, string> Excluded { get; }
    public string Fingerprint { get; }
    public string CatalogFingerprint { get; }
    public bool AcquisitionScanComplete { get; }

    private ItemProfileScope(CatalogSnapshot catalog, SortedDictionary<string, string> excluded, bool complete)
        : this(catalog.Fingerprint, excluded, complete) { }
    private ItemProfileScope(string catalogFingerprint, SortedDictionary<string, string> excluded, bool complete)
    {
        CatalogFingerprint = catalogFingerprint;
        Excluded = new ReadOnlyDictionary<string, string>(excluded);
        AcquisitionScanComplete = complete;
        Fingerprint = ClassificationValidation.Hash(writer => {
            writer.Write(Version); writer.Write(catalogFingerprint); writer.Write(complete); writer.Write(excluded.Count);
            foreach (var pair in excluded) { writer.Write(pair.Key); writer.Write(pair.Value); }
        });
    }

    public static ItemProfileScope Build(CatalogSnapshot catalog, IEnumerable<LoadedSourceFact>? observations = null,
        CancellationToken cancellation = default)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        var rows = ClassificationValidation.Bounded(observations ?? Array.Empty<LoadedSourceFact>(), 65536);
        var preserve = new HashSet<string>(StringComparer.Ordinal);
        var consumers = new HashSet<string>(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        var complete = false;
        foreach (var fact in catalog.Facts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fact.Kind is not (CatalogFactKind.Recipe or CatalogFactKind.Requirement)) continue;
            preserve.Add(fact.Value("input")); preserve.Add(fact.Value("output"));
        }
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            if (row == null) throw new InvalidDataException("Null scope observation.");
            if (row.Kind == LoadedSourceFactKind.ProfileScope)
            {
                if (row.Method == "complete")
                {
                    if (complete || row.Output.Length != 0 || !row.Eligible || row.Detail.Length != 64 ||
                        row.Detail.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                        throw new InvalidDataException("Invalid/duplicate complete scope receipt.");
                    complete = true;
                }
                else
                {
                    if (!catalog.Items.TryGetValue(row.Output, out var item))
                        throw new InvalidDataException("Scope observation references an unregistered item.");
                    if (!row.Eligible || row.Amount < 0) throw new InvalidDataException("Invalid scope observation.");
                    switch (row.Method)
                    {
                        case "preserve": preserve.Add(row.Output); break;
                        case "npc-consumer": consumers.Add(row.Output); break;
                        case "definition":
                            observed.Add(row.Output);
                            if (row.Amount != 0 || row.Input != item.Value("type")) conflicting.Add(row.Output);
                            break;
                        default: throw new InvalidDataException("Unsupported scope observation.");
                    }
                }
                continue;
            }
            // Disabled sources, semantic consumers and unresolved routes veto negative proof too.
            if (row.Kind == LoadedSourceFactKind.Coverage) continue;
            preserve.Add(row.Output); preserve.Add(row.Input);
        }
        var excluded = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in catalog.Items.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            if (item.Value("type") == "Customization") excluded.Add(item.Identity, "typed-customization");
            else if (complete && item.Value("hasIcon") == "False" && observed.Contains(item.Identity) && consumers.Contains(item.Identity) &&
                !conflicting.Contains(item.Identity) && !preserve.Contains(item.Identity))
                excluded.Add(item.Identity, "proven-npc-internal");
        }
        return new ItemProfileScope(catalog, excluded, complete);
    }

    /// <summary>The server's exclusions, bound to the server's catalog fingerprint; clients need not share that catalog (D042).</summary>
    internal static ItemProfileScope FromServer(string catalogFingerprint, SortedDictionary<string, string> excluded, bool complete,
        bool hasSourceReceipt)
    {
        foreach (var reason in excluded.Values)
            if (reason != "typed-customization" && (reason != "proven-npc-internal" || !complete || !hasSourceReceipt))
                throw new InvalidDataException("Invalid authoritative profile exclusion.");
        return new ItemProfileScope(catalogFingerprint, excluded, complete);
    }
}
