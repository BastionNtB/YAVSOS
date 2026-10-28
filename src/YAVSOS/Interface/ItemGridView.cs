using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YAVSOS.Core.Interface;

namespace YAVSOS.Interface;

/// <summary>
/// P6-18: the Items tab's icon grid, like VNEI's item list. Only the rows in view exist: a fixed pool of icon cells and
/// section titles is moved and refilled as the list scrolls, so a category of hundreds of items costs what one screen
/// does. Icons are the game's own sprites; hovering shows the game's item tooltip (filled only for the hovered icon),
/// with the mark's meaning; a click cycles the mark. Unity game thread only.
/// </summary>
internal sealed class ItemGridView
{
    internal const int Columns = 8;
    private const float Cell = 46, Icon = 38, Header = 24, Pad = 4;
    private static readonly Color Slot = new Color(0, 0, 0, 0.35f), Dimmed = new Color(1, 1, 1, 0.35f), KeptOutSlot = new Color(0.94f, 0.66f, 0.18f, 0.45f);
    private readonly ScrollRect scroll;
    private readonly RectTransform content;
    private readonly List<CellView> cells = new();
    private readonly List<Text> headers = new();
    private readonly Action<GridEntry> click;
    private readonly Func<string, Sprite?> icon;
    private readonly Func<GridEntry, (string Topic, string Text)> tooltip;
    private readonly Func<GridEntry, bool> keptOut;
    private readonly List<(float Top, string? Title, int First, int Count)> lines = new();
    private IReadOnlyList<GridEntry> entries = Array.Empty<GridEntry>();
    private int firstShown = -1, lastShown = -1;
    private float viewportShown = -1;

    internal ItemGridView(JotunnWidgets ui, Transform parent, float height, Action<GridEntry> click, Func<string, Sprite?> icon,
        Func<GridEntry, (string Topic, string Text)> tooltip, Func<GridEntry, bool> keptOut)
    {
        this.click = click; this.icon = icon; this.tooltip = tooltip; this.keptOut = keptOut;
        (scroll, content) = ui.ScrollArea(parent, height);
        scroll.onValueChanged.AddListener(_ => Bind(false));
        WheelStep.Attach(scroll, Cell);
        var rows = Mathf.CeilToInt(height / Cell) + 2;
        for (var i = 0; i < rows * Columns; i++) cells.Add(new CellView(this, content));
        for (var i = 0; i < 4; i++)
        {
            var title = ui.Label(content, "", JotunnWidgets.SmallSize, ui.Orange, false);
            var rect = title.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(Columns * Cell, Header);
            title.gameObject.SetActive(false);
            headers.Add(title);
        }
    }

    /// <summary>The grid's frame (its scroll area), for a note drawn over an empty grid (D101).</summary>
    internal RectTransform Frame => (RectTransform)scroll.transform;

    /// <summary>The grid shown last, for the lab only.</summary>
    internal static ItemGridView? Last { get; private set; }
    internal int Count => entries.Count;
    /// <summary>Icon cells in use now: the pool's size bounds the cost however long the list is.</summary>
    internal int Pool => cells.Count;

