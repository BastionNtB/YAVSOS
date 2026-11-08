using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Templates;

namespace YAVSOS.Interface;

/// <summary>What the Templates tab's name field is for: nothing, a new template, or renaming the selected one.</summary>
internal enum TemplateNaming { None, Save, Rename }

/// <summary>
/// X23 (D157, D166): the Templates tab. Personal templates are one list every character shares, in BepInEx/config (<see cref="PersonalTemplates"/>);
/// server templates are the server's list (<see cref="Networking.TemplateHost"/>), which only admins change in 1.3, each
/// change naming the revision the admin saw. Apply sets the selected template on the open chest as Paste does, so it
/// saves through the owner's game on another player's chest (X32) and ↶ takes it back. Anything that changes a server
/// template, and deleting a personal one, asks first in place of the buttons; a template has no undo.
/// </summary>
internal sealed partial class RuleControls
{
    private readonly Random templateIds = new Random();
    private (TemplateListKind Kind, string Id, ulong Seen)? templateSelected;
    private string askedName = "";

    internal bool PickingTemplates { get; private set; }
    internal void ShowTemplates() => Queue(() => { PickingTemplates = true; PickingCategories = PickingItems = PickingFilters = false; Revision++; });

    internal TemplateNaming Naming { get; private set; }
    /// <summary>The name field's text, as typed.</summary>
    internal string NameDraft { get; private set; } = "";
    /// <summary>The question shown in place of the buttons, or null.</summary>
    internal TemplateAsk? Asking { get; private set; }

    /// <summary>The personal templates every character on the PC shares; null before the plugin starts.</summary>
    internal TemplateListRead? PersonalList => Plugin.Instance?.PersonalTemplates.Read();

    /// <summary>
    /// The server's templates, or null when the Server templates section is absent: in single player, and on a server
    /// without YAVSOS (it sends no list).
    /// </summary>
    internal TemplateListRead? ServerList => !ZNet.IsSinglePlayer && Plugin.Instance?.Templates is { } host ? host.List : null;

    /// <summary>For which buttons show: this player looks like a server admin (the server checks each change).</summary>
    internal bool TemplateAdmin => Plugin.Instance?.Templates?.LooksAdmin() == true;
    /// <summary>A change to a server template waits for the server's answer.</summary>
    internal bool TemplateWaiting => Plugin.Instance?.Templates?.Waiting == true;

    /// <summary>The selected template and its list, or null (none selected, or it went).</summary>
    internal RuleTemplate? SelectedTemplate(out TemplateListKind kind)
    {
        kind = TemplateListKind.Personal;
        if (templateSelected is not { } selected) return null;
        kind = selected.Kind;
        var list = selected.Kind == TemplateListKind.Server ? ServerList?.List : PersonalList?.List;
        return list?.Find(selected.Id);
    }

    internal bool IsSelected(TemplateListKind kind, string id) => templateSelected is { } s && s.Kind == kind && s.Id == id;

    /// <summary>The template's parts that don't exist here (D157), by ID.</summary>
    internal TemplateMissing MissingParts(RuleTemplate template)
    {
        var plugin = Plugin.Instance!; var taxonomy = plugin.RuleTaxonomy; var db = ObjectDB.instance;
        Func<string, bool>? hasItem = db ? prefab => db.GetItemPrefab(prefab) != null : null;
        return TemplateMissing.Find(template, taxonomy.Categories.ContainsKey, hasItem, CanCheck);
    }

    // A known filter value this game can check: EpicLoot's rarity needs EpicLoot; an origin filter names a loaded mod.
    private static bool CanCheck(string dimension, string value) => dimension switch
    {
        "rarity" => Chainloader.PluginInfos.ContainsKey("randyknapp.mods.epicloot"),
        "mod" or "related-mod" or "construction-mod" => value == RuleText.Vanilla || Chainloader.PluginInfos.ContainsKey(value),
        _ => true
    };

    /// <summary>A category's name for template text, escaped for rich text; an ID this category set lacks as it is.</summary>
    internal static string TemplateCategoryName(string id)
    {
        var taxonomy = Plugin.Instance!.RuleTaxonomy;
        return Texts.Escape(taxonomy.Categories.ContainsKey(id) ? CategoryName(taxonomy, id) : id);
    }

    internal static string TemplateItemName(string prefab) => Texts.Escape(ItemName(prefab));

