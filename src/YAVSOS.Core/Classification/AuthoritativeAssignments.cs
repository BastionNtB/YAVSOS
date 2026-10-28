using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Classification;

[Flags]
public enum AssignmentCapabilities { Identity = 1, Categories = 2 }

/// <summary>
/// Detached server assignment. Under D042 the server's answer wins per item: clients do not require an identical
/// loaded catalog. Items the server did not classify resolve to the Misc catch-all once categories are ready.
/// It carries no instance rarity, admission or inventory authority.
/// </summary>
public sealed class AuthoritativeAssignmentSnapshot
{
    public int Schema => AuthoritativeAssignmentsCodec.Schema;
    public AssignmentCapabilities Capabilities { get; }
    public bool IdentityReady => (Capabilities & AssignmentCapabilities.Identity) != 0;
    public bool CategoriesReady => (Capabilities & AssignmentCapabilities.Categories) != 0;
    public bool HasFacetEvidence(string prefab, string dimension) => CategoriesReady &&
        Items.TryGetValue(prefab, out var item) && item.Facets.Any(facet => facet.Dimension == dimension);
    public string ClassifierVersion { get; }
    public string PipelineVersion { get; }
    public string CatalogFingerprint { get; }
    public string PolicyFingerprint { get; }
    public string TaxonomyFingerprint { get; }
    public string SourceFingerprint { get; }
    public string Fingerprint { get; private set; } = "";
    public ItemProfileScope ProfileScope { get; }
    public IReadOnlyDictionary<string, ClassificationResult> Items { get; }
    private readonly ClassificationTaxonomy taxonomy;
    /// <summary>Catch-all category for loaded items without a server classification (D042).</summary>
    public const string FallbackCategory = "misc";

    /// <summary>
    /// Per-item resolution against the local loaded catalog: the server row when present; otherwise, once categories
    /// are ready, a Misc catch-all for a local item the server does not know. Typed customization and items the server
    /// excluded receive no profile. Identity-only publications resolve nothing.
    /// </summary>
    public ClassificationResult? Resolve(string prefab, CatalogSnapshot local)
    {
        if (prefab == null || local == null) throw new ArgumentNullException(nameof(prefab));
        if (!CategoriesReady || ProfileScope.Excluded.ContainsKey(prefab)) return null;
        if (Items.TryGetValue(prefab, out var assigned)) return assigned;
        if (!local.Items.TryGetValue(prefab, out var fact)) return null;
        var type = fact.Value("type");
        if (type == "Customization") return null;
        bool.TryParse(fact.Value("quest"), out var quest); bool.TryParse(fact.Value("hasIcon"), out var icon);
        return new ClassificationResult(prefab, FallbackCategory, quest, icon, false, AuthoritativeAssignmentsCodec.InstanceRarityType(type), null,
            CategoryClosure.Apply(new[] { new MembershipEvidence(FallbackCategory, "server-fallback", "No server classification for this local item; Misc catch-all (D042).") }, taxonomy),
            Array.Empty<FacetEvidence>());
    }

    internal AuthoritativeAssignmentSnapshot(string classifier, string pipeline, string catalog, string policy, ClassificationTaxonomy taxonomy,
        string source, SortedDictionary<string, ClassificationResult> items, ItemProfileScope profileScope, CancellationToken cancellation,
        AssignmentCapabilities capabilities = AssignmentCapabilities.Identity | AssignmentCapabilities.Categories)
    {
        this.taxonomy = taxonomy; Capabilities = capabilities; ProfileScope = profileScope; ClassifierVersion = classifier; PipelineVersion = pipeline; CatalogFingerprint = catalog;
        PolicyFingerprint = policy; TaxonomyFingerprint = taxonomy.Fingerprint; SourceFingerprint = source;
        Items = new ReadOnlyDictionary<string, ClassificationResult>(items);
        Fingerprint = AuthoritativeAssignmentsCodec.Digest(AuthoritativeAssignmentsCodec.Body(this, cancellation));
    }

