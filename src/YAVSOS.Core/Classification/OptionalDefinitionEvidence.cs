using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

public enum OptionalDefinitionKind { ItemRegistration, PieceResource, CreatureDrop }

/// <summary>
/// One detached registration claim read on the Unity thread. The adapter has already proved that the supported
/// manager's registered prefab is the exact loaded object named by <see cref="Definition"/>. Names alone are never claims.
/// </summary>
public sealed class OptionalDefinitionObservation
{
    public OptionalDefinitionKind Kind { get; }
    public string Definition { get; }
    public int Index { get; }
    public string Owner { get; }
    public string Item { get; }
    public int Amount { get; }
    public bool Eligible { get; }
    public OptionalDefinitionObservation(OptionalDefinitionKind kind, string definition, string owner, string item,
        int index = 0, int amount = 1, bool eligible = true)
    {
        if (!Enum.IsDefined(typeof(OptionalDefinitionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrEmpty(definition) || definition.Length > 4096 || owner == null || owner.Length > 4096 ||
            item == null || item.Length > 4096 || index < 0) throw new ArgumentException("Invalid optional definition observation.");
        Kind = kind; Definition = definition; Index = index; Owner = owner; Item = item; Amount = amount; Eligible = eligible;
    }
}

/// <summary>
/// Converts exact supported-manager ownership into raw FacetEvidence/Coverage source facts. Ownership (mod),
/// building use (construction-mod) and creature drops (related-mod) stay separate dimensions; a use or drop never
/// changes the owner. Anything ambiguous, malformed, unresolved or over budget stays Unknown for that adapter.
/// </summary>
public static class OptionalDefinitionEvidence
{
    public const string Version = "yavsos-optional-definitions-1";
    public const int MaximumObservations = 16384;
    // Jotunn 2.30.2 BepInExUtils.GetSourceModMetadata falls back to Jotunn's own metadata when no caller resolves.
    public const string JotunnFallbackOwner = "com.jotunn.jotunn";

    public static IReadOnlyList<LoadedSourceFact> Build(CatalogSnapshot catalog, OptionalCapability capability,
        IEnumerable<OptionalDefinitionObservation> observations, CancellationToken cancellation = default)
    {
        if (catalog == null || capability == null || observations == null) throw new ArgumentNullException(nameof(catalog));
        var adapter = capability.Adapter;
        var rows = new List<LoadedSourceFact> { Coverage(adapter, "capability", capability.Witness + "; definitions=" + Version) };
        if (!capability.Supported) return rows.AsReadOnly();
        var input = observations.Take(MaximumObservations + 1).ToArray();
        if (input.Length > MaximumObservations || input.Any(x => x == null))
        {
            rows.Add(Coverage(adapter, "gap", "gap: observation bound exceeded or null row; all " + adapter + " facets Unknown"));
            return rows.AsReadOnly();
        }
        // Bind every observed claim, including ambiguous/unresolved/disabled ones. Facet compaction
        // and gap counts alone cannot identify a changed registration transaction.
        var observationHash = ClassificationValidation.Hash(writer => {
            void Raw(string text) { writer.Write(text.Length); foreach (var c in text) writer.Write((ushort)c); }
            foreach (var row in input.OrderBy(x => x.Kind).ThenBy(x => x.Definition, StringComparer.Ordinal)
                .ThenBy(x => x.Index).ThenBy(x => x.Owner, StringComparer.Ordinal).ThenBy(x => x.Item, StringComparer.Ordinal)
                .ThenBy(x => x.Amount).ThenBy(x => x.Eligible))
            {
                cancellation.ThrowIfCancellationRequested();
                writer.Write((int)row.Kind); Raw(row.Definition); writer.Write(row.Index); Raw(row.Owner); Raw(row.Item);
                writer.Write(row.Amount); writer.Write(row.Eligible);
            }
        });
        rows.Add(Coverage(adapter, "observations", "rows=" + N(input.Length) + "; sha256=" + observationHash));
        // Two claims for one slot cannot both be admitted; reject the adapter instead of the whole receipt.
        var slots = new HashSet<(OptionalDefinitionKind, string, int)>();
        foreach (var row in input)
            if (!slots.Add((row.Kind, row.Definition, row.Index)))
            {
                rows.Add(Coverage(adapter, "gap", "gap: duplicate definition slot; all " + adapter + " facets Unknown"));
                return rows.AsReadOnly();
            }
        int unresolved = 0, invalidOwner = 0, ambiguous = 0, oversized = 0;
        var owners = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var row in input.Where(x => x.Kind == OptionalDefinitionKind.ItemRegistration))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!row.Eligible || row.Amount <= 0) continue;
            if (row.Item != row.Definition || !catalog.Items.ContainsKey(row.Item)) { unresolved++; continue; }
            if (!owners.TryGetValue(row.Item, out var claims)) owners.Add(row.Item, claims = new SortedSet<string>(StringComparer.Ordinal));
            claims.Add(ValidOwner(row.Owner) ? row.Owner : "");
        }
        foreach (var pair in owners)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pair.Value.Count != 1) { ambiguous++; continue; }
            var owner = pair.Value.Min!;
            if (owner.Length == 0) { invalidOwner++; continue; }
            if (!Add(rows, adapter, "mod", pair.Key, pair.Key, 0, owner, adapter + " registered item: " + pair.Key, 1, true)) oversized++;
        }
        foreach (var row in input.Where(x => x.Kind != OptionalDefinitionKind.ItemRegistration).OrderBy(x => x.Kind)
            .ThenBy(x => x.Definition, StringComparer.Ordinal).ThenBy(x => x.Index))
        {
            cancellation.ThrowIfCancellationRequested();
            var piece = row.Kind == OptionalDefinitionKind.PieceResource;
            if (row.Item.Length == 0 || !catalog.Items.ContainsKey(row.Item)) { unresolved++; continue; }
            if (!ValidOwner(row.Owner)) { invalidOwner++; continue; }
            // Disabled/nonpositive slots stay raw receipt identity; the receipt admits only eligible positive facts.
            if (!Add(rows, adapter, piece ? "construction-mod" : "related-mod", row.Item, row.Definition, row.Index, row.Owner,
                adapter + (piece ? " registered piece resource: " : " registered creature drop: ") + row.Definition, row.Amount, row.Eligible)) oversized++;
        }
        rows.Add(Coverage(adapter, "gaps", "unresolved=" + N(unresolved) + "; invalid-owner=" + N(invalidOwner) +
            "; ambiguous-owner=" + N(ambiguous) + "; oversized=" + N(oversized) + "; unsupported registration paths remain Unknown"));
        return rows.AsReadOnly();
    }

    internal static bool ValidOwner(string owner)
    {
        if (owner == JotunnFallbackOwner || owner == "unknown") return false;
        try { ClassificationFilters.ValidateValue("mod", owner, false); return true; }
        catch (ArgumentException) { return false; }
    }

    private static bool Add(List<LoadedSourceFact> rows, string adapter, string dimension, string item, string definition,
        int index, string value, string route, int amount, bool eligible)
    {
        if (!Scalar(item) || !Scalar(route) || !Scalar(definition)) return false;
        var identity = "optional:" + adapter + ":" + dimension + ":" + definition + ":" + N(index);
        if (identity.Length > 1024) identity = "optional:" + adapter + ":" + dimension + ":sha256:" +
            ClassificationValidation.Hash(writer => { writer.Write(definition); writer.Write(index); });
        rows.Add(new LoadedSourceFact(LoadedSourceFactKind.FacetEvidence, identity, output: item, route: route,
            method: dimension, detail: value, amount: amount, eligible: eligible));
        return true;
    }
    internal static bool Scalar(string value)
    {
        try { ClassificationValidation.Text(value, 1024); return true; }
        catch (ArgumentException) { return false; }
    }
    internal static LoadedSourceFact Coverage(string adapter, string name, string detail) =>
        new LoadedSourceFact(LoadedSourceFactKind.Coverage, "optional:" + adapter + ":" + name, detail: detail);
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
