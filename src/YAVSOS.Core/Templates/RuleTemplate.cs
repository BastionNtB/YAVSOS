using System;
using System.Collections.Generic;
using System.Linq;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Templates;

/// <summary>X23 (D157): the limits on template lists, measured on the encoded text (UTF-8).</summary>
public static class TemplateLimits
{
    /// <summary>Templates in one list, personal or server.</summary>
    public const int PerList = 64;
    /// <summary>One template's encoded size.</summary>
    public const int TemplateBytes = 32 * 1024;
    /// <summary>The server's whole list: the categories file's measured, accepted delivery size (D014).</summary>
    public const int ServerListBytes = 256 * 1024;
    /// <summary>The personal templates file every character on the PC shares (D166).</summary>
    public const int PersonalListBytes = 512 * 1024;
    /// <summary>A template's name, in characters, after trimming.</summary>
    public const int NameLength = 64;
    /// <summary>A server template's "shared by" name, in characters.</summary>
    public const int ByLength = 64;

    public static int ListBytes(TemplateListKind kind) => kind == TemplateListKind.Server ? ServerListBytes : PersonalListBytes;
}

/// <summary>Which list a template is in.</summary>
public enum TemplateListKind { Personal, Server }

/// <summary>
/// X23 (D157): a named rule template. It holds what Copy takes (<see cref="RuleCopy"/>): categories by ID (known or not,
/// so renaming a category's display name changes nothing), Always and Never items, the filters of the edited
/// alternative and the priority; not "Don't clean up this chest", which is the chest's. Each template has an ID and a
/// revision; a server change names the revision it saw. Copies (Share, Keep a copy) are snapshots. Immutable.
/// </summary>
public sealed class RuleTemplate
{
    public string Id { get; }
    public string Name { get; }
    /// <summary>Bumped on every change to the template, from 1.</summary>
    public ulong Revision { get; }
    /// <summary>Who shared a server template (the server sets it from the sender's connection); "" for a personal one.</summary>
    public string By { get; }
    /// <summary>Category IDs, sorted; a category this category set lacks stays as stored (D050).</summary>
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> Includes { get; }
    public IReadOnlyList<string> Excludes { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Filters { get; }
    public int Priority { get; }

    public RuleTemplate(string id, string name, ulong revision, string by, IEnumerable<string>? categories, IEnumerable<string>? includes,
        IEnumerable<string>? excludes, IEnumerable<KeyValuePair<string, IEnumerable<string>>>? filters, int priority)
    {
        if (!TemplateIds.Valid(id)) throw new ArgumentException("Invalid template ID.", nameof(id));
        Id = id;
        Name = TemplateNames.Clean(name) ?? throw new ArgumentException("Invalid template name.", nameof(name));
        if (revision == 0) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
        By = by ?? "";
        if (By.Length > TemplateLimits.ByLength || By.Any(char.IsControl)) throw new ArgumentException("Invalid template author.", nameof(by));
        // The same bounds a chest rule's editable part has (ChestRuleEdit), so every template can be applied.
        var edit = new ChestRuleEdit(categories, includes, excludes, null, priority, filters);
        foreach (var category in edit.Categories) ClassificationValidation.Category(category);
        Categories = edit.Categories; Includes = edit.Includes; Excludes = edit.Excludes; Filters = edit.Filters;
        Priority = priority;
    }

    /// <summary>
    /// The open chest's rule as the editor holds it, as a template (what Copy takes). Null when Copy would take nothing
    /// (<see cref="RuleCopy.CanCopy"/>): no rule, or only parts the editor can't show (D086 D5).
    /// </summary>
    public static RuleTemplate? Of(RuleEditState state, string id, string name, ulong revision = 1, string by = "")
    {
        if (!RuleCopy.CanCopy(state)) return null;
        return new RuleTemplate(id, name, revision, by, state.Categories.Concat(state.Unknown), state.Includes, state.Excludes,
            state.Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)), state.Priority);
    }

    /// <summary>The same rule under another ID, name, revision or author (a copy, a rename, an update).</summary>
    public RuleTemplate With(string? id = null, string? name = null, ulong? revision = null, string? by = null) =>
        new RuleTemplate(id ?? Id, name ?? Name, revision ?? Revision, by ?? By, Categories, Includes, Excludes, FilterPairs(), Priority);

    /// <summary>This template's name, ID and author with <paramref name="content"/>'s rule: "Update from this chest".</summary>
    public RuleTemplate WithRuleOf(RuleTemplate content, ulong revision)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        return new RuleTemplate(Id, Name, revision, By, content.Categories, content.Includes, content.Excludes, content.FilterPairs(), content.Priority);
    }

    /// <summary>Whether the two hold the same rule (categories, items, filters and priority), whatever their names.</summary>
    public bool SameRule(RuleTemplate other) => other != null && Priority == other.Priority &&
        Categories.SequenceEqual(other.Categories, StringComparer.Ordinal) && Includes.SequenceEqual(other.Includes, StringComparer.Ordinal) &&
        Excludes.SequenceEqual(other.Excludes, StringComparer.Ordinal) && Filters.Count == other.Filters.Count &&
        Filters.All(x => other.Filters.TryGetValue(x.Key, out var values) && values.SequenceEqual(x.Value, StringComparer.Ordinal));

    /// <summary>What Paste puts on a chest: the template's rule as a copied rule, so Apply saves exactly as Paste does.</summary>
    public RuleCopy ToCopy() => new RuleCopy(this);

    internal IEnumerable<KeyValuePair<string, IEnumerable<string>>> FilterPairs() =>
        Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value));
}

