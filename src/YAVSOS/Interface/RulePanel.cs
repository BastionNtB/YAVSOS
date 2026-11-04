using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Core.Classification;
using YAVSOS.Cleanup;
using YAVSOS.Core;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-03 (D062): the chest rule panel, an opaque Jotunn wood panel inside the inventory screen, kept beside the open
/// chest window's real rectangle (ZenUI may scale or rotate the chest grid). One column of sections; each owns its
/// widgets and refreshes from <see cref="RuleControls"/>. Rows are rebuilt only when what they show changes.
/// D101: while the panel is in use its height doesn't change: every area that shows changing text has a fixed size
/// (chips two rows, the suggestion row, one tab body height, the strip under the header), and teaching text lives in
/// tooltips on the controls. Folding (X2) changes the height only at its click. P10-25 (D132): every message shows in
/// the strip under the header, and the control it came from keeps an amber outline until the next action.
/// Unity game thread only.
/// </summary>
internal sealed class RulePanel
{
    internal const float Width = 430;
    private const float Tick = 0.25f; // Texts that change without a rule edit (cleanup running, owner, answers ready).
    private readonly RuleControls rule;
    private readonly RectTransform parent, root;
    private readonly PanelSection[] sections;
    private readonly Vector3[] corners = new Vector3[4];
    private int shown = -1;
    private float next;
    private Vector2 drag;
    private PanelRect anchor, area, placed;
    private bool? folded; // As the sections were last gated (X2).
    private float unfoldedHeight;
    private Canvas? canvas; // The parent's canvas, found once: Place runs every frame.
    private readonly JotunnWidgets.ScaleWatch scale = new();

    internal RulePanel(JotunnWidgets ui, RectTransform parent, RuleControls rule)
    {
        this.rule = rule; this.parent = parent;
        root = ui.Panel(parent, Width);
        root.gameObject.SetActive(false);
        // Display order, layout A (D071): header with status marks, the strip (P10-25: messages, banners, chest label), Rule,
        // Edit (tabs, then the open tab's picker or filters, in one body of fixed height, D101), This chest, Clean up nearby.
        // Search (P6-06) lives in the Categories tab; run results have their own panel (P6-04); "Where would it go?" is in
        // the item tooltip (P6-23). Folded (X2), only the header and the strip show.
        var header = new HeaderSection(); var picker = new PickerSection();
        sections = new PanelSection[] { header, new StripSection(), new RuleSection(), picker, new FilterSection(), new ChestSection(), new CleanupSection() };
        try
        {
            foreach (var section in sections)
            {
                section.Root = ui.Column(section is FilterSection ? picker.Body : root, section.Name);
                section.Build(ui, rule);
            }
            var handle = header.Title.gameObject.AddComponent<PanelDrag>();
            handle.Moved = Drag; handle.Ended = EndDrag;
        }
        catch { Object.Destroy(root.gameObject); throw; } // A half-built panel must not stay on screen.
    }

    internal bool Alive => root; // Destroyed with the inventory screen on logout.

    internal void Show()
    {
        if (!root.gameObject.activeSelf) { root.gameObject.SetActive(true); root.SetAsLastSibling(); shown = -1; }
        // X2 (S4): a fold or unfold gates the sections and rebuilds them, so the height changes only at the click.
        var fold = rule.Folded.Value;
        if (fold != folded)
        {
            folded = fold; shown = -1;
            foreach (var section in sections) section.Open = PanelFold.Shows(section.Part, fold);
        }
        var structural = shown != rule.Revision;
        if (structural || Time.unscaledTime >= next)
        {
            var mode = rule.Mode; var changed = false;
            foreach (var section in sections)
            {
                var timed = UiCost.Start();
                changed |= section.Update(rule, mode, structural);
                UiCost.Stop("section " + section.Name, timed);
            }
            shown = rule.Revision; next = Time.unscaledTime + Tick;
            var laid = UiCost.Start();
            if (changed) LayoutRebuilder.ForceRebuildLayoutImmediate(root);
            UiCost.Stop("panel layout", laid);
        }
        Place();
    }

    internal void Hide()
    {
        if (Alive && root.gameObject.activeSelf) root.gameObject.SetActive(false);
        drag = Vector2.zero;
    }

    internal void Destroy() { if (Alive) Object.Destroy(root.gameObject); }

    // Follows the chest window every frame: its corners, not its anchors, so other mods' scaling and rotation count.
    private void Place()
    {
        var gui = InventoryGui.instance;
        if (!gui || !gui.m_container) return;
        anchor = Bounds(gui.m_container);
        var grid = gui.ContainerGrid ? gui.ContainerGrid.m_gridRoot : null;
        if (grid)
        {
            // The grid's visible frame is its parent (the scroll viewport); a rotated grid (ZenUI) no longer fits that frame.
            if (grid!.parent is RectTransform frame) anchor = anchor.Union(Bounds(frame));
            if (grid.rotation != Quaternion.identity) anchor = anchor.Union(Bounds(grid));
        }
        if (!canvas) canvas = parent.GetComponentInParent<Canvas>(true);
        scale.Check(root.gameObject, canvas);
        area = canvas && canvas!.rootCanvas.transform is RectTransform screen ? Bounds(screen) : Rect(parent.rect);
        var size = root.rect.size;
        if (folded == false) unfoldedHeight = size.y;
        // X2: folded, placed by the unfolded height, so the header stays where it was and the panel just ends higher.
        placed = PanelPlacement.Beside(anchor, size.x, PanelFold.PlacedHeight(folded == true, size.y, unfoldedHeight), area,
            rule.OffsetX.Value + drag.x, rule.OffsetY.Value + drag.y);
        root.localPosition = new Vector3(placed.Left, placed.Top, 0);
    }

    private PanelRect Bounds(RectTransform target)
    {
        target.GetWorldCorners(corners);
        return PanelRect.Around(Local(0), Local(1), Local(2), Local(3));
    }

    private (float, float) Local(int corner) { var local = parent.InverseTransformPoint(corners[corner]); return (local.x, local.y); }

    private static PanelRect Rect(Rect rect) => new PanelRect(rect.xMin, rect.yMin, rect.width, rect.height);

    private void Drag(Vector2 from, Vector2 to, Camera? camera)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, from, camera, out var a) ||
            !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, to, camera, out var b)) return;
        drag += new Vector2(b.x - a.x, a.y - b.y); // Offsets are stored x right, y down.
    }

    // Stored relative to the chest window and without any part dragged past the screen edge.
    private void EndDrag()
    {
        if (drag == Vector2.zero) return;
        var (x, y) = PanelPlacement.OffsetFor(anchor, placed.Width, placed.Height, area, placed);
        drag = Vector2.zero;
        rule.OffsetX.Value = Mathf.Round(x); rule.OffsetY.Value = Mathf.Round(y);
    }
}

/// <summary>One part of the panel. Built once; updated on every rule revision and on the panel's tick.</summary>
internal abstract class PanelSection
{
    internal RectTransform Root = null!;
    internal abstract string Name { get; }
    /// <summary>Which part of the panel this is, for folding (X2).</summary>
    internal abstract PanelPart Part { get; }
    private bool changed, gated;
    internal abstract void Build(JotunnWidgets ui, RuleControls rule);
    protected abstract void Refresh(RuleControls rule, RulePanelMode mode, bool structural);

