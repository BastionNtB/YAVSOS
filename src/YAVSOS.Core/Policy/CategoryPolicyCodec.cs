using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace YAVSOS.Core.Policy;

public static class CategoryPolicyCodec
{
    public const string FileName = "com.bastionntb.yavsos.categories.xml";
    public const string SyncIdentifier = "category-policy-v1"; // D078 B07: one style with authoritative-assignments-v1
    public const string Namespace = "urn:com.bastionntb.yavsos:categories";
    public const int MaxPayloadBytes = 262144;
    public const int MaxCategories = 256;
    public const int MaxAssignments = 2048;
    public static string EmptyDocument => $"<policy xmlns=\"{Namespace}\" schema=\"1\" revision=\"1\"><categories/><assignments/></policy>";

    [Flags]
    private enum Attributes { Schema = 1, Revision = 2, Id = 4, DisplayName = 8, Match = 16, Selector = 32, Primary = 64, Category = 128 }

    public static bool TryParse(string? payload, out CategoryPolicy? policy, out string diagnostic)
    {
        policy = null;
        try
        {
            Require(!string.IsNullOrWhiteSpace(payload), "Policy is empty.");
            Require(payload!.Length <= MaxPayloadBytes && Encoding.UTF8.GetByteCount(payload) <= MaxPayloadBytes,
                "Policy exceeds 262144 UTF-8 bytes.");
            using var input = new StringReader(payload);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxPayloadBytes, IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            reader.MoveToContent();
            Require(reader.NodeType == XmlNodeType.Element && reader.LocalName == "policy" && reader.NamespaceURI == Namespace,
                "Expected the YAVSOS policy namespace/root.");
            CheckAttributes(reader, Attributes.Schema | Attributes.Revision);
            Require(reader.GetAttribute("schema") == "1", "Unsupported policy schema; expected 1. Update compatible YAVSOS clients before applying it.");
            Require(long.TryParse(reader.GetAttribute("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
                    && revision > 0, "revision must be a positive 64-bit integer.");
            var categories = new List<CategoryDefinition>();
            var assignments = new List<CategoryAssignment>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var selectors = new HashSet<string>(StringComparer.Ordinal);
            var rootDepth = reader.Depth;
            var emptyRoot = reader.IsEmptyElement;
            reader.Read();
            var categoriesSeen = false;
            var assignmentsSeen = false;
            while (!emptyRoot && NextChild(reader, rootDepth, "policy"))
            {
                if (reader.NamespaceURI != Namespace) throw new InvalidDataException("Unknown element in policy.");
                if (reader.LocalName == "categories")
                {
                    Require(!categoriesSeen, "Expected exactly one categories element.");
                    categoriesSeen = true;
                    ReadCategories(reader, categories, ids);
                }
                else if (reader.LocalName == "assignments")
                {
                    Require(!assignmentsSeen, "Expected exactly one assignments element.");
                    assignmentsSeen = true;
                    ReadAssignments(reader, assignments, selectors);
                }
                else throw new InvalidDataException("Unknown element in policy.");
            }
            Require(categoriesSeen, "Expected exactly one categories element.");
            Require(assignmentsSeen, "Expected exactly one assignments element.");
            // Consume the whole document, including any invalid trailing content.
            while (reader.ReadState == ReadState.Interactive)
            {
                Require(IsWhitespace(reader), "Unexpected content after policy.");
                reader.Read();
            }
            // Sections can appear in either order. Resolve references after both
            // are read without retaining an XML object graph or provisional policy.
            foreach (var assignment in assignments)
                foreach (var id in assignment.Memberships)
                    if (!ids.Contains(id)) throw new InvalidDataException($"Unknown membership category '{id}'; define it in categories.");
            var sortedCategories = categories.ToArray();
            var sortedAssignments = assignments.ToArray();
            Array.Sort(sortedCategories, (left, right) => string.CompareOrdinal(left.Id, right.Id));
            Array.Sort(sortedAssignments, CompareAssignments);
            var canonical = Canonical(revision, sortedCategories, sortedAssignments, payload.Length);
            // D086 M5 (B1.6): what is published is the canonical form, which can be longer than the file (spaced empty
            // tags). Every client refuses one over the limit, so it is refused here and the last accepted policy stays.
            var canonicalBytes = Encoding.UTF8.GetByteCount(canonical);
            Require(canonicalBytes <= MaxPayloadBytes, $"Policy's canonical form is {canonicalBytes} UTF-8 bytes (the file is " +
                $"{Encoding.UTF8.GetByteCount(payload)}), over the {MaxPayloadBytes}-byte limit every peer accepts. Shorten the file.");
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var hex = new char[hash.Length * 2];
            const string digits = "0123456789abcdef";
            for (var i = 0; i < hash.Length; i++) { hex[i * 2] = digits[hash[i] >> 4]; hex[i * 2 + 1] = digits[hash[i] & 15]; }
            policy = new CategoryPolicy(revision, Array.AsReadOnly(sortedCategories), Array.AsReadOnly(sortedAssignments), canonical, new string(hex));
            diagnostic = "";
            return true;
        }
        catch (Exception exception) when (exception is XmlException || exception is ArgumentException || exception is InvalidDataException)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private static void ReadCategories(XmlReader reader, List<CategoryDefinition> categories, HashSet<string> ids)
    {
        CheckAttributes(reader, 0);
        var depth = reader.Depth;
        var empty = reader.IsEmptyElement;
        reader.Read();
        while (!empty && NextChild(reader, depth, "categories"))
        {
            Require(reader.LocalName == "category" && reader.NamespaceURI == Namespace, "Unknown element in categories.");
            Require(categories.Count < MaxCategories, "At most 256 categories are supported.");
            CheckAttributes(reader, Attributes.Id | Attributes.DisplayName);
            var id = Required(reader, "id");
            Require(ValidId(id), "Category IDs must contain 1–64 lowercase ASCII letters, digits, dots, underscores, or hyphens.");
            if (!ids.Add(id)) throw new InvalidDataException($"Duplicate category ID '{id}'.");
            var name = Required(reader, "displayName");
            Require(ValidDisplayName(name), "displayName must be 1–128 characters without controls.");
            ReadLeaf(reader, "category");
            categories.Add(new CategoryDefinition(id, name));
        }
    }

    private static void ReadAssignments(XmlReader reader, List<CategoryAssignment> assignments, HashSet<string> selectors)
    {
        CheckAttributes(reader, 0);
        var depth = reader.Depth;
        var empty = reader.IsEmptyElement;
        reader.Read();
        while (!empty && NextChild(reader, depth, "assignments"))
        {
            Require(reader.LocalName == "assignment" && reader.NamespaceURI == Namespace, "Unknown element in assignments.");
            Require(assignments.Count < MaxAssignments, "At most 2048 assignments are supported.");
            CheckAttributes(reader, Attributes.Match | Attributes.Selector | Attributes.Primary);
            var match = Required(reader, "match");
            var selector = Required(reader, "selector");
            Require(match == "exact" || match == "glob", "match must be exact or glob.");
            Require(ValidSelector(selector, out var wildcard), "selector must be a bounded ASCII prefab name/pattern.");
            Require(match != "exact" || !wildcard, "Exact selectors cannot contain wildcards.");
            Require(match != "glob" || wildcard, "Glob selectors must contain * or ?.");
            if (!selectors.Add(match + ":" + selector)) throw new InvalidDataException($"Duplicate {match} selector '{selector}'.");
            var primary = reader.GetAttribute("primary");
            var memberships = new HashSet<string>(StringComparer.Ordinal);
            var entryDepth = reader.Depth;
            var emptyEntry = reader.IsEmptyElement;
            reader.Read();
            while (!emptyEntry && NextChild(reader, entryDepth, "assignment"))
            {
                Require(reader.LocalName == "member" && reader.NamespaceURI == Namespace, "Unknown element in assignment.");
                CheckAttributes(reader, Attributes.Category);
                var id = Required(reader, "category");
                if (!memberships.Add(id)) throw new InvalidDataException($"Duplicate membership '{id}'.");
                ReadLeaf(reader, "member");
            }
            Require(memberships.Count > 0, "Each assignment needs at least one membership.");
            Require(primary == null || memberships.Contains(primary), "primary must be one of the assignment's memberships.");
            var orderedMemberships = new string[memberships.Count];
            memberships.CopyTo(orderedMemberships);
            Array.Sort(orderedMemberships, StringComparer.Ordinal);
            assignments.Add(new CategoryAssignment(match, selector, Array.AsReadOnly(orderedMemberships), primary));
        }
    }

    private static string Canonical(long revision, CategoryDefinition[] categories, CategoryAssignment[] assignments, int capacity)
    {
        var output = new StringBuilder(capacity);
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false }))
        {
            writer.WriteStartElement("policy", Namespace);
            writer.WriteAttributeString("schema", "1");
            writer.WriteAttributeString("revision", revision.ToString(CultureInfo.InvariantCulture));
            // Match schema-1 XElement's attribute order and spaced empty tags exactly.
            writer.WriteAttributeString("xmlns", Namespace);
            writer.WriteStartElement("categories", Namespace);
            foreach (var category in categories)
            {
                writer.WriteStartElement("category", Namespace);
                writer.WriteAttributeString("id", category.Id);
                writer.WriteAttributeString("displayName", category.DisplayName);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("assignments", Namespace);
            foreach (var assignment in assignments)
            {
                writer.WriteStartElement("assignment", Namespace);
                writer.WriteAttributeString("match", assignment.Match);
                writer.WriteAttributeString("selector", assignment.Selector);
                if (assignment.Primary != null) writer.WriteAttributeString("primary", assignment.Primary);
                foreach (var id in assignment.Memberships)
                {
                    writer.WriteStartElement("member", Namespace);
                    writer.WriteAttributeString("category", id);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        return output.ToString();
    }

    private static int CompareAssignments(CategoryAssignment left, CategoryAssignment right)
    {
        var match = string.CompareOrdinal(left.Match, right.Match);
        return match != 0 ? match : string.CompareOrdinal(left.Selector, right.Selector);
    }
    private static bool ValidId(string value)
    {
        if (value.Length > 64) return false;
        foreach (var c in value) if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        return true;
    }
    private static bool ValidDisplayName(string value)
    {
        if (value.Length > 128) return false;
        foreach (var c in value) if (char.IsControl(c)) return false;
        return true;
    }
    private static bool ValidSelector(string value, out bool wildcard)
    {
        wildcard = false;
        if (value.Length > 128) return false;
        foreach (var c in value)
        {
            if (c < '!' || c > '~' || c is '<' or '>' or '"' or '\'' or '&' or '\\' or '/' or '[' or ']') return false;
            if (c == '*' || c == '?') wildcard = true;
        }
        return true;
    }
    private static string Required(XmlReader reader, string name)
    {
        var value = reader.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{reader.LocalName} requires {name}.");
        return value!;
    }
    private static void CheckAttributes(XmlReader reader, Attributes allowed)
    {
        var element = reader.LocalName;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.NamespaceURI == "http://www.w3.org/2000/xmlns/") continue;
                var flag = reader.LocalName switch
                {
                    "schema" => Attributes.Schema, "revision" => Attributes.Revision,
                    "id" => Attributes.Id, "displayName" => Attributes.DisplayName,
                    "match" => Attributes.Match, "selector" => Attributes.Selector,
                    "primary" => Attributes.Primary, "category" => Attributes.Category,
                    _ => (Attributes)0
                };
                if (reader.NamespaceURI.Length != 0 || flag == 0 || (allowed & flag) == 0)
                    throw new InvalidDataException($"Unknown attribute on {element}.");
            } while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
    }
    private static bool NextChild(XmlReader reader, int parentDepth, string parent)
    {
        while (reader.ReadState == ReadState.Interactive)
        {
            if (reader.NodeType == XmlNodeType.Element) return true;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == parentDepth) { reader.Read(); return false; }
            if (!IsWhitespace(reader)) throw new InvalidDataException($"Unexpected text in {parent}.");
            reader.Read();
        }
        throw new InvalidDataException($"Incomplete {parent} element.");
    }
    private static bool IsWhitespace(XmlReader reader) =>
        (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        && string.IsNullOrWhiteSpace(reader.Value);
    private static void ReadLeaf(XmlReader reader, string name)
    {
        var depth = reader.Depth;
        var empty = reader.IsEmptyElement;
        reader.Read();
        if (!empty && NextChild(reader, depth, name)) throw new InvalidDataException($"Unknown element in {name}.");
    }
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
