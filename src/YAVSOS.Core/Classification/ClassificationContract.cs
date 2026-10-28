using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Classification;

public sealed class ClassificationCategory
{
    public string Id { get; }
    public string Name { get; }
    public int DisplayRank { get; }
    /// <summary>Explicit parent, for a child whose stable ID is outside its umbrella's prefix. Null derives it.</summary>
    public string? Parent { get; }
    /// <summary>An "Other" subcategory the taxonomy derived; rebuilt by every taxonomy, never an input.</summary>
    public bool Generated { get; }
    public ClassificationCategory(string id, string name, int displayRank, string? parent = null)
        : this(id, name, displayRank, parent, false) { }
    private ClassificationCategory(string id, string name, int displayRank, string? parent, bool generated)
    {
        ClassificationValidation.Category(id); ClassificationValidation.Text(name, 128);
        if (displayRank < 0 || displayRank > 999) throw new ArgumentOutOfRangeException(nameof(displayRank));
        if (parent != null)
        {
            ClassificationValidation.Category(parent);
            if (parent == id) throw new ArgumentException("A category cannot be its own parent.");
        }
        Id = id; Name = name; DisplayRank = displayRank; Parent = parent; Generated = generated;
    }
    internal static ClassificationCategory Other(ClassificationCategory umbrella, string id) =>
        new ClassificationCategory(id, umbrella.Name.Length <= 120 ? umbrella.Name + " · Other" : "Other", OtherRank, umbrella.Id, true);
    internal const int OtherRank = 998;
}

