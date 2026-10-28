using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace YAVSOS.Core.Catalog;

/// <summary>Deterministic evidence index. FactsReady is not classification or mutation readiness.</summary>
public sealed class CatalogSnapshot
{
    public const string Schema = "yavsos-loaded-catalog-1";
    public string Fingerprint { get; }
    public IReadOnlyList<CatalogFact> Facts { get; }
    public IReadOnlyDictionary<string, CatalogFact> Items { get; }
    public IReadOnlyDictionary<string, CatalogFact> Recipes { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<CatalogFact>> Requirements { get; }
    public IReadOnlyList<string> MissingReferences { get; }
    public int ScalarBytes { get; }

    private CatalogSnapshot(CatalogFact[] facts, string fingerprint, Dictionary<string, CatalogFact> items,
        Dictionary<string, CatalogFact> recipes, Dictionary<string, IReadOnlyList<CatalogFact>> requirements,
        string[] missing, int bytes)
    {
        Facts = Array.AsReadOnly(facts); Fingerprint = fingerprint;
        Items = new ReadOnlyDictionary<string, CatalogFact>(items); Recipes = new ReadOnlyDictionary<string, CatalogFact>(recipes);
        Requirements = new ReadOnlyDictionary<string, IReadOnlyList<CatalogFact>>(requirements);
        MissingReferences = Array.AsReadOnly(missing); ScalarBytes = bytes;
    }

    public static CatalogSnapshot Build(IEnumerable<CatalogFact> input, CatalogLimits? limits = null, CancellationToken cancellation = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        limits ??= new CatalogLimits();
        var rows = new List<CatalogFact>(); var bytes = 0; var itemCount = 0; var recipeCount = 0;
        foreach (var fact in input)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fact == null) throw new InvalidDataException("Null catalog fact.");
            CheckBounds(fact, limits, rows.Count + 1, ref bytes, ref itemCount, ref recipeCount);
            rows.Add(fact);
        }
        var facts = rows.OrderBy(x => x.Kind).ThenBy(x => x.Identity, StringComparer.Ordinal).ToArray();
        cancellation.ThrowIfCancellationRequested();
        var items = new Dictionary<string, CatalogFact>(StringComparer.Ordinal);
        var recipes = new Dictionary<string, CatalogFact>(StringComparer.Ordinal);
        var requirements = new Dictionary<string, List<CatalogFact>>(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        using var sha = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write(Schema); writer.Write(facts.Length);
        CatalogFact? previous = null;
        foreach (var fact in facts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (previous != null && previous.Kind == fact.Kind && previous.Identity == fact.Identity)
                throw new InvalidDataException("Ambiguous catalog identity: " + fact.Identity);
            previous = fact;
            writer.Write((int)fact.Kind); writer.Write(fact.Identity); writer.Write(fact.Values.Count);
            foreach (var pair in fact.Values) { writer.Write(pair.Key); writer.Write(pair.Value); }
            if (fact.Kind == CatalogFactKind.Item) items.Add(fact.Identity, fact);
            if (fact.Kind == CatalogFactKind.Recipe) recipes.Add(fact.Identity, fact);
            if (fact.Kind == CatalogFactKind.Requirement)
            {
                var recipe = fact.Value("recipe");
                if (string.IsNullOrWhiteSpace(recipe)) throw new InvalidDataException("Requirement lacks a recipe identity.");
                if (!requirements.TryGetValue(recipe, out var group)) requirements.Add(recipe, group = new List<CatalogFact>());
                group.Add(fact);
            }
        }
        foreach (var fact in facts)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fact.Kind is CatalogFactKind.Recipe or CatalogFactKind.Requirement)
            {
                var endpoint = fact.Value(fact.Kind == CatalogFactKind.Recipe ? "output" : "input");
                if (endpoint.Length == 0) missing.Add("empty-item-reference:" + fact.Identity);
                else if (!items.ContainsKey(endpoint)) missing.Add("item:" + endpoint);
            }
            if (fact.Kind == CatalogFactKind.Requirement && !recipes.ContainsKey(fact.Value("recipe")))
                throw new InvalidDataException("Requirement references an absent recipe.");
        }
        writer.Flush(); stream.FlushFinalBlock();
        cancellation.ThrowIfCancellationRequested();
        return new CatalogSnapshot(facts, BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant(), items, recipes,
            requirements.ToDictionary(x => x.Key, x => (IReadOnlyList<CatalogFact>)Array.AsReadOnly(x.Value.ToArray()), StringComparer.Ordinal),
            missing.ToArray(), bytes);
    }

    internal static void CheckBounds(CatalogFact fact, CatalogLimits limits, int count, ref int bytes, ref int items, ref int recipes)
    {
        bytes = checked(bytes + fact.ScalarBytes);
        if (fact.Kind == CatalogFactKind.Item) items++;
        if (fact.Kind == CatalogFactKind.Recipe) recipes++;
        if (count > limits.MaxRows || bytes > limits.MaxScalarBytes || items > limits.MaxItems || recipes > limits.MaxRecipes)
            throw new InvalidDataException("Catalog count/scalar budget exceeded; complete generation rejected.");
    }
}
