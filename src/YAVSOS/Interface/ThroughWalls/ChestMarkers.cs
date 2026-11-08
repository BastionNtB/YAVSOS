using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Core.Messages;

namespace YAVSOS.Interface;

/// <summary>
/// X66 (D163): a marker on the screen over each glowing chest that Zen.ModLib's outline doesn't show, like a waypoint:
/// a pulsing diamond in the chest's colour, with the chest's place in its stack under it ("2 of 3",
/// <c>yavsos_marker_place</c>). Plain Unity UI on an overlay canvas under the game's HUD and inventory screen, so it
/// shows through walls and other chests on its own; hidden while the chest is behind the camera. Placed just before the
/// canvases draw (<c>Canvas.willRenderCanvases</c>), after the camera moved this frame, so it doesn't trail a turn. No
/// shader or asset bundle. A failure turns the markers off for the session (the caller logs it). Visual and local only.
/// Unity game thread only.
/// </summary>
internal sealed class ChestMarkers
{
    internal const float Size = 18, Height = 0.5f;
    private sealed class Marker
    {
        internal RectTransform Root = null!;
        internal Image Diamond = null!;
        internal Text? Tag;
        internal Container Chest = null!;
        internal Color Color;
    }
    private readonly Dictionary<Container, Marker> markers = new();
    private readonly Action<Exception> failed;
    private Canvas? canvas;
    private bool hooked;

    /// <param name="failed">Called once when placing the markers fails inside the canvas callback.</param>
    internal ChestMarkers(Action<Exception> failed) { this.failed = failed; }

    internal int Count => markers.Count;

    /// <summary>Shows a marker over the chest in <paramref name="color"/>, with <paramref name="tag"/> under it (null: none).</summary>
    internal void Show(Container chest, Color color, Message? tag)
    {
        Hide(chest);
        color.a = 1;
        var go = new GameObject("YAVSOS chest marker", typeof(RectTransform));
        var rect = (RectTransform)go.transform; rect.SetParent(Canvas(), false); rect.sizeDelta = new Vector2(Size, Size);
        Image Square(string name, float size, Color tint)
        {
            var part = new GameObject(name, typeof(RectTransform), typeof(Image));
            var r = (RectTransform)part.transform; r.SetParent(rect, false); r.sizeDelta = new Vector2(size, size);
            r.localRotation = Quaternion.Euler(0, 0, 45);
            var image = part.GetComponent<Image>(); image.color = tint; image.raycastTarget = false;
            return image;
        }
        Square("Edge", Size + 4, new Color(0.08f, 0.06f, 0.04f, 0.9f));
        var marker = new Marker { Root = rect, Diamond = Square("Diamond", Size, color), Chest = chest, Color = color };
        var widgets = new JotunnWidgets();
        if (tag != null && widgets.Ready)
        {
            marker.Tag = widgets.Label(rect, Texts.Escape(Texts.Plain(tag)), JotunnWidgets.SmallSize, color, false);
            var t = (RectTransform)marker.Tag.transform;
            t.sizeDelta = new Vector2(120, 22); t.anchoredPosition = new Vector2(0, -Size - 4);
            marker.Tag.alignment = TextAnchor.MiddleCenter; marker.Tag.raycastTarget = false;
        }
        markers[chest] = marker;
        go.SetActive(false); // Placed (and shown) before the canvases draw.
        if (!hooked) { UnityEngine.Canvas.willRenderCanvases += Place; hooked = true; }
    }

    /// <summary>The chest's kind changed: the same marker in the new colour.</summary>
    internal void Recolor(Container chest, Color color)
    {
        if (!markers.TryGetValue(chest, out var marker)) return;
        color.a = 1; marker.Color = color;
        if (marker.Tag) marker.Tag!.color = color;
    }

    internal void Hide(Container chest)
    {
        if (!markers.TryGetValue(chest, out var marker)) return;
        if (marker.Root) UnityEngine.Object.Destroy(marker.Root.gameObject);
        markers.Remove(chest);
        if (markers.Count == 0) Unhook();
    }

    /// <summary>Once a frame while any marker shows: the diamonds pulse.</summary>
    internal void Frame(float now)
    {
        if (markers.Count == 0) return;
        var pulse = 0.7f + 0.3f * Mathf.Sin(now * Mathf.PI * 2);
        foreach (var marker in markers.Values)
        {
            if (!marker.Diamond) continue;
            var c = marker.Color; c.a = pulse; marker.Diamond.color = c;
        }
    }

    // Each frame just before the canvases draw: after the game camera moved, so markers don't trail a turn by a frame.
    private void Place()
    {
        try
        {
            var camera = Utils.GetMainCamera();
            foreach (var marker in markers.Values)
            {
                if (!marker.Root) continue;
                var visible = camera && marker.Chest;
                var point = visible ? camera!.WorldToScreenPoint(marker.Chest!.transform.position + Vector3.up * Height) : Vector3.zero;
                visible &= point.z > 0;
                if (marker.Root.gameObject.activeSelf != visible) marker.Root.gameObject.SetActive(visible);
                if (visible) marker.Root.position = new Vector3(Mathf.Round(point.x), Mathf.Round(point.y), 0);
            }
        }
        catch (Exception error) when (YAVSOS.Integrations.OptionalFailure.Recoverable(error)) { Unhook(); failed(error); }
    }

    private Transform Canvas()
    {
        if (canvas) return canvas!.transform;
        var go = new GameObject("YAVSOS chest markers", typeof(Canvas));
        UnityEngine.Object.DontDestroyOnLoad(go);
        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10; // Under the game's HUD and inventory screen.
        return go.transform;
    }

    private void Unhook()
    {
        if (hooked) { UnityEngine.Canvas.willRenderCanvases -= Place; hooked = false; }
    }

    /// <summary>The world ended, or the markers failed: every marker goes.</summary>
    internal void Clear()
    {
        foreach (var marker in markers.Values) if (marker.Root) UnityEngine.Object.Destroy(marker.Root.gameObject);
        markers.Clear(); Unhook();
    }
}