/// <summary>
/// Categories and their hierarchy (D051). A category's parent is its explicit <see cref="ClassificationCategory.Parent"/>,
/// else the longest dotted prefix that is itself a category, else none. Every category with children (an umbrella)
/// gets an "Other" subcategory, <c>&lt;id&gt;.other</c>, for members that fit no named child; a named row with that
/// ID is adopted instead. The hierarchy is structure only: item memberships come from the classifier.
/// </summary>
public sealed class ClassificationTaxonomy
{
    public const int MaximumNamedCategories = 512;
    public const string OtherSuffix = ".other";
    private readonly Dictionary<string, string> parents = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> children = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> others = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, int> depths = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ClassificationCategory> Categories { get; }
    public string Fingerprint { get; }
    public ClassificationTaxonomy(IEnumerable<ClassificationCategory> categories)
    {
        // Generated rows from an earlier taxonomy are dropped and derived again, which keeps WithPolicy idempotent.
        var rows = ClassificationValidation.Bounded(categories, MaximumNamedCategories * 2);
        var owned = new SortedDictionary<string, ClassificationCategory>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row == null) throw new ArgumentException("Ambiguous taxonomy category.");
            if (row.Generated) continue;
            if (!owned.TryAdd(row.Id, row)) throw new ArgumentException("Ambiguous taxonomy category.");
        }
        if (owned.Count > MaximumNamedCategories) throw new ArgumentException("Too many taxonomy categories.");
        if (!owned.ContainsKey("misc")) throw new ArgumentException("Taxonomy must define the misc fallback.");
        foreach (var row in owned.Values)
        {
            var parent = row.Parent ?? NearestPrefix(row.Id, owned);
            if (parent == null) continue;
            if (!owned.ContainsKey(parent)) throw new ArgumentException("Unknown parent category: " + parent);
            parents.Add(row.Id, parent);
        }
        foreach (var id in owned.Keys)
        {
            var steps = 0;
            for (var at = id; parents.TryGetValue(at, out var up); at = up)
                if (++steps > owned.Count) throw new ArgumentException("Cyclic parent category: " + id);
        }
        foreach (var umbrella in parents.Values.Distinct(StringComparer.Ordinal).ToArray())
        {
            var otherId = umbrella + OtherSuffix;
            if (owned.TryGetValue(otherId, out var named))
            {
                // A named row adopts the Other role only when it sits under that umbrella.
                if (parents.TryGetValue(otherId, out var up) && up == umbrella) others.Add(umbrella, otherId);
                continue;
            }
            if (otherId.Length > 64) continue;
            owned.Add(otherId, ClassificationCategory.Other(owned[umbrella], otherId));
            parents.Add(otherId, umbrella); others.Add(umbrella, otherId);
        }
        foreach (var group in parents.GroupBy(x => x.Value, x => x.Key, StringComparer.Ordinal))
            children.Add(group.Key, Array.AsReadOnly(group.OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        foreach (var id in owned.Keys)
        {
            var depth = 0;
            for (var at = id; parents.TryGetValue(at, out var up); at = up) depth++;
            depths.Add(id, depth);
        }
        Categories = new ReadOnlyDictionary<string, ClassificationCategory>(owned);
        Fingerprint = ClassificationValidation.Hash(writer => {
            // The fingerprint's format tag; the category set itself is hashed below, so content changes need no new tag.
            writer.Write("yavsos-taxonomy-2"); writer.Write(owned.Count);
            foreach (var row in owned.Values)
            {
                writer.Write(row.Id); writer.Write(row.Name); writer.Write(row.DisplayRank);
                writer.Write(parents.TryGetValue(row.Id, out var parent) ? parent : ""); writer.Write(row.Generated);
            }
        });
    }
    private static string? NearestPrefix(string id, IDictionary<string, ClassificationCategory> owned)
    {
        for (var cut = id.LastIndexOf('.'); cut > 0; cut = id.LastIndexOf('.', cut - 1))
        {
            var prefix = id.Substring(0, cut);
            if (owned.ContainsKey(prefix)) return prefix;
        }
        return null;
    }
    /// <summary>The resolved parent, or null for a root or unknown ID.</summary>
    public string? ParentOf(string id) => parents.TryGetValue(id, out var parent) ? parent : null;
    /// <summary>Direct children in ordinal order, including the Other subcategory; empty for a leaf or unknown ID.</summary>
    public IReadOnlyList<string> ChildrenOf(string id) => children.TryGetValue(id, out var rows) ? rows : Array.Empty<string>();
    /// <summary>The umbrella's Other subcategory, or null when the ID has no children or its Other could not be named.</summary>
    public string? OtherOf(string id) => others.TryGetValue(id, out var other) ? other : null;
    /// <summary>0 for a root; -1 for an unknown ID.</summary>
    public int DepthOf(string id) => depths.TryGetValue(id, out var depth) ? depth : -1;
    public bool IsAncestorOrSelf(string ancestor, string id)
    {
        if (!depths.ContainsKey(id)) return false;
        for (string? at = id; at != null; at = ParentOf(at))
            if (StringComparer.Ordinal.Equals(at, ancestor)) return true;
        return false;
    }
    public ClassificationTaxonomy WithPolicy(CategoryPolicy policy)
    {
        if (policy == null) throw new ArgumentNullException(nameof(policy));
        var definitions = Categories.Values.Where(x => !x.Generated).ToDictionary(x => x.Id, x => x, StringComparer.Ordinal);
        foreach (var category in policy.Categories)
            definitions[category.Id] = definitions.TryGetValue(category.Id, out var known)
                ? new ClassificationCategory(category.Id, category.DisplayName, known.DisplayRank, known.Parent)
                : new ClassificationCategory(category.Id, category.DisplayName,
                    category.Id.EndsWith(OtherSuffix, StringComparison.Ordinal) && Categories.TryGetValue(category.Id, out var other) && other.Generated
                        ? ClassificationCategory.OtherRank : 500);
        return new ClassificationTaxonomy(definitions.Values);
    }
    internal void Require(string id)
    {
        if (!Categories.ContainsKey(id)) throw new ArgumentException("Unknown/retired category: " + id);
    }
}

public sealed class MembershipEvidence
{
    public string Category { get; }
    public string Source { get; }
    public string Reason { get; }
    public MembershipEvidence(string category, string source, string reason)
    {
        ClassificationValidation.Category(category); ClassificationValidation.Text(source, 256); ClassificationValidation.Text(reason, 2048);
        Category = category; Source = source; Reason = reason;
    }
}

