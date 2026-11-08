using System;
using System.Linq;
using YAVSOS.Core.Templates;

namespace YAVSOS.Interface;

/// <summary>
/// X23 lab entry points (Lab configuration only, called through the test bridge's debug/call_method) for session B's
/// template checks. Every action goes through the Templates tab's own clicks (<see cref="RuleControls"/>), queued, so
/// read results with a later call (<see cref="List"/>, <c>UiLab.Panel</c> for the strip). Open the chest first
/// (<c>CleanupLab.Open</c>). A list is "personal" or "server"; a template is named by its name or ID. Unity game thread only.
/// </summary>
internal static class TemplateLab
{
    private static RuleControls? Rules => Plugin.Instance?.RuleControlsForLab;

    /// <summary>Both lists with IDs and revisions, the selection, admin hint and the waiting change.</summary>
    public static string List()
    {
        if (Rules is not { } r || Plugin.Instance is not { } p) return "no panel";
        string Of(TemplateListRead? read) => read == null ? "absent" : $"{read.State} writable={read.Writable} revision={read.List?.Revision} [" +
            string.Join("; ", read.List?.Templates.Select(t => $"{t.Id}@{t.Revision} \"{t.Name}\" by={t.By} cats={string.Join(",", t.Categories)} always={string.Join(",", t.Includes)} never={string.Join(",", t.Excludes)} filters={t.Filters.Count} priority={t.Priority} missing={r.MissingParts(t).Count}") ?? Array.Empty<string>()) + "]";
        return $"personal={Of(r.PersonalList)} | server={Of(r.ServerList)} | selected={r.LabSelection}; naming={r.Naming}; asking={r.Asking}; admin={r.TemplateAdmin}; " +
            $"waiting={(p.Templates.Pending is { } w ? w.Operation + " " + w.Id : "none")}; serving={p.Templates.Serving}";
    }

    /// <summary>Opens the Templates tab, as its tab button does.</summary>
    public static string Show() { Rules?.ShowTemplates(); return "queued"; }

    /// <summary>"+ Save this chest's rule as a template" with this name (empty: the suggested name), then Save.</summary>
    public static string Save(string name) => Step(r =>
    {
        r.BeginSaveTemplateNow();
        if (r.Naming != TemplateNaming.Save) return;
        if (name.Length != 0) r.NameTyped(name);
        r.SubmitNameNow();
    });

    /// <summary>Selects the template and presses Apply.</summary>
    public static string Apply(string list, string template) => Selected(list, template, r => r.ApplyTemplateNow());

    /// <summary>Selects the template, presses Update from this chest and, with <paramref name="yes"/>, answers Yes to the question a server template asks.</summary>
    public static string Update(string list, string template, bool yes) => Selected(list, template, r => { r.UpdateTemplateNow(); Answer(r, yes); });

    public static string Rename(string list, string template, string name, bool yes) => Selected(list, template, r =>
    {
        r.BeginRenameTemplateNow(); r.NameTyped(name); r.SubmitNameNow(); Answer(r, yes);
    });

    public static string Delete(string list, string template, bool yes) => Selected(list, template, r => { r.DeleteTemplateNow(); Answer(r, yes); });

    /// <summary>Share (a personal template, admins): a template with missing parts asks first.</summary>
    public static string Share(string template, bool yes) => Selected("personal", template, r => { r.ShareTemplateNow(); Answer(r, yes); });

    public static string Keep(string template) => Selected("server", template, r => r.KeepTemplateCopyNow());

    /// <summary>Selects a template and keeps it selected, as a click on its row (two admins: select here, change it on the other client, then Update here).</summary>
    public static string Select(string list, string template) => Selected(list, template, _ => { });

    /// <summary>Fixture: the selection names an older revision, standing in for a view that didn't refresh.</summary>
    public static string Seen(int revision) { Rules?.LabSeen((ulong)Math.Max(1, revision)); return "seen=" + revision; }

    /// <summary>Fixture: the personal templates file (D166) as stored: its path, length and start.</summary>
    public static string Raw()
    {
        if (Plugin.Instance is not { } p) return "no plugin";
        var path = p.PersonalTemplates.File.FilePath;
        if (!System.IO.File.Exists(path)) return $"none ({path})";
        var text = System.IO.File.ReadAllText(path);
        return $"{text.Length} chars ({path}): {text.Substring(0, Math.Min(120, text.Length)).Replace("\n", " ")}";
    }

    /// <summary>
    /// Fixture: replaces the personal templates file with this text (as a newer YAVSOS would write it); "" deletes the
    /// file. The panel then reads the file again, as after a game start.
    /// </summary>
    public static string WriteRaw(string text)
    {
        if (Plugin.Instance is not { } p) return "no plugin";
        var path = p.PersonalTemplates.File.FilePath;
        if (text.Length == 0) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); p.PersonalTemplates.File.Forget(); return "deleted " + path; }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        p.PersonalTemplates.File.Forget();
        return $"written {text.Length} ({path})";
    }

    /// <summary>Fixture: fills the personal list up to <paramref name="count"/> templates copied from the open chest's rule, for the limit check.</summary>
    public static string Fill(int count) => Step(r =>
    {
        for (var i = r.PersonalList?.List?.Count ?? 0; i < Math.Min(count, TemplateLimits.PerList); i++)
        {
            r.BeginSaveTemplateNow();
            if (r.Naming != TemplateNaming.Save) return;
            r.NameTyped("Lab fill " + i);
            r.SubmitNameNow();
        }
    });

    private static void Answer(RuleControls r, bool yes) { if (r.Asking != null) r.AnswerTemplateNow(yes); }

    private static string Selected(string list, string template, Action<RuleControls> then) => Step(r =>
    {
        var kind = list == "server" ? TemplateListKind.Server : TemplateListKind.Personal;
        var found = (kind == TemplateListKind.Server ? r.ServerList?.List : r.PersonalList?.List)?.Templates
            .FirstOrDefault(t => t.Id == template || TemplateNames.Comparer.Equals(t.Name, template));
        if (found == null || !r.LabSelect(kind, found.Id)) { r.Messages.Say(Core.Interface.PanelControl.Template, Core.Messages.Message.Of("yavsos_template_gone")); return; }
        then(r);
    });

    private static string Step(Action<RuleControls> step)
    {
        if (Rules is not { } r) return "no panel";
        r.LabQueue(() => step(r));
        return "queued";
    }
}
