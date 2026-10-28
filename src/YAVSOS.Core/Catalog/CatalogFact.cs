using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace YAVSOS.Core.Catalog;

public enum CatalogFactKind { Item, Recipe, Requirement, Warning, Context }

/// <summary>Owned scalar facts only. No localized labels, Unity references or inferred memberships.</summary>
public sealed class CatalogFact
{
    public CatalogFactKind Kind { get; }
    public string Identity { get; }
    public IReadOnlyDictionary<string, string> Values { get; }
    public int ScalarBytes { get; }

    public CatalogFact(CatalogFactKind kind, string identity, IEnumerable<KeyValuePair<string, string>> values)
    {
        if (!Enum.IsDefined(typeof(CatalogFactKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Check(identity, false);
        if (values == null) throw new ArgumentNullException(nameof(values));
        var owned = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetByteCount(identity);
        foreach (var pair in values)
        {
            Check(pair.Key, false); Check(pair.Value, true);
            if (owned.Count == 64) throw new ArgumentException("Catalog fact exceeds 64 scalar fields.");
            owned.Add(pair.Key, pair.Value);
            bytes += Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value);
        }
        Kind = kind; Identity = identity; Values = new ReadOnlyDictionary<string, string>(owned); ScalarBytes = bytes;
    }

    public string Value(string key) => Values.TryGetValue(key, out var value) ? value : "";

    private static void Check(string value, bool allowEmpty)
    {
        if (value == null || value.Length > 2048 || !allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Catalog scalar must be present and at most 2048 characters.");
        // Reject malformed UTF-16 so different identities cannot hash to the same replacement bytes.
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]))
            {
                if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i]))
                    throw new ArgumentException("Catalog scalar contains invalid UTF-16.");
            }
    }
}

public sealed class CatalogLimits
{
    public int MaxItems { get; }
    public int MaxRecipes { get; }
    public int MaxRows { get; }
    public int MaxScalarBytes { get; }
    public int StepsPerTick { get; }
    public double SliceMilliseconds { get; }
    public double BuildSeconds { get; }
    public CatalogLimits(int maxItems = 16384, int maxRecipes = 32768, int maxRows = 131072,
        int maxScalarBytes = 32 * 1024 * 1024, int stepsPerTick = 64, double sliceMilliseconds = 2, double buildSeconds = 30)
    {
        if (maxItems < 1 || maxItems > 16384 || maxRecipes < 1 || maxRecipes > 32768 || maxRows < 1 || maxRows > 131072 ||
            maxScalarBytes < 1 || maxScalarBytes > 32 * 1024 * 1024 || stepsPerTick < 1 || stepsPerTick > 64 ||
            !Finite(sliceMilliseconds) || sliceMilliseconds <= 0 || sliceMilliseconds > 2 ||
            !Finite(buildSeconds) || buildSeconds <= 0 || buildSeconds > 30)
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Catalog limits must remain within production safety caps.");
        MaxItems = maxItems; MaxRecipes = maxRecipes; MaxRows = maxRows; MaxScalarBytes = maxScalarBytes;
        StepsPerTick = stepsPerTick; SliceMilliseconds = sliceMilliseconds; BuildSeconds = buildSeconds;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
