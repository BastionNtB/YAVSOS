using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace YAVSOS.Core.Templates;

/// <summary>How a stored template list read.</summary>
public enum TemplateReadState
{
    /// <summary>Nothing stored yet: an empty list.</summary>
    Empty,
    Valid,
    /// <summary>A later schema: kept exactly as stored and never written over by this version (D153).</summary>
    Newer,
    /// <summary>Not a template list this version can read: kept as stored and never written over (D153).</summary>
    Malformed
}

public sealed class TemplateListRead
{
    public TemplateReadState State { get; }
    /// <summary>
    /// The list, for <see cref="TemplateReadState.Empty"/> and <see cref="TemplateReadState.Valid"/>; for an unreadable
    /// list, the last good list a server keeps showing (<see cref="Kept"/>), or null.
    /// </summary>
    public TemplateList? List { get; }
    /// <summary>For the log: why it couldn't be read.</summary>
    public string Diagnostic { get; }
    /// <summary>Whether this version may write the list: only one it read (or an empty one), never newer or damaged data.</summary>
    public bool Writable { get; }

    private TemplateListRead(TemplateReadState state, TemplateList? list, string diagnostic, bool writable)
    { State = state; List = list; Diagnostic = diagnostic; Writable = writable; }

    internal static TemplateListRead Read(TemplateReadState state, TemplateList? list, string diagnostic) =>
        new TemplateListRead(state, list, diagnostic, list != null && state is TemplateReadState.Empty or TemplateReadState.Valid);

    /// <summary>Unreadable without reading it (a file far over the limit): kept as it is, never written over.</summary>
    public static TemplateListRead Unreadable(string diagnostic) => new TemplateListRead(TemplateReadState.Malformed, null, diagnostic ?? "", false);

    /// <summary>
    /// A server whose file became unreadable (a hand edit, a newer version's file) keeps showing <paramref name="last"/>,
    /// the last list it read, and refuses every change until the file can be read again (D153).
    /// </summary>
    public static TemplateListRead Kept(TemplateListRead unreadable, TemplateList last)
    {
        if (unreadable == null) throw new ArgumentNullException(nameof(unreadable));
        return new TemplateListRead(unreadable.State, last ?? throw new ArgumentNullException(nameof(last)), unreadable.Diagnostic, false);
    }
}

/// <summary>
/// X23 (D157, D166): the versioned text form of a template list, the same for personal templates (a file in the game's save folder),
/// the server's file and its synced value. Schema 1 is strict: an element or attribute it doesn't know makes the list
/// unreadable rather than dropped on the next write, so a later version that adds one raises the schema, and this
/// version keeps that data as it is (D153: no lost data). Writing is canonical: the same list gives the same text.
/// </summary>
public static class RuleTemplateCodec
{
    public const int Schema = 1;
    /// <summary>The server's file, beside the categories file in BepInEx/config.</summary>
    public const string FileName = "com.bastionntb.yavsos.templates.xml";
    /// <summary>The ServerSync custom value that carries the server's list (as category-policy-v1 carries the categories file).</summary>
    public const string SyncIdentifier = "rule-templates-v1";
    private const string Root = "yavsos-templates", Element = "template";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>The list's text.</summary>
    public static string Encode(TemplateList list)
    {
        if (list == null) throw new ArgumentNullException(nameof(list));
        return Write(writer =>
        {
            writer.WriteStartElement(Root);
            writer.WriteAttributeString("schema", Schema.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("kind", KindName(list.Kind));
            writer.WriteAttributeString("revision", list.Revision.ToString(CultureInfo.InvariantCulture));
            foreach (var template in list.Templates) WriteTemplate(writer, template);
            writer.WriteEndElement();
        });
    }

    /// <summary>The list's encoded size in UTF-8 bytes, which the list limits are measured on.</summary>
    public static int Bytes(TemplateList list) => Utf8.GetByteCount(Encode(list));

    /// <summary>One template's encoded size in UTF-8 bytes (<see cref="TemplateLimits.TemplateBytes"/>).</summary>
    public static int TemplateBytes(RuleTemplate template) => Utf8.GetByteCount(Write(writer => WriteTemplate(writer, template)));

    /// <summary>Reads a stored list. Blank text is an empty list; text over the kind's size limit is malformed.</summary>
    public static TemplateListRead Decode(string? text, TemplateListKind kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return TemplateListRead.Read(TemplateReadState.Empty, TemplateList.Empty(kind), "");
        // Room for a newer schema's list to be recognized as newer, not cut off as damaged.
        var limit = TemplateLimits.ListBytes(kind);
        if (text!.Length > limit || Utf8.GetByteCount(text) > limit) return Bad($"the list is over {limit / 1024} KiB");
        XElement root;
        try { root = Parse(text, limit); }
        catch (Exception error) when (error is XmlException || error is ArgumentException || error is InvalidOperationException) { return Bad(error.Message); }
        if (root.Name.LocalName != Root || root.Name.Namespace != XNamespace.None) return Bad("not a template list");
        if (!int.TryParse((string?)root.Attribute("schema"), NumberStyles.None, CultureInfo.InvariantCulture, out var schema) || schema < 1)
            return Bad("no schema");
        if (schema > Schema) return TemplateListRead.Read(TemplateReadState.Newer, null, $"schema {schema} is newer than this YAVSOS version's {Schema}");
        try
        {
            Only(root, "schema", "kind", "revision");
            if ((string?)root.Attribute("kind") != KindName(kind)) throw new InvalidDataException("the list is of another kind");
            var revision = Unsigned(root, "revision");
            var templates = new List<RuleTemplate>();
            foreach (var node in root.Nodes())
            {
                if (node is XElement element && element.Name.LocalName == Element && element.Name.Namespace == XNamespace.None) templates.Add(ReadTemplate(element));
                else if (node is not XText { Value: var space } || !string.IsNullOrWhiteSpace(space)) throw new InvalidDataException("unknown content in the list");
            }
            return TemplateListRead.Read(TemplateReadState.Valid, new TemplateList(kind, revision, templates), "");
        }
        catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is FormatException || error is OverflowException)
        { return Bad(error.Message); }
    }

