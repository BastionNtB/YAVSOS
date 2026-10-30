using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Core.Classification;
using YAVSOS.Cleanup;
using YAVSOS.Core;
using YAVSOS.Core.Interface;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-03 (D062): the chest rule panel, an opaque Jotunn wood panel inside the inventory screen, kept beside the open
/// chest window's real rectangle (ZenUI may scale or rotate the chest grid). One column of sections; each owns its
/// widgets and refreshes from <see cref="RuleControls"/>. Rows are rebuilt only when what they show changes.
/// D101: while the panel is in use its height doesn't change: every area that shows changing text has a fixed size
/// (chips two rows, one tab body height, message spots of two lines), and teaching text lives in tooltips on the controls.
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
    private Canvas? canvas; // The parent's canvas, found once: Place runs every frame.
    private readonly JotunnWidgets.ScaleWatch scale = new();

    internal RulePanel(JotunnWidgets ui, RectTransform parent, RuleControls rule)
    {
        this.rule = rule; this.parent = parent;
        root = ui.Panel(parent, Width);
        root.gameObject.SetActive(false);
        // Display order, layout A (D071): header with status marks, problem banners, Rule, Edit (tabs, then the open
        // tab's picker or filters, in one body of fixed height, D101), This chest, Clean up nearby. Search (P6-06) lives in
        // the Categories tab; run results have their own panel (P6-04); "Where would it go?" is in the item tooltip (P6-23).
        var header = new HeaderSection(); var picker = new PickerSection();
        sections = new PanelSection[] { header, new BannerSection(), new RuleSection(), picker, new FilterSection(), new ChestSection(), new CleanupSection() };
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
        placed = PanelPlacement.Beside(anchor, size.x, size.y, area, rule.OffsetX.Value + drag.x, rule.OffsetY.Value + drag.y);
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
    private bool changed;
    internal abstract void Build(JotunnWidgets ui, RuleControls rule);
    protected abstract void Refresh(RuleControls rule, RulePanelMode mode, bool structural);

    /// <summary>Returns whether anything shown changed, so the panel lays itself out again only then.</summary>
    internal bool Update(RuleControls rule, RulePanelMode mode, bool structural)
    {
        changed = false;
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
    /// D101: a message spot, two lines high whether or not it says anything, so a refusal never moves the controls. A
    /// longer text is cut with "…"; its tooltip has the whole text.
    /// </summary>
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

    /// <summary>A spot's tooltip: the whole message at <paramref name="at"/>, when there is one.</summary>
    protected static System.Func<(string Topic, string Text)?> Whole(RuleControls rule, MessageSpot at) =>
        () => rule.Messages.At(at) is { Length: > 0 } said ? ("", said) : null;

    /// <summary>Shows the message for <paramref name="at"/> in its spot, amber for a refusal.</summary>
    protected void Say(Text spot, RuleControls rule, MessageSpot at, Color refusal, Color summary)
    {
        var said = rule.Messages.At(at);
        var color = rule.Messages.Kind == MessageKind.Refusal ? refusal : summary;
        if (spot.color != color) spot.color = color;
        var width = spot.rectTransform.rect.width;
        Set(spot, said.Length == 0 ? "" : JotunnWidgets.Fit(spot, said, width > 0 ? width : RulePanel.Width - 28, 2));
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
    internal Text Title { get; private set; } = null!;
    internal override string Name => "Header";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var line = ui.Row(Root, 6);
        Title = ui.Label(line, "YAVSOS", JotunnWidgets.TextSize + 4, ui.Orange);
        Tip(Title, "YAVSOS " + ProjectIdentity.Version, "Drag to move the panel.");
        row = ui.Row(line, 4);
        row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var marks = rule.Marks();
        var next = PanelMarks.Key(marks);
        if (next == key) return;
        key = next;
        JotunnWidgets.Clear(row); Rebuilt();
        foreach (var mark in marks) Mark(mark);
    }

    private void Mark(IntegrationMark mark)
    {
        var go = new GameObject("Mark " + mark.Name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
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
        var text = string.Join("\n", mark.Lines.Select(x => "<color=" + (x.State switch { MarkState.Working => "#7FD47F", MarkState.Broken => "#EF6B5B", MarkState.Caution or MarkState.Limited => "#F0C050", _ => "#A0957F" }) + ">" +
            PanelMarks.Word(x.State).Trim() + "</color> " + Plain(x.Text)));
        GameTooltip.Attach(go, () => (mark.Name, text));
    }

    private static string Plain(string text) => text.Replace("<", "‹").Replace(">", "›");
}

// D071: problem states at the top, both set when the chest opens: another player has the chest open, or an unreadable
// rule. Categories still loading is an amber Categories mark (D101); AzuAutoStore's config keeping some of the rule's
// items out of this chest type (D056) is a caution on its mark.
internal sealed class BannerSection : PanelSection
{
    private static readonly Color Tint = new Color(0.23f, 0.18f, 0.07f, 0.85f);
    private Text readOnly = null!, unreadable = null!;
    internal override string Name => "Banners";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        readOnly = ui.Banner(Root, "Another player is using this chest. You can view its rule; edit it after they close it.", Tint, Amber);
        unreadable = ui.Banner(Root, "", Tint, Amber);
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        Banner(readOnly, mode == RulePanelMode.ReadOnly);
        Banner(unreadable, mode == RulePanelMode.Unreadable);
        if (mode == RulePanelMode.Unreadable)
            Set(unreadable, "This chest has a rule from a different YAVSOS version or category set. It accepts nothing from " + RuleControls.Stasher + " until replaced.");
        Show(Root, mode is RulePanelMode.ReadOnly or RulePanelMode.Unreadable);
    }

    private void Banner(Text text, bool on) => Show(text.transform.parent.gameObject, on);
}

// D071: what the chest takes, as chips, with Clear rule in the section's header. D101: the chips area is two rows high
// with or without a rule; what doesn't fit folds into "+N more", and a chest without a rule says what stashing does with it.
internal sealed class RuleSection : PanelSection
{
    private const float Gap = 4;
    private static readonly Color Back = new Color(0, 0, 0, 0.32f), AlwaysBack = new Color(0.12f, 0.24f, 0.12f, 0.6f), NeverBack = new Color(0.3f, 0.1f, 0.08f, 0.6f),
        AlwaysMark = new Color(0.5f, 0.85f, 0.5f), NeverMark = new Color(0.94f, 0.42f, 0.36f), CautionBack = new Color(0.23f, 0.18f, 0.07f, 0.85f);
    private JotunnWidgets ui = null!;
    private Button clear = null!;
    private CanvasGroup clearShown = null!;
    private Text spot = null!;
    private RectTransform flow = null!;
    private string key = "\0";
    internal override string Name => "Rule";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var head = ui.Row(Root);
        ui.Label(head, "Rule", JotunnWidgets.TextSize, ui.Orange, false);
        spot = Spot(ui, head, Whole(rule, MessageSpot.Clear));
        spot.alignment = TextAnchor.MiddleRight;
        clear = ui.Button(head, "Clear rule", rule.ClearRule, 120);
        Tip(clear, "Clear rule", "Removes the whole rule, priority included. The chest goes back to working without a rule.");
        // Hidden without a rule, but keeping its place, so the row never changes.
        clearShown = clear.gameObject.AddComponent<CanvasGroup>();
        var go = new GameObject("Chips", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(Root, false);
        flow = (RectTransform)go.transform;
        var size = go.GetComponent<LayoutElement>(); size.flexibleWidth = 1;
        size.minHeight = size.preferredHeight = RuleChips.Rows * JotunnWidgets.ChipHeight + (RuleChips.Rows - 1) * Gap;
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
        Say(spot, rule, MessageSpot.Clear, Amber, ui.Orange);
        Show(flow, viewing);
        var chips = viewing ? rule.Chips(Plugin.Instance!.RuleTaxonomy) : System.Array.Empty<RuleChip>();
        var next = string.Join("\n", chips.Select(x => x.Kind + ":" + x.Text));
        if (next != key) { key = next; Flow(chips); }
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
            n => { var probe = Chip(new RuleChip("+" + n + " more", ChipKind.Note), out var w); Object.Destroy(probe.gameObject); probe.gameObject.SetActive(false); return w; },
            out var places, out var more);
        for (var i = 0; i < made.Count; i++)
        {
            if (i >= shown) { made[i].Rect.gameObject.SetActive(false); Object.Destroy(made[i].Rect.gameObject); continue; }
            Place(made[i].Rect, Mathf.Min(made[i].Width, width), places[i]);
        }
        if (more is { } at)
        {
            var rest = new RuleChip("+" + (chips.Count - shown) + " more", ChipKind.Note, RuleChips.More(chips, shown));
            var rect = Chip(rest, out var size);
            Place(rect, Mathf.Min(size, width), at);
        }
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
        var rect = ui.Chip(flow, chip.Text, back, icon, tint, out size);
        var text = rect.GetComponentInChildren<Text>();
        if (chip.Kind is ChipKind.Filter or ChipKind.Note or ChipKind.Caution)
            text.color = chip.Kind switch { ChipKind.Filter => ui.Orange, ChipKind.Caution => Amber, _ => ui.Faint };
        if (chip.Detail.Length != 0)
        {
            var lines = chip.Detail.Split(new[] { '\n' }, 2);
            Tip(rect.GetComponent<Image>(), lines[0], lines.Length > 1 ? lines[1] : "");
        }
        return rect;
    }

    private static void Place(RectTransform rect, float size, (float X, int Row) at)
    {
        rect.sizeDelta = new Vector2(size, JotunnWidgets.ChipHeight);
        rect.anchoredPosition = new Vector2(at.X, -at.Row * (JotunnWidgets.ChipHeight + Gap));
    }
}