    public static AuthoritativeAssignmentSnapshot CreateIdentity(CatalogSnapshot catalog, CategoryPolicy policy, CancellationToken cancellation = default)
    {
        if (catalog == null || policy == null) throw new ArgumentNullException(nameof(catalog));
        cancellation.ThrowIfCancellationRequested();
        var taxonomy = ClassificationRuleSet.FromSchema1(policy).Taxonomy;
        return new AuthoritativeAssignmentSnapshot(ClassificationIndex.Version, ClassificationPipeline.Version, catalog.Fingerprint,
            policy.Fingerprint, taxonomy, "none", new SortedDictionary<string, ClassificationResult>(StringComparer.Ordinal),
            ItemProfileScope.Build(catalog, cancellation: cancellation), cancellation, AssignmentCapabilities.Identity);
    }

    public static AuthoritativeAssignmentSnapshot Create(CatalogSnapshot catalog, ClassifiedCatalog classified,
        CategoryPolicy policy, CancellationToken cancellation = default)
    {
        if (catalog == null || classified == null || policy == null) throw new ArgumentNullException(nameof(catalog));
        cancellation.ThrowIfCancellationRequested();
        var taxonomy = ClassificationRuleSet.FromSchema1(policy).Taxonomy;
        // Pipeline origin fingerprints are checked in addition to the complete loaded item set below.
        if (classified.CatalogFingerprint != catalog.Fingerprint || classified.PolicyFingerprint != policy.Fingerprint ||
            classified.Index.Taxonomy.Fingerprint != taxonomy.Fingerprint)
            throw new InvalidDataException("Classification inputs do not match the authoritative catalog/policy/taxonomy.");
        var items = new SortedDictionary<string, ClassificationResult>(StringComparer.Ordinal);
        var evidence = 0;
        foreach (var row in classified.Index.Items.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            AuthoritativeAssignmentsCodec.ValidateItem(row, catalog, taxonomy, ref evidence);
            items.Add(row.Prefab, row);
        }
        AuthoritativeAssignmentsCodec.RequireWholeSet(items, catalog, classified.ProfileScope);
        return new AuthoritativeAssignmentSnapshot(ClassificationIndex.Version, ClassificationPipeline.Version, catalog.Fingerprint,
            policy.Fingerprint, taxonomy, classified.SourceFingerprint, items, classified.ProfileScope, cancellation);
    }
}

/// <summary>Strict schema-3 framed binary plus bounded Base64/Deflate. Decode belongs on a detached worker.</summary>
public static class AuthoritativeAssignmentsCodec
{
    public const int Schema = 3;
    public const int MaximumDecodedBytes = 8 * 1024 * 1024;
    public const int MaximumCompressedBytes = 512 * 1024;
    public const int MaximumItems = 16384;
    public const int MaximumEvidence = 262144;
    public const int MaximumEvidencePerItem = 4096;
    public const int MaximumWireCharacters = 4 * ((MaximumCompressedBytes + 2) / 3) + 5;
    private const string Prefix = "yas3:";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static string ToWire(AuthoritativeAssignmentSnapshot snapshot, CancellationToken cancellation = default)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        var body = Body(snapshot, cancellation);
        using var decoded = new MemoryStream();
        decoded.Write(body, 0, body.Length);
        using (var writer = new BinaryWriter(decoded, Utf8, true)) WriteText(writer, snapshot.Fingerprint, 64);
        if (decoded.Length > MaximumDecodedBytes) throw new InvalidDataException("Assignment decoded-byte bound exceeded.");
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
        {
            decoded.Position = 0; var buffer = new byte[8192]; int count;
            while ((count = decoded.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellation.ThrowIfCancellationRequested(); deflate.Write(buffer, 0, count);
                if (compressed.Length > MaximumCompressedBytes) throw new InvalidDataException("Assignment compressed-byte bound exceeded.");
            }
        }
        cancellation.ThrowIfCancellationRequested();
        if (compressed.Length > MaximumCompressedBytes) throw new InvalidDataException("Assignment compressed-byte bound exceeded.");
        return Prefix + Convert.ToBase64String(compressed.ToArray());
    }

