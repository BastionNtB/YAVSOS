using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-04: a short Jotunn wood panel at the bottom center of Jotunn's overlay canvas, so it shows over the
/// inventory screen too: what moved and what stayed, with reasons and item names. It closes by itself after the player's
/// ResultPanelSeconds (P9-23), or sooner with its Close button; the one-line message in the corner is shown either way.
/// X55: after something moved, a line per chest or drawer that received items, indented, within the line bound.
/// Lines are rebuilt only when a new result is published, and formatted from each line's fields (B2-09). A language
/// change destroys the panel and the next frame builds it again (RuleControls.LanguageChanged).
/// </summary>
internal sealed class ResultPanel
{
    internal const float Width = 520;
    private const int PlaceIndent = 14;
    private readonly JotunnWidgets ui;
    private readonly RunResults results;
    private readonly RectTransform root, lines;
    private readonly Text title, more;
    private int shown = -1;
    private Canvas? canvas;
    private readonly JotunnWidgets.ScaleWatch scale = new();

    internal ResultPanel(JotunnWidgets ui, Transform parent, RunResults results)
    {
        this.ui = ui; this.results = results;
        root = ui.Panel(parent, Width, "YAVSOS result panel");
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0); root.pivot = new Vector2(0.5f, 0); root.anchoredPosition = new Vector2(0, 170);
        root.gameObject.SetActive(false);
        try
        {
            var head = ui.Row(root);
            title = ui.Label(head, "", JotunnWidgets.TextSize + 2, ui.Orange);
            ui.Button(head, Texts.Get("yavsos_result_close"), results.Dismiss, 80);
            lines = ui.Column(root, "Lines", 2);
            more = ui.Label(root, "", JotunnWidgets.SmallSize, ui.Faint);
        }
        catch { UnityEngine.Object.Destroy(root.gameObject); throw; }
    }

    internal bool Alive => root;

    internal void Show()
    {
        if (!root.gameObject.activeSelf) { root.gameObject.SetActive(true); root.SetAsLastSibling(); }
        if (!canvas) canvas = root.GetComponentInParent<Canvas>(true);
        scale.Check(root.gameObject, canvas);
        if (shown == results.Revision) return;
        shown = results.Revision;
        var summary = results.Latest!;
        // B2-09: one title message per run kind; nothing is lower-cased in code.
        title.text = Texts.Get(summary.Title);
        JotunnWidgets.Clear(lines);
        RectTransform? places = null;
        foreach (var line in summary.Lines)
        {
            // X55: the chests and drawers that received items sit indented under the moved line, in one group.
            var place = line.Kind is ResultKind.Place or ResultKind.MorePlaces;
            if (place && places == null)
            {
                places = ui.Column(lines, "Places", 2);
                places.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(PlaceIndent, 0, 0, 0);
            }
            ui.Label(place ? places! : lines, ResultSummary.Bound(Texts.Get(line.Text), ResultSummary.MaximumText), JotunnWidgets.SmallSize,
                line.Kind switch { ResultKind.Refused or ResultKind.Unconfirmed => ui.Orange, ResultKind.MorePlaces => ui.Faint, _ => (Color?)null });
        }
        // Lines past the bound are still counted in the one-line summary.
        more.gameObject.SetActive(summary.MoreLines != 0);
        if (summary.MoreLines != 0) more.text = Texts.Get(summary.Headline);
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
    }

    internal void Hide() { if (Alive && root.gameObject.activeSelf) root.gameObject.SetActive(false); }

    internal void Destroy() { if (Alive) UnityEngine.Object.Destroy(root.gameObject); }
}
