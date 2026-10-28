using System;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using YAVSOS.Core.Interface;

namespace YAVSOS.Interface;

/// <summary>
/// P6-03 (D062): Valheim-styled widgets from Jotunn's <c>GUIManager</c>, laid out by Unity UI layout groups. The only
/// file that calls Jotunn; <see cref="JotunnGuiCapability"/> checks every member used here before the first call.
/// Unity game thread only.
/// </summary>
internal sealed class JotunnWidgets
{
    internal const int TextSize = 17, SmallSize = 15, RowHeight = 26; // Raised 2026-10-21: smaller than the game's own text.
    private const float BackingInset = 6;
    private static readonly Color Backing = new Color(0.11f, 0.08f, 0.05f, 1f), Dim = new Color(0.62f, 0.58f, 0.5f, 1f);
    private readonly GUIManager gui = GUIManager.Instance;
    internal Color Orange => gui.ValheimOrange;
    internal Color Beige => gui.ValheimBeige;
    internal Color Faint => Dim;

    /// <summary>Jotunn loads its sprites with the game's GUI; widgets made earlier would be unstyled.</summary>
    internal bool Ready => gui.ValheimControlResources.standard != null;

    /// <summary>Jotunn's overlay canvas above the game's GUI, including the inventory screen; null until Jotunn has made it.</summary>
    internal static Transform? Front => GUIManager.CustomGUIFront ? GUIManager.CustomGUIFront.transform : null;

