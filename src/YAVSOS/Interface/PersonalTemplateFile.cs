using System;
using System.IO;
using System.Text;
using YAVSOS.Core.Templates;

namespace YAVSOS.Interface;

/// <summary>
/// D166: personal templates as one file in BepInEx/config, shared by every character that plays with this mod-manager
/// profile. Free of Unity and the game so the detached tests can run it against a temporary folder.
///
/// The file is read once and kept; this game is its only writer, so each save replaces it with the list the panel shows
/// (the last save wins, as with the game's own save files). Writes go to a temporary file of their own, then replace the
/// file, so a crash mid-save leaves the old file whole. A file this version can't read (newer or damaged) is kept as it
/// is and never written over (D153).
/// </summary>
internal sealed class PersonalTemplateFile
{
    /// <summary>Named after the plugin ID, as the main config and the server's templates file are, so it sorts beside them.</summary>
    internal const string FileName = "com.bastionntb.yavsos.personal-templates.xml";

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private TemplateListRead? read;

    /// <param name="path">The file (<see cref="In"/> gives it for a config folder).</param>
    internal PersonalTemplateFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("A path is required.", nameof(path));
        FilePath = path;
    }

    /// <summary>The file in a config folder.</summary>
    internal static string In(string configFolder) => Path.Combine(configFolder, FileName);

    internal string FilePath { get; }

    /// <summary>Why the last read or write of the file failed, for the log; "" after one worked.</summary>
    internal string LastError { get; private set; } = "";

    /// <summary>
    /// The list, read from the file on first use and then kept; the same object comes back until a save changes it. When
    /// the file couldn't be opened the list is unreadable (nothing is written) until a save tries the file again.
    /// </summary>
    internal TemplateListRead Read() => read ??= Load();

    /// <summary>Lab only: forgets the list, so the next <see cref="Read"/> reads the file as a game start would.</summary>
    internal void Forget() { read = null; LastError = ""; }

    /// <summary>
    /// Saves <paramref name="list"/>. Refused when the file can't be read by this version (<see
    /// cref="TemplateOutcome.Unreadable"/>), the list is over its limit, or the file couldn't be written (<see
    /// cref="TemplateOutcome.Unavailable"/>, with <see cref="LastError"/>).
    /// </summary>
    internal TemplateOutcome Write(TemplateList list)
    {
        if (list == null) throw new ArgumentNullException(nameof(list));
        if (list.Kind != TemplateListKind.Personal) throw new ArgumentException("Not a personal list.", nameof(list));
        if (LastError.Length != 0) read = null; // The last read or save failed: try the file again.
        var current = Read();
        if (LastError.Length != 0) return TemplateOutcome.Unavailable; // The file couldn't be opened just now.
        if (!current.Writable) return TemplateOutcome.Unreadable;
        var text = RuleTemplateCodec.Encode(list);
        if (Utf8.GetByteCount(text) > TemplateLimits.PersonalListBytes) return TemplateOutcome.ListTooLarge;
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, text, Utf8);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
        {
            LastError = $"{error.GetType().Name}: {error.Message}";
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception again) when (again is IOException || again is UnauthorizedAccessException) { }
            return TemplateOutcome.Unavailable;
        }
        LastError = "";
        read = RuleTemplateCodec.Decode(text, TemplateListKind.Personal);
        return TemplateOutcome.Done;
    }

    // Sets LastError when the file couldn't be opened. A missing file is an empty list; one far over the limit or not
    // UTF-8 is unreadable without decoding, so it is kept as it is.
    private TemplateListRead Load()
    {
        try
        {
            var info = new FileInfo(FilePath);
            LastError = "";
            if (!info.Exists) return RuleTemplateCodec.Decode("", TemplateListKind.Personal);
            if (info.Length > TemplateLimits.PersonalListBytes * 4L)
                return TemplateListRead.Unreadable($"the file is over {TemplateLimits.PersonalListBytes / 1024} KiB");
            var text = File.ReadAllText(FilePath, StrictUtf8).TrimStart('﻿');
            return RuleTemplateCodec.Decode(text, TemplateListKind.Personal);
        }
        catch (DecoderFallbackException error)
        {
            LastError = "";
            return TemplateListRead.Unreadable("not UTF-8 text: " + error.Message);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            LastError = $"{error.GetType().Name}: {error.Message}";
            return TemplateListRead.Unreadable(LastError);
        }
    }
}