public sealed class FacetEvidence
{
    public string Dimension { get; }
    public string Value { get; }
    public string Source { get; }
    public string Reason { get; }
    /// <summary>
    /// False for a row received from a server that knows a dimension or value this version doesn't (D081). It is kept
    /// unchanged, so the server's snapshot still checks out, and no rule this version writes can name it: a filter on its
    /// dimension sees it as present but not chosen, so it takes less, never more.
    /// </summary>
    public bool Recognized { get; }
    public FacetEvidence(string dimension, string value, string source, string reason) : this(dimension, value, source, reason, false) { }
    private FacetEvidence(string dimension, string value, string source, string reason, bool received)
    {
        Recognized = !received || ClassificationFilters.IsKnownValue(dimension, value);
        if (Recognized) ClassificationFilters.ValidateValue(dimension, value, false);
        else
        {
            // D081: a later version's vocabulary, checked only for shape. Missing and unreadable evidence keep their meaning.
            ClassificationValidation.Category(dimension); ClassificationValidation.Text(value, 128);
            if (value is "unknown" or ClassificationFilters.Unreadable) throw new ArgumentException("Unknown and unreadable are not facet values.");
        }
        ClassificationValidation.Text(source, 256); ClassificationValidation.Text(reason, 2048);
        Dimension = dimension; Value = value; Source = source; Reason = reason;
    }
    /// <summary>A row from the server's assignment snapshot: this version's vocabulary is validated, a later one's kept (D081).</summary>
    internal static FacetEvidence Received(string dimension, string value, string source, string reason) =>
        new FacetEvidence(dimension, value, source, reason, true);
}

/// <summary>Admitted detached evidence for one actually registered item; definition examples are not registrations.</summary>
public sealed class ItemClassificationEvidence
{
    public string Prefab { get; }
    public IReadOnlyList<MembershipEvidence> Memberships { get; }
    public IReadOnlyList<FacetEvidence> Facets { get; }
    public ItemClassificationEvidence(string prefab, IEnumerable<MembershipEvidence>? memberships = null, IEnumerable<FacetEvidence>? facets = null)
    {
        ClassificationValidation.Text(prefab, 2048);
        Prefab = prefab;
        var members = ClassificationValidation.Bounded(memberships ?? Array.Empty<MembershipEvidence>(), 128);
        var facts = ClassificationValidation.Bounded(facets ?? Array.Empty<FacetEvidence>(), 64);
        if (members.Any(x => x == null) || facts.Any(x => x == null)) throw new ArgumentException("Null evidence row.");
        Memberships = Array.AsReadOnly(members.GroupBy(x => (x.Category, x.Source, x.Reason)).Select(x => x.First())
            .OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray());
        Facets = Array.AsReadOnly(facts.GroupBy(x => (x.Dimension, x.Value, x.Source, x.Reason)).Select(x => x.First())
            .OrderBy(x => x.Dimension, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray());
    }
}

public enum OverrideOperation { Replace, Add, Remove }

public sealed class FacetOverride
{
    public string Dimension { get; }
    public OverrideOperation Operation { get; }
    public IReadOnlyList<string> Values { get; }
    public FacetOverride(string dimension, OverrideOperation operation, IEnumerable<string> values)
    {
        ClassificationValidation.Operation(operation);
        var selected = ClassificationValidation.Bounded(values, 64).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        ClassificationFilters.ValidateDimension(dimension);
        if (dimension is "upgraded" or "damaged" or "upgradable" or "quality") throw new ArgumentException("Item condition is current instance evidence; it has no prefab override.");
        foreach (var value in selected) ClassificationFilters.ValidateValue(dimension, value, false);
        if (operation != OverrideOperation.Replace && selected.Length == 0) throw new ArgumentException("Add/remove facet override needs values.");
        Dimension = dimension; Operation = operation; Values = Array.AsReadOnly(selected);
    }
}

public sealed class ClassificationOverride
{
    public string Match { get; }
    public string Selector { get; }
    public OverrideOperation Operation { get; }
    public IReadOnlyList<string> Memberships { get; }
    public string? Primary { get; }
    public IReadOnlyList<FacetOverride> Facets { get; }
    internal PrefabPattern Pattern { get; }
    public ClassificationOverride(string match, string selector, OverrideOperation operation,
        IEnumerable<string> memberships, string? primary = null, IEnumerable<FacetOverride>? facets = null)
    {
        ClassificationValidation.Operation(operation);
        Pattern = new PrefabPattern(match, selector);
        var selected = ClassificationValidation.Bounded(memberships, 256).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var id in selected) ClassificationValidation.Category(id);
        if (primary != null) ClassificationValidation.Category(primary);
        if (operation == OverrideOperation.Replace && primary != null && !selected.Contains(primary, StringComparer.Ordinal) ||
            operation == OverrideOperation.Remove && primary != null)
            throw new ArgumentException("Replacement primary must be a member; removal cannot select a primary.");
        var edits = ClassificationValidation.Bounded(facets ?? Array.Empty<FacetOverride>(), ClassificationFilters.Dimensions.Count);
        if (edits.Any(x => x == null) || edits.Select(x => x.Dimension).Distinct(StringComparer.Ordinal).Count() != edits.Length)
            throw new ArgumentException("Duplicate or null facet edit.");
        if (operation != OverrideOperation.Replace && selected.Length == 0 && primary == null && edits.Length == 0)
            throw new ArgumentException("Override must change memberships, primary or facets.");
        Match = match; Selector = selector; Operation = operation; Memberships = Array.AsReadOnly(selected);
        Primary = primary; Facets = Array.AsReadOnly(edits.OrderBy(x => x.Dimension, StringComparer.Ordinal).ToArray());
    }
}

