using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

/// <summary>
/// P10-04: formats messages from the real English file (<c>src/YAVSOS/Translations/English/yavsos.json</c>), so a test
/// can assert the English a player reads. A key missing from the file shows as <c>[key]</c>, so the assertion fails.
/// </summary>
internal static class EnglishText
{
    private static Dictionary<string, string>? file;

    internal static string Root([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
    internal static string Translations => Path.Combine(Root(), "src", "YAVSOS", "Translations");
    internal static string FilePath => Path.Combine(Translations, "English", "yavsos.json");

    /// <summary>Every English key and text, read once.</summary>
    internal static IReadOnlyDictionary<string, string> File => file ??= FlatJson.Parse(System.IO.File.ReadAllText(FilePath));

    private static string? Lookup(string key) => File.TryGetValue(key, out var text) ? text : null;

    /// <summary>The message's English, tinted values shown plain.</summary>
    /// <summary>Category names as an English player sees them (S13): built-in names from the file, admin-set names as written.</summary>
    internal static CategoryNames Names(ClassificationTaxonomy taxonomy) => new CategoryNames(taxonomy, Of);

    internal static string Of(Message message) => MessageFormat.Format(message, Lookup);

    /// <summary>The message's English with each tinted value marked <c>[Tone:text]</c>, to assert what is colored.</summary>
    internal static string Marked(Message message) => MessageFormat.Format(message, Lookup, tint: (tone, text) => "[" + tone + ":" + text + "]");

    /// <summary>A list's English on its own.</summary>
    internal static string Of(MessageList list) => MessageFormat.Format(list, Lookup);

    /// <summary>A tooltip's heading and body in English.</summary>
    internal static (string Topic, string Text) Of(Tooltip tooltip) => tooltip.Format(Lookup);
}