    /// <summary>
    /// S4 (audit D05): the panel's gate over the section, for folding (X2). While closed the section is hidden and not
    /// refreshed; once open again its own <see cref="Refresh"/> decides, as before, whether it shows.
    /// </summary>
    internal bool Open { get; set; } = true;

    /// <summary>Returns whether anything shown changed, so the panel lays itself out again only then.</summary>
    internal bool Update(RuleControls rule, RulePanelMode mode, bool structural)
    {
        changed = false;
        if (!Open) { gated = true; Show(Root, false); return changed; }
        if (gated) { gated = false; Show(Root, true); structural = true; } // Reopened: rebuilt from scratch, then hidden again if Refresh says so.
        Refresh(rule, mode, structural);
        return changed;
    }

    protected void Set(Text text, string value) { if (text.text != value) { text.text = value; changed = true; } }
    protected void Set(Button button, string value) { var text = button.GetComponentInChildren<Text>(true); if (text) Set(text, value); }
    protected void Show(Component part, bool on) => Show(part.gameObject, on);
    protected void Show(GameObject part, bool on) { if (part.activeSelf != on) { part.SetActive(on); changed = true; } }
    protected void Rebuilt() => changed = true;

    /// <summary>D101: the game's tooltip on a control; a label is made to catch the pointer for it.</summary>
    protected static void Tip(Component part, System.Func<(string Topic, string Text)?> fill)
    {
        if (part is Graphic graphic) graphic.raycastTarget = true;
        GameTooltip.Attach(part.gameObject, fill);
    }

    protected static void Tip(Component part, string topic, string text) => Tip(part, () => (topic, text));

    /// <summary>
    /// P10-04: a typed tooltip (heading and body), formatted in the player's language each time it opens, so it follows a
    /// language change. Null shows no tooltip.
    /// </summary>
    protected static void Tip(Component part, System.Func<Tooltip?> fill) => Tip(part, () => fill() is { } tip ? Texts.Get(tip) : null);

    protected static void Tip(Component part, Tooltip tooltip) => Tip(part, () => (Tooltip?)tooltip);

    /// <summary>
    /// D101: a status line, two lines high whether or not it says anything, so it never moves the controls. A longer text
    /// is cut with "…"; its tooltip has the whole text.
    /// </summary>
    protected static Text Spot(JotunnWidgets ui, Transform parent, System.Func<Tooltip?> tooltip) =>
        Spot(ui, parent, () => tooltip() is { } tip ? Texts.Get(tip) : null);

    protected static Text Spot(JotunnWidgets ui, Transform parent, System.Func<(string Topic, string Text)?> tooltip)
    {
        var text = ui.Label(parent, "", JotunnWidgets.SmallSize, ui.Orange);
        text.verticalOverflow = VerticalWrapMode.Truncate;
        var layout = text.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = JotunnWidgets.LinesHeight(text, 2);
        // Only the room the row's other parts leave: asking for the text's width squeezed the heading beside it, which
        // then wrapped and grew the row (H4 client check, 2026-10-28).
        layout.preferredWidth = 0;
        Tip(text, tooltip);
        return text;
    }

    /// <summary>P10-25 (D132): an amber outline on a control, shown while the strip's refusal came from it.</summary>
    protected static GameObject Flag(Component control) => JotunnWidgets.Frame((RectTransform)control.transform, Amber);

    /// <summary>Shows <paramref name="flag"/> while <paramref name="control"/> was refused, until the next action.</summary>
    protected void Flagged(GameObject flag, RuleControls rule, PanelControl control) => Show(flag, rule.Messages.Flagged(control));

    /// <summary>A part of a row that takes the room the others leave, so the controls after it sit at the right.</summary>
    protected static void Spacer(Transform row)
    {
        var go = new GameObject("Gap", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(row, false);
        var layout = go.GetComponent<LayoutElement>(); layout.flexibleWidth = 1; layout.minWidth = 0;
    }

    internal static readonly Color Amber = new Color(0.95f, 0.76f, 0.31f);
}

// D071: the title (drag handle) and one status mark per installed integration, hover for what is on or off.
internal sealed class HeaderSection : PanelSection
{
    private static readonly Color Ok = new Color(0.5f, 0.83f, 0.5f), Bad = new Color(0.94f, 0.42f, 0.36f), Off = new Color(0.6f, 0.58f, 0.54f),
        Caution = new Color(0.94f, 0.75f, 0.31f);
    private JotunnWidgets ui = null!;
    private RectTransform row = null!;
    private string key = "\0";
    private RectTransform arrow = null!;
    private TipFill? foldTip;
    internal Text Title { get; private set; } = null!;
    internal override string Name => "Header";
    internal override PanelPart Part => PanelPart.Header;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var line = ui.Row(Root, 6);
        // X2 (D116): ▾ folds the panel, ▸ unfolds it; drawn, since the game's font may lack the glyphs.
        var fold = ui.Button(line, "", rule.ToggleFold, 24);
        var go = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
        arrow = (RectTransform)go.transform;
        arrow.SetParent(fold.transform, false);
        arrow.anchorMin = arrow.anchorMax = arrow.pivot = new Vector2(0.5f, 0.5f); arrow.sizeDelta = new Vector2(12, 12);
        var image = go.GetComponent<Image>(); image.sprite = PanelArt.Down; image.color = ui.Beige; image.raycastTarget = false;
        Tip(fold, () => (Tooltip?)PanelFold.ButtonTooltip(rule.Folded.Value));
        foldTip = fold.GetComponent<TipFill>();
        Title = ui.Label(line, "YAVSOS", JotunnWidgets.TextSize + 4, ui.Orange); // The mod's name, not translated.
        // X53: say what the name stands for (the English name, as a name).
        Tip(Title, Tooltip.Of(Message.Of("yavsos_panel_title_heading").With("version", ProjectIdentity.Version),
            Message.Of("yavsos_panel_title_tip").With("name", ProjectIdentity.Name)));
        row = ui.Row(line, 4);
        row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var turn = rule.Folded.Value ? 90f : 0f; // Turned anticlockwise from down: points right.
        if (!Mathf.Approximately(arrow.localEulerAngles.z, turn))
        {
            arrow.localEulerAngles = new Vector3(0, 0, turn);
            // The tooltip is filled when the pointer enters; after a click it names the new action at once (batch E, low).
            foldTip?.Refresh();
        }
        var marks = rule.Marks();
        var next = PanelMarks.Key(marks);
        if (next == key) return;
        key = next;
        JotunnWidgets.Clear(row); Rebuilt();
        foreach (var mark in marks) Mark(mark);
    }

