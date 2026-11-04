using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace YAVSOS.Core.Messages;

/// <summary>
/// X30 step 2 (P10-04, EXTRACTION-TOOL-CHECK recommendation 4): turns a <see cref="Message"/> into text with named
/// placeholders in i18next syntax, <c>{{name}}</c>, which Weblate's i18next JSON v4 format checks and Tolgee converts.
/// Neither Jotunn (no placeholders) nor the game (positional <c>$1</c>…<c>$9</c>) has named ones. Plural forms are
/// separate keys with CLDR suffixes (D115): <c>key_one</c>, <c>key_other</c>, picked by the message's
/// <see cref="CountName"/> value. Pure: the caller supplies the lookup, so the game side reads Jotunn and tests read the file.
/// </summary>
public static class MessageFormat
{
    /// <summary>i18next's name for the value that picks the plural form.</summary>
    public const string CountName = "count";
    /// <summary>The CLDR plural categories; a key ending in one of them is a plural form (i18next JSON v4).</summary>
    public static readonly IReadOnlyList<string> PluralForms = new[] { "zero", "one", "two", "few", "many", "other" };
    private const int MaxDepth = 8;

    /// <summary>English's rule (D128: the rules for other languages come with the first language that needs them).</summary>
    public static string English(long count) => count == 1 ? "one" : "other";

