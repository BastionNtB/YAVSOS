using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Classification;

/// <summary>
/// D163 (X60): a folding heading in the Categories tree over top-level categories of one kind, such as the categories
/// made from one mod's stores ("Storehouse stores (46)"). It is not a category: it has no tick, a rule never stores it,
/// and its <see cref="Id"/> can't be a category ID (a category ID has no <c>:</c>), so it can sit in the tree's fold sets.
/// </summary>
public sealed class CategoryGroup
{
    public const string IdPrefix = "heading:";
    public string Id { get; }
    /// <summary>The mod's name as it shows it ("Storehouse"); null when unknown.</summary>
    public string? Mod { get; }
    /// <summary>The grouped categories, in ID order.</summary>
    public IReadOnlyList<string> Categories { get; }
    internal CategoryGroup(string? mod, IReadOnlyList<string> categories)
    {
        Mod = mod; Categories = categories; Id = IdPrefix + (mod ?? "");
    }

    /// <summary>The heading's text, naming <paramref name="count"/> shown categories.</summary>
    public Message Heading(int count) => Mod == null
        ? Message.Of("yavsos_tree_stores_unnamed").With("count", count)
        : Message.Of("yavsos_tree_stores").With("mod", Mod).With("count", count);

    /// <summary>The heading's tooltip body: what the rows under it are and that each is ticked on its own.</summary>
    public static Message Tip => Message.Of("yavsos_tree_stores_tip");

    public static bool IsHeading(string? id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);
}

/// <summary>
/// D163: which top-level categories the tree folds under a heading, and the shorter name each shows there ("Timber
/// Crib" under "Storehouse stores", where "Storehouse Timber Crib" would repeat the heading). Everywhere else (chips,
/// labels, search) a category keeps its full name. Immutable.
/// </summary>
public sealed class CategoryGrouping
{
    public static readonly CategoryGrouping None = new CategoryGrouping(Array.Empty<(string, string?, string, string?)>());
    private readonly Dictionary<string, CategoryGroup> byCategory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Full, string Short)> shortNames = new(StringComparer.Ordinal);
    public IReadOnlyList<CategoryGroup> Groups { get; }

    /// <param name="members">Per category: its ID, its group's mod (null: unknown), its full name and its short name
    /// (null: the full one).</param>
    public CategoryGrouping(IEnumerable<(string Category, string? Mod, string Name, string? ShortName)> members)
    {
        if (members == null) throw new ArgumentNullException(nameof(members));
        var rows = members.Where(x => x.Category != null).GroupBy(x => x.Category, StringComparer.Ordinal).Select(x => x.First()).ToList();
        var groups = new List<CategoryGroup>();
        foreach (var group in rows.GroupBy(x => string.IsNullOrWhiteSpace(x.Mod) ? null : x.Mod!.Trim(), StringComparer.Ordinal)
                     .OrderBy(x => x.Key == null ? 1 : 0).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            var heading = new CategoryGroup(group.Key, group.Select(x => x.Category).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            groups.Add(heading);
            foreach (var row in group)
            {
                byCategory[row.Category] = heading;
                if (!string.IsNullOrWhiteSpace(row.ShortName) && row.Name != null) shortNames[row.Category] = (row.Name, row.ShortName!.Trim());
            }
        }
        Groups = groups.AsReadOnly();
    }

    /// <summary>The heading <paramref name="category"/> folds under; null when it isn't grouped.</summary>
    public CategoryGroup? Of(string category) => category != null && byCategory.TryGetValue(category, out var group) ? group : null;

    /// <summary>
    /// The name a grouped category shows under its heading: the short name while the taxonomy still names it as given
    /// (<paramref name="taxonomyName"/>, so a server's own category of that ID keeps its name), else the shown one.
    /// </summary>
    public string UnderHeading(string category, string? taxonomyName, string shown) =>
        category != null && shortNames.TryGetValue(category, out var names) && StringComparer.Ordinal.Equals(names.Full, taxonomyName) ? names.Short : shown;
}