// P4-00c (D051/D054): the category tree, exact items, and D050's ignored unknown categories. D101: the three tabs share
// one body of fixed height, with one status line under it (search result or an edit's refusal).
internal sealed class PickerSection : PanelSection
{
    private const float Search = JotunnWidgets.RowHeight + 4, Spacing = 2, Grid = 46 * 6 + 24;
    /// <summary>The tab body's height: a search box above the tallest content, the Items grid (D101).</summary>
    internal const float BodyHeight = Search + Spacing + Grid;
    private JotunnWidgets ui = null!;
    private Button categories = null!, items = null!, filters = null!;
    private RectTransform unknown = null!, tree = null!, categoryPart = null!, itemPart = null!;
    private Text status = null!, gridEmpty = null!;
    private SearchBox search = null!, itemSearch = null!;
    private ItemGridView grid = null!;
    private string unknownKey = "\0", treeKey = "\0";
    private int gridShown = -1;
    internal RectTransform Body { get; private set; } = null!;
    internal override string Name => "Picker";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        ui.Label(Root, "Edit", JotunnWidgets.TextSize, ui.Orange);
        var tabs = ui.Row(Root);
        categories = ui.Button(tabs, "Categories", rule.ShowCategories);
        Tip(categories, "Categories", "Tick the kinds of item this chest takes. Search finds categories and items, and lights up nearby chests that take them.");
        items = ui.Button(tabs, "Items", rule.ShowItems);
        Tip(items, "Items", "Every item the rule takes. Click an icon to make it Always here, then Never here, then clear. Dimmed icons are not taken; " +
            "an amber one is taken by the rule, but AzuAutoStore's config keeps it out of this chest type. Search adds any item.");
        filters = ui.Button(tabs, "Filters", rule.ShowFilters);
        Tip(filters, "Filters", "Narrow the ticked categories. An item must match every filter that has a ticked value; values within one filter are " +
            "alternatives. Rarity, upgrade level, damaged and crafter are checked on each stack.");
        unknown = ui.Column(Root, "Ignored categories", 2);
        Body = ui.Column(Root, "Body", Spacing);
        var size = Body.gameObject.AddComponent<LayoutElement>(); size.minHeight = size.preferredHeight = BodyHeight;
        // P6-06: search over category and item names; the tree shows what matches and lights up nearby chests.
        categoryPart = ui.Column(Body, "Categories", Spacing);
        search = ui.Search(categoryPart, "Search categories and items…", rule.SearchFor);
        Tip(search.Field, "Search", "Search category and item names. The tree shows only what matches; point at a row to light up the nearby chests that take it.");
        tree = ui.ScrollList(categoryPart, Grid);
        // P6-18: every item the rule takes as icons; a click cycles Always → Never → clear.
        itemPart = ui.Column(Body, "Items", Spacing);
        itemSearch = ui.Search(itemPart, "Search all items…", rule.SearchItems);
        Tip(itemSearch.Field, "Search", "Search every item. Matching items show in the grid; the nearby chests that take them light up.");
        grid = new ItemGridView(ui, itemPart, Grid, rule.CycleMark, rule.GridIcon, rule.GridTooltip, rule.KeptOut);
        // Drawn inside the grid, so the empty grid keeps its size.
        gridEmpty = ui.Label(grid.Frame, "Nothing here yet: tick categories, or search to add items.", JotunnWidgets.SmallSize, ui.Faint);
        gridEmpty.GetComponent<LayoutElement>().ignoreLayout = true; gridEmpty.alignment = TextAnchor.MiddleCenter;
        var empty = gridEmpty.rectTransform; empty.anchorMin = Vector2.zero; empty.anchorMax = Vector2.one; empty.offsetMin = new Vector2(16, 0); empty.offsetMax = new Vector2(-16, 0);
        // The status line's tooltip: the whole refusal, or the chests the search lit with their distances (C7).
        status = Spot(ui, Root, () => Whole(rule, MessageSpot.Tab)() ?? rule.HighlightTooltip());
        status.color = ui.Faint;
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
        var plugin = Plugin.Instance!;
        var line = "";
        if (rule.PickingCategories)
        {
            if (!search.Field.isFocused && search.Field.text != rule.Typed) search.Field.SetTextWithoutNotify(rule.Typed);
            if (search.Refresh()) Rebuilt();
            var taxonomy = plugin.RuleTaxonomy;
            key = rule.Revision + "|" + rule.SearchRevision + "|" + plugin.ClassificationEvidenceReady + "|" + RuntimeHelpers.GetHashCode(taxonomy);
            if (key != treeKey) { treeKey = key; Tree(rule, taxonomy); }
            line = rule.Search.Active && rule.Search.Hits.Count == 0 ? "Nothing found." : rule.HighlightStatus();
        }
        else treeKey = "\0";
        if (rule.PickingItems)
        {
            if (!itemSearch.Field.isFocused && itemSearch.Field.text != rule.GridQuery) itemSearch.Field.SetTextWithoutNotify(rule.GridQuery);
            if (itemSearch.Refresh()) Rebuilt();
            var entries = rule.GridEntries();
            rule.GridPointer(grid.Hovered);
            line = rule.GridQuery.Trim().Length != 0 ? rule.HighlightStatus() : "";
            if (gridShown != rule.GridRevision) { gridShown = rule.GridRevision; grid.Set(entries, RuleControls.GridTitle); }
            else grid.Refresh();
            Show(gridEmpty, entries.Count == 0);
        }
        else { gridShown = -1; rule.GridPointer(null); }
        // D101 (C6): an edit's refusal takes the line until the next action; otherwise the open tab's search result.
        if (rule.Messages.At(MessageSpot.Tab).Length != 0) Say(status, rule, MessageSpot.Tab, Amber, ui.Faint);
        else
        {
            if (status.color != ui.Faint) status.color = ui.Faint;
            Set(status, line);
        }
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
        foreach (var ignored in rule.Unknown)
        {
            var row = ui.Row(unknown);
            ui.Label(row, $"Ignored category '{ignored}' (removed or from another version)", JotunnWidgets.SmallSize);
            var id = ignored;
            ui.Button(row, "Remove", () => rule.RemoveUnknown(id), 80);
        }
        // D077 B01: a filter this version doesn't know is kept and takes nothing.
        foreach (var dimension in rule.UnknownFilters)
        {
            var row = ui.Row(unknown);
            ui.Label(row, $"Ignored filter '{dimension}' (another version or add-on; takes nothing)", JotunnWidgets.SmallSize);
            var id = dimension;
            ui.Button(row, "Remove", () => rule.RemoveFilter(id), 80);
        }
        // D086 D6: a value of a known filter this version doesn't know is kept the same way.
        foreach (var (dimension, value) in rule.UnknownFilterValues)
        {
            var row = ui.Row(unknown);
            ui.Label(row, $"Ignored filter value '{dimension}: {value}' (another version; takes nothing)", JotunnWidgets.SmallSize);
            var d = dimension; var v = value;
            ui.Button(row, "Remove", () => rule.RemoveFilterValue(d, v), 80);
        }
        // D086 D5: no hidden rule parts. What this panel can't show is named here and kept; Clear rule removes it.
        if (rule.HiddenParts != 0)
            ui.Label(ui.Row(unknown), rule.HiddenParts == 1
                ? "This rule has 1 more part this version can't show or edit. It is kept; Clear rule removes it."
                : $"This rule has {rule.HiddenParts} more parts this version can't show or edit. They are kept; Clear rule removes them.", JotunnWidgets.SmallSize);
        Show(unknown, rule.Unknown.Count + rule.UnknownFilters.Count + rule.UnknownFilterValues.Count + rule.HiddenParts != 0);
    }

    // A ticked umbrella is stored as itself, so it also takes subcategories added later. D101 (C9): while searching, a row
    // names the items found in it, faintly, on one line; pointing at a row lights the chests that take it.
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
                var note = "";
                for (var shown = CategorySearch.NoteNames; shown >= 0; shown--)
                {
                    note = " · " + CategorySearch.Note(names, shown);
                    if (JotunnWidgets.LineWidth(label, row.Name + note) <= room) break;
                }
                label.supportRichText = true;
                label.text = Plain(row.Name) + "<color=#A0957F>" + Plain(note) + "</color>";
            }
            Tip(check.Root.GetComponent<Toggle>(), row.Name, (names.Count != 0 ? "Has " + string.Join(", ", names) + ".\n" : hit ? "" : "Shown because something under it matches.\n") +
                "Tick to take the whole category. Point at a row to light up the nearby chests that take it.");
            var report = check.Root.AddComponent<PointerReport>();
            report.Changed = on => rule.RowPointer(id, on);
        }
    }

    private static string Plain(string text) => text.Replace("<", "‹").Replace(">", "›");
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
        if (choices.Count == 0) ui.Label(list, "No filter applies to these categories.", JotunnWidgets.SmallSize, ui.Faint);
        foreach (var choice in choices)
        {
            var dimension = choice.Dimension;
            var name = ui.Label(list, RuleText.FilterName(dimension) + (choice.Applies ? "" : " (doesn't apply to these categories)"), JotunnWidgets.SmallSize,
                choice.Applies ? ui.Orange : ui.Faint);
            Tip(name, RuleText.FilterName(dimension), "Tick values to narrow the ticked categories. Values within one filter are alternatives; an item must " +
                "match every filter that has a ticked value." + (ClassificationFilters.InstanceDimensions.Contains(dimension) ? " Checked on each stack as it moves." : ""));
            rule.Filters.TryGetValue(dimension, out var ticked);
            if (dimension == "quality" && choice.Applies) { Levels(rule, choice, ticked); continue; }
            RectTransform? row = null;
            for (var i = 0; i < choice.Values.Count; i++)
            {
                if (i % 3 == 0) row = ui.Row(list, 2);
                var value = choice.Values[i]; var dim = dimension;
                var text = value != "unknown" && ClassificationFilters.Values(dimension) == null ? RuleControls.OpenValueName(dimension, value) : RuleText.ValueName(dimension, value);
                if (choice.Applies && choice.Stale.Contains(value)) text += " (none here)";
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
            ui.Label(list, "Saved levels none of these items reach: " + string.Join(", ", choice.Stale), JotunnWidgets.SmallSize, ui.Faint);
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
    private Text heading = null!, spot = null!;
    private CanvasGroup bar = null!;
    private readonly List<(Image Back, Text Text, Outline Edge)> levels = new();
    private int shownLevel = -1;
    private bool shownRuled;
    internal override string Name => "This chest";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var head = ui.Row(Root);
        heading = ui.Label(head, "This chest", JotunnWidgets.TextSize, ui.Orange, false);
        spot = Spot(ui, head, Whole(rule, MessageSpot.Chest));
        spot.alignment = TextAnchor.MiddleRight;
        var row = ui.Row(Root, 2);
        bar = row.gameObject.AddComponent<CanvasGroup>();
        for (var level = ChestPriority.Highest; level <= ChestPriority.Lowest; level++)
        {
            var chosen = level;
            var (button, back, text) = ui.Swatch(row, level.ToString(), Bands[ChestPriority.Band(level)], () => rule.SetLevel(chosen));
            var edge = back.gameObject.AddComponent<Outline>(); edge.effectColor = ui.Orange; edge.effectDistance = new Vector2(2, -2); edge.enabled = false;
            levels.Add((back, text, edge));
            Tip(button, () => ChestPriority.Tooltip(chosen, ChestPriority.Level(rule.Priority), rule.Stored));
        }
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var edit = mode == RulePanelMode.Edit;
        Show(Root, edit);
        if (!edit) return;
        var ruled = rule.Stored && rule.Categories.Count + rule.Includes.Count + rule.Unknown.Count + rule.Filters.Count + rule.HiddenParts != 0;
        Set(heading, ChestPriority.Heading(rule.Priority, ruled));
        Say(spot, rule, MessageSpot.Chest, Amber, ui.Orange);
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

// D071: "Clean up nearby" at the bottom, on one row with the chest's clean-up opt-out (maintainer, H4 client check). D101:
// a refusal, or the summary of a run with nothing to move, on the line under them until the next action; a started run
// says nothing here (the button turns to "Cleaning up…" and the result panel follows).
internal sealed class CleanupSection : PanelSection
{
    private JotunnWidgets ui = null!;
    private Button cleanup = null!;
    private Text spot = null!;
    private TriStateToggle optOut = null!;
    internal override string Name => "Cleanup";

    internal override void Build(JotunnWidgets ui, RuleControls rule)
    {
        this.ui = ui;
        var row = ui.Row(Root);
        optOut = ui.Check(row, "Don't clean up this chest", rule.ToggleCleanupOptOut);
        Tip(optOut.Root.GetComponent<Toggle>(), "Don't clean up this chest", "Clean up nearby leaves this chest's items where they are. Items can still be stashed here.");
        cleanup = ui.Button(row, "Clean up nearby", rule.CleanUpNearby, 170);
        Tip(cleanup, () =>
        {
            var settings = Plugin.Instance?.Settings;
            var key = settings == null ? "" : CleanupTriggers.KeyName(settings.CleanupShortcut);
            return ("Clean up nearby" + (key.Length == 0 ? "" : " [" + key + "]"), "Moves misplaced items from chests within " +
                (settings?.EffectiveCleanupRange ?? 0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                " m to the chests whose rules take them. A result panel lists what went where.");
        });
        spot = Spot(ui, Root, Whole(rule, MessageSpot.Cleanup));
    }

    protected override void Refresh(RuleControls rule, RulePanelMode mode, bool structural)
    {
        var edit = mode == RulePanelMode.Edit;
        Show(Root, edit);
        if (!edit) return;
        Set(cleanup, rule.Cleanup.Running ? "Cleaning up…" : "Clean up nearby");
        optOut.Set(ChestRuleStore.CleanupOptOut(rule.Chest!) ? CheckboxState.Ticked : CheckboxState.Empty);
        Say(spot, rule, MessageSpot.Cleanup, Amber, ui.Orange);
    }
}