    private void Mark(IntegrationMark mark)
    {
        var go = new GameObject("Mark " + mark.Kind, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(row, false);
        var layout = go.GetComponent<LayoutElement>(); layout.minWidth = layout.preferredWidth = layout.minHeight = layout.preferredHeight = 24;
        var disc = go.GetComponent<Image>(); disc.sprite = PanelArt.Disc; disc.color = new Color(0, 0, 0, 0.5f); // Catches the pointer for the tooltip.
        var symbol = new GameObject("Symbol", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)symbol.transform;
        rect.SetParent(go.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(15, 15);
        var image = symbol.GetComponent<Image>(); image.raycastTarget = false;
        // A caution is an amber tick: the integration works, with something to know about this chest.
        image.sprite = mark.State switch { MarkState.Working => PanelArt.Check, MarkState.Caution or MarkState.Limited => PanelArt.Bang, MarkState.Broken => PanelArt.Cross, _ => PanelArt.Dash };
        image.color = mark.State switch { MarkState.Working => Ok, MarkState.Broken => Bad, MarkState.Caution or MarkState.Limited => Caution, _ => Off };
        // The heading is the mark's name; each line starts with its state word in the state's color (Core picks the tone).
        var tip = mark.Tooltip;
        GameTooltip.Attach(go, () => Texts.Get(tip));
    }
}

// P10-25 (D132, variant B): one strip under the header for every message: a refusal (amber, its control outlined) or a
// cleanup summary until the next action, else a banner (another player is using the chest; a rule from another version),
// else the chest label, faint, or "No rule". Two lines high, three for a rule from another version, whose panel shows no
// chips. Folded (X2), it is the line under the header. Cut with "…"; the tooltip has the whole text.
internal sealed class StripSection : PanelSection
{
    private static readonly Color Tint = new Color(0.23f, 0.18f, 0.07f, 0.85f), Quiet = new Color(0, 0, 0, 0.12f), Plain = new Color(0, 0, 0, 0.19f),
        LabelColor = new Color32(0xE8, 0xB0, 0x4A, 0xFF);
    private JotunnWidgets ui = null!;
    private Image band = null!;
    private Text line = null!;
    private LayoutElement size = null!;
    private int lines;
    internal override string Name => "Strip";
    internal override PanelPart Part => PanelPart.Strip;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var go = new GameObject("Strip", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(Root, false);
        band = go.GetComponent<Image>(); band.color = Quiet;
        size = go.GetComponent<LayoutElement>(); size.flexibleWidth = 1;
        line = ui.Label(go.transform, "", JotunnWidgets.SmallSize, ui.Faint);
        line.GetComponent<LayoutElement>().ignoreLayout = true;
        line.verticalOverflow = VerticalWrapMode.Overflow; // Fit cuts it to the lines; Truncate can hide a last line drawn a little taller.
        line.alignment = TextAnchor.MiddleLeft;
        var rect = line.rectTransform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(7, 1); rect.offsetMax = new Vector2(-7, -1);
        Lines(PanelStrip.Lines);
        Tip(band, () => (Tooltip?)rule.Strip(rule.Mode).Tip);
    }

    private void Lines(int count)
    {
        if (lines == count) return;
        lines = count;
        size.minHeight = size.preferredHeight = JotunnWidgets.LinesHeight(line, count) + 2;
        Rebuilt();
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var strip = rule.Strip(mode);
        Lines(strip.Lines);
        var (back, color) = strip.Kind switch
        {
            StripKind.Refusal or StripKind.Banner => (Tint, Amber),
            StripKind.Summary => (Plain, ui.Orange),
            // D132: the idle label faint while the panel is open; as the folded panel's one line, in full (X2).
            StripKind.Label => (Quiet, rule.Folded.Value ? LabelColor : new Color(LabelColor.r, LabelColor.g, LabelColor.b, 0.7f)),
            _ => (Quiet, ui.Faint)
        };
        if (band.color != back) band.color = back;
        if (line.color != color) line.color = color;
        // Shown without markup: the label is plain text, and a message's tinted values would show their tags here.
        var text = strip.Label ?? (strip.Text is { } said ? Texts.Plain(said) : "");
        if (strip.Kind == StripKind.Label) text = rule.Label() ?? text; // Unescaped: this label shows tags as written.
        var width = line.rectTransform.rect.width;
        Set(line, JotunnWidgets.Fit(line, text, width > 0 ? width : RulePanel.Width - 28 - 14, strip.Lines));
    }
}

// D071: what the chest takes, as chips, with ↶ (X26), Copy, Paste (X2) and Clear rule in the section's header. D101: the chips area is two
// rows high with or without a rule; what doesn't fit folds into "+N more", and a chest without a rule says what stashing does with it.
// X22 (D130, D132): the suggestion chips on their own row under them.
internal sealed class RuleSection : PanelSection
{
    private const float Gap = 4;
    private static readonly Color Back = new Color(0, 0, 0, 0.32f), AlwaysBack = new Color(0.12f, 0.24f, 0.12f, 0.6f), NeverBack = new Color(0.3f, 0.1f, 0.08f, 0.6f),
        AlwaysMark = new Color(0.5f, 0.85f, 0.5f), NeverMark = new Color(0.94f, 0.42f, 0.36f), CautionBack = new Color(0.23f, 0.18f, 0.07f, 0.85f);
    private JotunnWidgets ui = null!;
    private Button clear = null!;
    private CanvasGroup clearShown = null!, copyShown = null!, pasteShown = null!, undoShown = null!;
    private GameObject undoFlag = null!, pasteFlag = null!, clearFlag = null!, suggestFlag = null!;
    private RectTransform flow = null!, area = null!, undoArrow = null!, suggestions = null!;
    private TipFill? undoTip;
    private bool? redoShown;
    private string key = "\0", suggestedKey = "\0";
    private static readonly Color SuggestBack = new Color(0.24f, 0.18f, 0.07f, 0.9f);
    internal override string Name => "Rule";
    internal override PanelPart Part => PanelPart.Rule;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var head = ui.Row(Root);
        // X2 (D116, variant C): the chest's hover label is in the tooltip of the heading and the chips.
        Tip(ui.Label(head, Texts.Get("yavsos_rule_heading"), JotunnWidgets.TextSize, ui.Orange, false), rule.LabelTooltip);
        Spacer(head); // P10-25: the Rule spot is gone (messages are in the strip); the buttons stay at the right.
        // X26 (D130, variant A): ↶ beside Copy, drawn (the game's font may lack the glyph); ↷ after an undo.
        var undo = ui.Button(head, "", rule.Undo, 30);
        var go0 = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
        undoArrow = (RectTransform)go0.transform;
        undoArrow.SetParent(undo.transform, false);
        undoArrow.anchorMin = undoArrow.anchorMax = undoArrow.pivot = new Vector2(0.5f, 0.5f); undoArrow.sizeDelta = new Vector2(16, 16);
        var arrowImage = go0.GetComponent<Image>(); arrowImage.sprite = PanelArt.Undo; arrowImage.color = ui.Beige; arrowImage.raycastTarget = false;
        Tip(undo, () => (Tooltip?)rule.UndoTooltip());
        undoTip = undo.GetComponent<TipFill>();
        undoShown = undo.gameObject.AddComponent<CanvasGroup>();
        undoFlag = Flag(undo);
        // X2 (variant A): Copy and Paste beside Clear rule; disabled, they keep their place and tooltip.
        var copy = ui.Button(head, Texts.Get("yavsos_copy_button"), rule.CopyRule, 64);
        Tip(copy, () => (Tooltip?)RuleCopy.CopyTooltip(rule.CanCopy, rule.HiddenParts));
        copyShown = copy.gameObject.AddComponent<CanvasGroup>();
        var paste = ui.Button(head, Texts.Get("yavsos_copy_paste_button"), rule.PasteRule, 64);
        Tip(paste, () => (Tooltip?)RuleCopy.PasteTooltip(rule.Copied, rule.Mode == RulePanelMode.Edit, rule.Stored));
        pasteShown = paste.gameObject.AddComponent<CanvasGroup>();
        pasteFlag = Flag(paste);
        var clearText = Message.Of("yavsos_rule_clear");
        clear = ui.Button(head, Texts.Get(clearText), rule.ClearRule, 120);
        Tip(clear, Tooltip.Of(clearText, Message.Of("yavsos_rule_clear_tip")));
        // Hidden without a rule, but keeping its place, so the row never changes.
        clearShown = clear.gameObject.AddComponent<CanvasGroup>();
        clearFlag = Flag(clear);
        var go = new GameObject("Chips", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(Root, false);
        area = (RectTransform)go.transform;
        var size = go.GetComponent<LayoutElement>(); size.flexibleWidth = 1;
        size.minHeight = size.preferredHeight = RuleChips.Rows * JotunnWidgets.ChipHeight + (RuleChips.Rows - 1) * Gap;
        // The label's tooltip sits behind the chips, not around them, so a chip's own tooltip still shows over it.
        var back = new GameObject("Label tooltip", typeof(RectTransform), typeof(Image));
        Stretch((RectTransform)back.transform, area);
        var catcher = back.GetComponent<Image>(); catcher.color = Color.clear;
        Tip(catcher, rule.LabelTooltip);
        var chips = new GameObject("Flow", typeof(RectTransform));
        flow = (RectTransform)chips.transform;
        Stretch(flow, area);
        // X22 (D130, D132, D139): two reserved chip rows under the rule's chips, so a click on a suggestion never changes the height.
        var row = new GameObject("Suggestions", typeof(RectTransform), typeof(LayoutElement));
        row.transform.SetParent(Root, false);
        suggestions = (RectTransform)row.transform;
        var rowSize = row.GetComponent<LayoutElement>(); rowSize.flexibleWidth = 1; rowSize.minHeight = rowSize.preferredHeight = 2 * JotunnWidgets.ChipHeight + Gap;
        suggestFlag = Flag(suggestions);
    }

    private static void Stretch(RectTransform rect, Transform parent)
    {
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
    }

    // Shown or kept invisible in its place; a shown but disabled button is dimmed and keeps its tooltip.
    private static void Enable(CanvasGroup group, bool shown, bool on)
    {
        var alpha = !shown ? 0 : on ? 1 : 0.45f;
        if (group.alpha != alpha) group.alpha = alpha;
        if (group.interactable != (shown && on)) group.interactable = shown && on;
        if (group.blocksRaycasts != shown) group.blocksRaycasts = shown;
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var edit = mode == RulePanelMode.Edit;
        var viewing = edit || mode == RulePanelMode.ReadOnly;
        var clearing = edit || mode == RulePanelMode.Unreadable && rule.Owner();
        Show(Root, viewing || clearing);
        if (!viewing && !clearing) return;
        var on = clearing && (!edit || rule.Stored); // No rule: nothing to clear (runtime 2026-10-23, D086 B3.4).
        if (clearShown.interactable != on) { clearShown.alpha = on ? 1 : 0; clearShown.interactable = clearShown.blocksRaycasts = on; }
        // X26: shown wherever Copy or Clear rule is; clickable with a record while this game may write the rule.
        var record = rule.UndoRecord;
        Enable(undoShown, viewing || clearing, clearing && record != null);
        var redo = record?.Redo == true;
        if (redoShown != redo)
        {
            redoShown = redo;
            undoArrow.localScale = new Vector3(redo ? -1 : 1, 1, 1); // ↷: the undo arrow mirrored.
            undoTip?.Refresh();
        }
        Enable(copyShown, viewing, rule.CanCopy);
        Enable(pasteShown, viewing, edit && rule.Copied != null);
        Flagged(undoFlag, rule, PanelControl.Undo); Flagged(pasteFlag, rule, PanelControl.Paste); Flagged(clearFlag, rule, PanelControl.Clear);
        Show(area, viewing);
        Show(suggestions, viewing);
        Flagged(suggestFlag, rule, PanelControl.Suggestion);
        var chips = viewing ? rule.Chips(Plugin.Instance!.RuleTaxonomy) : System.Array.Empty<RuleChip>();
        var next = string.Join("\n", chips.Select(x => x.Kind + ":" + ChipText(x)));
        if (next != key) { key = next; Flow(chips); }
        if (viewing) Suggest(rule);
    }

    // X22: "✦ Set rule: {category}", "✓ Always: these items" and ✕, rebuilt only when what is offered changes. D139
    // (Q12, variant B): a row each (RuleSuggestion.Rows); a chip wider than its row is cut with "…", the tooltip has it whole.
    private void Suggest(RuleControls rule)
    {
        var offer = rule.Suggestions();
        var taxonomy = Plugin.Instance!.RuleTaxonomy;
        var name = offer.Category is { } id ? RuleControls.CategoryName(taxonomy, id) : "";
        var next = offer.Any ? offer.Signature + "|" + name + "|" + Texts.Generation + "|" + rule.Stored : "";
        if (next == suggestedKey) return;
        suggestedKey = next;
        JotunnWidgets.Clear(suggestions, keep: suggestFlag); Rebuilt(); // The amber frame is a child of the row (P10-25).
        if (!offer.Any) return;
        var width = RulePanel.Width - 28;
        RectTransform? set = null, always = null; float setWidth = 0, alwaysWidth = 0;
        if (offer.Category is { } category)
        {
            set = SuggestChip(Texts.Get(RuleSuggestion.SetText(name)), PanelArt.Sparkle, ui.Orange, RuleSuggestion.SetTip(name, offer.Stacks, offer.Total), () => rule.SuggestCategory(category), out setWidth);
        }
        if (offer.Missed.Count != 0)
        {
            var items = offer.Missed;
            var names = items.Select(x => Texts.Escape(RuleControls.ItemName(x))).ToList();
            always = SuggestChip(Texts.Get(RuleSuggestion.AlwaysText), PanelArt.Check, AlwaysMark, RuleSuggestion.AlwaysTip(names, rule.Stored), () => rule.AcceptContents(items), out alwaysWidth);
        }
        var signature = offer.Signature;
        var dismiss = SuggestChip("", PanelArt.Cross, ui.Faint, RuleSuggestion.DismissTip, () => rule.DismissSuggestions(signature), out var dismissWidth);
        var (setShown, alwaysShown, alwaysRow) = RuleSuggestion.Rows(setWidth, alwaysWidth, dismissWidth, width, Gap);
        if (set != null) Cut(set, setShown);
        if (always != null) { Cut(always, alwaysShown); always.anchoredPosition = new Vector2(0, -alwaysRow * (JotunnWidgets.ChipHeight + Gap)); }
        dismiss.anchoredPosition = new Vector2(width - dismissWidth, 0); // ✕ at the first row's end.
    }

    private RectTransform SuggestChip(string text, Sprite icon, Color tint, Tooltip tip, System.Action click, out float width)
    {
        var rect = ui.Chip(suggestions, text, SuggestBack, icon, tint, out width);
        var label = rect.GetComponentInChildren<Text>();
        if (label) label.color = ui.Orange;
        var image = rect.GetComponent<Image>(); image.raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; button.onClick.AddListener(() => click());
        Tip(image, tip);
        rect.anchoredPosition = Vector2.zero;
        return rect;
    }

    // A chip narrowed to its share of the row, its text cut with "…" to one line (D132: long category names).
    private static void Cut(RectTransform chip, float width)
    {
        if (Mathf.Approximately(chip.sizeDelta.x, width) || chip.sizeDelta.x <= width) return;
        chip.sizeDelta = new Vector2(width, JotunnWidgets.ChipHeight);
        var label = chip.GetComponentInChildren<Text>();
        if (!label) return;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.text = JotunnWidgets.Fit(label, label.text, Mathf.Max(1, width - 24 - 8), 1); // The chip's icon and padding (JotunnWidgets.Chip).
    }

    // Chips left to right, wrapping at the panel's inner width; Unity's layout groups have no wrapping row. Only as many
    // chips are made as could fit (each is at least its padding wide), so a rule of hundreds of items makes a few dozen.
    private void Flow(IReadOnlyList<RuleChip> chips)
    {
        JotunnWidgets.Clear(flow); Rebuilt();
        var width = RulePanel.Width - 28; // The panel's padding, 14 each side.
        var made = new List<(RectTransform Rect, float Width)>();
        var room = RuleChips.Rows * width;
        float used = 0;
        foreach (var chip in chips)
        {
            if (used > room) break;
            var rect = Chip(chip, out var size);
            made.Add((rect, size)); used += Mathf.Min(size, width) + Gap;
        }
        var shown = RuleChips.Fit(made.Select(x => x.Width).ToList(), width, Gap,
            n => { var probe = Chip(RuleChips.Folded(n), out var w); Object.Destroy(probe.gameObject); probe.gameObject.SetActive(false); return w; },
            out var places, out var more);
        for (var i = 0; i < made.Count; i++)
        {
            if (i >= shown) { made[i].Rect.gameObject.SetActive(false); Object.Destroy(made[i].Rect.gameObject); continue; }
            Place(made[i].Rect, Mathf.Min(made[i].Width, width), places[i]);
        }
        if (more is { } at)
        {
            var rect = Chip(RuleChips.Folded(chips, shown), out var size);
            Place(rect, Mathf.Min(size, width), at);
        }
    }

    /// <summary>A chip's text in the player's language: a name as it is, else its message; the filters chip cut as before (D101).</summary>
    internal static string ChipText(RuleChip chip)
    {
        if (chip.Said is not { } said) return chip.Name;
        var text = Texts.Get(said);
        return chip.Kind == ChipKind.Filter ? RuleChips.Cut(text) : text;
    }

    private RectTransform Chip(RuleChip chip, out float size)
    {
        var (back, icon, tint) = chip.Kind switch
        {
            ChipKind.Always => (AlwaysBack, PanelArt.Check, AlwaysMark),
            ChipKind.Never => (NeverBack, PanelArt.Cross, NeverMark),
            ChipKind.Caution => (CautionBack, (Sprite?)null, Color.white),
            _ => (Back, (Sprite?)null, Color.white)
        };
        var rect = ui.Chip(flow, ChipText(chip), back, icon, tint, out size);
        var text = rect.GetComponentInChildren<Text>();
        if (chip.Kind is ChipKind.Filter or ChipKind.Note or ChipKind.Caution)
            text.color = chip.Kind switch { ChipKind.Filter => ui.Orange, ChipKind.Caution => Amber, _ => ui.Faint };
        if (chip.Tip is { } tip) Tip(rect.GetComponent<Image>(), tip);
        return rect;
    }

    private static void Place(RectTransform rect, float size, (float X, int Row) at)
    {
        rect.sizeDelta = new Vector2(size, JotunnWidgets.ChipHeight);
        rect.anchoredPosition = new Vector2(at.X, -at.Row * (JotunnWidgets.ChipHeight + Gap));
    }
}

// P4-00c (D051/D054): the category tree, exact items, and D050's ignored unknown categories. D101: the three tabs share
// one body of fixed height, with one status line under it (the search result). P10-25: an edit's refusal shows in the
// strip, and the body keeps an amber outline until the next action.
internal sealed class PickerSection : PanelSection
{
    private const float Search = JotunnWidgets.RowHeight + 4, Spacing = 2, Grid = 46 * 6 + 24;
    /// <summary>The tab body's height: a search box above the tallest content, the Items grid (D101).</summary>
    internal const float BodyHeight = Search + Spacing + Grid;
    private JotunnWidgets ui = null!;
    private Button categories = null!, items = null!, filters = null!;
    private RectTransform unknown = null!, tree = null!, categoryPart = null!, itemPart = null!;
    private Text status = null!, gridEmpty = null!;
    private GameObject bodyFlag = null!;
    private SearchBox search = null!, itemSearch = null!;
    private readonly List<(Image Tag, TipFill? Tip)> takers = new();
    private (bool On, Color Color)? takersShown;
    private ItemGridView grid = null!;
    private string unknownKey = "\0", treeKey = "\0";
    private int gridShown = -1;
    internal RectTransform Body { get; private set; } = null!;
    internal override string Name => "Picker";
    internal override PanelPart Part => PanelPart.Edit;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        ui.Label(Root, Texts.Get("yavsos_tab_heading"), JotunnWidgets.TextSize, ui.Orange);
        var tabs = ui.Row(Root);
        // P10-04: the tab names are keys that other messages quote ("Its icon on the Items tab is amber").
        var categoriesTab = Message.Of("yavsos_tab_categories");
        categories = ui.Button(tabs, Texts.Get(categoriesTab), rule.ShowCategories);
        Tip(categories, Tooltip.Of(categoriesTab, Message.Of("yavsos_tab_categories_tip")));
        var itemsTab = Message.Of("yavsos_tab_items");
        items = ui.Button(tabs, Texts.Get(itemsTab), rule.ShowItems);
        Tip(items, Tooltip.Of(itemsTab, Message.Of("yavsos_tab_items_tip").With("always", ItemGrid.Always).With("never", ItemGrid.Never)));
        var filtersTab = Message.Of("yavsos_tab_filters");
        filters = ui.Button(tabs, Texts.Get(filtersTab), rule.ShowFilters);
        Tip(filters, Tooltip.Of(filtersTab, Message.Of("yavsos_tab_filters_tip")));
        unknown = ui.Column(Root, "Ignored categories", 2);
        Body = ui.Column(Root, "Body", Spacing);
        var size = Body.gameObject.AddComponent<LayoutElement>(); size.minHeight = size.preferredHeight = BodyHeight;
        // P6-06: search over category and item names; the tree shows what matches and lights up nearby chests.
        categoryPart = ui.Column(Body, "Categories", Spacing);
        var searchRow = ui.Row(categoryPart, 4);
        search = ui.Search(searchRow, Texts.Get("yavsos_search_categories_hint"), rule.SearchFor);
        Tip(search.Field, Tooltip.Of(Message.Of("yavsos_search_heading"), Message.Of("yavsos_search_categories_tip")));
        Takers(ui, rule, searchRow);
        tree = ui.ScrollList(categoryPart, Grid);
        // P6-18: every item the rule accepts as icons; a click cycles Always → Never → clear.
        itemPart = ui.Column(Body, "Items", Spacing);
        var itemRow = ui.Row(itemPart, 4);
        itemSearch = ui.Search(itemRow, Texts.Get("yavsos_search_items_hint"), rule.SearchItems);
        Tip(itemSearch.Field, Tooltip.Of(Message.Of("yavsos_search_heading"), Message.Of("yavsos_search_items_tip")));
        Takers(ui, rule, itemRow);
        grid = new ItemGridView(ui, itemPart, Grid, rule.CycleMark, rule.GridIcon, rule.GridTooltip, rule.KeptOut);
        // Drawn inside the grid, so the empty grid keeps its size.
        gridEmpty = ui.Label(grid.Frame, Texts.Get("yavsos_grid_empty"), JotunnWidgets.SmallSize, ui.Faint);
        gridEmpty.GetComponent<LayoutElement>().ignoreLayout = true; gridEmpty.alignment = TextAnchor.MiddleCenter;
        var empty = gridEmpty.rectTransform; empty.anchorMin = Vector2.zero; empty.anchorMax = Vector2.one; empty.offsetMin = new Vector2(16, 0); empty.offsetMax = new Vector2(-16, 0);
        // The status line's tooltip: the chests the search lit with their distances (C7).
        status = Spot(ui, Root, rule.HighlightTooltip);
        status.color = ui.Faint;
        bodyFlag = Flag(Body);
    }

    // D122: the "Also show chests that accept it" button at the end of a search box; on, the search also lights the
    // chests whose rules accept a match.
    private void Takers(JotunnWidgets ui, RuleControls rule, Transform row)
    {
        var button = ui.Button(row, "", rule.ToggleTakers, JotunnWidgets.RowHeight + 4);
        var go = new GameObject("Rule tag", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(button.transform, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(16, 16);
        var image = go.GetComponent<Image>(); image.sprite = PanelArt.Tag; image.color = ui.Faint; image.raycastTarget = false;
        Tip(button, () => (Tooltip?)HighlightText.TakesToggle(rule.ShowsTakers));
        takers.Add((image, button.GetComponent<TipFill>()));
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        Show(Root, mode == RulePanelMode.Edit);
        if (mode != RulePanelMode.Edit) return;
        Tab(categories, rule.PickingCategories); Tab(items, rule.PickingItems); Tab(filters, rule.PickingFilters);
        var key = string.Join("\n", rule.Unknown) + "|" + string.Join("\n", rule.UnknownFilters) + "|" +
            string.Join("\n", rule.UnknownFilterValues.Select(x => x.Dimension + "=" + x.Value)) + "|" + rule.HiddenParts;
        if (key != unknownKey) { unknownKey = key; Unknown(rule); }
        Show(categoryPart, rule.PickingCategories);
        Show(itemPart, rule.PickingItems);
        var takersNow = (rule.ShowsTakers, ChestHighlight.ColorOf(ChestHighlight.Kind.Takes));
        if (takersShown != takersNow)
        {
            takersShown = takersNow;
            foreach (var (tag, tip) in takers) { tag.color = rule.ShowsTakers ? ChestHighlight.ColorOf(ChestHighlight.Kind.Takes) : ui.Faint; tip?.Refresh(); }
        }
        var plugin = Plugin.Instance!;
        Message? line = null;
        if (rule.PickingCategories)
        {
            if (!search.Field.isFocused && search.Field.text != rule.Typed) search.Field.SetTextWithoutNotify(rule.Typed);
            if (search.Refresh()) Rebuilt();
            var taxonomy = plugin.RuleTaxonomy;
            key = rule.Revision + "|" + rule.SearchRevision + "|" + plugin.ClassificationEvidenceReady + "|" + RuntimeHelpers.GetHashCode(taxonomy);
            if (key != treeKey) { treeKey = key; Tree(rule, taxonomy); }
            line = rule.Search.Active && rule.Search.Hits.Count == 0 ? Message.Of("yavsos_search_none") : rule.HighlightStatus();
        }
        else treeKey = "\0";
        if (rule.PickingItems)
        {
            if (!itemSearch.Field.isFocused && itemSearch.Field.text != rule.GridQuery) itemSearch.Field.SetTextWithoutNotify(rule.GridQuery);
            if (itemSearch.Refresh()) Rebuilt();
            var entries = rule.GridEntries();
            rule.GridPointer(grid.Hovered);
            line = rule.GridQuery.Trim().Length != 0 ? rule.HighlightStatus() : null;
            if (gridShown != rule.GridRevision) { gridShown = rule.GridRevision; grid.Set(entries, RuleControls.GridTitle); }
            else grid.Refresh();
            Show(gridEmpty, entries.Count == 0);
        }
        else { gridShown = -1; rule.GridPointer(null); }
        // The open tab's search result; P10-25 (D132): an edit's refusal is in the strip, the tab body outlined.
        Set(status, line == null ? "" : Texts.Get(line));
        Flagged(bodyFlag, rule, PanelControl.Edit);
    }

    private void Tab(Button tab, bool active)
    {
        var text = tab.GetComponentInChildren<Text>(true);
        var color = active ? Color.white : ui.Orange;
        if (text && text.color != color) text.color = color;
    }

    private void Unknown(RuleControls rule)
    {
        JotunnWidgets.Clear(unknown); Rebuilt();
        // The stored IDs are shown as they are (inventory case 6), escaped for rich text.
        var remove = Texts.Get("yavsos_tree_remove");
        foreach (var ignored in rule.Unknown)
        {
            var row = ui.Row(unknown);
            ui.Label(row, Texts.Get(Message.Of("yavsos_tree_ignored_category").With("id", Texts.Escape(ignored))), JotunnWidgets.SmallSize);
            var id = ignored;
            ui.Button(row, remove, () => rule.RemoveUnknown(id), 80);
        }
        // D077 B01: a filter this version doesn't know is kept and accepts nothing.
        foreach (var dimension in rule.UnknownFilters)
        {
            var row = ui.Row(unknown);
            ui.Label(row, Texts.Get(Message.Of("yavsos_tree_ignored_filter").With("filter", Texts.Escape(dimension))), JotunnWidgets.SmallSize);
            var id = dimension;
            ui.Button(row, remove, () => rule.RemoveFilter(id), 80);
        }
        // D086 D6: a value of a known filter this version doesn't know is kept the same way.
        foreach (var (dimension, value) in rule.UnknownFilterValues)
        {
            var row = ui.Row(unknown);
            ui.Label(row, Texts.Get(Message.Of("yavsos_tree_ignored_value").With("filter", Texts.Escape(dimension)).With("value", Texts.Escape(value))), JotunnWidgets.SmallSize);
            var d = dimension; var v = value;
            ui.Button(row, remove, () => rule.RemoveFilterValue(d, v), 80);
        }
        // D086 D5: no hidden rule parts. What this panel can't show is named here and kept; Clear rule removes it.
        if (rule.HiddenParts != 0)
            ui.Label(ui.Row(unknown), Texts.Get(Message.Of("yavsos_tree_hidden").With("button", Message.Of("yavsos_rule_clear")).Count(rule.HiddenParts)), JotunnWidgets.SmallSize);
        Show(unknown, rule.Unknown.Count + rule.UnknownFilters.Count + rule.UnknownFilterValues.Count + rule.HiddenParts != 0);
    }

    // A ticked umbrella is stored as itself, so it also takes subcategories added later. D101 (C9): while searching, a row
    // names the items found in it, faintly, on one line; pointing at a row lights the chests that hold it (D122).
    private void Tree(RuleControls rule, ClassificationTaxonomy taxonomy)
    {
        JotunnWidgets.Clear(tree); Rebuilt();
        var found = rule.Search;
        foreach (var row in rule.CategoryRows(taxonomy))
        {
            var line = ui.Row(tree, 2);
            if (row.Depth > 0) ui.Space(line, row.Depth * 18);
            var id = row.Id; var open = row.Expanded;
            if (row.HasChildren) ui.Button(line, open ? "−" : "+", () => rule.Fold(id, open), 24);
            else ui.Space(line, 24);
            var (state, inherited) = CategoryCheckbox.Of(row.State);
            var hit = found.Hits.Contains(id);
            var check = ui.Check(line, row.Name, () => rule.ToggleCategory(taxonomy, id));
            check.Set(state, inherited, hit);
            if (!found.Active) continue;
            var names = found.Names(id);
            if (names.Count != 0 && check.Label is { } label)
            {
                // The room left for the note: the tree's inner width less the indent, fold button and checkbox.
                var room = RulePanel.Width - 28 - 8 - 14 - row.Depth * 18 - 24 - 2 - 26;
                var escaped = names.Select(Texts.Escape).ToList();
                Message? note = null;
                for (var shown = CategorySearch.NoteNames; shown >= 0; shown--)
                {
                    note = CategorySearch.Note(Texts.Escape(row.Name), escaped, shown);
                    if (note == null || JotunnWidgets.LineWidth(label, Texts.Plain(note)) <= room) break;
                }
                label.supportRichText = true;
                if (note != null) label.text = Texts.Get(note);
            }
            // The row's tooltip: what the search found in it, or why it shows, then what ticking does.
            var tip = Message.Of("yavsos_tree_tip");
            Tip(check.Root.GetComponent<Toggle>(), Tooltip.Named(row.Name, names.Count != 0
                ? Message.Of("yavsos_tree_tip_has").With("names", MessageList.Of(ListKind.And, names)).With("tip", tip)
                : hit ? tip : Message.Of("yavsos_tree_tip_shown").With("tip", tip)));
            var report = check.Root.AddComponent<PointerReport>();
            report.Changed = on => rule.RowPointer(id, on);
        }
    }
}

// P6-07b (D062 Q6): built-in filters for the edited alternative. Values within one filter are alternatives; an item must
// pass every filter that has a ticked value. Instance filters (rarity, upgraded, damaged) are read from each stack as it
// moves. D101: inside the tab body, as tall as the other tabs; how filters combine is in the tab's and each name's tooltip.
internal sealed class FilterSection : PanelSection
{
    private const float Cell = 112;
    private JotunnWidgets ui = null!;
    private RectTransform list = null!;
    private string key = "\0";
    internal override string Name => "Filters";
    internal override PanelPart Part => PanelPart.Filters;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        list = ui.ScrollList(Root, PickerSection.BodyHeight);
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var shown = mode == RulePanelMode.Edit && rule.PickingFilters;
        Show(Root, shown);
        if (!shown) { key = "\0"; return; }
        // P6-14 (D065): only filters and values the ticked categories' items can have; a saved one that no longer
        // applies stays, marked, until the player unticks it.
        var choices = rule.FilterOptions();
        var next = rule.Revision + "|" + RuntimeHelpers.GetHashCode(choices);
        if (next == key) return;
        key = next;
        JotunnWidgets.Clear(list); Rebuilt();
        if (choices.Count == 0) ui.Label(list, Texts.Get("yavsos_filtertab_none"), JotunnWidgets.SmallSize, ui.Faint);
        foreach (var choice in choices)
        {
            var dimension = choice.Dimension;
            var filterName = RuleControls.FilterNameText(dimension);
            var name = ui.Label(list, choice.Applies ? filterName : Texts.Get(Message.Of("yavsos_filtertab_inapplicable").With("filter", filterName)), JotunnWidgets.SmallSize,
                choice.Applies ? ui.Orange : ui.Faint);
            Tip(name, Tooltip.Named(filterName, Message.Of(ClassificationFilters.InstanceDimensions.Contains(dimension) ? "yavsos_filtertab_filter_tip_stack" : "yavsos_filtertab_filter_tip")));
            rule.Filters.TryGetValue(dimension, out var ticked);
            if (dimension == "quality" && choice.Applies) { Levels(rule, choice, ticked); continue; }
            RectTransform? row = null;
            for (var i = 0; i < choice.Values.Count; i++)
            {
                if (i % 3 == 0) row = ui.Row(list, 2);
                var value = choice.Values[i]; var dim = dimension;
                var text = RuleControls.ValueText(dimension, value);
                if (choice.Applies && choice.Stale.Contains(value)) text = Texts.Get(Message.Of("yavsos_filtertab_value_unused").With("value", text));
                ui.Check(row!, text, () => rule.ToggleFilter(dim, value), Cell).Set(ticked?.Contains(value) == true ? CheckboxState.Ticked : CheckboxState.Empty);
            }
        }
    }

    private static readonly Color LevelOn = new Color(0.45f, 0.32f, 0.12f, 0.95f), LevelOff = new Color(0, 0, 0, 0.35f);
    private const float StarSize = 18;

    // P6-21 (maintainer, 2026-10-23): upgrade levels as the game's stars, one cell per level (the last is 5 and up);
    // click a cell to tick or untick it, like the other filters' values.
    private void Levels(RuleControls rule, FilterChoice choice, SortedSet<string>? ticked)
    {
        var row = ui.Row(list, 4);
        foreach (var value in choice.Values)
        {
            if (value == "unknown") continue;
            var on = ticked?.Contains(value) == true; var level = value;
            var go = new GameObject("Level " + value, typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            var back = go.GetComponent<Image>(); back.color = on ? LevelOn : LevelOff;
            var button = go.GetComponent<Button>(); button.targetGraphic = back; button.onClick.AddListener(() => rule.ToggleFilter("quality", level));
            var layout = go.GetComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter; layout.spacing = 1; layout.padding = new RectOffset(2, 2, 5, 5);
            layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var stars = value == "5+" ? 5 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            // Width in proportion to the stars, so each star can be drawn large enough to stay sharp (12 px blurred, 2026-10-23).
            var size = go.GetComponent<LayoutElement>(); size.flexibleWidth = stars + (value == "5+" ? 1 : 0);
            size.minWidth = stars * StarSize + 6; size.minHeight = size.preferredHeight = StarSize + 10;
            for (var i = 0; i < stars; i++)
            {
                var star = PanelArt.StarImage(go.transform, StarSize);
                if (!on) foreach (var graphic in star.GetComponentsInChildren<Graphic>()) graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, graphic.color.a * 0.45f);
            }
            if (value == "5+") ui.Label(go.transform, "+", JotunnWidgets.SmallSize, on ? Color.white : ui.Faint, false);
        }
        if (choice.Stale.Count != 0)
            ui.Label(list, Texts.Get(Message.Of("yavsos_filtertab_levels_unused").With("levels", MessageList.Of(ListKind.Comma, choice.Stale)).Count(choice.Stale.Count)),
                JotunnWidgets.SmallSize, ui.Faint);
    }
}

// D071 "This chest": settings about the chest rather than its rule. Priority as a 1–10 bar (D069; left fills first),
// coloured by name. D101: the bar is always there, faint without a rule; the heading names the chosen level; what each
// number means is in its tooltip. The clean-up opt-out (D057, D061) shares the Clean up nearby row.
internal sealed class ChestSection : PanelSection
{
    private static readonly Color[] Bands =
    {
        new Color32(0x7a, 0x2b, 0x22, 0xff), new Color32(0x7a, 0x4a, 0x22, 0xff), new Color32(0x5f, 0x5a, 0x2a, 0xff),
        new Color32(0x3f, 0x5a, 0x33, 0xff), new Color32(0x33, 0x4a, 0x5a, 0xff), new Color32(0x3c, 0x3c, 0x48, 0xff)
    };
    private JotunnWidgets ui = null!;
    private Text heading = null!;
    private GameObject barFlag = null!;
    private CanvasGroup bar = null!;
    private readonly List<(Image Back, Text Text, Outline Edge)> levels = new();
    private int shownLevel = -1;
    private bool shownRuled;
    internal override string Name => "This chest";
    internal override PanelPart Part => PanelPart.Chest;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var head = ui.Row(Root);
        heading = ui.Label(head, Texts.Get(ChestPriority.Heading(0, false)), JotunnWidgets.TextSize, ui.Orange, false);
        var row = ui.Row(Root, 2);
        bar = row.gameObject.AddComponent<CanvasGroup>();
        barFlag = Flag(row);
        for (var level = ChestPriority.Highest; level <= ChestPriority.Lowest; level++)
        {
            var chosen = level;
            var (button, back, text) = ui.Swatch(row, level.ToString(), Bands[ChestPriority.Band(level)], () => rule.SetLevel(chosen));
            var edge = back.gameObject.AddComponent<Outline>(); edge.effectColor = ui.Orange; edge.effectDistance = new Vector2(2, -2); edge.enabled = false;
            levels.Add((back, text, edge));
            Tip(button, () => (Tooltip?)ChestPriority.Tip(chosen, ChestPriority.Level(rule.Priority), rule.Stored));
        }
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var edit = mode == RulePanelMode.Edit;
        Show(Root, edit);
        if (!edit) return;
        var ruled = rule.Stored && rule.Categories.Count + rule.Includes.Count + rule.Unknown.Count + rule.Filters.Count + rule.HiddenParts != 0;
        Set(heading, Texts.Get(ChestPriority.Heading(rule.Priority, ruled)));
        Flagged(barFlag, rule, PanelControl.Priority);
        var level = ChestPriority.Level(rule.Priority);
        if (level != shownLevel || ruled != shownRuled)
        {
            shownLevel = level; shownRuled = ruled;
            bar.alpha = ruled ? 1 : 0.4f; bar.interactable = ruled; // Tooltips still show: they say a rule comes first.
            for (var i = 0; i < levels.Count; i++)
            {
                var on = ruled && i + ChestPriority.Highest == level;
                levels[i].Edge.enabled = on;
                levels[i].Text.color = on ? Color.white : new Color(0.8f, 0.76f, 0.68f);
                levels[i].Text.fontStyle = on ? FontStyle.Bold : FontStyle.Normal;
            }
        }
    }
}

// D071: "Clean up nearby" at the bottom, on one row with the chest's clean-up opt-out (maintainer, H4 client check). D101,
// D132: a refusal, or the summary of a run with nothing to move, in the strip until the next action, the refused control
// outlined; a started run says nothing (the button turns to "Cleaning up…" and the result panel follows).
internal sealed class CleanupSection : PanelSection
{
    private JotunnWidgets ui = null!;
    private Button cleanup = null!;
    private GameObject cleanupFlag = null!, optOutFlag = null!;
    private TriStateToggle optOut = null!;
    internal override string Name => "Cleanup";
    internal override PanelPart Part => PanelPart.Cleanup;

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var row = ui.Row(Root);
        var optOutText = Message.Of("yavsos_cleanup_optout");
        optOut = ui.Check(row, Texts.Get(optOutText), rule.ToggleCleanupOptOut);
        Tip(optOut.Root.GetComponent<Toggle>(), Tooltip.Of(optOutText, Message.Of("yavsos_cleanup_optout_tip")));
        var button = Message.Of("yavsos_cleanup_button");
        cleanup = ui.Button(row, Texts.Get(button), rule.CleanUpNearby, 170);
        Tip(cleanup, () =>
        {
            var settings = Plugin.Instance?.Settings;
            var key = settings == null ? null : CleanupTriggers.KeyName(settings.CleanupShortcut);
            var meters = (long)System.Math.Round(settings?.EffectiveCleanupRange ?? 0, System.MidpointRounding.AwayFromZero);
            return (Tooltip?)Tooltip.Of(key == null ? button : Message.Of("yavsos_cleanup_button_key").With("button", button).With("key", key),
                Message.Of("yavsos_cleanup_button_tip").With("meters", meters));
        });
        cleanupFlag = Flag(cleanup);
        optOutFlag = JotunnWidgets.Frame((RectTransform)optOut.Root.transform, Amber);
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var edit = mode == RulePanelMode.Edit;
        Show(Root, edit);
        if (!edit) return;
        Set(cleanup, Texts.Get(rule.Cleanup.Running ? "yavsos_cleanup_running" : "yavsos_cleanup_button"));
        optOut.Set(ChestRuleStore.CleanupOptOut(rule.Chest!) ? CheckboxState.Ticked : CheckboxState.Empty);
        Flagged(cleanupFlag, rule, PanelControl.Cleanup); Flagged(optOutFlag, rule, PanelControl.OptOut);
    }
}
