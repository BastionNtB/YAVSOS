using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Transfers;

public enum ContainerPolicyReadState { Absent, Supported, Unsupported }

/// <summary>Why an edit was refused as <see cref="TransferRefusal.UnsupportedCapability"/> (B4.7), so the player is told the real cause.</summary>
public enum ChestRuleEditProblem { None, UnreadableRule, RevisionExhausted, UnknownCategory, TooLarge, Invalid }

/// <summary>The original payload survives unsupported/unknown formats; parsing grants no edit authority.</summary>
public sealed class ContainerPolicyRead
{
    public ContainerPolicyReadState State { get; }
    public string OriginalPayload { get; }
    public ulong Revision { get; }
    public ChestRules? Rules { get; }
    internal ContainerPolicyRead(ContainerPolicyReadState state, string payload, ulong revision = 0, ChestRules? rules = null)
    { State = state; OriginalPayload = payload; Revision = revision; Rules = rules; }
    /// <summary>A configured rule decides for this chest; absent or unconfigured leaves the stasher's defaults.</summary>
    public bool HasRule => State == ContainerPolicyReadState.Supported && Rules?.State == ChestRuleState.Configured;
}

/// <summary>
/// The editable part of a chest rule: one alternative of categories with its filters (D048, P6-07b), the exact
/// includes/excludes and priority. Other stored alternatives stay exactly as stored.
/// </summary>
public sealed class ChestRuleEdit
{
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    /// <summary>Stored category IDs the current category set lacks; kept on save unless removed (D050).</summary>
    public IReadOnlyList<string> UnknownCategories { get; }
    /// <summary>
    /// Stored parts this view leaves out and an editor cannot show or edit (D086 D5): other alternatives and the overflow
    /// flag. They stay on save; clearing the rule removes them. Zero for an edit built by hand.
    /// </summary>
    public int HiddenParts { get; private set; }
    public const int MaximumPriority = 999;
    /// <summary>Orders equally specific chests, higher first (D057 O3); null keeps the stored priority.</summary>
    public int? Priority { get; }
    /// <summary>Filters of the edited alternative: values within a dimension are OR, dimensions AND (D048). Empty: none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; }
    /// <summary>
    /// The stored filter set whose alternatives this edit replaces, as <see cref="From"/> presented it. Empty: the
    /// unfiltered category alternatives, as before filters had an editor.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> EditedFilters { get; }
    public ChestRuleEdit(IEnumerable<string>? categories, IEnumerable<string>? includes, IEnumerable<string>? excludes,
        IEnumerable<string>? unknownCategories = null, int? priority = null,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? filters = null,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? editedFilters = null)
    {
        Categories = Own(categories, 256); Includes = Own(includes, 2048); Excludes = Own(excludes, 2048);
        UnknownCategories = Own(unknownCategories, 256);
        if (priority is < -MaximumPriority or > MaximumPriority) throw new ArgumentOutOfRangeException(nameof(priority));
        Priority = priority;
        Filters = OwnFilters(filters); EditedFilters = OwnFilters(editedFilters);
    }
    /// <summary>
    /// The editable view of a stored rule. The edited alternatives are the unfiltered category ones. With
    /// <paramref name="withFilters"/>, when there are none and every filtered alternative shares one filter set (all
    /// with categories, or a single filters-only one), those instead; an editor that passes this view must save its
    /// <see cref="Filters"/> and <see cref="EditedFilters"/>. Anything else stays stored as is and is not shown here.
    /// </summary>
    public static ChestRuleEdit From(ChestRules rules, bool withFilters = false)
    {
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        var rows = Alternatives(rules).ToArray();
        var key = "";
        // B1.12: another writer may store a priority outside the editor's range; the view shows the nearest one.
        var priority = Math.Max(-MaximumPriority, Math.Min(MaximumPriority, rules.Priority));
        if (withFilters && !rows.Any(x => x.Key.Length == 0 && x.HasCategories))
        {
            var filtered = rows.Where(x => x.Key.Length != 0).ToArray();
            var keys = filtered.Select(x => x.Key).Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Length == 1 && (filtered.All(x => x.HasCategories) || filtered.Length == 1)) key = keys[0];
        }
        var edited = rows.Where(x => Edited(x, key)).ToArray();
        var filters = key.Length == 0 ? null : edited[0].Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray();
        return new ChestRuleEdit(edited.SelectMany(x => x.Known), rules.Includes, rules.Excludes, edited.SelectMany(x => x.Unknown),
            priority, filters, filters) { HiddenParts = rows.Length - edited.Length + (rules.Overflow ? 1 : 0) };
    }

    internal sealed class Alternative
    {
        internal string Key = ""; internal bool HasCategories; internal IReadOnlyDictionary<string, IReadOnlyList<string>> Filters = null!;
        internal IEnumerable<string> Known = Array.Empty<string>(); internal IEnumerable<string> Unknown = Array.Empty<string>();
        internal ClassificationFilterRule? Full; internal ChestRuleAlternative? Preserved;
    }
    internal static IEnumerable<Alternative> Alternatives(ChestRules rules) =>
        rules.FullRules.Select(x => new Alternative { Key = Key(x.Filters), HasCategories = x.Categories.Count != 0, Filters = x.Filters, Known = x.Categories, Full = x })
            .Concat(rules.Preserved.Select(x => new Alternative { Key = Key(x.Filters), HasCategories = true, Filters = x.Filters,
                Known = x.Effective?.Categories ?? (IEnumerable<string>)Array.Empty<string>(), Unknown = x.UnknownCategories, Preserved = x }));
    /// <summary>An alternative the edit with this filter key replaces; an unfiltered category-free ("any") one has no editor.</summary>
    internal static bool Edited(Alternative row, string key) => row.Key == key && (row.HasCategories || key.Length != 0);
    internal static string Key(IReadOnlyDictionary<string, IReadOnlyList<string>> filters) =>
        string.Join(";", filters.Where(x => x.Value.Count != 0).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + string.Join(",", x.Value.OrderBy(v => v, StringComparer.Ordinal))));
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> OwnFilters(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? input)
    {
        var owned = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in ClassificationValidation.Bounded(input ?? Array.Empty<KeyValuePair<string, IEnumerable<string>>>(), ClassificationFilters.MaximumRuleFilters))
        {
            var values = ClassificationValidation.Bounded(pair.Value ?? throw new ArgumentNullException(nameof(input)), 64)
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            ClassificationFilters.ValidateRuleFilter(pair.Key, values);
            if (owned.ContainsKey(pair.Key)) throw new ArgumentException("Duplicate filter dimension.");
            if (values.Length != 0) owned.Add(pair.Key, Array.AsReadOnly(values));
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(owned);
    }
    private static IReadOnlyList<string> Own(IEnumerable<string>? input, int limit)
    {
        var rows = ClassificationValidation.Bounded(input ?? Array.Empty<string>(), limit);
        foreach (var row in rows) ClassificationValidation.Text(row, 2048);
        return Array.AsReadOnly(rows.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Schema-1 JSON owner envelope around the existing detached schema-1 rules XML.</summary>
public static class ContainerPolicyEnvelope
{
    public const string ZdoKey = "com.bastionntb.yavsos.container-policy";
    private const string RuleFormat = "yavsos-chest-rules-xml-1";
    public static string Serialize(ulong revision, ChestRules rules)
    {
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        var root = new XElement("root", new XAttribute("type", "object"),
            Value("schemaVersion", "number", "1"), Value("revision", "string", revision.ToString(CultureInfo.InvariantCulture)),
            new XElement("policy", new XAttribute("type", "object"), Value("format", "string", RuleFormat),
                Value("rulesXml", "string", ChestRuleCodec.Serialize(rules))));
        using var stream = new BoundedTransferStream(TransferLimits.MaximumPolicyBytes);
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, TransferCanonical.Utf8, false)) root.WriteTo(writer);
        return TransferCanonical.Utf8.GetString(stream.ToArray());
    }
    public static ContainerPolicyRead Read(string payload, ClassificationTaxonomy taxonomy)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        if (payload.Length == 0) return new ContainerPolicyRead(ContainerPolicyReadState.Absent, payload);
        try
        {
            TransferCanonical.Text(payload, TransferLimits.MaximumPolicyBytes, false);
            ValidateJsonSyntax(payload);
            var bytes = TransferCanonical.Utf8.GetBytes(payload);
            var quotas = new XmlDictionaryReaderQuotas
            {
                MaxDepth = 8, MaxStringContentLength = TransferLimits.MaximumPolicyBytes,
                MaxArrayLength = TransferLimits.MaximumPolicyBytes, MaxBytesPerRead = TransferLimits.MaximumPolicyBytes,
                MaxNameTableCharCount = 1024
            };
            using var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, 0, bytes.Length, TransferCanonical.Utf8, quotas, null);
            var root = XElement.Load(reader, LoadOptions.None);
            Object(root, "schemaVersion", "revision", "policy");
            if (Leaf(root, "schemaVersion", "number") != "1") throw new InvalidDataException("Unknown policy envelope schema.");
            var text = Leaf(root, "revision", "string");
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision.ToString(CultureInfo.InvariantCulture) != text)
                throw new InvalidDataException("Invalid policy revision.");
            var policy = root.Element("policy")!; Object(policy, "format", "rulesXml");
            if (Leaf(policy, "format", "string") != RuleFormat) throw new InvalidDataException("Unsupported policy rules format.");
            var rules = ChestRuleCodec.Parse(Leaf(policy, "rulesXml", "string"), taxonomy);
            return new ContainerPolicyRead(ContainerPolicyReadState.Supported, payload, revision, rules);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is XmlException || ex is InvalidDataException || ex is FormatException)
        { return new ContainerPolicyRead(ContainerPolicyReadState.Unsupported, payload); }
    }
    /// <summary>Constructs a candidate edit after exact payload comparison. The owner adapter must recheck access and ownership.
    /// Alternatives the edit doesn't replace, priority and overflow are preserved. An edit that leaves no part of the rule
    /// removes it, as clearing does (D051): the updated payload is empty, so the chest is unruled for every reader.</summary>
    /// <param name="edit">Categories and exact items (D048); null clears every part of the rule, priority included (D086 D5).</param>
    public static TransferRefusal TryEdit(string currentPayload, string expectedPayload, ClassificationTaxonomy taxonomy,
        ChestRuleEdit? edit, out string? updatedPayload) => TryEdit(currentPayload, expectedPayload, taxonomy, edit, out updatedPayload, out _);

    /// <param name="problem">The cause when the result is <see cref="TransferRefusal.UnsupportedCapability"/>; otherwise None.</param>
    public static TransferRefusal TryEdit(string currentPayload, string expectedPayload, ClassificationTaxonomy taxonomy,
        ChestRuleEdit? edit, out string? updatedPayload, out ChestRuleEditProblem problem)
    {
        updatedPayload = null; problem = ChestRuleEditProblem.None;
        if (!StringComparer.Ordinal.Equals(currentPayload, expectedPayload)) return TransferRefusal.PolicyChanged;
        var read = Read(currentPayload, taxonomy);
        // The owner may explicitly clear a rule this build cannot read; it is never edited or reinterpreted.
        if (edit == null) { updatedPayload = ""; return TransferRefusal.None; }
        if (read.State == ContainerPolicyReadState.Unsupported) { problem = ChestRuleEditProblem.UnreadableRule; return TransferRefusal.UnsupportedCapability; }
        if (read.Revision == ulong.MaxValue) { problem = ChestRuleEditProblem.RevisionExhausted; return TransferRefusal.UnsupportedCapability; }
        var previous = read.Rules;
        try
        {
            ChestRules rules;
            {
                // The panel edits one alternative (categories and its filters), exact items and priority (D057 O3,
                // P6-07b). Every other alternative and overflow have no editor and stay exactly as stored; the panel
                // says they are there (D086 D5).
                var key = ChestRuleEdit.Key(edit.EditedFilters);
                var others = previous == null ? Array.Empty<ChestRuleEdit.Alternative>()
                    : ChestRuleEdit.Alternatives(previous).Where(x => !ChestRuleEdit.Edited(x, key)).ToArray();
                var kept = others.Where(x => x.Full != null).Select(x => x.Full!);
                var keptPreserved = others.Where(x => x.Preserved != null).Select(x => x.Preserved!);
                // Unknown category IDs the owner kept stay stored with the edited categories (D050).
                foreach (var id in edit.Categories)
                    if (!taxonomy.Categories.ContainsKey(id)) { problem = ChestRuleEditProblem.UnknownCategory; return TransferRefusal.UnsupportedCapability; }
                var filters = edit.Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray();
                var edited = edit.Categories.Count + edit.UnknownCategories.Count == 0
                    ? filters.Length == 0 ? (null, null) : (new ClassificationFilterRule(taxonomy, Array.Empty<string>(), filters), null)
                    : ChestRuleCodec.Alternative(taxonomy, edit.Categories.Concat(edit.UnknownCategories), filters);
                var overflow = previous?.State == ChestRuleState.Configured && previous.Overflow;
                if (edited.Rule == null && edited.Preserved == null && others.Length == 0 && edit.Includes.Count + edit.Excludes.Count == 0 && !overflow)
                { updatedPayload = ""; return TransferRefusal.None; }
                rules = new ChestRules(taxonomy, ChestRuleState.Configured,
                    edited.Rule == null ? kept : kept.Concat(new[] { edited.Rule }), edit.Includes, edit.Excludes,
                    edit.Priority ?? previous?.Priority ?? 0, overflow,
                    preserved: edited.Preserved == null ? keptPreserved : keptPreserved.Concat(new[] { edited.Preserved }));
            }
            try { updatedPayload = Serialize(read.Revision + 1, rules); }
            catch (InvalidDataException) { problem = ChestRuleEditProblem.TooLarge; throw; }
            return TransferRefusal.None;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException)
        {
            updatedPayload = null;
            if (problem == ChestRuleEditProblem.None) problem = ChestRuleEditProblem.Invalid;
            return TransferRefusal.UnsupportedCapability;
        }
    }
    private static XElement Value(string name, string type, string value) => new(name, new XAttribute("type", type), value);
    // The framework's JSON/XML reader tolerates a missing final object delimiter.
    // Validate this deliberately small object/string/number format completely before decoding.
    private static void ValidateJsonSyntax(string text)
    {
        var at = 0;
        void Space() { while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n')) at++; }
        bool Take(char value) { Space(); if (at >= text.Length || text[at] != value) return false; at++; return true; }
        void Need(char value) { if (!Take(value)) throw new InvalidDataException("Incomplete policy JSON."); }
        void String()
        {
            Need('"');
            while (at < text.Length)
            {
                var value = text[at++];
                if (value == '"') return;
                if (value < ' ') throw new InvalidDataException("Unescaped policy JSON control character.");
                if (value != '\\') continue;
                if (at >= text.Length) throw new InvalidDataException("Incomplete policy JSON escape.");
                var escape = text[at++];
                if (escape == 'u')
                {
                    for (var digit = 0; digit < 4; digit++)
                    {
                        if (at >= text.Length || !Uri.IsHexDigit(text[at++])) throw new InvalidDataException("Invalid policy JSON Unicode escape.");
                    }
                }
                else if (escape != '"' && escape != '\\' && escape != '/' && escape != 'b' && escape != 'f' && escape != 'n' && escape != 'r' && escape != 't')
                    throw new InvalidDataException("Invalid policy JSON escape.");
            }
            throw new InvalidDataException("Incomplete policy JSON string.");
        }
        bool Digit() => at < text.Length && text[at] >= '0' && text[at] <= '9';
        void Number()
        {
            if (at < text.Length && text[at] == '-') at++;
            if (!Digit()) throw new InvalidDataException("Invalid policy JSON number.");
            if (text[at] == '0') at++;
            else while (Digit()) at++;
            if (at < text.Length && text[at] == '.')
            { at++; if (!Digit()) throw new InvalidDataException("Invalid policy JSON fraction."); while (Digit()) at++; }
            if (at < text.Length && (text[at] == 'e' || text[at] == 'E'))
            {
                at++; if (at < text.Length && (text[at] == '+' || text[at] == '-')) at++;
                if (!Digit()) throw new InvalidDataException("Invalid policy JSON exponent."); while (Digit()) at++;
            }
        }
        void ObjectSyntax(int depth)
        {
            if (depth > 8) throw new InvalidDataException("Policy JSON depth bound.");
            Need('{'); if (Take('}')) return;
            do
            {
                String(); Need(':'); Space();
                if (at >= text.Length) throw new InvalidDataException("Incomplete policy JSON value.");
                if (text[at] == '{') ObjectSyntax(depth + 1);
                else if (text[at] == '"') String();
                else Number();
                if (Take('}')) return;
                Need(',');
            } while (true);
        }
        ObjectSyntax(1); Space();
        if (at != text.Length) throw new InvalidDataException("Trailing policy JSON content.");
    }
    private static void Object(XElement element, params string[] fields)
    {
        if ((string?)element.Attribute("type") != "object" || element.Attributes().Count() != 1 ||
            element.Elements().Count() != fields.Length || fields.Any(f => element.Elements(f).Count() != 1) ||
            element.Nodes().Any(n => n is not XElement)) throw new InvalidDataException("Unsupported policy object shape.");
    }
    private static string Leaf(XElement parent, string name, string type)
    {
        var element = parent.Element(name) ?? throw new InvalidDataException("Missing policy field.");
        if ((string?)element.Attribute("type") != type || element.Attributes().Count() != 1 || element.HasElements)
            throw new InvalidDataException("Invalid policy field type.");
        return element.Value;
    }
}