    internal void Set(IReadOnlyList<GridEntry> rows, Func<GridSection, int, string> title)
    {
        entries = rows; lines.Clear(); Last = this;
        var top = 0f;
        for (var at = 0; at < rows.Count;)
        {
            var section = rows[at].Section; var end = at;
            while (end < rows.Count && rows[end].Section == section) end++;
            lines.Add((top, title(section, end - at), at, 0)); top += Header;
            for (var first = at; first < end; first += Columns)
            {
                lines.Add((top, null, first, Math.Min(Columns, end - first))); top += Cell;
            }
            at = end;
        }
        content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, top + Pad);
        Bind(true);
    }

    /// <summary>Called on the panel's tick: the first fill may run before layout gives the viewport its real height.</summary>
    internal void Refresh() => Bind(false);

    /// <summary>P8-18: the entry under the pointer, for the chest highlight to narrow to (null: none).</summary>
    internal GridEntry? Hovered
    {
        get
        {
            foreach (var cell in cells) if (cell.Hovered is { } entry) return entry;
            return null;
        }
    }

    internal void ScrollTo(float fraction) { scroll.verticalNormalizedPosition = 1 - Mathf.Clamp01(fraction); Bind(false); }

    // Lays the pooled cells and titles over the lines in view; nothing is created or destroyed.
    private void Bind(bool force)
    {
        var viewport = scroll.viewport ? scroll.viewport.rect.height : 0;
        var offset = Mathf.Max(0, content.anchoredPosition.y);
        var first = Find(offset); var last = Find(offset + viewport);
        if (!force && first == firstShown && last == lastShown && Mathf.Approximately(viewport, viewportShown)) return;
        firstShown = first; lastShown = last; viewportShown = viewport;
        var timed = UiCost.Start();
        var cell = 0; var header = 0;
        for (var line = first; line <= last && line < lines.Count; line++)
        {
            var (top, title, start, count) = lines[line];
            if (title != null)
            {
                if (header == headers.Count) continue;
                var text = headers[header++];
                text.text = title; text.rectTransform.anchoredPosition = new Vector2(Pad, -top);
                if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
                continue;
            }
            for (var i = 0; i < count && cell < cells.Count; i++)
                cells[cell++].Show(entries[start + i], new Vector2(Pad + i * Cell, -top));
        }
        for (; cell < cells.Count; cell++) cells[cell].Hide();
        for (; header < headers.Count; header++) if (headers[header].gameObject.activeSelf) headers[header].gameObject.SetActive(false);
        UiCost.Stop("item grid rows", timed);
    }

    private int Find(float y)
    {
        int low = 0, high = lines.Count - 1;
        while (low < high) { var mid = (low + high + 1) / 2; if (lines[mid].Top <= y) low = mid; else high = mid - 1; }
        return Math.Max(0, low);
    }

    private sealed class CellView
    {
        private readonly RectTransform root;
        private readonly Image slot, image, mark;
        private readonly UITooltip? tip;
        private readonly TipFill fill;
        private GridEntry? entry;

        internal CellView(ItemGridView owner, RectTransform parent)
        {
            var go = new GameObject("Item", typeof(RectTransform), typeof(Image), typeof(Button), typeof(TipFill));
            root = (RectTransform)go.transform;
            root.SetParent(parent, false);
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0, 1);
            root.sizeDelta = new Vector2(Cell - 4, Cell - 4);
            slot = go.GetComponent<Image>(); slot.color = Slot;
            image = Child("Icon", Icon); image.preserveAspect = true;
            mark = Child("Mark", Icon - 8);
            fill = go.GetComponent<TipFill>();
            // The game's own tooltip, from its inventory slots; filled just before it shows (TipFill runs first).
            var prefab = InventoryGui.instance && InventoryGui.instance.m_playerGrid && InventoryGui.instance.m_playerGrid.m_elementPrefab
                ? InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<UITooltip>()?.m_tooltipPrefab : null;
            if (prefab) { tip = go.AddComponent<UITooltip>(); tip.m_tooltipPrefab = prefab; }
            fill.Fill = () => { if (entry != null && tip) { var (topic, text) = owner.tooltip(entry); tip!.Set(topic, text); } };
            go.GetComponent<Button>().onClick.AddListener(() => { if (entry != null) owner.click(entry); });
            this.owner = owner;
            go.SetActive(false);
        }
        private readonly ItemGridView owner;

        private Image Child(string name, float size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)go.transform;
            rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f); rect.sizeDelta = new Vector2(size, size);
            var picture = go.GetComponent<Image>(); picture.raycastTarget = false;
            return picture;
        }

        internal void Show(GridEntry row, Vector2 at)
        {
            if (!root.gameObject.activeSelf) root.gameObject.SetActive(true);
            root.anchoredPosition = at;
            if (ReferenceEquals(entry, row)) return;
            entry = row;
            var sprite = owner.icon(row.Prefab);
            image.sprite = sprite; image.enabled = sprite;
            // P4-00f (D056): taken by the rule, but AzuAutoStore's config keeps it out of this chest type.
            var kept = owner.keptOut(row);
            image.color = row.Taken && !kept ? Color.white : Dimmed;
            slot.color = kept ? KeptOutSlot : Slot;
            if (row.Mark == GridMark.None) mark.enabled = false;
            else
            {
                mark.sprite = row.Mark == GridMark.Always ? PanelArt.Check : PanelArt.Cross;
                mark.color = row.Mark == GridMark.Always ? new Color(0.35f, 0.9f, 0.35f, 0.7f) : new Color(0.95f, 0.3f, 0.25f, 0.7f);
                mark.enabled = true;
            }
            // A recycled cell must not keep another item's text. Under the pointer (a click rebuilds the grid while the
            // tooltip is open) it is refilled instead, or the open tooltip shrank to an empty black bar (2026-10-25).
            if (fill.Hovered) fill.Fill?.Invoke();
            else if (tip && tip!.m_text.Length != 0) tip.Set("", "");
        }

        internal GridEntry? Hovered => fill.Hovered && root.gameObject.activeSelf ? entry : null;

        internal void Hide() { if (root.gameObject.activeSelf) root.gameObject.SetActive(false); entry = null; }
    }
}

