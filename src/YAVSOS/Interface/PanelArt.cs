using System;
using UnityEngine;

namespace YAVSOS.Interface;

/// <summary>
/// Small shapes drawn once in code (check, cross, dash, disc), so the grid marks and the status marks (D071) need no
/// game or Jotunn sprite, and no glyph the game's font may lack. Unity game thread only.
/// </summary>
internal static class PanelArt
{
    private const int Size = 32;
    private static Sprite? check, cross, dash, disc;
    internal static Sprite Check => check ??= Lines((5, 16, 13, 7), (13, 7, 27, 25));
    internal static Sprite Cross => cross ??= Lines((6, 6, 26, 26), (6, 26, 26, 6));
    internal static Sprite Dash => dash ??= Lines((8, 16, 24, 16));
    /// <summary>A filled circle with a soft edge: the status marks' backing.</summary>
    internal static Sprite Disc => disc ??= Draw(Size, false, (x, y) =>
    {
        var d = Mathf.Sqrt((x - 15.5f) * (x - 15.5f) + (y - 15.5f) * (y - 15.5f));
        return Mathf.Clamp01(15.5f - d);
    });

    private static Sprite? star;
    /// <summary>A filled five-pointed star, for when the game's own star (<see cref="StarImage"/>) cannot be found.</summary>
    internal static Sprite DrawnStar => star ??= Draw(Size * 2, true, (x, y) =>
    {
        // Inside the star: the point lies inside the polygon of 10 alternating outer and inner corners.
        float px = (x + 0.5f) / 2, py = (y + 0.5f) / 2; var inside = false;
        for (int i = 0, j = 9; i < 10; j = i++)
        {
            var (xi, yi) = Corner(i); var (xj, yj) = Corner(j);
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside ? 1 : 0;
    });
    private static (float, float) Corner(int i)
    {
        var radius = i % 2 == 0 ? 15f : 6.2f; var angle = Mathf.PI / 2 + i * Mathf.PI / 5;
        return (16 + radius * Mathf.Cos(angle), 16 + radius * Mathf.Sin(angle));
    }

    private static bool looked;
    private static (Sprite Back, Color BackColor, Sprite? Front, Color FrontColor)? gameStar;

    /// <summary>
    /// One star the way the game draws a creature's level (EnemyHud's "level_2/star": an outline image with a filled
    /// image inside), sized for a row; the drawn star if a game update moved it (D058). P6-21 upgrade levels.
    /// </summary>
    internal static RectTransform StarImage(Transform parent, float size)
    {
        if (!looked)
        {
            looked = true;
            try
            {
                var hud = EnemyHud.instance;
                var found = hud && hud!.m_baseHud ? hud.m_baseHud.transform.Find("level_2/star") : null;
                var back = found ? found!.GetComponent<UnityEngine.UI.Image>() : null;
                var front = found && found!.childCount > 0 ? found.GetChild(0).GetComponent<UnityEngine.UI.Image>() : null;
                if (back && back!.sprite) gameStar = (back.sprite, back.color, front ? front!.sprite : null, front ? front!.color : Color.white);
            }
            catch (Exception error) when (error is NullReferenceException or MissingMemberException) { gameStar = null; }
        }
        var go = new GameObject("Star", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.LayoutElement));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        var layout = go.GetComponent<UnityEngine.UI.LayoutElement>();
        layout.minWidth = layout.preferredWidth = layout.minHeight = layout.preferredHeight = size;
        var image = go.GetComponent<UnityEngine.UI.Image>(); image.raycastTarget = false; image.preserveAspect = true;
        if (gameStar is { } game)
        {
            image.sprite = game.Back; image.color = game.BackColor;
            if (game.Front)
            {
                var inner = new GameObject("Fill", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                var innerRect = (RectTransform)inner.transform;
                innerRect.SetParent(rect, false);
                innerRect.anchorMin = Vector2.zero; innerRect.anchorMax = Vector2.one; innerRect.offsetMin = innerRect.offsetMax = Vector2.zero;
                var fill = inner.GetComponent<UnityEngine.UI.Image>(); fill.sprite = game.Front; fill.color = game.FrontColor;
                fill.raycastTarget = false; fill.preserveAspect = true;
            }
        }
        else { image.sprite = DrawnStar; image.color = new Color(1f, 0.82f, 0.3f); }
        return rect;
    }

    private static Sprite Lines(params (float X0, float Y0, float X1, float Y1)[] lines)
    {
        var on = new bool[Size * Size];
        foreach (var (x0, y0, x1, y1) in lines)
            for (var t = 0f; t <= 1f; t += 0.01f)
            {
                int cx = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), cy = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                for (var dx = -2; dx <= 2; dx++) for (var dy = -2; dy <= 2; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && y >= 0 && x < Size && y < Size) on[y * Size + x] = true;
                }
            }
        return Draw(Size, false, (x, y) => on[y * Size + x] ? 1 : 0);
    }

    // Smoothing levels (mipmaps) keep a sprite drawn much smaller than its texture from blurring or shimmering.
    private static Sprite Draw(int size, bool smooth, Func<int, int, float> alpha)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, smooth) { filterMode = smooth ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) pixels[y * size + x] = new Color(1, 1, 1, alpha(x, y));
        texture.SetPixels(pixels); texture.Apply(smooth);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}

/// <summary>
/// The game's own tooltip on one of YAVSOS's widgets: the prefab the inventory slots use, filled just before it shows
/// (<see cref="TipFill"/> runs first and moves it to the top canvas). Without the prefab (a game change) no tooltip shows.
/// </summary>
internal static class GameTooltip
{
    internal static UITooltip? Attach(GameObject go, Func<(string Topic, string Text)?> fill)
    {
        var prefab = InventoryGui.instance && InventoryGui.instance.m_playerGrid && InventoryGui.instance.m_playerGrid.m_elementPrefab
            ? InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<UITooltip>()?.m_tooltipPrefab : null;
        var tipFill = go.GetComponent<TipFill>() ?? go.AddComponent<TipFill>(); // Must be added before the UITooltip.
        if (!prefab) return null;
        var tip = go.AddComponent<UITooltip>(); tip.m_tooltipPrefab = prefab;
        tipFill.Fill = () => { if (fill() is { } shown) tip.Set(shown.Topic, shown.Text); };
        return tip;
    }
}
