using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Core.Templates;

namespace YAVSOS.Interface;

/// <summary>
/// X23 (D157, wireframe x23-templates-list-wireframes.html): the Templates tab, inside the Edit section's tab body (D101:
/// as tall as the other tabs). From the top: "+ Save this chest's rule as a template" (or the name field in its place),
/// the list with its Personal templates and Server templates sections, then the details area, which keeps its size: a
/// placeholder and greyed-out buttons until a template is selected, a question in place of the buttons before a change
/// with no undo, the name field in their place for Rename. The panel never grows or shrinks. Rows are rebuilt only when
/// the lists, the selection or the language change. Unity game thread only.
/// </summary>
internal sealed class TemplateSection : PanelSection
{
    private const float Pad = 6, RowGap = 2;
    private static readonly Color RowBack = new Color(0, 0, 0, 0.25f), RowChosen = new Color(0.35f, 0.24f, 0.04f, 0.6f),
        DetailBack = new Color(0, 0, 0, 0.25f), Warning = new Color(0.95f, 0.76f, 0.31f);
    private JotunnWidgets ui = null!;
    private RectTransform saveRow = null!, nameRow = null!, list = null!, buttons = null!, confirm = null!, renameRow = null!;
    private Button saveButton = null!;
    private InputField nameField = null!, renameField = null!;
    private Text title = null!, details = null!, missing = null!, question = null!;
    private CanvasGroup apply = null!, update = null!, rename = null!, delete = null!, share = null!, keep = null!;
    private GameObject saveFlag = null!, detailFlag = null!;
    private string rowsKey = "\0", detailsKey = "\0";
    private TemplateNaming namingShown;
    private float textWidth;
    internal override string Name => "Templates";
    internal override PanelPart Part => PanelPart.Filters; // Inside the Edit section's body, folded with it (X2).

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        Root.GetComponent<VerticalLayoutGroup>().spacing = RowGap;
        // The top row: the Save button, or the name field with Save and ✕ in its place (the same height).
        saveRow = ui.Row(Root);
        var saveText = Message.Of("yavsos_template_save");
        saveButton = ui.Button(saveRow, Texts.Get(saveText), rule.BeginSaveTemplate);
        Tip(saveButton, Tooltip.Of(saveText, Message.Of("yavsos_template_save_tip")));
        saveFlag = Flag(saveButton);
        nameRow = ui.Row(Root);
        nameField = NameField(nameRow, rule, "yavsos_template_name_hint");
        var save = ui.Button(nameRow, Texts.Get("yavsos_template_name_save"), rule.SubmitName, 80);
        Tip(save, Tooltip.Of(Message.Of("yavsos_template_name_save"), Message.Of("yavsos_template_name_save_tip")));
        Cancel(nameRow, rule);
        Show(nameRow, false);
        // The details area's fixed size: a title line, two lines of what it holds, the missing line, two rows of buttons.
        // Measured on labels of the details area's own sizes, so the sum is what Fixed gives them.
        var probe = ui.Label(Root, "", JotunnWidgets.SmallSize); var heading = ui.Label(Root, "", JotunnWidgets.SmallSize + 1);
        var titleHeight = JotunnWidgets.LinesHeight(heading, 1); var textHeight = JotunnWidgets.LinesHeight(probe, 2); var lineHeight = JotunnWidgets.LinesHeight(probe, 1);
        foreach (var gone in new[] { probe, heading }) { gone.gameObject.SetActive(false); Object.Destroy(gone.gameObject); }
        var actions = 2 * (JotunnWidgets.RowHeight + 4) + RowGap;
        var detailHeight = 2 * Pad + titleHeight + textHeight + lineHeight + actions + 3 * RowGap;
        var listHeight = PickerSection.BodyHeight - (JotunnWidgets.RowHeight + 4) - detailHeight - 2 * RowGap;
        list = ui.ScrollList(Root, listHeight);
        var box = new GameObject("Template details", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        box.transform.SetParent(Root, false);
        box.GetComponent<Image>().color = DetailBack;
        var layout = box.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad); layout.spacing = RowGap;
        layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        var size = box.GetComponent<LayoutElement>(); size.minHeight = size.preferredHeight = detailHeight; size.flexibleWidth = 1;
        textWidth = RulePanel.Width - 28 - 2 * Pad;
        title = Fixed(ui.Label(box.transform, "", JotunnWidgets.SmallSize + 1, ui.Orange), 1);
        details = Fixed(ui.Label(box.transform, "", JotunnWidgets.SmallSize, ui.Beige), 2);
        missing = Fixed(ui.Label(box.transform, "", JotunnWidgets.SmallSize, Warning), 1);
        Tip(details, () => SelectedTip(rule)); Tip(missing, () => SelectedTip(rule));
        detailFlag = Flag((RectTransform)box.transform);
        // The buttons, the question or the rename field: one at a time, in the same space.
        buttons = Slot(box.transform, actions);
        var first = ui.Row(buttons, 4); var second = ui.Row(buttons, 4);
        apply = Action(first, "yavsos_template_apply", "yavsos_template_apply_tip", rule.ApplyTemplate, 84);
        update = Action(first, "yavsos_template_update", "yavsos_template_update_tip", rule.UpdateTemplate, 0);
        rename = Action(first, "yavsos_template_rename", "yavsos_template_rename_tip", rule.BeginRenameTemplate, 98);
        delete = Action(second, "yavsos_template_delete", "yavsos_template_delete_tip", rule.DeleteTemplate, 98);
        share = Action(second, "yavsos_template_share", "yavsos_template_share_tip", rule.ShareTemplate, 98);
        keep = Action(second, "yavsos_template_keep", "yavsos_template_keep_tip", rule.KeepTemplateCopy, 0);
        confirm = Slot(box.transform, actions);
        var ask = ui.Row(confirm, 4);
        question = ui.Label(ask, "", JotunnWidgets.SmallSize, Warning);
        question.verticalOverflow = VerticalWrapMode.Truncate;
        question.GetComponent<LayoutElement>().minHeight = actions;
        ui.Button(ask, Texts.Get("yavsos_template_yes"), () => rule.AnswerTemplate(true), 64);
        ui.Button(ask, Texts.Get("yavsos_template_no"), () => rule.AnswerTemplate(false), 64);
        Show(confirm, false);
        renameRow = Slot(box.transform, actions);
        var renaming = ui.Row(renameRow, 4);
        renameField = NameField(renaming, rule, "yavsos_template_name_hint");
        var done = ui.Button(renaming, Texts.Get("yavsos_template_name_rename"), rule.SubmitName, 98);
        Tip(done, Tooltip.Of(Message.Of("yavsos_template_name_rename"), Message.Of("yavsos_template_name_rename_tip")));
        Cancel(renaming, rule);
        Show(renameRow, false);
    }

    // A label of fixed lines: longer text is cut with "…", the tooltip has it whole (D101).
    private static Text Fixed(Text label, int lines)
    {
        label.verticalOverflow = VerticalWrapMode.Truncate;
        var layout = label.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = JotunnWidgets.LinesHeight(label, lines);
        return label;
    }

    private RectTransform Slot(Transform parent, float height)
    {
        var slot = ui.Column(parent, "Template actions", RowGap);
        var size = slot.gameObject.AddComponent<LayoutElement>(); size.minHeight = size.preferredHeight = height;
        return slot;
    }

    private CanvasGroup Action(Transform row, string key, string tip, System.Action click, float width)
    {
        var text = Message.Of(key);
        var button = ui.Button(row, Texts.Get(text), click, width);
        Tip(button, Tooltip.Of(text, Message.Of(tip)));
        return button.gameObject.AddComponent<CanvasGroup>();
    }

    private InputField NameField(Transform row, RuleControls rule, string hint)
    {
        var field = ui.Input(row, Texts.Get(hint), rule.NameTyped);
        field.characterLimit = TemplateLimits.NameLength;
        // Enter saves, as the Save button does; leaving the field otherwise keeps what was typed.
        field.onEndEdit.AddListener(_ =>
        {
            var input = BepInEx.UnityInput.Current;
            if (input.GetKeyDown(KeyCode.Return) || input.GetKeyDown(KeyCode.KeypadEnter)) rule.SubmitName();
        });
        return field;
    }

    private void Cancel(Transform row, RuleControls rule)
    {
        var button = ui.Button(row, "", rule.CancelName, JotunnWidgets.RowHeight + 4);
        var go = new GameObject("Cross", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(button.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(12, 12);
        var image = go.GetComponent<Image>(); image.sprite = PanelArt.Cross; image.color = ui.Beige; image.raycastTarget = false;
        Tip(button, Tooltip.Of(Message.Of("yavsos_template_name_cancel"), Message.Of("yavsos_template_name_cancel_tip")));
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var shown = mode == RulePanelMode.Edit && rule.PickingTemplates;
        Show(Root, shown);
        if (!shown) { rowsKey = detailsKey = "\0"; namingShown = TemplateNaming.None; return; }
        var personal = rule.PersonalList; var server = rule.ServerList; var admin = rule.TemplateAdmin;
        var plugin = Plugin.Instance!;
        var selected = rule.SelectedTemplate(out var kind);
        var lists = RuntimeHelpers.GetHashCode(personal) + "|" + RuntimeHelpers.GetHashCode(server) + "|" + admin + "|" + Texts.Generation + "|" +
            RuntimeHelpers.GetHashCode(plugin.RuleTaxonomy) + "|" + (plugin.LoadedCatalog?.Fingerprint ?? "");
        var key = lists + "|" + (selected == null ? "-" : kind + "/" + selected.Id);
        if (key != rowsKey) { rowsKey = key; Rows(rule, personal, server, admin); }
        Naming(rule);
        var waiting = rule.TemplateWaiting;
        var next = key + "|" + rule.Naming + "|" + rule.Asking + "|" + waiting + "|" + rule.CanCopy + "|" + (selected?.Revision ?? 0);
        if (next != detailsKey) { detailsKey = next; Details(rule, selected, kind, personal, server, admin, waiting); }
        Flagged(saveFlag, rule, PanelControl.Template); Flagged(detailFlag, rule, PanelControl.Template);
    }

    // Shows the name field when naming starts, filled with the suggested name and focused.
    private void Naming(RuleControls rule)
    {
        var naming = rule.Naming;
        Show(saveRow, naming != TemplateNaming.Save); Show(nameRow, naming == TemplateNaming.Save);
        if (naming == namingShown) return;
        namingShown = naming;
        var field = naming == TemplateNaming.Save ? nameField : naming == TemplateNaming.Rename ? renameField : null;
        if (field == null) return;
        field.SetTextWithoutNotify(rule.NameDraft);
        field.ActivateInputField(); field.Select();
    }

    private void Rows(RuleControls rule, TemplateListRead? personal, TemplateListRead? server, bool admin)
    {
        JotunnWidgets.Clear(list); Rebuilt();
        Section(Message.Of("yavsos_template_personal_heading"), personal?.List?.Count ?? 0);
        if (personal == null) Note(Message.Of("yavsos_edit_no_player"));
        else if (!personal.Writable) Note(Message.Of("yavsos_template_personal_unreadable"));
        else if (personal.List!.Count == 0) Note(Message.Of("yavsos_template_personal_none"));
        else foreach (var template in personal.List.Templates) Row(rule, template, TemplateListKind.Personal);
        if (server == null) return; // Single player, or a server without YAVSOS: no section (D157).
        Section(Message.Of("yavsos_template_server_heading"), server.List?.Count ?? 0);
        if (server.List == null) Note(Message.Of("yavsos_template_server_unreadable"));
        else if (server.List.Count == 0) Note(Message.Of(admin ? "yavsos_template_server_none_admin" : "yavsos_template_server_none"));
        else foreach (var template in server.List.Templates) Row(rule, template, TemplateListKind.Server);
    }

    private void Section(Message heading, int count)
    {
        var row = ui.Row(list, 6);
        ui.Label(row, Texts.Get(heading), JotunnWidgets.SmallSize, ui.Orange);
        ui.Label(row, Texts.Get(TemplateText.Count(count)), JotunnWidgets.SmallSize - 2, ui.Faint, false).alignment = TextAnchor.MiddleRight;
    }

    private void Note(Message text) => ui.Label(list, Texts.Get(text), JotunnWidgets.SmallSize - 1, ui.Faint);

    // A row: the name, then "1 part missing here" in amber and the summary, each one line; a click selects it.
    private void Row(RuleControls rule, RuleTemplate template, TemplateListKind kind)
    {
        var go = new GameObject("Template " + template.Id, typeof(RectTransform), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(list, false);
        var back = go.GetComponent<Image>(); back.color = rule.IsSelected(kind, template.Id) ? RowChosen : RowBack;
        var button = go.GetComponent<Button>(); button.targetGraphic = back;
        var id = template.Id;
        button.onClick.AddListener(() => rule.SelectTemplate(kind, id));
        var layout = go.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(6, 6, 3, 3); layout.spacing = 0;
        layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        var room = RulePanel.Width - 28 - 8 - 12 - 14; // The list's padding, the row's, and the scroll bar.
        var name = ui.Label(go.transform, "", JotunnWidgets.SmallSize, ui.Beige);
        name.text = JotunnWidgets.Fit(name, template.Name, room, 1);
        var line = ui.Row(go.transform, 4);
        line.GetComponent<LayoutElement>().minHeight = 0;
        var parts = rule.MissingParts(template);
        float used = 0;
        if (parts.Count != 0)
        {
            var warn = ui.Label(line, Texts.Plain(TemplateText.Missing(parts.Count)), JotunnWidgets.SmallSize - 2, Warning, false);
            used = JotunnWidgets.LineWidth(warn, warn.text) + 4;
            warn.GetComponent<LayoutElement>().preferredWidth = used - 4;
        }
        var summary = ui.Label(line, "", JotunnWidgets.SmallSize - 2, ui.Faint);
        summary.horizontalOverflow = HorizontalWrapMode.Wrap; summary.verticalOverflow = VerticalWrapMode.Truncate;
        summary.GetComponent<LayoutElement>().preferredWidth = 0;
        summary.text = JotunnWidgets.Fit(summary, Texts.Plain(TemplateText.Summary(template, kind, RuleControls.TemplateCategoryName)),
            Mathf.Max(1, room - used), 1);
        Tip(back, () => Tip(rule, template, kind, parts));
    }

    private static Tooltip Tip(RuleControls rule, RuleTemplate template, TemplateListKind kind, TemplateMissing parts) =>
        TemplateText.Tip(Texts.Escape(template.Name), TemplateText.Details(template, kind, RuleControls.TemplateCategoryName, RuleControls.TemplateItemName,
            RuleControls.TemplateFilters(template)), TemplateText.MissingLine(parts, Texts.Escape));

    private static Tooltip? SelectedTip(RuleControls rule) =>
        rule.SelectedTemplate(out var kind) is { } template ? Tip(rule, template, kind, rule.MissingParts(template)) : null;

    private void Details(RuleControls rule, RuleTemplate? template, TemplateListKind kind, TemplateListRead? personal, TemplateListRead? server, bool admin, bool waiting)
    {
        if (template == null)
        {
            Set(title, Texts.Get("yavsos_template_details_heading")); title.color = ui.Faint;
            Set(details, Texts.Get("yavsos_template_details_none")); details.color = ui.Faint;
            Set(missing, "");
        }
        else
        {
            var heading = kind == TemplateListKind.Server ? Texts.Plain(Message.Of("yavsos_template_details_server").With("name", template.Name)) : template.Name;
            Set(title, JotunnWidgets.Fit(title, heading, textWidth, 1));
            title.color = ui.Orange; details.color = ui.Beige;
            var lines = TemplateText.Details(template, kind, RuleControls.TemplateCategoryName, RuleControls.TemplateItemName, RuleControls.TemplateFilters(template));
            Set(details, JotunnWidgets.Fit(details, Texts.Plain(lines), textWidth, 2));
            var line = TemplateText.MissingLine(rule.MissingParts(template));
            Set(missing, line == null ? "" : JotunnWidgets.Fit(missing, Texts.Plain(line), textWidth, 1));
        }
        var asking = rule.AskingText();
        var renaming = template != null && rule.Naming == TemplateNaming.Rename;
        Show(buttons, asking == null && !renaming); Show(confirm, asking != null); Show(renameRow, renaming);
        if (asking != null) Set(question, JotunnWidgets.Fit(question, Texts.Plain(asking), Mathf.Max(1, textWidth - 2 * 64 - 8), 2));
        // Greyed out until a template is selected; a button that doesn't apply to the selected one keeps its place, hidden.
        var any = template != null; var mine = any && kind == TemplateListKind.Personal; var theirs = any && kind == TemplateListKind.Server;
        var personalWritable = personal?.Writable == true; var serverReady = server?.Writable == true && !waiting;
        var changes = !any || mine || admin;
        Enable(apply, true, any && rule.Mode == RulePanelMode.Edit);
        Enable(update, changes, any && rule.CanCopy && (mine ? personalWritable : serverReady));
        Enable(rename, changes, any && (mine ? personalWritable : serverReady));
        Enable(delete, changes, any && (mine ? personalWritable : serverReady));
        Enable(share, mine && admin && server != null, serverReady);
        Enable(keep, theirs, personalWritable);
    }

    private void Enable(CanvasGroup group, bool shown, bool on)
    {
        var alpha = !shown ? 0 : on ? 1 : 0.45f;
        if (group.alpha != alpha) { group.alpha = alpha; Rebuilt(); }
        if (group.interactable != (shown && on)) group.interactable = shown && on;
        if (group.blocksRaycasts != shown) group.blocksRaycasts = shown;
    }
}
