using System;
using System.Collections.Generic;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Classification;

/// <summary>
/// P10-04 (S13): the category names a player sees, for one taxonomy. A built-in category's name is its message
/// (<see cref="ClassificationCategory.NameKey"/>), an Other subcategory's is one message naming its umbrella (also when the
/// categories file lists it with its derived name), and a name
/// an admin set in the categories file (or an ID this taxonomy doesn't know) is shown as written. Core never formats a
/// message itself: the caller passes the formatter (the player's language in the game, the English file in tests), and
/// sorting, search and the chips' cut use the formatted names. Each name is formatted once per instance.
/// </summary>
public sealed class CategoryNames
{
    private readonly Func<Message, string> format;
    private readonly Dictionary<string, string> shown = new Dictionary<string, string>(StringComparer.Ordinal);
    public ClassificationTaxonomy Taxonomy { get; }

    public CategoryNames(ClassificationTaxonomy taxonomy, Func<Message, string> format)
    {
        Taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
        this.format = format ?? throw new ArgumentNullException(nameof(format));
    }

    /// <summary>The name as the player sees it, formatted.</summary>
    public string Shown(string id)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (shown.TryGetValue(id, out var text)) return text;
        var name = Of(Taxonomy, id);
        text = name is Message message ? format(message) : (string)name;
        if (shown.Count < ClassificationTaxonomy.MaximumNamedCategories * 4) shown[id] = text;
        return text;
    }

    /// <summary>
    /// The name to put in a message or list: a <see cref="Message"/> for a built-in or Other subcategory, else the
    /// <see cref="string"/> as written (like <see cref="MessageList"/> items).
    /// </summary>
    public static object Of(ClassificationTaxonomy taxonomy, string id)
    {
        if (taxonomy == null) throw new ArgumentNullException(nameof(taxonomy));
        if (id == null || !taxonomy.Categories.TryGetValue(id, out var row)) return id ?? "";
        // A derived Other, or one the categories file lists with its derived name, is the Other message.
        var parent = row.Generated ? row.Parent : taxonomy.ParentOf(id);
        if (parent != null && taxonomy.Categories.TryGetValue(parent, out var umbrella)
            && (row.Generated || (taxonomy.OtherOf(parent) == id && StringComparer.Ordinal.Equals(row.Name, ClassificationCategory.OtherName(umbrella)))))
            return umbrella.Name.Length <= ClassificationCategory.OtherNameLimit
                ? With(Message.Of("yavsos_category_other_name"), "umbrella", Of(taxonomy, umbrella.Id))
                : Message.Of("yavsos_category_other_alone_name");
        return row.NameKey is { } key ? Message.Of(key) : row.Name;
    }

    /// <summary>Sets <paramref name="name"/> to a category name from <see cref="Of"/>.</summary>
    public static Message With(Message message, string name, object categoryName) => categoryName switch
    {
        Message keyed => message.With(name, keyed),
        string written => message.With(name, written),
        _ => throw new ArgumentException("A category name is a message or a string.", nameof(categoryName))
    };
}
