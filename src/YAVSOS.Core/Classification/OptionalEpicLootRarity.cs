using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using YAVSOS.Core.Catalog;

namespace YAVSOS.Core.Classification;

/// <summary>
/// EpicLoot 0.14.13 rarity semantics as detached data. Static material rarity comes from the loaded definition's
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
    public const int SupportedMagicItemVersion = 3;
    public const int MaximumInstancePayload = 65536;
    // EpicLoot.ItemRarity declaration order; Newtonsoft writes the numeric value by default.
    public static IReadOnlyList<string> RarityNames { get; } = Array.AsReadOnly(new[] { "Magic", "Rare", "Epic", "Legendary", "Mythic", "Ancient" });
    private const string InstanceSource = "epicloot-instance:" + AdapterVersion;

    /// <summary>CraftingItemExtensions.IsMagicCraftingMaterial/IsRunestone plus GetCraftingMaterialRarity, strictly.</summary>
    public static string? MaterialRarity(CatalogFact item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));
        if (item.Kind != CatalogFactKind.Item || item.Value("type") != "Material") return null;
        var parts = item.Value("ammoType").Split('|');
        if (parts.Length != 2 || !parts[1].EndsWith("MagicCraftingMaterial", StringComparison.Ordinal) &&
            !parts[1].EndsWith("Runestone", StringComparison.Ordinal)) return null;
        var index = IndexOf(parts[0]);
        return index < 0 ? null : RarityNames[index].ToLowerInvariant();
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
        var index = complete ? StoredRarityIndex(members!) : -1;
        return index < 0 ? new[] { ClassificationFilters.UnreadableRow("rarity") } : new[] { new FacetEvidence("rarity",
            RarityNames[index].ToLowerInvariant(), InstanceSource, "EpicLoot MagicItem rarity on this item instance") };
    }

    private static int IndexOf(string name)
    {
        for (var i = 0; i < RarityNames.Count; i++) if (RarityNames[i] == name) return i;
        return -1;
    }

    // Accepts only a complete JSON object with exactly one supported Version and one Rarity. Null MagicItem,
    // malformed/duplicate/case-variant members, non-integer or undeclared values remain Unknown.
    internal static int InstanceRarityIndex(string payload)
    {
        var reader = new JsonScan(payload);
        if (!reader.TopLevel(out var members)) return -1;
        return members == null ? -1 : StoredRarityIndex(members);
    }

    internal static int StoredRarityIndex(Dictionary<string, JsonMember> members)
    {
        if (!members.TryGetValue("Version", out var version) || !members.TryGetValue("Rarity", out var rarity)) return -1;
        if (version.Kind != JsonKind.Integer || version.Integer != SupportedMagicItemVersion) return -1;
        if (rarity.Kind == JsonKind.Integer) return rarity.Integer >= 0 && rarity.Integer < RarityNames.Count ? (int)rarity.Integer : -1;
        return rarity.Kind == JsonKind.String ? IndexOf(rarity.Text) : -1;
    }

    internal enum JsonKind { Other, Integer, String, Boolean }
    internal readonly struct JsonMember
    {
        internal JsonKind Kind { get; }
        internal long Integer { get; }
        internal string Text { get; }
        internal JsonMember(JsonKind kind, long integer, string text) { Kind = kind; Integer = integer; Text = text; }
    }

    /// <summary>Strict bounded RFC 8259 scanner. It records only top-level Version/Rarity values.</summary>
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
                        var relevant = string.Equals(key, "Version", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "Rarity", StringComparison.OrdinalIgnoreCase) ||
                            identification && string.Equals(key, "IsUnidentified", StringComparison.OrdinalIgnoreCase);
                        if (relevant && key != "Version" && key != "Rarity" && key != "IsUnidentified") return false;
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
