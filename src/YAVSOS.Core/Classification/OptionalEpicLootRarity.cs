using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>
/// EpicLoot rarity semantics as detached data (verified on 0.14.13). The rarity names and values are EpicLoot's own
/// <c>ItemRarity</c> declaration, read at startup (#104, D171); until then, and without EpicLoot, the six known ones. Static material rarity comes from the loaded definition's
/// item type and ammo-type tag; equipment rarity is read freshly from one instance's existing custom data and is
/// never cached, written or attached to the prefab. Unsupported shapes stay Unknown instead of using EpicLoot's
/// permissive Magic fallback.
/// </summary>
public static class OptionalEpicLootRarity
{
    public const string Adapter = "epicloot";
    public const string AdapterVersion = "yavsos-epicloot-rarity-1";
    public const string PluginGuid = "randyknapp.mods.epicloot";
    // Upstream version the adapter was verified on; support is decided by the probed shape (D058).
    public const string VerifiedVersion = "0.14.13";
    // ItemInfo.dataKey(classKey(MagicItemComponent, "")): mod GUID + '#' + type full name; same-assembly types have no suffix.
    public const string InstanceDataKey = PluginGuid + "#EpicLoot.MagicItemComponent";
    public const int MaximumInstancePayload = 65536;
    public const int MaximumRarities = 64;
    /// <summary>The rarities EpicLoot 0.14.13 declares, in value order 0–5; the default before a declaration is read.</summary>
    public static IReadOnlyList<string> KnownRarityNames { get; } = Array.AsReadOnly(new[] { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient" });

    private sealed class Declaration
    {
        internal readonly string[] Names; internal readonly int[] Values; internal readonly IReadOnlyList<string> FilterValues;
        internal Declaration(string[] names, int[] values)
        { Names = names; Values = values; FilterValues = Array.AsReadOnly(names.Select(x => x.ToLowerInvariant()).ToArray()); }
    }
    private static volatile Declaration declared = new(KnownRarityNames.ToArray(), Enumerable.Range(0, 6).ToArray());

    /// <summary>The current rarity names in EpicLoot's spelling, in value order.</summary>
    public static IReadOnlyList<string> RarityNames => Array.AsReadOnly(declared.Names);
    /// <summary>The rule filter's values: <see cref="RarityNames"/> in lower case, in the same order.</summary>
    public static IReadOnlyList<string> FilterValues => declared.FilterValues;

    /// <summary>
    /// #104 (D171): the rarities the loaded EpicLoot declares, as (stored value, enum name). Newtonsoft stores the value
    /// by default. Called once at startup, before classification reads them; a rule naming a rarity no longer declared
    /// keeps it and shows it as stale (D065). Throws on a declaration YAVSOS can't use: none, too many, a duplicate value
    /// or name (ignoring case, since filter values are lower case), or a name that isn't a plain identifier.
    /// </summary>
    public static void Declare(IEnumerable<KeyValuePair<int, string>> rarities) => declared = Validate(rarities);

    /// <summary>Checks a declaration the way <see cref="Declare"/> does, without using it.</summary>
    public static void Validate(IEnumerable<KeyValuePair<int, string>> rarities, out IReadOnlyList<string> names)
    { var checkedOne = Validate(rarities); names = Array.AsReadOnly(checkedOne.Names); }

    private static Declaration Validate(IEnumerable<KeyValuePair<int, string>> rarities)
    {
        if (rarities == null) throw new ArgumentNullException(nameof(rarities));
        var list = rarities.Take(MaximumRarities + 1).OrderBy(x => x.Key).ToList();
        if (list.Count == 0 || list.Count > MaximumRarities) throw new ArgumentException("Unsupported number of rarities.");
        if (list.Select(x => x.Key).Distinct().Count() != list.Count) throw new ArgumentException("Duplicate rarity value.");
        foreach (var (_, name) in list)
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsLetter(name[0]) || name.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
                throw new ArgumentException("Unsupported rarity name.");
        if (list.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count) throw new ArgumentException("Duplicate rarity name.");
        return new Declaration(list.Select(x => x.Value).ToArray(), list.Select(x => x.Key).ToArray());
    }

    /// <summary>The index in <see cref="RarityNames"/> of a stored rarity value; -1 when EpicLoot doesn't declare it.</summary>
    private static int IndexOfValue(Declaration current, long value)
    {
        for (var i = 0; i < current.Values.Length; i++) if (current.Values[i] == value) return i;
        return -1;
    }

    /// <summary>The filter value of a stored rarity (a number, or its name), read against one declaration; null when undeclared.</summary>
    private static string? StoredRarity(Dictionary<string, JsonMember> members)
    {
        var current = declared;
        if (!members.TryGetValue("Rarity", out var rarity)) return null;
        var index = rarity.Kind == JsonKind.Integer ? IndexOfValue(current, rarity.Integer)
            : rarity.Kind == JsonKind.String ? IndexOf(current, rarity.Text) : -1;
        return index < 0 ? null : current.FilterValues[index];
    }
    private const string InstanceSource = "epicloot-instance:" + AdapterVersion;

    /// <summary>CraftingItemExtensions.IsMagicCraftingMaterial/IsRunestone plus GetCraftingMaterialRarity, strictly.</summary>
    public static string? MaterialRarity(CatalogFact item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (item.Kind != CatalogFactKind.Item || item.Value("type") != "Material") return null;
        var parts = item.Value("ammoType").Split('|');
        if (parts.Length != 2 || !parts[1].EndsWith("MagicCraftingMaterial", StringComparison.Ordinal) &&
            !parts[1].EndsWith("Runestone", StringComparison.Ordinal)) return null;
        var current = declared;
        var index = IndexOf(current, parts[0]);
        return index < 0 ? null : current.FilterValues[index];
    }

    /// <summary>Raw FacetEvidence/Coverage rows for registered static materials when EpicLoot is supported.</summary>
    public static IReadOnlyList<LoadedSourceFact> MaterialFacts(CatalogSnapshot catalog, OptionalCapability capability,
        CancellationToken cancellation = default)
    {
        if (catalog == null || capability == null) throw new ArgumentNullException(nameof(catalog));
        if (capability.Adapter != Adapter) throw new ArgumentException("EpicLoot capability required.");
        var rows = new List<LoadedSourceFact> { OptionalDefinitionEvidence.Coverage(Adapter, "capability",
            capability.Witness + "; rarity=" + AdapterVersion + "; equipment rarity is instance-only") };
        if (!capability.Supported) return rows.AsReadOnly();
        var skipped = 0;
        foreach (var item in catalog.Items.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var rarity = MaterialRarity(item);
            if (rarity == null) continue;
            var route = "EpicLoot crafting material definition: " + item.Value("ammoType");
            var identity = "optional:epicloot:rarity:" + item.Identity;
            if (!OptionalDefinitionEvidence.Scalar(identity) || !OptionalDefinitionEvidence.Scalar(route)) { skipped++; continue; }
            rows.Add(new LoadedSourceFact(LoadedSourceFactKind.FacetEvidence, identity,
                output: item.Identity, route: route, method: "rarity", detail: rarity));
        }
        rows.Add(OptionalDefinitionEvidence.Coverage(Adapter, "gaps", "oversized=" + skipped.ToString(CultureInfo.InvariantCulture) +
            "; shard stones and unsupported tags remain Unknown"));
        return rows.AsReadOnly();
    }

    /// <summary>
    /// Fresh current rarity for one equipment instance, shaped for <see cref="ClassificationAcceptance.Match"/>.
    /// Call on the thread that owns <paramref name="customData"/>; only a read is performed. Returns zero or one row.
    /// </summary>
    public static IReadOnlyList<FacetEvidence> ReadInstance(OptionalCapability capability, ClassificationResult item,
        IReadOnlyDictionary<string, string>? customData)
    {
        if (capability == null || item == null) throw new ArgumentNullException(nameof(item));
        if (capability.Adapter != Adapter || !capability.Supported || !item.InstanceRarity) return Array.Empty<FacetEvidence>();
        var rows = ReadInstanceForFilters(capability, item, customData);
        return rows.Count == 1 && rows[0].Value == ClassificationFilters.Unreadable ? Array.Empty<FacetEvidence>() : rows;
    }

    /// <summary>
    /// <see cref="ReadInstance"/> for rule filters (P6-07b), keeping "has no rarity" apart from "could not be read".
    /// Empty (plain, "unknown" in a rule) only when EpicLoot is absent or the instance verifiably carries no magic item:
    /// no component data, an empty value or JSON <c>null</c>. An incompatible adapter or an unsupported, malformed or
    /// oversized payload gives one <see cref="ClassificationFilters.Unreadable"/> row, which no rarity filter accepts.
    /// </summary>
    public static IReadOnlyList<FacetEvidence> ReadInstanceForFilters(OptionalCapability capability, ClassificationResult item,
        IReadOnlyDictionary<string, string>? customData)
    {
        if (capability == null || item == null) throw new ArgumentNullException(nameof(item));
        if (capability.Adapter != Adapter) throw new ArgumentException("EpicLoot capability required.");
        if (!item.InstanceRarity || capability.State == OptionalCapabilityState.Absent) return Array.Empty<FacetEvidence>();
        if (!capability.Supported) return new[] { ClassificationFilters.UnreadableRow("rarity") };
        if (customData == null || !customData.TryGetValue(InstanceDataKey, out var payload) || payload == null || payload.Length == 0)
            return Array.Empty<FacetEvidence>();
        if (payload.Length > MaximumInstancePayload) return new[] { ClassificationFilters.UnreadableRow("rarity") };
        var complete = new JsonScan(payload).TopLevel(out var members);
        if (complete && members == null) return Array.Empty<FacetEvidence>();
        var value = complete ? StoredRarity(members!) : null;
        return value == null ? new[] { ClassificationFilters.UnreadableRow("rarity") } : new[] { new FacetEvidence("rarity",
            value, InstanceSource, "EpicLoot MagicItem rarity on this item instance") };
    }

    private static int IndexOf(Declaration current, string name)
    {
        for (var i = 0; i < current.Names.Length; i++) if (current.Names[i] == name) return i;
        return -1;
    }

    // Accepts only a complete JSON object with one Rarity; its data version is not checked (#104, D171). Null
    // MagicItem, malformed/duplicate/case-variant members, non-integer or undeclared values remain Unknown.
    internal static int InstanceRarityIndex(string payload)
    {
        var reader = new JsonScan(payload);
        if (!reader.TopLevel(out var members)) return -1;
        return members == null ? -1 : StoredRarityIndex(members);
    }

    internal static int StoredRarityIndex(Dictionary<string, JsonMember> members)
    {
        if (!members.TryGetValue("Rarity", out var rarity)) return -1;
        var current = declared;
        if (rarity.Kind == JsonKind.Integer) return IndexOfValue(current, rarity.Integer);
        return rarity.Kind == JsonKind.String ? IndexOf(current, rarity.Text) : -1;
    }

    internal enum JsonKind { Other, Integer, String, Boolean }
    internal readonly struct JsonMember
    {
        internal JsonKind Kind { get; }
        internal long Integer { get; }
        internal string Text { get; }
        internal JsonMember(JsonKind kind, long integer, string text) { Kind = kind; Integer = integer; Text = text; }
    }

    /// <summary>Strict bounded RFC 8259 scanner. It records only the top-level Rarity (and IsUnidentified) values.</summary>
    internal sealed class JsonScan
    {
        private const int MaximumDepth = 32;
        private readonly string text;
        private readonly bool identification;
        private int position;
        internal JsonScan(string text, bool identification = false) { this.text = text; this.identification = identification; }

        internal bool TopLevel(out Dictionary<string, JsonMember>? members)
        {
            members = null;
            try
            {
                Space();
                if (Literal("null")) { Space(); return position == text.Length; }
                if (!Peek('{')) return false;
                members = new Dictionary<string, JsonMember>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                position++; Space();
                if (!Peek('}'))
                    while (true)
                    {
                        var key = String(); Space(); Expect(':'); Space();
                        // Newtonsoft binds members case-insensitively; any variant/duplicate makes the binding ambiguous.
                        if (!seen.Add(key)) return false;
                        var relevant = string.Equals(key, "Rarity", StringComparison.OrdinalIgnoreCase) ||
                            identification && string.Equals(key, "IsUnidentified", StringComparison.OrdinalIgnoreCase);
                        if (relevant && key != "Rarity" && key != "IsUnidentified") return false;
                        var value = Value(1, relevant);
                        if (relevant) members.Add(key, value);
                        Space();
                        if (Peek(',')) { position++; Space(); continue; }
                        break;
                    }
                Expect('}'); Space();
                return position == text.Length;
            }
            catch (FormatException) { members = null; return false; }
        }

        private JsonMember Value(int depth, bool capture)
        {
            if (depth > MaximumDepth || position >= text.Length) throw new FormatException();
            var c = text[position];
            if (c == '"') { var value = String(); return new JsonMember(JsonKind.String, 0, capture ? value : ""); }
            if (c == '{' || c == '[') { Container(depth); return default; }
            if (c == '-' || c >= '0' && c <= '9') return Number();
            if (Literal("true")) return capture ? new JsonMember(JsonKind.Boolean, 1, "") : default;
            if (Literal("false")) return capture ? new JsonMember(JsonKind.Boolean, 0, "") : default;
            if (Literal("null")) return default;
            throw new FormatException();
        }
        private void Container(int depth)
        {
            var close = text[position] == '{' ? '}' : ']'; var isObject = close == '}';
            position++; Space();
            if (Peek(close)) { position++; return; }
            while (true)
            {
                if (isObject) { String(); Space(); Expect(':'); Space(); }
                Value(depth + 1, false); Space();
                if (Peek(',')) { position++; Space(); continue; }
                Expect(close); return;
            }
        }
        private JsonMember Number()
        {
            var start = position;
            if (Peek('-')) position++;
            if (Peek('0')) position++;
            else if (Digit()) while (Digit()) { }
            else throw new FormatException();
            var integer = true;
            if (Peek('.')) { integer = false; position++; if (!Digit()) throw new FormatException(); while (Digit()) { } }
            if (Peek('e') || Peek('E'))
            {
                integer = false; position++;
                if (Peek('+') || Peek('-')) position++;
                if (!Digit()) throw new FormatException();
                while (Digit()) { }
            }
            return integer && long.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value) ? new JsonMember(JsonKind.Integer, value, "") : default;
        }
        private string String()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (true)
            {
                if (position >= text.Length) throw new FormatException();
                var c = text[position++];
                if (c == '"') return builder.ToString();
                if (c < ' ') throw new FormatException();
                if (c != '\\') { builder.Append(c); continue; }
                if (position >= text.Length) throw new FormatException();
                switch (text[position++])
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length || !int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                        builder.Append((char)code); position += 4; break;
                    default: throw new FormatException();
                }
            }
        }
        private bool Digit()
        {
            if (position < text.Length && text[position] >= '0' && text[position] <= '9') { position++; return true; }
            return false;
        }
        private bool Literal(string word)
        {
            if (string.CompareOrdinal(text, position, word, 0, word.Length) != 0) return false;
            position += word.Length; return true;
        }
        private bool Peek(char c) => position < text.Length && text[position] == c;
        private void Expect(char c) { if (!Peek(c)) throw new FormatException(); position++; }
        private void Space() { while (position < text.Length && text[position] is ' ' or '\t' or '\n' or '\r') position++; }
    }
}