    private static TemplateListRead Bad(string why) => TemplateListRead.Read(TemplateReadState.Malformed, null, why);

    internal static XElement Parse(string text, int limit)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit, MaxCharactersFromEntities = 0,
            IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = false, CloseInput = true
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        return XDocument.Load(reader, LoadOptions.None).Root ?? throw new InvalidDataException("empty document");
    }

    internal static string Write(Action<XmlWriter> body)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\n", Encoding = Utf8 };
        using (var writer = XmlWriter.Create(builder, settings)) body(writer);
        return builder.ToString();
    }

    internal static void WriteTemplate(XmlWriter writer, RuleTemplate template)
    {
        writer.WriteStartElement(Element);
        writer.WriteAttributeString("id", template.Id);
        writer.WriteAttributeString("revision", template.Revision.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", template.Name);
        if (template.By.Length != 0) writer.WriteAttributeString("by", template.By);
        if (template.Priority != 0) writer.WriteAttributeString("priority", template.Priority.ToString(CultureInfo.InvariantCulture));
        foreach (var id in template.Categories) writer.WriteElementString("category", id);
        foreach (var item in template.Includes) writer.WriteElementString("always", item);
        foreach (var item in template.Excludes) writer.WriteElementString("never", item);
        foreach (var filter in template.Filters)
        {
            writer.WriteStartElement("filter");
            writer.WriteAttributeString("dimension", filter.Key);
            foreach (var value in filter.Value) writer.WriteElementString("value", value);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    internal static RuleTemplate ReadTemplate(XElement element)
    {
        Only(element, "id", "revision", "name", "by", "priority");
        var id = (string?)element.Attribute("id") ?? throw new InvalidDataException("a template has no ID");
        var name = (string?)element.Attribute("name") ?? throw new InvalidDataException("a template has no name");
        var revision = Unsigned(element, "revision");
        var by = (string?)element.Attribute("by") ?? "";
        var priority = element.Attribute("priority") is { } p ? int.Parse(p.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var categories = new List<string>(); var always = new List<string>(); var never = new List<string>();
        var filters = new List<KeyValuePair<string, IEnumerable<string>>>();
        foreach (var node in element.Nodes())
        {
            if (node is XText { Value: var space } && string.IsNullOrWhiteSpace(space)) continue;
            if (node is not XElement child || child.Name.Namespace != XNamespace.None) throw new InvalidDataException("unknown content in a template");
            switch (child.Name.LocalName)
            {
                case "category": categories.Add(Leaf(child)); break;
                case "always": always.Add(Leaf(child)); break;
                case "never": never.Add(Leaf(child)); break;
                case "filter":
                    Only(child, "dimension");
                    var dimension = (string?)child.Attribute("dimension") ?? throw new InvalidDataException("a filter has no dimension");
                    var values = new List<string>();
                    foreach (var inner in child.Nodes())
                    {
                        if (inner is XText { Value: var gap } && string.IsNullOrWhiteSpace(gap)) continue;
                        if (inner is not XElement value || value.Name != "value") throw new InvalidDataException("unknown content in a filter");
                        values.Add(Leaf(value));
                    }
                    filters.Add(new KeyValuePair<string, IEnumerable<string>>(dimension, values));
                    break;
                default: throw new InvalidDataException("unknown element " + child.Name.LocalName);
            }
        }
        if (categories.Count + always.Count + never.Count + filters.Count == 0) throw new InvalidDataException("a template holds no rule");
        return new RuleTemplate(id, name, revision, by, categories, always, never, filters, priority);
    }

    private static string Leaf(XElement element)
    {
        if (element.HasAttributes || element.HasElements) throw new InvalidDataException("unknown content in " + element.Name.LocalName);
        return element.Value;
    }

    internal static void Only(XElement element, params string[] names)
    {
        foreach (var attribute in element.Attributes())
            if (attribute.Name.Namespace != XNamespace.None || !names.Contains(attribute.Name.LocalName))
                throw new InvalidDataException("unknown attribute " + attribute.Name.LocalName);
    }

    internal static ulong Unsigned(XElement element, string name) =>
        ulong.Parse((string?)element.Attribute(name) ?? throw new InvalidDataException("no " + name), NumberStyles.None, CultureInfo.InvariantCulture);

    private static string KindName(TemplateListKind kind) => kind == TemplateListKind.Server ? "server" : "personal";
}
