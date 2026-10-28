using System;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Cleanup;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-04: the latest stash or cleanup result, published once per run when it finishes. The detail
/// (<see cref="CleanupReport.Detail"/>) is built then, never per frame. Read only. Unity game thread only.
/// </summary>
internal sealed class RunResults
{
    /// <summary>How long a result stays up while the inventory is closed.</summary>
    internal const float Seconds = 12;
    internal ResultSummary? Latest { get; private set; }
    /// <summary>Bumped on every publish, so the panel rebuilds its lines only then.</summary>
    internal int Revision { get; private set; }
    private float left;

    internal void Publish(CleanupReport report)
    {
        Latest = report.Detail(CleanupRunner.Name);
        Revision++;
        left = Latest.Empty ? 0 : Seconds; // An empty run has nothing to add to its one-line message.
    }

    internal void Dismiss() => left = 0;

    /// <summary>The countdown is held while the inventory is open, so the result can be read and closed there.</summary>
    internal bool Visible(bool held, float elapsed)
    {
        if (left > 0 && !held) left -= elapsed;
        return left > 0 && Latest != null;
    }
}

/// <summary>
/// P6-04: a short Jotunn wood panel at the bottom centre of Jotunn's overlay canvas, so it shows over the
/// inventory screen too: what moved and what stayed, with reasons and item names. A Close button dismisses it; the
/// one-line message in the corner is shown either way. Lines are rebuilt only when a new result is published.
/// </summary>
internal sealed class ResultPanel
{
    internal const float Width = 520;
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
            ui.Button(head, "Close", results.Dismiss, 80);
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
        title.text = "YAVSOS " + summary.Title.ToLowerInvariant() + " result";
        JotunnWidgets.Clear(lines);
        foreach (var line in summary.Lines)
            ui.Label(lines, line.Text, JotunnWidgets.SmallSize, line.Kind is ResultKind.Refused or ResultKind.Unconfirmed ? ui.Orange : (Color?)null);
        // Lines past the bound are still counted in the one-line summary.
        more.gameObject.SetActive(summary.MoreLines != 0);
        if (summary.MoreLines != 0) more.text = summary.Headline;
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
    }

    internal void Hide() { if (Alive && root.gameObject.activeSelf) root.gameObject.SetActive(false); }

    internal void Destroy() { if (Alive) UnityEngine.Object.Destroy(root.gameObject); }
}

/// <summary>
/// The stack under the pointer in one of the inventory screen's grids, through the public
/// <c>InventoryGrid.GetItem(Vector2i)</c> screen-point lookup (works with grids other mods scale or rotate). Null when
/// nothing is there or anything differs. Unity game thread only.
/// </summary>
internal static class GridHover
{
    internal static ItemDrop.ItemData? Item(InventoryGrid? grid)
    {
        if (!grid) return null;
        try
        {
            var pointer = ZInput.pointerPosition;
            return grid!.GetItem(new Vector2i((int)pointer.x, (int)pointer.y));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
}
