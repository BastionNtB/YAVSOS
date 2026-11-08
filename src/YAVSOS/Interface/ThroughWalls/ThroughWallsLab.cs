using UnityEngine;
using YAVSOS.Core.Interface;

namespace YAVSOS.Interface;

/// <summary>
/// X66 (D163): Lab commands for the through-walls check, through the test bridge's debug/call_method. <see cref="Way"/>
/// forces one way for every glowing chest (so the marker can be checked with Zen.ModLib installed), <see cref="Status"/>
/// reports what shows, and <see cref="Look"/> turns the player toward a point for a screenshot. Lab build only; the
/// release DLL carries none of this. Unity game thread only.
/// </summary>
internal static class ThroughWallsLab
{
    /// <summary>"auto" (the release choice), "outline", "marker" or "none"; returns the status.</summary>
    public static string Way(string way)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Highlight == null) return "no plugin";
        plugin.Highlight.ThroughWalls.ForcedForLab = (way ?? "").Trim().ToLowerInvariant() switch
        {
            "auto" or "" => null,
            "outline" => ThroughWallsWay.Outline,
            "marker" => ThroughWallsWay.Marker,
            "none" => ThroughWallsWay.None,
            _ => plugin.Highlight.ThroughWalls.ForcedForLab
        };
        return Status();
    }

    /// <summary>What shows now, the Zen.ModLib check and any part that turned itself off.</summary>
    public static string Status()
    {
        var plugin = Plugin.Instance;
        if (plugin?.Highlight == null) return "no plugin";
        var walls = plugin.Highlight.ThroughWalls;
        var capability = walls.Outline.Capability;
        return $"way={(walls.ForcedForLab?.ToString() ?? "auto")}; setting={plugin.Settings.ShowThroughWalls.Value}; {walls.Status()}; " +
               $"capability={capability.State}: {capability.Diagnostic}";
    }

    /// <summary>Turns the player, and so the camera, toward (x, y, z).</summary>
    public static string Look(float x, float y, float z)
    {
        var player = Player.m_localPlayer;
        if (!player) return "no player";
        var look = new Vector3(x, y, z) - player.m_eye.position;
        player.SetLookDir(look.normalized);
        return $"looking at ({x:F2},{y:F2},{z:F2}), {look.magnitude:F1} m";
    }
}
