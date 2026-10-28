using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

// ProfileScope rows are read only by ItemProfileScope.Build. The loaded-source receipt rejects them; no capture produces them.
public enum LoadedSourceFactKind { RouteInput, Coverage, ItemUse, FacetEvidence, Acquisition, ProfileScope }

/// <summary>Only detached scalars. Disabled and unsupported facts still contribute to receipt identity.</summary>
public sealed class LoadedSourceFact
{
    public LoadedSourceFactKind Kind { get; }
    public string Identity { get; }
    public string Output { get; }
    public string Input { get; }
    public string Route { get; }
    public string Method { get; }
    public string Detail { get; }
    public int Amount { get; }
    public bool Eligible { get; }
    public int ScalarBytes { get; }
    public LoadedSourceFact(LoadedSourceFactKind kind, string identity, string output = "", string input = "",
        string route = "", string method = "", string detail = "", int amount = 1, bool eligible = true)
    {
        if (!Enum.IsDefined(typeof(LoadedSourceFactKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ClassificationValidation.Text(identity, 1024);
        foreach (var value in new[] { output, input, route, method })
        {
            if (value == null) throw new ArgumentException("Invalid source scalar.");
            if (value.Length != 0) ClassificationValidation.Text(value, 1024);
        }
        if (detail == null || detail.Length > 4096 || detail.Any(char.IsControl)) throw new ArgumentException("Invalid source detail.");
        _ = new UTF8Encoding(false, true).GetByteCount(detail);
        Kind = kind; Identity = identity; Output = output; Input = input; Route = route; Method = method;
        Detail = detail; Amount = amount; Eligible = eligible;
        ScalarBytes = new[] { identity, output, input, route, method, detail }.Sum(Encoding.UTF8.GetByteCount);
    }
}

public sealed class LoadedSourceLimits
{
    public int MaxFacts { get; }
    public int MaxScalarBytes { get; }
    public int StepsPerTick { get; }
    public double SliceMilliseconds { get; }
    public double BuildSeconds { get; }
    public LoadedSourceLimits(int maxFacts = 65536, int maxScalarBytes = 8 * 1024 * 1024,
        int stepsPerTick = 32, double sliceMilliseconds = 1, double buildSeconds = 30)
    {
        if (maxFacts < 1 || maxFacts > 65536 || maxScalarBytes < 1 || maxScalarBytes > 8 * 1024 * 1024 ||
            stepsPerTick < 1 || stepsPerTick > 256 || double.IsNaN(sliceMilliseconds) || sliceMilliseconds <= 0 || sliceMilliseconds > 10 ||
            double.IsNaN(buildSeconds) || buildSeconds <= 0 || buildSeconds > 900) throw new ArgumentOutOfRangeException(nameof(maxFacts));
        MaxFacts = maxFacts; MaxScalarBytes = maxScalarBytes; StepsPerTick = stepsPerTick;
        SliceMilliseconds = sliceMilliseconds; BuildSeconds = buildSeconds;
    }
}

/// <summary>Freshness is supplied by the main-thread host. This pure builder never accesses game objects.</summary>
public sealed class LoadedSourceReceipt
{
    public const string Version = "yavsos-loaded-sources-17";
    public ClassificationSourceSnapshot Snapshot { get; }
    public IReadOnlyList<LoadedSourceFact> Facts { get; }
    public IReadOnlyList<string> Coverage { get; }
    public int ScalarBytes { get; }
    private LoadedSourceReceipt(ClassificationSourceSnapshot snapshot, LoadedSourceFact[] facts, string[] coverage, int bytes)
    { Snapshot = snapshot; Facts = Array.AsReadOnly(facts); Coverage = Array.AsReadOnly(coverage); ScalarBytes = bytes; }

    public static LoadedSourceReceipt Build(CatalogSnapshot catalog, IEnumerable<LoadedSourceFact> observations,
        LoadedSourceLimits? limits = null, CancellationToken cancellation = default)
    {
        if (catalog == null || observations == null) throw new ArgumentNullException(nameof(catalog));
        limits ??= new LoadedSourceLimits();
        var rows = new List<LoadedSourceFact>(); var bytes = 0;
        foreach (var row in observations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (row == null || rows.Count >= limits.MaxFacts) throw new InvalidDataException("Loaded source fact limit exceeded.");
            if (row.Kind == LoadedSourceFactKind.ProfileScope)
                throw new InvalidDataException("Ordinary category evidence cannot claim inventory-census observations.");
            bytes = checked(bytes + row.ScalarBytes);
            if (bytes > limits.MaxScalarBytes) throw new InvalidDataException("Loaded source scalar limit exceeded.");
            rows.Add(row);
        }
        cancellation.ThrowIfCancellationRequested();
        rows.Sort((a, b) => StringComparer.Ordinal.Compare(a.Identity, b.Identity));
        for (var i = 1; i < rows.Count; i++)
            if (rows[i - 1].Identity == rows[i].Identity) throw new InvalidDataException("Ambiguous loaded source fact identity.");
        // Charge derived working scalars before expansion, not after constructing an oversized graph.
        var compactBudget = new ClassificationSourceSnapshot.ScalarBudget(cancellation);
        var names = new HashSet<string>(catalog.Items.Keys, StringComparer.Ordinal);
        foreach (var name in names) compactBudget.Charge(name);
        void AddName(string name)
        {
            if (names.Contains(name)) return;
            if (names.Count >= 16384) throw new InvalidDataException("Loaded source identity bound exceeded.");
            compactBudget.Charge(name); names.Add(name);
        }
        var routes = new List<MaterialRoute>();
        var coverage = new List<string>(); var groups = new Dictionary<string, List<LoadedSourceFact>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            if (row.Kind == LoadedSourceFactKind.Coverage) { coverage.Add(row.Identity + ":" + row.Detail); continue; }
            if (row.Output.Length == 0) throw new InvalidDataException("Missing loaded source output identity.");
            // Required-slot placeholders are unresolved graph leaves. They cannot be produced.
            if (row.Output.StartsWith("yavsos:missing-source:", StringComparison.Ordinal))
                throw new InvalidDataException("Reserved missing source input identity cannot be an output.");
            if (row.Kind is LoadedSourceFactKind.ItemUse or LoadedSourceFactKind.FacetEvidence or LoadedSourceFactKind.Acquisition)
                continue; // Uses, optional facets and acquisition methods never add graph nodes.
            AddName(row.Output);
            if (row.Input.Length == 0 || row.Route.Length == 0 || row.Method.Length == 0)
                throw new InvalidDataException("Incomplete loaded source route input.");
            if (row.Input.StartsWith("yavsos:missing-source:", StringComparison.Ordinal) && catalog.Items.ContainsKey(row.Input))
                throw new InvalidDataException("Missing source input placeholder collides with a loaded item.");
            AddName(row.Input);
            if (!groups.TryGetValue(row.Route, out var inputs)) groups.Add(row.Route, inputs = new List<LoadedSourceFact>());
            inputs.Add(row);
        }
        foreach (var group in groups.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            var first = group.Value[0];
            if (group.Value.Count > 256 || group.Value.Any(x => x.Output != first.Output || x.Method != first.Method))
                throw new InvalidDataException("Inconsistent/oversized loaded source route.");
            // One unavailable required slot disables the complete alternative, never weakens its AND group.
            if (group.Value.Any(x => !x.Eligible || x.Amount <= 0)) continue;
            if (routes.Count >= 65536) throw new InvalidDataException("Loaded source route bound exceeded.");
            compactBudget.ChargeBytes(5 + Encoding.UTF8.GetByteCount(group.Key));
            foreach (var input in group.Value)
            {
                compactBudget.Charge(input.Input); compactBudget.Charge(input.Output);
                compactBudget.Charge(input.Method); compactBudget.Charge(group.Key);
            }
            routes.Add(new MaterialRoute("live:" + group.Key, group.Value.Select(x =>
                new MaterialLink(x.Input, x.Output, x.Method, group.Key, amount: x.Amount))));
        }
        if (names.Count > 16384 || routes.Count > 65536)
            throw new InvalidDataException("Loaded source inference bounds exceeded.");
        var semanticEvidence = LoadedItemSemantics.Build(catalog, rows, routes, compactBudget, cancellation, coverage);
        var fingerprint = ClassificationValidation.Hash(writer => {
            writer.Write(Version); writer.Write(catalog.Fingerprint); writer.Write(rows.Count);
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                writer.Write((int)row.Kind); writer.Write(row.Identity); writer.Write(row.Output); writer.Write(row.Input);
                writer.Write(row.Route); writer.Write(row.Method); writer.Write(row.Detail);
                writer.Write(row.Amount); writer.Write(row.Eligible);
            }
        });
        var snapshot = ClassificationSourceSnapshot.FromLoadedReceipt(catalog, fingerprint, routes, semanticEvidence, ItemProfileScope.Build(catalog, rows, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return new LoadedSourceReceipt(snapshot, rows.ToArray(), coverage.OrderBy(x => x, StringComparer.Ordinal).ToArray(), bytes);
    }
}