    public static AuthoritativeAssignmentSnapshot ParseWire(string payload, CategoryPolicy effective,
        ClassificationTaxonomy? taxonomy = null, CancellationToken cancellation = default)
    {
        if (effective == null) throw new ArgumentNullException(nameof(effective));
        cancellation.ThrowIfCancellationRequested();
        if (payload == null || payload.Length > MaximumWireCharacters || !payload.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Missing, oversized or unsupported assignment wire envelope.");
        byte[] compressed;
        try { compressed = Convert.FromBase64String(payload.Substring(Prefix.Length)); }
        catch (FormatException) { throw new InvalidDataException("Invalid assignment Base64."); }
        if (compressed.Length == 0 || compressed.Length > MaximumCompressedBytes || Prefix + Convert.ToBase64String(compressed) != payload)
            throw new InvalidDataException("Noncanonical or oversized assignment Base64.");
        using var input = new ExactDeflateInput(compressed);
        using var decoded = new MemoryStream();
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress, true))
        {
            var buffer = new byte[8192]; int count;
            while ((count = deflate.Read(buffer, 0, Math.Min(buffer.Length, MaximumDecodedBytes + 1 - (int)decoded.Length))) != 0)
            {
                cancellation.ThrowIfCancellationRequested(); decoded.Write(buffer, 0, count);
                if (decoded.Length > MaximumDecodedBytes) throw new InvalidDataException("Assignment expansion exceeds decoded-byte bound.");
            }
        }
        if (input.Position != compressed.Length) throw new InvalidDataException("Trailing compressed assignment data.");
        cancellation.ThrowIfCancellationRequested();
        return ParseBytes(decoded.ToArray(), effective, taxonomy, cancellation);
    }

    internal static AuthoritativeAssignmentSnapshot ParseBytes(byte[] bytes, CategoryPolicy effective,
        ClassificationTaxonomy? taxonomy, CancellationToken cancellation)
    {
        try
        {
            var effectiveTaxonomy = ClassificationRuleSet.FromSchema1(effective).Taxonomy;
            if (taxonomy != null && taxonomy.Fingerprint != effectiveTaxonomy.Fingerprint)
                throw new InvalidDataException("Optional assignment taxonomy differs from the effective policy; no assignments accepted.");
            taxonomy = effectiveTaxonomy;
            cancellation.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream, Utf8, true);
            if (reader.ReadInt32() != Schema) throw new InvalidDataException("Unsupported authoritative assignment schema.");
            var classifier = ReadText(reader, 128); var pipeline = ReadText(reader, 128);
            var catalogFingerprint = ReadFingerprint(reader); var policyFingerprint = ReadFingerprint(reader); var taxonomyFingerprint = ReadFingerprint(reader);
            var source = ReadText(reader, 128);
            if (source != "none") RequireFingerprint(source);
            // D078 B02: the server's answer wins per item (D042), so a classifier or pipeline difference is not a reason to
            // refuse it; the versions stay in the digest and the log. An update every client needs raises the minimum version.
            if (policyFingerprint != effective.Fingerprint || taxonomyFingerprint != taxonomy.Fingerprint)
                throw new InvalidDataException("Authoritative policy/taxonomy mismatch; wait for matching effective definitions.");
            var capabilities = (AssignmentCapabilities)reader.ReadInt32();
            if (capabilities != AssignmentCapabilities.Identity && capabilities != (AssignmentCapabilities.Identity | AssignmentCapabilities.Categories))
                throw new InvalidDataException("Unsupported authoritative capability flags.");
            var categoriesReady = (capabilities & AssignmentCapabilities.Categories) != 0;
            var scanComplete = ReadBoolean(reader);
            var exclusionCount = ReadCount(reader, MaximumItems);
            var exclusions = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string? previousExcluded = null;
            for (var i = 0; i < exclusionCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var prefab = ReadText(reader, 2048); var reason = ReadText(reader, 64);
                if (previousExcluded != null && StringComparer.Ordinal.Compare(previousExcluded, prefab) >= 0)
                    throw new InvalidDataException("Duplicate or noncanonical profile exclusion.");
                exclusions.Add(prefab, reason); previousExcluded = prefab;
            }
            var scope = ItemProfileScope.FromServer(catalogFingerprint, exclusions, scanComplete, source != "none");
            var count = ReadCount(reader, MaximumItems);
            if (!categoriesReady && count != 0) throw new InvalidDataException("Identity-only publication cannot carry item rows.");
            var items = new SortedDictionary<string, ClassificationResult>(StringComparer.Ordinal); var evidence = 0;
            string? previous = null;
            for (var i = 0; i < count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var prefab = ReadText(reader, 2048);
                if (previous != null && StringComparer.Ordinal.Compare(previous, prefab) >= 0) throw new InvalidDataException("Duplicate or noncanonical assignment item.");
                previous = prefab;
                var primary = ReadText(reader, 64);
                var quest = ReadBoolean(reader); var icon = ReadBoolean(reader); var catalogOnly = ReadBoolean(reader); var instanceRarity = ReadBoolean(reader);
                var selectedOverride = ReadBoolean(reader) ? ReadText(reader, 256) : null;
                var memberships = new List<MembershipEvidence>(); var facets = new List<FacetEvidence>();
                var memberCount = ReadCount(reader, MaximumEvidencePerItem);
                for (var member = 0; member < memberCount; member++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++evidence > MaximumEvidence) throw new InvalidDataException("Assignment evidence bound exceeded.");
                    var row = new MembershipEvidence(ReadText(reader, 64), ReadText(reader, 256), ReadText(reader, 2048));
                    if (CategoryClosure.IsDerived(row)) throw new InvalidDataException("Derived closure memberships are rebuilt locally, never received.");
                    if (memberships.Count > 0 && Compare(memberships[memberships.Count - 1], row) >= 0)
                        throw new InvalidDataException("Duplicate or noncanonical assignment membership evidence.");
                    memberships.Add(row);
                }
                var facetCount = ReadCount(reader, MaximumEvidencePerItem);
                for (var facet = 0; facet < facetCount; facet++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (++evidence > MaximumEvidence) throw new InvalidDataException("Assignment evidence bound exceeded.");
                    // D081: a dimension or value from a later version is kept unchanged instead of refusing the whole snapshot.
                    var row = FacetEvidence.Received(ReadText(reader, 128), ReadText(reader, 128), ReadText(reader, 256), ReadText(reader, 2048));
                    if (facets.Count > 0 && Compare(facets[facets.Count - 1], row) >= 0)
                        throw new InvalidDataException("Duplicate or noncanonical assignment facet evidence.");
                    facets.Add(row);
                }
                foreach (var membership in memberships) taxonomy.Require(membership.Category);
                var item = new ClassificationResult(prefab, primary, quest, icon, catalogOnly, instanceRarity, selectedOverride,
                    CategoryClosure.Apply(memberships, taxonomy), facets);
                if (exclusions.ContainsKey(prefab)) throw new InvalidDataException("Excluded item cannot carry a profile.");
                var unused = 0; ValidateStructure(item, taxonomy, ref unused); items.Add(prefab, item);
            }
            var bodyLength = (int)stream.Position; var incomingFingerprint = ReadFingerprint(reader);
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing decoded assignment data.");
            if (!categoriesReady && (source != "none" || scanComplete || exclusions.Values.Any(reason => reason != "typed-customization")))
                throw new InvalidDataException("Identity-only publication cannot claim source or exclusion evidence.");
            var result = new AuthoritativeAssignmentSnapshot(classifier, pipeline, catalogFingerprint, policyFingerprint, taxonomy, source, items, scope, cancellation, capabilities);
            if (result.Fingerprint != incomingFingerprint || Body(result, cancellation).Length != bodyLength)
                throw new InvalidDataException("Authoritative assignment digest mismatch; full content is recomputed.");
            cancellation.ThrowIfCancellationRequested(); return result;
        }
        catch (Exception exception) when (exception is ArgumentException || exception is EndOfStreamException || exception is DecoderFallbackException)
        { throw new InvalidDataException("Malformed authoritative assignment: " + exception.Message, exception); }
    }

    internal static void RequireWholeSet(SortedDictionary<string, ClassificationResult> items, CatalogSnapshot catalog, ItemProfileScope scope)
    {
        if (scope.CatalogFingerprint != catalog.Fingerprint || items.Count > MaximumItems || items.Count != catalog.Items.Count - scope.Excluded.Count ||
            items.Keys.Any(id => !catalog.Items.ContainsKey(id) || scope.Excluded.ContainsKey(id)))
            throw new InvalidDataException("Authoritative profiles and exclusions must partition registered items exactly; no partial fallback accepted.");
    }
    /// <summary>Server-side consistency of its own publication against its loaded catalog.</summary>
    internal static void ValidateItem(ClassificationResult item, CatalogSnapshot catalog, ClassificationTaxonomy taxonomy, ref int evidence)
    {
        if (!catalog.Items.TryGetValue(item.Prefab, out var fact)) throw new InvalidDataException("Unknown assignment prefab: " + item.Prefab);
        ValidateStructure(item, taxonomy, ref evidence);
        var type = fact.Value("type");
        if (!bool.TryParse(fact.Value("quest"), out var quest) || !bool.TryParse(fact.Value("hasIcon"), out var icon) ||
            item.Quest != quest || item.HasIcon != icon || item.CatalogOnly != (type == "Customization") || item.InstanceRarity != InstanceRarityType(type))
            throw new InvalidDataException("Assignment flags differ from loaded catalog facts.");
    }
    // C04: one list, so the server's own publication can't fail its validation after an edit to only one copy.
    internal static bool InstanceRarityType(string type) => ClassificationIndex.IsInstanceRarityType(type);
    /// <summary>Catalog-independent checks applied to every received row; clients accept the server's answer per item.</summary>
    internal static void ValidateStructure(ClassificationResult item, ClassificationTaxonomy taxonomy, ref int evidence)
    {
        ClassificationValidation.Category(item.Primary); taxonomy.Require(item.Primary);
        if (item.Memberships.Count == 0 || item.Memberships.Count > MaximumEvidencePerItem || item.Facets.Count > MaximumEvidencePerItem ||
            !item.Memberships.Any(x => x.Category == item.Primary)) throw new InvalidDataException("Assignment primary must remain a bounded selected membership.");
        evidence = checked(evidence + item.Memberships.Count + item.Facets.Count);
        if (evidence > MaximumEvidence) throw new InvalidDataException("Assignment evidence bound exceeded.");
        foreach (var membership in item.Memberships) taxonomy.Require(membership.Category);
        var complete = CategoryClosure.Apply(CategoryClosure.Underived(item.Memberships).ToArray(), taxonomy);
        if (complete.Count != item.Memberships.Count || complete.Any(row => !item.Memberships.Any(x => x.Category == row.Category && x.Source == row.Source && x.Reason == row.Reason)))
            throw new InvalidDataException("Assignment memberships must equal their taxonomy closure.");
        if (item.InstanceRarity && item.Facets.Any(x => x.Dimension == "rarity")) throw new InvalidDataException("Equipment rarity must remain fresh instance evidence.");
        if (item.Facets.Any(x => x.Dimension is "upgraded" or "damaged" or "upgradable" or "quality")) throw new InvalidDataException("Item condition must remain fresh instance evidence.");
    }
    internal static byte[] Body(AuthoritativeAssignmentSnapshot snapshot, CancellationToken cancellation)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Schema); WriteText(writer, snapshot.ClassifierVersion, 128); WriteText(writer, snapshot.PipelineVersion, 128);
        WriteText(writer, snapshot.CatalogFingerprint, 64); WriteText(writer, snapshot.PolicyFingerprint, 64); WriteText(writer, snapshot.TaxonomyFingerprint, 64);
        WriteText(writer, snapshot.SourceFingerprint, 128); writer.Write((int)snapshot.Capabilities);
        writer.Write(snapshot.ProfileScope.AcquisitionScanComplete); writer.Write(snapshot.ProfileScope.Excluded.Count);
        foreach (var pair in snapshot.ProfileScope.Excluded)
        {
            cancellation.ThrowIfCancellationRequested(); WriteText(writer, pair.Key, 2048); WriteText(writer, pair.Value, 64);
        }
        writer.Write(snapshot.Items.Count);
        foreach (var item in snapshot.Items.Values)
        {
            cancellation.ThrowIfCancellationRequested(); WriteText(writer, item.Prefab, 2048); WriteText(writer, item.Primary, 64);
            writer.Write(item.Quest); writer.Write(item.HasIcon); writer.Write(item.CatalogOnly); writer.Write(item.InstanceRarity);
            writer.Write(item.SelectedOverride != null); if (item.SelectedOverride != null) WriteText(writer, item.SelectedOverride, 256);
            // Closure rows are a fixed function of the rest and the taxonomy; the receiver rebuilds them (D051).
            var sent = CategoryClosure.Underived(item.Memberships).ToArray();
            writer.Write(sent.Length);
            foreach (var membership in sent)
            {
                cancellation.ThrowIfCancellationRequested(); WriteText(writer, membership.Category, 64); WriteText(writer, membership.Source, 256); WriteText(writer, membership.Reason, 2048);
            }
            writer.Write(item.Facets.Count);
            foreach (var facet in item.Facets)
            {
                cancellation.ThrowIfCancellationRequested(); WriteText(writer, facet.Dimension, 128); WriteText(writer, facet.Value, 128);
                WriteText(writer, facet.Source, 256); WriteText(writer, facet.Reason, 2048);
            }
        }
        cancellation.ThrowIfCancellationRequested(); writer.Flush();
        if (stream.Length > MaximumDecodedBytes - 68) throw new InvalidDataException("Assignment decoded-byte bound exceeded.");
        return stream.ToArray();
    }
    internal static string Digest(byte[] bytes)
    {
        using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void WriteText(BinaryWriter writer, string value, int maximum)
    {
        ClassificationValidation.Text(value, maximum); var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
        if (writer.BaseStream.Position > MaximumDecodedBytes) throw new InvalidDataException("Assignment decoded-byte bound exceeded.");
    }
    private static string ReadText(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 1 || count > maximum * 4 || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Assignment scalar length exceeds bounds or available bytes.");
        var value = Utf8.GetString(reader.ReadBytes(count)); ClassificationValidation.Text(value, maximum); return value;
    }
    private static int ReadCount(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException("Assignment count bound exceeded."); return count;
    }
    private static bool ReadBoolean(BinaryReader reader)
    {
        var value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Noncanonical assignment boolean."); return value != 0;
    }
    private static string ReadFingerprint(BinaryReader reader) { var value = ReadText(reader, 64); RequireFingerprint(value); return value; }
    private static void RequireFingerprint(string value)
    {
        if (value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Assignment requires exact lowercase SHA-256 fingerprint.");
    }
    private static int Compare(MembershipEvidence a, MembershipEvidence b)
    {
        var order = StringComparer.Ordinal.Compare(a.Category, b.Category); if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(a.Source, b.Source); return order != 0 ? order : StringComparer.Ordinal.Compare(a.Reason, b.Reason);
    }
    private static int Compare(FacetEvidence a, FacetEvidence b)
    {
        var order = StringComparer.Ordinal.Compare(a.Dimension, b.Dimension); if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(a.Value, b.Value); if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(a.Source, b.Source); return order != 0 ? order : StringComparer.Ordinal.Compare(a.Reason, b.Reason);
    }
    // Prevent inflater read-ahead from consuming bytes after the final deflate block. This permits strict trailing-data rejection.
    private sealed class ExactDeflateInput : MemoryStream
    {
        internal ExactDeflateInput(byte[] bytes) : base(bytes, false) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Math.Min(1, buffer.Length)));
    }
}
