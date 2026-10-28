using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace YAVSOS.Core.Classification;

public enum ChestRuleState { Unconfigured, Configured }

/// <summary>Immutable detached chest policy. An explicitly configured empty document refuses every item.</summary>
public sealed class ChestRules
{
    public ChestRuleState State { get; }
    public int Priority { get; }
    public bool Overflow { get; }
    public bool ExistingItemFallback { get; }
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    public ClassificationAcceptance Acceptance { get; }
    /// <summary>Stored alternatives naming categories the current set lacks (D050), kept verbatim.</summary>
    public IReadOnlyList<ChestRuleAlternative> Preserved { get; }
    /// <summary>Category IDs this rule stores but the current category set lacks; they match nothing (D050).</summary>
    public IReadOnlyList<string> UnknownCategories { get; }
    /// <summary>Alternatives whose categories are all known.</summary>
    public IReadOnlyList<ClassificationFilterRule> FullRules { get; }
    public string Fingerprint { get; }
    public ChestRules(ClassificationTaxonomy taxonomy, ChestRuleState state,
        IEnumerable<ClassificationFilterRule>? rules = null, IEnumerable<string>? includes = null,
        IEnumerable<string>? excludes = null, int priority = 0, bool overflow = false, bool existingItemFallback = false,
        IEnumerable<ChestRuleAlternative>? preserved = null)
    {
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        if (!Enum.IsDefined(typeof(ChestRuleState), state)) throw new ArgumentOutOfRangeException(nameof(state));
        var input = ClassificationValidation.Bounded(rules ?? Array.Empty<ClassificationFilterRule>(), 64);
        if (input.Any(x => x == null)) throw new ArgumentException("Null chest acceptance rule.");
        // Revalidate links against the selected current taxonomy; labels/ranks never enter chest identity.
        var validated = input.Select(x => new ClassificationFilterRule(taxonomy, x.Categories,
            x.Filters.Where(f => f.Value.Count != 0).Select(f => new KeyValuePair<string, IEnumerable<string>>(f.Key, f.Value))))
            .GroupBy(ChestRuleCodec.RuleXml, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.First()).ToArray();
        Includes = OwnPrefabs(includes); Excludes = OwnPrefabs(excludes);
        var kept = ClassificationValidation.Bounded(preserved ?? Array.Empty<ChestRuleAlternative>(), 64);
        if (kept.Any(x => x == null)) throw new ArgumentException("Null preserved chest alternative.");
        Preserved = Array.AsReadOnly(kept.GroupBy(x => x.Xml, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.First()).ToArray());
        if (validated.Length + Preserved.Count > 64) throw new ArgumentException("Chest alternative count bound exceeded.");
        UnknownCategories = Array.AsReadOnly(Preserved.SelectMany(x => x.UnknownCategories).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        if (state == ChestRuleState.Unconfigured && (validated.Length != 0 || Preserved.Count != 0 || Includes.Count != 0 || overflow))
            throw new ArgumentException("Unconfigured chests cannot have positive acceptance or overflow rules.");
        if (state == ChestRuleState.Configured && existingItemFallback)
            throw new ArgumentException("Existing-item fallback requires an explicitly unconfigured chest.");
        FullRules = Array.AsReadOnly(validated);
        State = state; Priority = priority; Overflow = overflow; ExistingItemFallback = existingItemFallback;
        // An alternative whose categories are all unknown is dropped from evaluation, never widened (D050).
        Acceptance = new ClassificationAcceptance(validated.Concat(Preserved.Where(x => x.Effective != null).Select(x => x.Effective!)), Includes, Excludes);
        var canonical = ChestRuleCodec.Serialize(this);
        Fingerprint = ClassificationValidation.Hash(w => { w.Write("yavsos-chest-rules-1"); w.Write(canonical); });
    }
    private static IReadOnlyList<string> OwnPrefabs(IEnumerable<string>? prefabs)
    {
        var rows = ClassificationValidation.Bounded(prefabs ?? Array.Empty<string>(), 2048);
        foreach (var row in rows) ClassificationValidation.Text(row, 2048);
        return Array.AsReadOnly(rows.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>A stored alternative that names categories the current category set lacks (D050).</summary>
public sealed class ChestRuleAlternative
{
    /// <summary>Canonical stored element, written back unchanged.</summary>
    public string Xml { get; }
    /// <summary>The known-category part used for evaluation; null when no named category is known.</summary>
    public ClassificationFilterRule? Effective { get; }
    public IReadOnlyList<string> UnknownCategories { get; }
    public bool Filtered => Filters.Count != 0;
    /// <summary>The stored filters, also when no named category is known.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; }
    internal ChestRuleAlternative(string xml, ClassificationFilterRule? effective, IEnumerable<string> unknown,
        IReadOnlyDictionary<string, IReadOnlyList<string>> filters)
    { Xml = xml; Effective = effective; UnknownCategories = Array.AsReadOnly(unknown.ToArray()); Filters = filters; }
}

/// <summary>Strict schema-1 detached XML. No persistence, migration, network or ZDO side effects.</summary>
public static class ChestRuleCodec
{
    public const int SchemaVersion = 1;
    public const int MaximumUtf8Bytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    public static string Serialize(ChestRules rules)
    {
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        var root = new XElement("chestRules", new XAttribute("schema", "1"),
            new XAttribute("state", rules.State == ChestRuleState.Configured ? "configured" : "unconfigured"),
            new XAttribute("priority", rules.Priority.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("overflow", rules.Overflow ? "true" : "false"),
            new XAttribute("existingItemFallback", rules.ExistingItemFallback ? "true" : "false"));
        foreach (var prefab in rules.Includes) root.Add(new XElement("include", new XAttribute("prefab", prefab)));
        foreach (var prefab in rules.Excludes) root.Add(new XElement("exclude", new XAttribute("prefab", prefab)));
        foreach (var rule in rules.FullRules) root.Add(RuleElement(rule));
        foreach (var alternative in rules.Preserved) root.Add(XElement.Parse(alternative.Xml));
        var xml = root.ToString(SaveOptions.DisableFormatting); Bound(xml); return xml;
    }
    internal static string RuleXml(ClassificationFilterRule rule) => RuleElement(rule).ToString(SaveOptions.DisableFormatting);
    private static XElement RuleElement(ClassificationFilterRule rule) => new XElement("rule",
        rule.Categories.Select(x => new XElement("category", new XAttribute("id", x))),
        rule.Filters.Where(x => x.Value.Count != 0).Select(x => new XElement("filter", new XAttribute("dimension", x.Key),
            x.Value.Select(v => new XElement("value", new XAttribute("id", v))))));
    public static ChestRules Parse(string xml, ClassificationTaxonomy taxonomy)
    {
        if (xml == null) throw new ArgumentNullException(nameof(xml));
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        Bound(xml);
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaximumUtf8Bytes, IgnoreComments = false, IgnoreProcessingInstructions = false });
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Nodes().Any(x => x is not XElement && !(x is XText t && string.IsNullOrWhiteSpace(t.Value))))
                throw new ArgumentException("Unsupported chest document node.");
            var root = document.Root ?? throw new ArgumentException("Missing chest document root.");
            Element(root, "chestRules", "schema", "state", "priority", "overflow", "existingItemFallback");
            if (Attr(root, "schema") != "1") throw new ArgumentException("Unsupported chest rule schema.");
            var state = Attr(root, "state") switch { "configured" => ChestRuleState.Configured, "unconfigured" => ChestRuleState.Unconfigured,
                _ => throw new ArgumentException("Unknown chest rule state.") };
            if (!int.TryParse(Attr(root, "priority"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var priority))
                throw new ArgumentException("Invalid chest priority.");
            var overflow = Boolean(root, "overflow"); var fallback = Boolean(root, "existingItemFallback");
            var includes = new List<string>(); var excludes = new List<string>(); var rules = new List<ClassificationFilterRule>();
            var preserved = new List<ChestRuleAlternative>();
            foreach (var child in root.Elements())
            {
                if (child.Name == "include" || child.Name == "exclude")
                {
                    Element(child, child.Name.LocalName, "prefab"); Leaf(child);
                    var list = child.Name == "include" ? includes : excludes;
                    if (list.Count == 2048) throw new ArgumentException("Chest prefab count bound exceeded.");
                    var prefab = Attr(child, "prefab");
                    if (list.Contains(prefab, StringComparer.Ordinal)) throw new ArgumentException("Duplicate chest prefab selector.");
                    list.Add(prefab);
                }
                else if (child.Name == "rule")
                {
                    if (rules.Count + preserved.Count == 64) throw new ArgumentException("Chest alternative count bound exceeded.");
                    var alternative = ParseRule(child, taxonomy);
                    if (alternative.Rule != null) rules.Add(alternative.Rule); else preserved.Add(alternative.Preserved!);
                }
                else throw new ArgumentException("Unknown chest rule element.");
            }
            return new ChestRules(taxonomy, state, rules, includes, excludes, priority, overflow, fallback, preserved);
        }
        catch (XmlException ex) { throw new ArgumentException("Malformed chest rule XML.", nameof(xml), ex); }
    }
    private static (ClassificationFilterRule? Rule, ChestRuleAlternative? Preserved) ParseRule(XElement rule, ClassificationTaxonomy taxonomy)
    {
        Element(rule, "rule"); var categories = new List<string>();
        var filters = new List<KeyValuePair<string, IEnumerable<string>>>();
        foreach (var child in rule.Elements())
        {
            if (child.Name == "category")
            {
                Element(child, "category", "id"); Leaf(child);
                if (categories.Count == 256) throw new ArgumentException("Chest category count bound exceeded.");
                var id = Attr(child, "id");
                if (categories.Contains(id, StringComparer.Ordinal)) throw new ArgumentException("Duplicate category link.");
                categories.Add(id);
            }
            else if (child.Name == "filter")
            {
                Element(child, "filter", "dimension");
                if (filters.Count == ClassificationFilters.MaximumRuleFilters) throw new ArgumentException("Chest dimension count bound exceeded.");
                var values = new List<string>();
                foreach (var value in child.Elements())
                {
                    Element(value, "value", "id"); Leaf(value);
                    if (values.Count == 64) throw new ArgumentException("Chest filter choice count bound exceeded.");
                    var id = Attr(value, "id");
                    if (values.Contains(id, StringComparer.Ordinal)) throw new ArgumentException("Duplicate filter choice.");
                    values.Add(id);
                }
                filters.Add(new KeyValuePair<string, IEnumerable<string>>(Attr(child, "dimension"), values));
            }
            else throw new ArgumentException("Unknown acceptance rule element.");
        }
        return Alternative(taxonomy, categories, filters);
    }

    /// <summary>
    /// D050: a full rule when every category is known; otherwise a preserved alternative that keeps the stored IDs and
    /// evaluates only the known ones (or nothing when none are known).
    /// </summary>
    public static (ClassificationFilterRule? Rule, ChestRuleAlternative? Preserved) Alternative(ClassificationTaxonomy taxonomy,
        IEnumerable<string> categories, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? filters = null)
    {
        var ids = ClassificationValidation.Bounded(categories, 256).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var id in ids) ClassificationValidation.Category(id);
        var choices = (filters ?? Array.Empty<KeyValuePair<string, IEnumerable<string>>>()).ToArray();
        var unknown = ids.Where(x => !taxonomy.Categories.ContainsKey(x)).ToArray();
        if (unknown.Length == 0) return (new ClassificationFilterRule(taxonomy, ids, choices), null);
        var known = ids.Where(x => taxonomy.Categories.ContainsKey(x)).ToArray();
        var effective = known.Length == 0 ? null : new ClassificationFilterRule(taxonomy, known, choices);
        // Filters are validated by the rule above when it exists; otherwise validate them on an empty-category probe.
        var shape = effective ?? new ClassificationFilterRule(taxonomy, Array.Empty<string>(), choices);
        var element = new XElement("rule", ids.Select(x => new XElement("category", new XAttribute("id", x))),
            shape.Filters.Where(x => x.Value.Count != 0).Select(x => new XElement("filter", new XAttribute("dimension", x.Key),
                x.Value.Select(v => new XElement("value", new XAttribute("id", v))))));
        var stored = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var filter in shape.Filters.Where(x => x.Value.Count != 0)) stored.Add(filter.Key, filter.Value);
        return (null, new ChestRuleAlternative(element.ToString(SaveOptions.DisableFormatting), effective, unknown,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(stored)));
    }
    private static void Bound(string xml)
    { if (xml.Length > MaximumUtf8Bytes || Utf8.GetByteCount(xml) > MaximumUtf8Bytes) throw new ArgumentException("Chest XML byte bound exceeded."); }
    private static string Attr(XElement element, string name) => element.Attribute(name)!.Value;
    private static bool Boolean(XElement element, string name) => Attr(element, name) switch
    { "true" => true, "false" => false, _ => throw new ArgumentException("Invalid chest rule boolean.") };
    private static void Element(XElement element, string name, params string[] attributes)
    {
        if (element.Name != name || element.Attributes().Any(x => !attributes.Contains(x.Name.ToString(), StringComparer.Ordinal)) ||
            attributes.Any(x => element.Attribute(x) == null)) throw new ArgumentException("Unknown or missing chest XML attributes/elements.");
        if (element.Nodes().Any(x => x is not XElement && !(x is XText t && string.IsNullOrWhiteSpace(t.Value))))
            throw new ArgumentException("Unsupported chest XML content.");
    }
    private static void Leaf(XElement element)
    { if (element.HasElements) throw new ArgumentException("Unexpected nested chest XML element."); }
}
