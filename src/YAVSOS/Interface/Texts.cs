using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;
using YAVSOS.Core.Messages;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// X30 step 2 (P10-04): YAVSOS's player text. The English file is embedded in <c>YAVSOS.dll</c> and handed to Jotunn in
/// <see cref="Plugin"/>'s <c>Awake</c>, before the settings bind (EXTRACTION-TOOL-CHECK recommendation 1); lookups go
/// through YAVSOS's own <see cref="CustomLocalization"/>, never <c>LocalizationManager.Instance.TryTranslate</c>, which
/// could return another mod's key of the same name. If Jotunn can't take the file, the English read here is used, so
/// players still get words. Nothing in YAVSOS's UI translates itself (Jotunn's GUIManager never localizes): code that
/// keeps text on screen redraws it on <see cref="Changed"/>. Unity game thread only.
/// </summary>
internal static class Texts
{
    internal const string Resource = "YAVSOS.Translations.English.yavsos.json";
    private static Dictionary<string, string> english = new(StringComparer.Ordinal);
    private static CustomLocalization? jotunn;
    private static ManualLogSource? log;
    private static bool listening;

    /// <summary>Raised after the game's language changed, and after Jotunn added mods' text at the main menu.</summary>
    internal static event Action? Changed;

    /// <summary>In <c>Awake</c>. Doesn't touch <c>Localization.instance</c>, whose getter sets the game's language up.</summary>
    internal static void Load(ManualLogSource logger)
    {
        log = logger;
        string text;
        try
        {
            using var stream = typeof(Texts).Assembly.GetManifestResourceStream(Resource)
                ?? throw new FileNotFoundException("The embedded English text is missing.", Resource);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            english = FlatJson.Parse(text);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogError("YAVSOS's English text could not be read, so its messages show as [keys]: " + error.GetType().Name + ": " + error.Message);
            return;
        }
        try
        {
            jotunn = LocalizationManager.Instance.GetLocalization();
            jotunn.AddJsonFile("English", text);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            jotunn = null; // English only, from the file read above.
            logger.LogWarning("Jotunn could not take YAVSOS's text, so YAVSOS shows English: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>In <c>Start</c>: the game's and Jotunn's language events (EXTRACTION-TOOL-CHECK §1, "Language changes").</summary>
    internal static void Listen()
    {
        if (listening) return;
        listening = true;
        try
        {
            Localization.OnLanguageChange += Raise;
            LocalizationManager.OnLocalizationAdded += Raise;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            log?.LogWarning("YAVSOS text won't follow a language change until the game restarts: " + error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>Bumped before each <see cref="Changed"/>: a cache of formatted text keys on it, so it is built again in the new language.</summary>
    internal static int Generation { get; private set; }

    private static void Raise()
    {
        Generation++;
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { log?.LogWarning("Redrawing YAVSOS text after a language change failed: " + error.GetType().Name + ": " + error.Message); }
    }

    /// <summary>The message in the player's language, with <see cref="Tinted"/> values in Unity color markup.</summary>
    internal static string Get(Message message) => MessageFormat.Format(message, Lookup, tint: Tint);
    internal static string Get(string key) => Get(Message.Of(key));
    /// <summary>A tooltip's heading and body in the player's language, for <c>GameTooltip</c> and <c>UITooltip.Set</c>.</summary>
    internal static (string Topic, string Text) Get(Tooltip tooltip) => tooltip.Format(Lookup, tint: Tint);
    /// <summary>The message as plain text in the player's language: no markup, for a widget that shows tags as written.</summary>
    internal static string Plain(Message message) => MessageFormat.Format(message, Lookup);

    /// <summary>
    /// The message in English from the embedded file, without markup, for log lines and Lab output; never shown to
    /// players. Works before Jotunn has the file and when it refused it.
    /// </summary>
    internal static string English(Message message) => MessageFormat.Format(message, key => english.TryGetValue(key, out var text) ? text : null);

    /// <summary>A list on its own (a key chord, say) in the player's language, with tinted items in markup.</summary>
    internal static string Get(MessageList list) => MessageFormat.Format(list, Lookup, tint: Tint);
    /// <summary>A list on its own in English, for log lines.</summary>
    internal static string English(MessageList list) => MessageFormat.Format(list, key => english.TryGetValue(key, out var text) ? text : null);
    /// <summary>A tooltip's heading and body in English, for Lab output.</summary>
    internal static (string Topic, string Text) English(Tooltip tooltip) => tooltip.Format(key => english.TryGetValue(key, out var text) ? text : null);

    /// <summary>
    /// A name from configuration or another mod made safe for rich text: its <c>&lt;</c> and <c>&gt;</c> can't open a tag.
    /// Use it on names put into a message that is shown with markup.
    /// </summary>
    internal static string Escape(string text) => text.Replace("<", "‹").Replace(">", "›");

    /// <summary>Each <see cref="Tone"/>'s color. Core names the role; only this side writes markup.</summary>
    internal static string Color(Tone tone) => tone switch
    {
        Tone.Label => "#E8B04A",
        Tone.Lead => "#F0A64A",
        Tone.Faint => "#A0957F",
        Tone.Caution => "#F0C050",
        Tone.Warning => "orange",
        Tone.Working => "#7FD47F",
        Tone.Broken => "#EF6B5B",
        _ => "#E8B04A"
    };

    private static string Tint(Tone tone, string text) => "<color=" + Color(tone) + ">" + text + "</color>";

    /// <summary>
    /// A key's text in the player's language, else English; null for a key the English file lacks. English decides
    /// which keys exist, so a probe for a plural form English hasn't got never reaches the game's own table.
    /// </summary>
    private static string? Lookup(string key)
    {
        if (!english.TryGetValue(key, out var fallback)) return null;
        if (jotunn == null) return fallback;
        var text = jotunn.TryTranslate("$" + key);
        return string.IsNullOrEmpty(text) || text.StartsWith("[$", StringComparison.Ordinal) ? fallback : text;
    }
}