    /// <summary>
    /// The text of <paramref name="message"/>. <paramref name="lookup"/> returns a key's text in the player's language, or
    /// null. A missing key shows as <c>[key]</c> and an unknown placeholder stays as written, so both are visible.
    /// <paramref name="tint"/> wraps a <see cref="Tinted"/> value in the game's markup; without it the value shows plain
    /// (logs, tests, plain-text spots).
    /// </summary>
    public static string Format(Message message, Func<string, string?> lookup, Func<long, string>? plural = null, Func<Tone, string, string>? tint = null)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        return Format(message, new Context(lookup, plural ?? English, tint), 0);
    }

    /// <summary>A list's text on its own, such as a tooltip body that is only a list of names.</summary>
    public static string Format(MessageList list, Func<string, string?> lookup, Func<long, string>? plural = null, Func<Tone, string, string>? tint = null)
    {
        if (list == null) throw new ArgumentNullException(nameof(list));
        if (lookup == null) throw new ArgumentNullException(nameof(lookup));
        return List(list, new Context(lookup, plural ?? English, tint), 0);
    }

    private sealed class Context
    {
        internal readonly Func<string, string?> Lookup; internal readonly Func<long, string> Plural; internal readonly Func<Tone, string, string>? Tint;
        internal Context(Func<string, string?> lookup, Func<long, string> plural, Func<Tone, string, string>? tint) { Lookup = lookup; Plural = plural; Tint = tint; }
    }

    private static string Format(Message message, Context context, int depth)
    {
        if (depth > MaxDepth) return "[" + message.Key + "]";
        var template = Template(message, context);
        if (template == null) return "[" + message.Key + "]";
        if (template.IndexOf("{{", StringComparison.Ordinal) < 0) return template;
        var text = new StringBuilder(template.Length + 16);
        var at = 0;
        while (at < template.Length)
        {
            var open = template.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) { text.Append(template, at, template.Length - at); break; }
            text.Append(template, at, open - at);
            var close = NameEnd(template, open + 2);
            if (close < 0) { text.Append("{{"); at = open + 2; continue; }
            var name = template.Substring(open + 2, close - open - 2);
            if (message.TryGet(name, out var value)) text.Append(Value(value, context, depth));
            else text.Append(template, open, close + 2 - open);
            at = close + 2;
        }
        return text.ToString();
    }

    private static string? Template(Message message, Context context)
    {
        if (message.TryGet(CountName, out var count) && count is long n)
            return context.Lookup(message.Key + "_" + context.Plural(n)) ?? context.Lookup(message.Key + "_other") ?? context.Lookup(message.Key);
        return context.Lookup(message.Key);
    }

    private static string Value(object value, Context context, int depth) => value switch
    {
        long n => n.ToString(CultureInfo.InvariantCulture),
        Message inner => Format(inner, context, depth + 1),
        Tinted tinted => Tint(tinted, context, depth),
        MessageList list => List(list, context, depth),
        _ => value.ToString() ?? "",
    };

    private static string Tint(Tinted tinted, Context context, int depth)
    {
        var text = Value(tinted.Value, context, depth);
        return context.Tint == null || text.Length == 0 ? text : context.Tint(tinted.Tone, text);
    }

    // CLDR list patterns: the end pattern joins the last two, the middle one each item before them, the start one the first.
    private static string List(MessageList list, Context context, int depth)
    {
        var texts = new List<string>(list.Items.Count);
        foreach (var item in list.Items) texts.Add(Value(item, context, depth));
        string joined;
        if (MessageList.Patterns(list.Kind) is not { } keys) joined = string.Join("\n", texts);
        else if (texts.Count < 2) joined = texts.Count == 0 ? "" : texts[0];
        else if (texts.Count == 2) joined = Pattern(keys.Pair, texts[0], texts[1], context, depth);
        else
        {
            joined = Pattern(keys.End, texts[texts.Count - 2], texts[texts.Count - 1], context, depth);
            for (var i = texts.Count - 3; i >= 1; i--) joined = Pattern(keys.Middle, texts[i], joined, context, depth);
            joined = Pattern(keys.Start, texts[0], joined, context, depth);
        }
        if (list.More == 0) return joined;
        var more = Format(Message.Of(MessageList.MoreKey(list.Kind)).With("list", joined).Count(list.More), context, depth + 1);
        return texts.Count == 0 ? more.Trim() : more;
    }

    private static string Pattern(string key, string first, string rest, Context context, int depth) =>
        Format(Message.Of(key).With("first", first).With("rest", rest), context, depth + 1);

    /// <summary>The index of the closing <c>}}</c> of a placeholder name starting at <paramref name="start"/>, or -1.</summary>
    private static int NameEnd(string text, int start)
    {
        var at = start;
        while (at < text.Length && IsNameChar(text[at])) at++;
        return at > start && at + 1 < text.Length && text[at] == '}' && text[at + 1] == '}' ? at : -1;
    }

    private static bool IsNameChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    /// <summary>The placeholder names in <paramref name="template"/>, in order, each once (for the parity test).</summary>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        var names = new List<string>();
        var at = 0;
        while ((at = template.IndexOf("{{", at, StringComparison.Ordinal)) >= 0)
        {
            var close = NameEnd(template, at + 2);
            if (close < 0) { at += 2; continue; }
            var name = template.Substring(at + 2, close - at - 2);
            if (!names.Contains(name)) names.Add(name);
            at = close + 2;
        }
        return names;
    }

    /// <summary>
    /// A valid YAVSOS message key: <c>yavsos_</c>, then lower-case letters, digits and <c>_</c> (Jotunn refuses dots and
    /// other punctuation; EXTRACTION-TOOL-CHECK §1).
    /// </summary>
    public static bool IsValidKey(string key)
    {
        if (key == null || !key.StartsWith("yavsos_", StringComparison.Ordinal) || key.Length == 7 || key.EndsWith("_", StringComparison.Ordinal)) return false;
        foreach (var c in key)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) return false;
        return key.IndexOf("__", StringComparison.Ordinal) < 0;
    }

    /// <summary>The plural form a key ends in (<c>one</c> for <c>yavsos_x_one</c>), or null.</summary>
    public static string? PluralForm(string key)
    {
        var cut = key.LastIndexOf('_');
        if (cut < 0) return null;
        var tail = key.Substring(cut + 1);
        foreach (var form in PluralForms)
            if (form == tail) return form;
        return null;
    }

    /// <summary>The key without its plural suffix (<c>yavsos_x</c> for <c>yavsos_x_one</c>), or the key itself.</summary>
    public static string BaseKey(string key) => PluralForm(key) is { } form ? key.Substring(0, key.Length - form.Length - 1) : key;
}