/// <summary>
/// Runs before the game's <see cref="UITooltip"/> on the same cell (added first), so its text is current when it opens.
/// While hovered it also makes the tooltip ignore the mouse: the game places it at the pointer, and inside this panel's
/// canvas it caught the pointer itself, so it flickered off and on and swallowed clicks (maintainer, 2026-10-23).
/// </summary>
internal sealed class TipFill : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal Action? Fill;
    // The game's one shared tooltip object (private); if a game update renames it, tooltips work as before, unpatched.
    private static readonly System.Reflection.FieldInfo? Shared = HarmonyLib.AccessTools.DeclaredField(typeof(UITooltip), "m_tooltip");
    private bool hovered;
    internal bool Hovered => hovered;
    private static GameObject? passive;
    public void OnPointerEnter(PointerEventData eventData) { hovered = true; Fill?.Invoke(); }
    public void OnPointerExit(PointerEventData eventData) => hovered = false;
    private void OnDisable() => hovered = false;
    private void LateUpdate()
    {
        if (!hovered) return;
        if (Shared?.GetValue(null) is not GameObject tip || !tip || tip == passive) return;
        passive = tip;
        // The game makes the tooltip under the nearest canvas, which here is the panel's own: its column layout took the
        // tooltip as a row, the panel grew, the icon left the pointer, the tooltip closed and the panel shrank again, over
        // and over (maintainer's videos, 2026-10-23). It belongs on the screen's top canvas, as the inventory's tooltips are.
        var top = tip.transform.parent ? tip.transform.parent.GetComponentInParent<Canvas>()?.rootCanvas : null;
        if (top && tip.transform.parent != top!.transform) tip.transform.SetParent(top.transform, true);
        tip.transform.SetAsLastSibling();
        // It is drawn at the pointer, so it also lets the pointer through to the icon below.
        if (!tip.GetComponent<CanvasGroup>()) tip.AddComponent<CanvasGroup>();
        foreach (var group in tip.GetComponentsInChildren<CanvasGroup>(true)) { group.blocksRaycasts = false; group.interactable = false; }
        foreach (var graphic in tip.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
    }
}

/// <summary>
/// One mouse-wheel notch moves the list by a fixed step, whatever size the platform reports for a notch (on the
/// maintainer's Linux client it moved a few pixels, 2026-10-23). Replaces the ScrollRect's own wheel handling.
/// </summary>
internal sealed class WheelStep : MonoBehaviour, IScrollHandler
{
    private ScrollRect scroll = null!;
    private float step;

    internal static void Attach(ScrollRect scroll, float step)
    {
        scroll.scrollSensitivity = 0;
        var wheel = scroll.gameObject.AddComponent<WheelStep>();
        wheel.scroll = scroll; wheel.step = step;
    }

    public void OnScroll(PointerEventData eventData)
    {
        var delta = eventData.scrollDelta.y;
        if (delta == 0 || !scroll || !scroll.content || !scroll.viewport) return;
        var notches = Mathf.Max(1f, Mathf.Round(Mathf.Abs(delta)));
        var content = scroll.content;
        var room = Mathf.Max(0, content.rect.height - scroll.viewport.rect.height);
        var y = Mathf.Clamp(content.anchoredPosition.y - Mathf.Sign(delta) * notches * step, 0, room);
        scroll.velocity = Vector2.zero;
        content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
    }
}
