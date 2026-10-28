using System;
using System.Collections.Generic;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>An axis-aligned rectangle in a parent's local space, y up (Unity UI convention).</summary>
public readonly struct PanelRect
{
    public float Left { get; }
    public float Bottom { get; }
    public float Width { get; }
    public float Height { get; }
    public float Right => Left + Width;
    public float Top => Bottom + Height;
    public PanelRect(float left, float bottom, float width, float height)
    {
        if (!(width >= 0) || !(height >= 0)) throw new ArgumentOutOfRangeException(nameof(width));
        Left = left; Bottom = bottom; Width = width; Height = height;
    }

    /// <summary>The bounds of transformed corners: a scaled or rotated window still yields its real screen area.</summary>
    public static PanelRect Around(IEnumerable<(float X, float Y)> points)
    {
        if (points == null) throw new ArgumentNullException(nameof(points));
        float left = float.PositiveInfinity, bottom = float.PositiveInfinity, right = float.NegativeInfinity, top = float.NegativeInfinity;
        foreach (var (x, y) in points)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); bottom = Math.Min(bottom, y); top = Math.Max(top, y);
        }
        if (left > right) throw new ArgumentException("No finite corner.", nameof(points));
        return new PanelRect(left, bottom, right - left, top - bottom);
    }

    /// <summary><see cref="Around(IEnumerable{ValueTuple{float, float}})"/> for one rectangle's four corners, without allocating (per-frame placement).</summary>
    public static PanelRect Around((float X, float Y) a, (float X, float Y) b, (float X, float Y) c, (float X, float Y) d)
    {
        float left = float.PositiveInfinity, bottom = float.PositiveInfinity, right = float.NegativeInfinity, top = float.NegativeInfinity;
        Add(a); Add(b); Add(c); Add(d);
        if (left > right) throw new ArgumentException("No finite corner.");
        return new PanelRect(left, bottom, right - left, top - bottom);

        void Add((float X, float Y) p)
        {
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y)) return;
            left = Math.Min(left, p.X); right = Math.Max(right, p.X); bottom = Math.Min(bottom, p.Y); top = Math.Max(top, p.Y);
        }
    }

    public PanelRect Union(PanelRect other)
    {
        var left = Math.Min(Left, other.Left); var bottom = Math.Min(Bottom, other.Bottom);
        return new PanelRect(left, bottom, Math.Max(Right, other.Right) - left, Math.Max(Top, other.Top) - bottom);
    }
}

/// <summary>
/// P6-03 (D062): where the rule panel goes. Beside the open chest window, top-aligned with it: right of it when it fits,
/// else left of it, else over it. The player's offset (dragged, kept in config) is applied last, x right and y down,
/// then the panel is kept inside the screen. Pure; the interface supplies rectangles in one local space.
/// </summary>
public static class PanelPlacement
{
    public const float Gap = 8;

    public static PanelRect Beside(PanelRect anchor, float width, float height, PanelRect area, float offsetX, float offsetY)
    {
        if (!(width >= 0) || !(height >= 0)) throw new ArgumentOutOfRangeException(nameof(width));
        var left = anchor.Right + Gap + width <= area.Right ? anchor.Right + Gap
            : anchor.Left - Gap - width >= area.Left ? anchor.Left - Gap - width
            : anchor.Right + Gap;
        var top = anchor.Top;
        if (Finite(offsetX)) left += offsetX;
        if (Finite(offsetY)) top -= offsetY;
        left = Math.Max(area.Left, Math.Min(left, area.Right - width));
        // A panel taller than the screen keeps its top (title and tabs) visible.
        top = Math.Min(area.Top, Math.Max(top, area.Bottom + height));
        return new PanelRect(left, top - height, width, height);
    }

    /// <summary>
    /// The offset that reproduces <paramref name="placed"/> from the unshifted place. Stored after a drag, so the panel
    /// keeps its place relative to the chest window rather than the screen, and a drag past the edge stores no excess.
    /// </summary>
    public static (float X, float Y) OffsetFor(PanelRect anchor, float width, float height, PanelRect area, PanelRect placed)
    {
        var natural = Beside(anchor, width, height, area, 0, 0);
        return (placed.Left - natural.Left, natural.Top - placed.Top);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>The checkbox drawn for a picker row (P6-03): empty, partly ticked (some subcategories) or ticked.</summary>
public enum CheckboxState { Empty, Partial, Ticked }

public static class CategoryCheckbox
{
    /// <summary>A category included through a ticked umbrella is drawn ticked but inherited (dimmed); clicking it still toggles.</summary>
    public static (CheckboxState State, bool Inherited) Of(CategorySelectionState state) => state switch
    {
        CategorySelectionState.On => (CheckboxState.Ticked, false),
        CategorySelectionState.Implied => (CheckboxState.Ticked, true),
        CategorySelectionState.Partial => (CheckboxState.Partial, false),
        _ => (CheckboxState.Empty, false)
    };
}
