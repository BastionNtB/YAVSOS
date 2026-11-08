using System;
using BepInEx.Logging;
using YAVSOS.Core.Templates;

namespace YAVSOS.Interface;

/// <summary>
/// X23 (D157, D166): personal templates, one list for every character, in
/// <c>BepInEx/config/com.bastionntb.yavsos.personal-templates.xml</c> (<see cref="PersonalTemplateFile"/>), beside the
/// main config. Data this version can't read (a newer schema, or damaged) is kept exactly as it is and never written over
/// (D153): saving is refused until it can be read. No networking. Unity game thread only.
/// </summary>
internal sealed class PersonalTemplates
{
    private readonly ManualLogSource log;
    private string warned = "", failed = "";

    /// <param name="configFolder">BepInEx's config folder (<c>Paths.ConfigPath</c>).</param>
    internal PersonalTemplates(string configFolder, ManualLogSource log)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        File = new PersonalTemplateFile(PersonalTemplateFile.In(configFolder));
    }

    internal PersonalTemplateFile File { get; }

    /// <summary>The list; the same object until a save changes it.</summary>
    internal TemplateListRead Read()
    {
        var read = File.Read();
        Report(File.LastError);
        if (!read.Writable && File.LastError.Length == 0 && warned != read.Diagnostic) // An open failure is Report's.
        {
            warned = read.Diagnostic;
            log.LogWarning($"Personal rule templates in {File.FilePath} can't be read ({read.State}: {read.Diagnostic}). They stay saved as they are; YAVSOS won't change them.");
        }
        return read;
    }

    /// <summary>Saves the list; refused when the file can't be read (D153).</summary>
    internal TemplateOutcome Write(TemplateList list)
    {
        var outcome = File.Write(list);
        Report(File.LastError);
        return outcome;
    }

    // Each new failure to open or save the file is logged once.
    private void Report(string error)
    {
        if (error == failed) return;
        failed = error;
        if (error.Length != 0) log.LogWarning($"Couldn't use the personal rule templates file {File.FilePath} ({error}).");
    }
}