    // Opaque: a solid backing under the wood so the game's labels never show through (P6-03).
    internal RectTransform Panel(Transform parent, float width, string name = "YAVSOS rule panel")
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        root.layer = 5;
        var rect = (RectTransform)root.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f); rect.pivot = new Vector2(0, 1);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        // The root image only catches clicks, so none reach the game's GUI behind the panel.
        root.GetComponent<Image>().color = Color.clear;
        var wood = gui.CreateWoodpanel(rect, Vector2.zero, Vector2.one, Vector2.zero, 0, 0, false);
        var woodRect = (RectTransform)wood.transform;
        woodRect.anchorMin = Vector2.zero; woodRect.anchorMax = Vector2.one; woodRect.offsetMin = woodRect.offsetMax = Vector2.zero;
        wood.AddComponent<LayoutElement>().ignoreLayout = true;
        woodRect.SetAsFirstSibling();
        // A dark backing under the wood keeps the game's labels from showing through (P6-03). It stops short of the
        // edge: the wood's edges and corners fade out, and a backing to the edge showed through them as a dark rim,
        // most at the corners (maintainer, 2026-10-23). Inset, the edges fade into the world like the game's panels.
        var backing = new GameObject("Backing", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        var backingRect = (RectTransform)backing.transform;
        backingRect.SetParent(rect, false);
        backingRect.anchorMin = Vector2.zero; backingRect.anchorMax = Vector2.one;
        backingRect.offsetMin = new Vector2(BackingInset, BackingInset); backingRect.offsetMax = new Vector2(-BackingInset, -BackingInset);
        backing.GetComponent<LayoutElement>().ignoreLayout = true;
        backingRect.SetAsFirstSibling();
        var backingImage = backing.GetComponent<Image>(); backingImage.color = Backing; backingImage.raycastTarget = false;
        var woodImage = wood.GetComponent<Image>();
        if (woodImage && woodImage.sprite)
        {
            backingImage.sprite = woodImage.sprite; backingImage.type = Image.Type.Sliced; backingImage.pixelsPerUnitMultiplier = woodImage.pixelsPerUnitMultiplier;
            Grain(rect, woodImage);
        }
        var layout = root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 12, 12); layout.spacing = 6;
        Controls(layout);
        root.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        // Snap every row to whole screen pixels: the legacy text drawn at half-pixel positions blurred every other line,
        // unlike the game's own text (runtime 2026-10-21). A nested canvas needs its own raycaster for the controls.
        var canvas = root.AddComponent<Canvas>();
        canvas.overridePixelPerfect = true; canvas.pixelPerfect = true;
        root.AddComponent<GraphicRaycaster>();
        return rect;
    }

    /// <summary>
    /// The wood's centre, tiled: a taller panel shows more wood instead of stretched grain, while the frame (corners and
    /// edges) stays stretched, so it is seamless (maintainer, 2026-10-23: tiling the whole image repeated the edge strips,
    /// which did not line up at the top corners). The centre is cut from the same sprite, inside its borders. Without
    /// borders the whole image is tiled.
    /// </summary>
    private static void Grain(RectTransform panel, Image wood)
    {
        var sprite = wood.sprite!; var border = sprite.border;
        if (border == Vector4.zero) { wood.type = Image.Type.Tiled; return; }
        Rect source;
        try { source = sprite.textureRect; } catch (Exception) { source = sprite.rect; } // A tightly packed atlas sprite has no textureRect.
        var inner = new Rect(source.x + border.x, source.y + border.y, source.width - border.x - border.z, source.height - border.y - border.w);
        if (inner.width < 4 || inner.height < 4) { wood.type = Image.Type.Tiled; return; }
        var centre = Sprite.Create(sprite.texture, inner, new Vector2(0.5f, 0.5f), sprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        var go = new GameObject("Grain", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        var rect = (RectTransform)go.transform;
        rect.SetParent(panel, false);
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        var image = go.GetComponent<Image>();
        // The game's lit panel material (Jotunn's woodpanel style) shades the wood; without it the grain was far too bright.
        image.sprite = centre; image.type = Image.Type.Tiled; image.raycastTarget = false; image.color = wood.color; image.material = wood.material;
        image.pixelsPerUnitMultiplier = wood.pixelsPerUnitMultiplier;
        // The frame's borders in layout units, as the sliced image draws them; the grain stops a little under them.
        var scale = Math.Max(0.0001f, wood.pixelsPerUnit * wood.pixelsPerUnitMultiplier);
        const float overlap = 2;
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(border.x / scale - overlap, border.y / scale - overlap);
        rect.offsetMax = new Vector2(-(border.z / scale - overlap), -(border.w / scale - overlap));
        rect.SetSiblingIndex(wood.transform.GetSiblingIndex()); // Under the frame.
        wood.type = Image.Type.Sliced; wood.fillCenter = false;
    }

    /// <summary>A vertical group: one panel section, or a list's content.</summary>
    internal RectTransform Column(Transform parent, string name, float spacing = 4)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(VerticalLayoutGroup));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<VerticalLayoutGroup>(); layout.spacing = spacing;
        Controls(layout);
        return (RectTransform)go.transform;
    }

    internal RectTransform Row(Transform parent, float spacing = 4)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing; layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true; layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        go.GetComponent<LayoutElement>().minHeight = RowHeight;
        return (RectTransform)go.transform;
    }

    /// <summary>
    /// Redraws a panel's text when the interface scale changes while it is open (a window resize): the legacy text keeps
    /// glyphs drawn for the old size and looks blurred until shown again (runtime 2026-10-21). Showing it again is
    /// what redraws it, so this hides and shows the panel within the frame. Call once per frame with the panel's canvas.
    /// </summary>
    internal sealed class ScaleWatch
    {
        private float seen = -1;
        internal void Check(GameObject panel, Canvas? canvas)
        {
            if (!canvas || !panel.activeSelf) return;
            var scale = canvas!.rootCanvas.scaleFactor;
            if (seen > 0 && !Mathf.Approximately(scale, seen)) { panel.SetActive(false); panel.SetActive(true); }
            seen = scale;
        }
    }

    internal Text Label(Transform parent, string text, int size = TextSize, Color? color = null, bool flexible = true)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var label = go.GetComponent<Text>();
        gui.ApplyTextStyle(label, color ?? Beige, size, true);
        label.supportRichText = false; label.raycastTarget = false; label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Overflow;
        label.text = text;
        if (flexible) go.GetComponent<LayoutElement>().flexibleWidth = 1;
        return label;
    }

    internal void Space(Transform parent, float width)
    {
        var go = new GameObject("Space", typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<LayoutElement>(); layout.minWidth = layout.preferredWidth = width;
    }

    /// <summary>A button; width 0 shares the row's free width.</summary>
    internal Button Button(Transform parent, string text, Action click, float width = 0)
    {
        var go = gui.CreateButton(text, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, width > 0 ? width : 100, RowHeight + 4);
        var label = go.GetComponentInChildren<Text>(true);
        if (label) { label.fontSize = TextSize; label.supportRichText = false; }
        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = RowHeight + 4;
        if (width > 0) layout.minWidth = layout.preferredWidth = width; else layout.flexibleWidth = 1;
        var button = go.GetComponent<Button>();
        button.onClick.AddListener(() => click());
        return button;
    }

    /// <summary>A flat clickable cell with centred text, sharing the row's width (the priority bar, D071).</summary>
    internal (Button Button, Image Back, Text Text) Swatch(Transform parent, string text, Color color, Action click)
    {
        var go = new GameObject("Swatch", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var back = go.GetComponent<Image>(); back.color = color;
        var layout = go.GetComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = RowHeight; layout.minWidth = 20; layout.flexibleWidth = 1;
        var button = go.GetComponent<Button>(); button.targetGraphic = back; button.onClick.AddListener(() => click());
        var label = Label(go.transform, text, SmallSize, Beige, false);
        label.alignment = TextAnchor.MiddleCenter; Fill(label.rectTransform, 0, 0);
        return (button, back, label);
    }

    internal const float ChipHeight = 24;

    /// <summary>
    /// A rounded-off tag with text and an optional icon (the Rule section's chips, D071), placed by its owner: anchored
    /// at the top left, with <paramref name="width"/> its full width in layout units.
    /// </summary>
    internal RectTransform Chip(Transform parent, string text, Color back, Sprite? icon, Color iconColor, out float width)
    {
        var go = new GameObject("Chip", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        var image = go.GetComponent<Image>(); image.color = back; image.raycastTarget = false;
        var left = 8f;
        if (icon != null)
        {
            var mark = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            var markRect = (RectTransform)mark.transform;
            markRect.SetParent(rect, false);
            markRect.anchorMin = markRect.anchorMax = markRect.pivot = new Vector2(0, 0.5f);
            markRect.anchoredPosition = new Vector2(6, 0); markRect.sizeDelta = new Vector2(14, 14);
            var picture = mark.GetComponent<Image>(); picture.sprite = icon; picture.color = iconColor; picture.raycastTarget = false;
            left = 24;
        }
        var label = Label(rect, text, SmallSize, Beige, false);
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        width = left + Mathf.Ceil(label.preferredWidth) + 8;
        Fill(label.rectTransform, left, 8);
        rect.sizeDelta = new Vector2(width, ChipHeight);
        return rect;
    }

    /// <summary>A coloured strip with one line of text for a problem state (D071 banners); show or hide its parent object.</summary>
    internal Text Banner(Transform parent, string text, Color tint, Color color)
    {
        var go = new GameObject("Banner", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>(); image.color = tint; image.raycastTarget = false;
        var layout = go.GetComponent<VerticalLayoutGroup>(); layout.padding = new RectOffset(8, 8, 4, 4);
        Controls(layout);
        return Label(go.transform, text, SmallSize, color);
    }

    private static void Fill(RectTransform rect, float left, float right)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(left, 0); rect.offsetMax = new Vector2(-right, 0);
    }

    /// <summary>A checkbox with its label; clicking either reports the click, and the owner sets the state it should show.</summary>
    internal TriStateToggle Check(Transform parent, string text, Action click, float width = 0)
    {
        var go = gui.CreateToggle(parent, 20, 20);
        go.transform.SetParent(parent, false); // CreateToggle keeps world position; reset scale and placement for layout.
        go.transform.localScale = Vector3.one;
        var toggle = go.GetComponent<Toggle>();
        toggle.toggleTransition = Toggle.ToggleTransition.None; // No fade: the owner sets the shown state at once.
        var background = go.transform.Find("Background") as RectTransform;
        if (background) { background!.anchorMin = background.anchorMax = new Vector2(0, 0.5f); background.anchoredPosition = new Vector2(10, 0); }
        var label = go.transform.Find("Label")?.GetComponent<Text>();
        if (label)
        {
            gui.ApplyTextStyle(label!, Beige, TextSize, true);
            label!.supportRichText = false; label.alignment = TextAnchor.MiddleLeft; label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Overflow;
            var labelRect = label.rectTransform; labelRect.offsetMin = new Vector2(26, 0); labelRect.offsetMax = Vector2.zero;
        }
        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = width > 0 && label ? Math.Max(RowHeight, TextHeight(label!, width - 26) + 4) : RowHeight;
        if (width > 0) layout.minWidth = layout.preferredWidth = width; else layout.flexibleWidth = 1;
        // The partial mark: a bar across the box, shown instead of the tick (no stock Valheim sprite for it).
        Image? partial = null;
        if (background)
        {
            var bar = new GameObject("Partial", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(background, false);
            var barRect = (RectTransform)bar.transform;
            barRect.anchorMin = barRect.anchorMax = new Vector2(0.5f, 0.5f); barRect.sizeDelta = new Vector2(12, 4);
            partial = bar.GetComponent<Image>(); partial.color = Orange; partial.raycastTarget = false; partial.enabled = false;
        }
        toggle.onValueChanged.AddListener(_ => click());
        return new TriStateToggle(toggle, partial, label, Beige, Dim, Orange);
    }

    // A fixed-width label that wraps ("no tame eats it" in a filter cell) overlapped the row below at one row's height (runtime 2026-10-21).
    private static float TextHeight(Text label, float width)
    {
        var settings = label.GetGenerationSettings(new Vector2(width, 0));
        settings.scaleFactor = 1; // Layout units, whatever the canvas scale.
        return label.cachedTextGeneratorForLayout.GetPreferredHeight(label.text, settings);
    }

    /// <summary>A vertical scroll list of fixed height; returns the content to fill.</summary>
    internal RectTransform ScrollList(Transform parent, float height)
    {
        var go = DefaultControls.CreateScrollView(gui.ValheimControlResources);
        go.transform.SetParent(parent, false);
        var scroll = go.GetComponent<ScrollRect>();
        if (scroll.horizontalScrollbar) UnityEngine.Object.Destroy(scroll.horizontalScrollbar.gameObject);
        scroll.horizontalScrollbar = null; scroll.horizontal = false;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = height; layout.flexibleWidth = 1;
        var content = scroll.content;
        content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(0, 1); content.sizeDelta = Vector2.zero;
        var list = content.gameObject.AddComponent<VerticalLayoutGroup>();
        list.spacing = 2; list.padding = new RectOffset(4, 4, 4, 4);
        Controls(list);
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        gui.ApplyScrollRectStyle(scroll);
        WheelStep.Attach(scroll, RowHeight * 2); // About two rows per notch, on every platform.
        return content;
    }

    /// <summary>A vertical scroll area of fixed height whose content the owner places itself (the item grid, P6-18).</summary>
    internal (ScrollRect Scroll, RectTransform Content) ScrollArea(Transform parent, float height)
    {
        var go = DefaultControls.CreateScrollView(gui.ValheimControlResources);
        go.transform.SetParent(parent, false);
        var scroll = go.GetComponent<ScrollRect>();
        if (scroll.horizontalScrollbar) UnityEngine.Object.Destroy(scroll.horizontalScrollbar.gameObject);
        scroll.horizontalScrollbar = null; scroll.horizontal = false;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = height; layout.flexibleWidth = 1;
        var content = scroll.content;
        content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(0, 1); content.sizeDelta = Vector2.zero;
        gui.ApplyScrollRectStyle(scroll);
        return (scroll, content);
    }

    /// <summary>A text field that blocks the game's keys while focused (search, P6-06).</summary>
    internal InputField Input(Transform parent, string placeholder, Action<string> changed)
    {
        var go = gui.CreateInputField(parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero,
            InputField.ContentType.Standard, placeholder, TextSize, 200, RowHeight + 4);
        var layout = go.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = RowHeight + 4; layout.flexibleWidth = 1;
        go.AddComponent<TypingBlock>();
        var field = go.GetComponent<InputField>();
        field.onValueChanged.AddListener(x => changed(x));
        return field;
    }

    /// <summary>
    /// A search field with a clear button at its right (D101, Phase 9 X17 brought forward): the cross shows only while the
    /// field holds text (<see cref="SearchBox.Refresh"/>); clicking it empties the field, which reports "" as typing does.
    /// </summary>
    internal SearchBox Search(Transform parent, string placeholder, Action<string> changed)
    {
        var field = Input(parent, placeholder, changed);
        var go = new GameObject("Clear", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        var rect = (RectTransform)go.transform;
        rect.SetParent(field.transform, false);
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        rect.anchorMin = new Vector2(1, 0.5f); rect.anchorMax = new Vector2(1, 0.5f); rect.pivot = new Vector2(1, 0.5f);
        rect.anchoredPosition = new Vector2(-4, 0); rect.sizeDelta = new Vector2(RowHeight - 2, RowHeight - 2);
        var back = go.GetComponent<Image>(); back.color = new Color(0, 0, 0, 0.01f); // Catches the click around the cross.
        var cross = new GameObject("Cross", typeof(RectTransform), typeof(Image));
        var crossRect = (RectTransform)cross.transform;
        crossRect.SetParent(rect, false);
        crossRect.anchorMin = crossRect.anchorMax = crossRect.pivot = new Vector2(0.5f, 0.5f); crossRect.sizeDelta = new Vector2(12, 12);
        var image = cross.GetComponent<Image>(); image.sprite = PanelArt.Cross; image.color = Beige; image.raycastTarget = false;
        var button = go.GetComponent<Button>(); button.targetGraphic = back;
        button.onClick.AddListener(() => field.text = "");
        // The typed text stops short of the cross.
        if (field.textComponent) field.textComponent.rectTransform.offsetMax -= new Vector2(RowHeight, 0);
        go.SetActive(false);
        return new SearchBox(field, go);
    }

    /// <summary>The height of <paramref name="lines"/> lines of <paramref name="label"/>'s text, in layout units.</summary>
    internal static float LinesHeight(Text label, int lines)
    {
        var settings = label.GetGenerationSettings(new Vector2(1000, 0));
        settings.scaleFactor = 1; settings.verticalOverflow = VerticalWrapMode.Overflow; // A truncating label measures as zero lines.
        return Mathf.Ceil(label.cachedTextGeneratorForLayout.GetPreferredHeight(string.Join("\n", new string[lines].Select(_ => "Ag")), settings));
    }

    /// <summary>
    /// <paramref name="text"/> cut at a word and ended with "…" so it fits <paramref name="lines"/> lines of
    /// <paramref name="width"/> (D101: a message spot keeps its size; the whole text is in its tooltip).
    /// </summary>
    internal static string Fit(Text label, string text, float width, int lines)
    {
        if (text.Length == 0 || width <= 0) return text;
        var settings = label.GetGenerationSettings(new Vector2(width, 0));
        settings.scaleFactor = 1; settings.verticalOverflow = VerticalWrapMode.Overflow;
        var limit = LinesHeight(label, lines) + 1;
        bool Fits(string candidate) => label.cachedTextGeneratorForLayout.GetPreferredHeight(candidate, settings) <= limit;
        if (Fits(text)) return text;
        var cut = text;
        while (cut.Length > 0)
        {
            var space = cut.LastIndexOf(' ');
            cut = space <= 0 ? cut.Substring(0, cut.Length - 1) : cut.Substring(0, space);
            if (Fits(cut.TrimEnd(',', '.', ':', ';') + "…")) return cut.TrimEnd(',', '.', ':', ';') + "…";
        }
        return "…";
    }

    /// <summary>The width <paramref name="text"/> takes on one line in <paramref name="label"/>'s style, in layout units.</summary>
    internal static float LineWidth(Text label, string text)
    {
        var settings = label.GetGenerationSettings(new Vector2(0, 0));
        settings.scaleFactor = 1; settings.horizontalOverflow = HorizontalWrapMode.Overflow;
        return label.cachedTextGeneratorForLayout.GetPreferredWidth(text, settings);
    }

    internal static void BlockInput(bool state) => GUIManager.BlockInput(state);

    internal static void Clear(Transform content)
    {
        for (var i = content.childCount - 1; i >= 0; i--)
        {
            var child = content.GetChild(i).gameObject;
            child.SetActive(false); // Destroy waits for the frame end; keep it out of this frame's layout.
            UnityEngine.Object.Destroy(child);
        }
    }

    private static void Controls(HorizontalOrVerticalLayoutGroup layout)
    {
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;
    }
}

/// <summary>A search field and its clear button (D101).</summary>
internal sealed class SearchBox
{
    internal InputField Field { get; }
    private readonly GameObject clear;
    internal SearchBox(InputField field, GameObject clear) { Field = field; this.clear = clear; }

    /// <summary>Shows the clear button while the field holds text; returns whether that changed.</summary>
    internal bool Refresh()
    {
        var on = !string.IsNullOrEmpty(Field.text);
        if (clear.activeSelf == on) return false;
        clear.SetActive(on);
        return true;
    }
}

/// <summary>Reports the pointer entering and leaving a widget (D101: the search tree row the highlight follows).</summary>
internal sealed class PointerReport : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal Action<bool>? Changed;
    public void OnPointerEnter(PointerEventData data) => Changed?.Invoke(true);
    public void OnPointerExit(PointerEventData data) => Changed?.Invoke(false);
    private void OnDisable() => Changed?.Invoke(false);
}

/// <summary>Empty, partly ticked or ticked (P6-03). Inherited ticks are dimmed. State is set by the owner, never by the click.</summary>
internal sealed class TriStateToggle
{
    private readonly Toggle toggle;
    private readonly Image? partial;
    private readonly Text? label;
    private readonly Color text, faint, found, mark;
    private CheckboxState state = CheckboxState.Empty;
    private bool inherited, marked;
    internal TriStateToggle(Toggle toggle, Image? partial, Text? label, Color text, Color faint, Color found)
    {
        this.toggle = toggle; this.partial = partial; this.label = label; this.text = text; this.faint = faint; this.found = found;
        mark = toggle.graphic ? toggle.graphic.color : Color.white;
        Show();
    }

    /// <param name="highlighted">A search hit (P6-06): its label is shown in orange.</param>
    internal void Set(CheckboxState value, bool dimmed = false, bool highlighted = false)
    {
        state = value; inherited = dimmed; marked = highlighted;
        Show();
    }

    internal GameObject Root => toggle.gameObject;
    internal Text? Label => label;

    private void Show()
    {
        // The Toggle shows or hides the tick itself (renderer alpha); the colour only dims an inherited tick.
        toggle.SetIsOnWithoutNotify(state == CheckboxState.Ticked);
        if (toggle.graphic) toggle.graphic.color = inherited ? new Color(mark.r, mark.g, mark.b, mark.a * 0.45f) : mark;
        if (partial) partial!.enabled = state == CheckboxState.Partial;
        if (label) label!.color = marked ? found : inherited ? faint : text;
    }
}

/// <summary>
/// Stops game keys while the player types (D062), and always releases its block. Driven by the field's real focus,
/// not by selection events: the EventSystem keeps a hidden field selected, so a later click focuses it without a new
/// OnSelect. <see cref="Active"/> lets YAVSOS's own key polls (BepInEx shortcuts, which BlockInput does not stop) step aside.
/// </summary>
internal sealed class TypingBlock : MonoBehaviour
{
    private static int holders;
    /// <summary>Whether any YAVSOS text field has focus now.</summary>
    internal static bool Active => holders != 0;
    private InputField? field;
    private bool blocking;
    private void LateUpdate()
    {
        if (!field) field = GetComponent<InputField>();
        Block(field && field!.isFocused);
    }
    private void OnDisable()
    {
        Block(false);
        // Drop the selection too, so the next click selects the field afresh.
        var events = EventSystem.current;
        if (events && events!.currentSelectedGameObject == gameObject) events.SetSelectedGameObject(null);
    }
    private void Block(bool state)
    {
        if (blocking == state) return;
        blocking = state; holders += state ? 1 : -1;
        try { JotunnWidgets.BlockInput(state); }
        catch (Exception error) when (error is MissingMemberException or TypeLoadException) { }
    }
}

/// <summary>Drag by the panel's title; reports screen positions so the panel can convert them in its own space.</summary>
internal sealed class PanelDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    internal Action<Vector2, Vector2, Camera?>? Moved;
    internal Action? Ended;
    public void OnBeginDrag(PointerEventData data) { }
    public void OnDrag(PointerEventData data) => Moved?.Invoke(data.position - data.delta, data.position, data.pressEventCamera);
    public void OnEndDrag(PointerEventData data) => Ended?.Invoke();
}