public sealed class ClassificationResult
{
    public string Prefab { get; }
    public string Primary { get; }
    public bool Quest { get; }
    public bool HasIcon { get; }
    public bool CatalogOnly { get; }
    public bool InstanceRarity { get; }
    public string? SelectedOverride { get; }
    public IReadOnlyList<MembershipEvidence> Memberships { get; }
    public IReadOnlyList<FacetEvidence> Facets { get; }
    internal ClassificationResult(string prefab, string primary, bool quest, bool icon, bool catalogOnly, bool instanceRarity,
        string? selectedOverride, IEnumerable<MembershipEvidence> memberships, IEnumerable<FacetEvidence> facets)
    {
        Prefab = prefab; Primary = primary; Quest = quest; HasIcon = icon; CatalogOnly = catalogOnly; InstanceRarity = instanceRarity;
        SelectedOverride = selectedOverride;
        Memberships = Array.AsReadOnly(memberships.GroupBy(x => (x.Category, x.Source, x.Reason)).Select(x => x.First())
            .OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray());
        Facets = Array.AsReadOnly(facets.GroupBy(x => (x.Dimension, x.Value, x.Source, x.Reason)).Select(x => x.First())
            .OrderBy(x => x.Dimension, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
            .ThenBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray());
    }
}

internal sealed class ClassificationWork
{
    private readonly int maximum;
    private readonly CancellationToken cancellation;
    public int Used { get; private set; }
    internal ClassificationWork(int maximum, CancellationToken cancellation)
    {
        if (maximum < 1 || maximum > 10000000) throw new ArgumentOutOfRangeException(nameof(maximum));
        this.maximum = maximum; this.cancellation = cancellation;
    }
    internal void Spend()
    {
        cancellation.ThrowIfCancellationRequested();
        if (++Used > maximum) throw new InvalidOperationException("Classification work limit exceeded; no partial index accepted.");
    }
}

internal static class ClassificationValidation
{
    // C01: runs per identity and candidate on hot paths, so one pass and no allocation. Unpaired surrogates are refused,
    // as the strict UTF-8 encoder did, so different identities cannot hash to the same replacement bytes.
    internal static void Text(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("Invalid/bounded classification scalar.");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c)) throw new ArgumentException("Invalid/bounded classification scalar.");
            if (char.IsSurrogate(c) && (!char.IsHighSurrogate(c) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i])))
                throw new ArgumentException("Classification scalar contains invalid UTF-16.");
        }
    }
    internal static void Category(string id)
    {
        Text(id, 64);
        if (id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))) throw new ArgumentException("Invalid category ID.");
    }
    internal static void Operation(OverrideOperation operation)
    {
        if (!Enum.IsDefined(typeof(OverrideOperation), operation)) throw new ArgumentOutOfRangeException(nameof(operation));
    }
    internal static T[] Bounded<T>(IEnumerable<T> rows, int maximum)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var owned = rows.Take(maximum + 1).ToArray();
        if (owned.Length > maximum) throw new ArgumentException("Classification input count bound exceeded.");
        return owned;
    }
    internal static string Hash(Action<BinaryWriter> write)
    {
        using var sha = SHA256.Create(); using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        write(writer); writer.Flush(); stream.FlushFinalBlock();
        return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
    }
}