/// <summary>Template IDs: 1 to 32 lower-case letters and digits, made by the list that holds the template.</summary>
public static class TemplateIds
{
    public const int Length = 32;

    public static bool Valid(string? id) => id != null && id.Length is > 0 and <= Length && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>A new ID that <paramref name="taken"/> doesn't hold: 12 characters from <paramref name="random"/>.</summary>
    public static string New(Func<int, int> random, Func<string, bool> taken)
    {
        if (random == null) throw new ArgumentNullException(nameof(random));
        if (taken == null) throw new ArgumentNullException(nameof(taken));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var chars = new char[12];
            for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[Math.Abs(random(alphabet.Length)) % alphabet.Length];
            var id = new string(chars);
            if (!taken(id)) return id;
        }
        throw new InvalidOperationException("No free template ID.");
    }
}

/// <summary>Template names: trimmed, 1 to 64 characters, no control characters; unique in a list, ignoring case.</summary>
public static class TemplateNames
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The name as stored, or null when it can't be one (empty, too long, control characters, broken UTF-16).</summary>
    public static string? Clean(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > TemplateLimits.NameLength) return null;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (char.IsControl(c)) return null;
            if (char.IsSurrogate(c) && (!char.IsHighSurrogate(c) || i + 1 == trimmed.Length || !char.IsLowSurrogate(trimmed[++i]))) return null;
        }
        return trimmed;
    }

    /// <summary>
    /// "Keep a copy" (D157): the name, or "name 2", "name 3" … when the list has it, cut so it stays within the limit.
    /// </summary>
    public static string Free(string name, Func<string, bool> taken)
    {
        if (taken == null) throw new ArgumentNullException(nameof(taken));
        var clean = Clean(name) ?? throw new ArgumentException("Invalid template name.", nameof(name));
        if (!taken(clean)) return clean;
        for (var n = 2; n < 1000; n++)
        {
            var suffix = " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var stem = clean.Length + suffix.Length <= TemplateLimits.NameLength ? clean : Cut(clean, TemplateLimits.NameLength - suffix.Length);
            var candidate = stem + suffix;
            if (!taken(candidate)) return candidate;
        }
        throw new InvalidOperationException("No free template name.");
    }

    // Never between a surrogate pair.
    private static string Cut(string text, int length)
    {
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text.Substring(0, length).TrimEnd();
    }
}