    /// <summary>The template's filters in a few words, as the chips show them; unknown ones are named as missing instead.</summary>
    internal static MessageList? TemplateFilters(RuleTemplate template) => RuleText.Short(template.Filters.Where(x => ClassificationFilters.IsKnown(x.Key))
        .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.Where(v => ClassificationFilters.IsKnownRuleValue(x.Key, v)).ToList(), StringComparer.Ordinal), OpenValueName);

    // ---- Clicks. Each runs from the queue on the next LateUpdate, like every panel click.
    internal void SelectTemplate(TemplateListKind kind, string id) => Queue(() =>
    {
        Naming = TemplateNaming.None; Asking = null;
        if (IsSelected(kind, id)) templateSelected = null;
        else
        {
            var list = kind == TemplateListKind.Server ? ServerList?.List : PersonalList?.List;
            // The revision seen now is what a change names (D157).
            templateSelected = list?.Find(id) is { } found ? (kind, id, found.Revision) : null;
        }
        Revision++;
    });

    /// <summary>"+ Save this chest's rule as a template": asks for a name, filled with the rule's first category (D157).</summary>
    internal void BeginSaveTemplate() => Queue(BeginSaveTemplateNow);
    internal void BeginSaveTemplateNow()
    {
        Asking = null;
        if (!RuleCopy.CanCopy(edit)) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NoRule, "")); return; }
        Naming = TemplateNaming.Save; NameDraft = DefaultTemplateName(PersonalList?.List);
        templateSelected = null; Revision++;
    }

    internal void BeginRenameTemplate() => Queue(BeginRenameTemplateNow);
    internal void BeginRenameTemplateNow()
    {
        if (SelectedTemplate(out _) is not { } template) return;
        Asking = null; Naming = TemplateNaming.Rename; NameDraft = template.Name; Revision++;
    }

    /// <summary>Typing in the name field (not queued: nothing is saved until Save or Rename).</summary>
    internal void NameTyped(string text) => NameDraft = text ?? "";

    internal void CancelName() => Queue(() => { Naming = TemplateNaming.None; Revision++; });

    /// <summary>Save or Rename beside the name field (or Enter in it).</summary>
    internal void SubmitName() => Queue(SubmitNameNow);
    internal void SubmitNameNow()
    {
        var naming = Naming; var typed = NameDraft;
        if (naming == TemplateNaming.Save) SaveTemplate(typed);
        else if (naming == TemplateNaming.Rename) RenameTemplate(typed);
        Revision++;
    }

    internal void ApplyTemplate() => Queue(ApplyTemplateNow);
    internal void ApplyTemplateNow()
    {
        if (Mode != RulePanelMode.Edit || SelectedTemplate(out _) is not { } template) return;
        // Exactly as Paste: the editor takes the template's rule, then the one save (compare-and-set, or the owner's game).
        edit.Paste(template.ToCopy(), Plugin.Instance!.RuleTaxonomy);
        Save(edit.Edit(), PanelControl.Template);
    }

    /// <summary>"Update from this chest": a personal template changes at once; a server template asks first.</summary>
    internal void UpdateTemplate() => Queue(UpdateTemplateNow);
    internal void UpdateTemplateNow()
    {
        if (SelectedTemplate(out var kind) is not { } template) return;
        if (!RuleCopy.CanCopy(edit)) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NoRule, "")); return; }
        if (kind == TemplateListKind.Server) { Ask(TemplateAsk.UpdateServer); return; }
        ChangePersonal(list => list.Replace(template.Id, RuleTemplate.Of(edit, template.Id, template.Name)!), template.Name);
    }

    internal void DeleteTemplate() => Queue(DeleteTemplateNow);
    internal void DeleteTemplateNow()
    {
        if (SelectedTemplate(out var kind) is null) return;
        Ask(kind == TemplateListKind.Server ? TemplateAsk.DeleteServer : TemplateAsk.DeletePersonal);
    }

    /// <summary>Share (admins): a copy of a personal template to the server; one with missing parts asks first.</summary>
    internal void ShareTemplate() => Queue(ShareTemplateNow);
    internal void ShareTemplateNow()
    {
        if (SelectedTemplate(out var kind) is not { } template || kind != TemplateListKind.Personal) return;
        if (MissingParts(template).Count != 0) { Ask(TemplateAsk.ShareMissing); return; }
        SendServer(id => TemplateChangeRequest.Share(id, template));
    }

    /// <summary>Keep a copy: a server template into the personal templates, as a snapshot.</summary>
    internal void KeepTemplateCopy() => Queue(KeepTemplateCopyNow);
    internal void KeepTemplateCopyNow()
    {
        if (SelectedTemplate(out var kind) is not { } template || kind != TemplateListKind.Server) return;
        var read = Plugin.Instance!.PersonalTemplates.Read();
        if (read.List is not { } list || !read.Writable) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.Unreadable, "")); return; }
        var copy = template.With(id: list.NewId(templateIds.Next), name: TemplateNames.Free(template.Name, n => list.HasName(n)), revision: 1, by: "");
        if (ChangePersonal(current => current.Add(copy), copy.Name) is { } added) templateSelected = (TemplateListKind.Personal, added.Id, added.Revision);
    }

    /// <summary>Yes or No in place of the buttons.</summary>
    internal void AnswerTemplate(bool yes) => Queue(() => AnswerTemplateNow(yes));
    internal void AnswerTemplateNow(bool yes)
    {
        var asked = Asking; Asking = null; Revision++;
        if (!yes || asked == null || SelectedTemplate(out _) is not { } template) return;
        var seen = templateSelected!.Value.Seen;
        switch (asked.Value)
        {
            case TemplateAsk.DeletePersonal:
                if (ChangePersonal(list => list.Delete(template.Id), template.Name, deleted: true) != null || PersonalList?.List?.Find(template.Id) == null) templateSelected = null;
                break;
            case TemplateAsk.UpdateServer:
                if (!RuleCopy.CanCopy(edit)) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NoRule, "")); return; }
                // This chest's rule as it is at Yes; built inside the send, whose refusals are said in the strip.
                SendServer(id => TemplateChangeRequest.Update(id, template.Id, seen, RuleTemplate.Of(edit, template.Id, template.Name)!));
                break;
            case TemplateAsk.RenameServer:
                var name = askedName;
                SendServer(id => TemplateChangeRequest.Rename(id, template.Id, seen, name));
                break;
            case TemplateAsk.DeleteServer:
                SendServer(id => TemplateChangeRequest.Delete(id, template.Id, seen));
                break;
            case TemplateAsk.ShareMissing:
                SendServer(id => TemplateChangeRequest.Share(id, template));
                break;
        }
    }

    private void Ask(TemplateAsk ask) { Naming = TemplateNaming.None; Asking = ask; Revision++; }

    /// <summary>The question's text, with the names it changes.</summary>
    internal Message? AskingText()
    {
        if (Asking is not { } ask || SelectedTemplate(out _) is not { } template) return null;
        return TemplateText.Confirm(ask, Texts.Escape(template.Name), Texts.Escape(askedName), MissingParts(template).Count);
    }

    private void SaveTemplate(string typed)
    {
        var read = Plugin.Instance!.PersonalTemplates.Read();
        if (read.List is not { } list || !read.Writable) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.Unreadable, "")); return; }
        if (TemplateNames.Clean(typed) is not { } name) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NameInvalid, "")); return; }
        RuleTemplate? template;
        try { template = RuleTemplate.Of(edit, list.NewId(templateIds.Next), name); }
        catch (ArgumentException) { template = null; }
        if (template == null) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NoRule, "")); return; }
        if (ChangePersonal(current => current.Add(template), name) is { } saved)
        {
            Naming = TemplateNaming.None;
            templateSelected = (TemplateListKind.Personal, saved.Id, saved.Revision);
        }
    }

    private void RenameTemplate(string typed)
    {
        if (SelectedTemplate(out var kind) is not { } template) { Naming = TemplateNaming.None; return; }
        if (TemplateNames.Clean(typed) is not { } name) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.NameInvalid, "")); return; }
        if (StringComparer.Ordinal.Equals(name, template.Name)) { Naming = TemplateNaming.None; return; } // Nothing to change.
        if (kind == TemplateListKind.Server) { askedName = name; Ask(TemplateAsk.RenameServer); return; }
        if (ChangePersonal(list => list.Rename(template.Id, name), name) != null) Naming = TemplateNaming.None;
    }

    /// <summary>One change to the personal list, then saved; the changed template, or null when refused (said in the strip).</summary>
    private RuleTemplate? ChangePersonal(Func<TemplateList, TemplateEdit> change, string name, bool deleted = false)
    {
        var store = Plugin.Instance!.PersonalTemplates;
        var read = store.Read();
        if (read.List is not { } list || !read.Writable) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.Unreadable, "")); return null; }
        TemplateEdit result;
        try { result = change(list); }
        catch (ArgumentException) { SayTemplate(TemplateText.PersonalRefusal(TemplateOutcome.Malformed, "")); return null; }
        if (!result.Done) { SayTemplate(TemplateText.PersonalRefusal(result.Outcome, Texts.Escape(name))); return null; }
        var wrote = store.Write(result.List);
        if (wrote != TemplateOutcome.Done) { SayTemplate(TemplateText.PersonalRefusal(wrote, Texts.Escape(name))); return null; }
        Revision++;
        return deleted ? null : result.Template;
    }

    // D101: a personal change says nothing when it worked (the list shows it); a refusal stays in the strip.
    private void SayTemplate(Message? message, MessageKind kind = MessageKind.Refusal)
    {
        Messages.Say(PanelControl.Template, message, kind); Revision++;
    }

    private void SendServer(Func<ulong, TemplateChangeRequest> make)
    {
        if (Plugin.Instance?.Templates is not { } host || ServerList == null) return;
        TemplateOutcome? refused;
        try { refused = host.Send(make); }
        catch (ArgumentException) { refused = TemplateOutcome.Malformed; }
        // Sent: the strip says so until the answer replaces it (the host's own game answered already).
        if (refused == null) { if (host.Waiting) SayTemplate(Message.Of("yavsos_template_waiting"), MessageKind.Summary); return; }
        var name = SelectedTemplate(out _)?.Name ?? "";
        var (text, kind) = TemplateText.ServerAnswer(TemplateOperation.Share, refused.Value, Texts.Escape(name), gone: false);
        SayTemplate(text, kind);
    }

    // The server answered (or didn't in time). The selection follows what was saved; the strip says the outcome.
    private void TemplateAnswered(TemplateChangeRequest request, TemplateChangeReply reply)
    {
        var list = ServerList?.List;
        var current = request.Id.Length == 0 ? null : list?.Find(request.Id);
        var name = request.Operation switch
        {
            TemplateOperation.Share => request.Template!.Name,
            TemplateOperation.Rename when reply.Outcome == TemplateOutcome.Done => request.Name,
            _ => current?.Name ?? request.Template?.Name ?? ""
        };
        if (reply.Outcome == TemplateOutcome.Done)
        {
            if (request.Operation == TemplateOperation.Share) templateSelected = (TemplateListKind.Server, reply.Id, reply.Revision);
            else if (request.Operation == TemplateOperation.Delete) { if (IsSelected(TemplateListKind.Server, request.Id)) templateSelected = null; }
            else if (IsSelected(TemplateListKind.Server, request.Id)) templateSelected = (TemplateListKind.Server, request.Id, reply.Revision);
        }
        // "This template changed, look again": the details already show the new version; the next try names it.
        else if (reply.Outcome == TemplateOutcome.Changed && current != null && IsSelected(TemplateListKind.Server, request.Id))
            templateSelected = (TemplateListKind.Server, request.Id, current.Revision);
        var (text, kind) = TemplateText.ServerAnswer(request.Operation, reply.Outcome, Texts.Escape(name), gone: reply.Outcome == TemplateOutcome.Changed && current == null);
        SayTemplate(text, kind);
    }

    /// <summary>The name Save suggests: the rule's first category as the chips order them, else its first Always item.</summary>
    private string DefaultTemplateName(TemplateList? list)
    {
        var taxonomy = Plugin.Instance!.RuleTaxonomy; var names = CategoryText.For(taxonomy);
        var first = CategorySelection.Normalize(taxonomy, Categories.Where(taxonomy.Categories.ContainsKey))
            .OrderBy(x => taxonomy.Categories[x].DisplayRank).ThenBy(names.Shown, StringComparer.CurrentCulture).Select(names.Shown).FirstOrDefault()
            ?? Includes.Select(ItemName).OrderBy(x => x, StringComparer.CurrentCulture).FirstOrDefault();
        var name = TemplateNames.Clean(first) ?? TemplateNames.Clean(first?.Substring(0, Math.Min(first.Length, TemplateLimits.NameLength)))
            ?? Texts.Get("yavsos_template_default_name");
        return list == null ? name : TemplateNames.Free(name, n => list.HasName(n));
    }

    // Another chest: a half-typed name or an open question goes; the selection stays, to apply it here too.
    private void TemplateChestChanged() { Naming = TemplateNaming.None; Asking = null; }

    private void TemplatesLeftWorld() { templateSelected = null; Naming = TemplateNaming.None; Asking = null; }

#if YAVSOS_LAB
    /// <summary>Lab: the selection and the revision it saw, for TemplateLab.</summary>
    internal string LabSelection => templateSelected is { } s ? $"{s.Kind}/{s.Id}@{s.Seen}" : "none";
    /// <summary>Lab: runs a step at the next LateUpdate, as a click would.</summary>
    internal void LabQueue(Action step) => Queue(step);
    /// <summary>Lab: selects a template now (inside a queued step), whatever was selected.</summary>
    internal bool LabSelect(TemplateListKind kind, string id)
    {
        var list = kind == TemplateListKind.Server ? ServerList?.List : PersonalList?.List;
        templateSelected = list?.Find(id) is { } found ? (kind, id, found.Revision) : null;
        Naming = TemplateNaming.None; Asking = null; Revision++;
        return templateSelected != null;
    }
    /// <summary>Lab: names the selection's seen revision, to stand in for a stale view.</summary>
    internal void LabSeen(ulong seen) { if (templateSelected is { } s) templateSelected = (s.Kind, s.Id, seen); }
#endif
}
