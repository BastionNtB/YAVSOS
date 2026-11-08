using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Configuration;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// X66 (#79, D163): shows every glowing chest through walls and other chests too, beside the game's glow, which stays:
/// the chests the search lights (holds, accepts) and those an arrival or stays mark lights after YAVSOS's stash or
/// cleanup (<see cref="ChestArrivals"/>), each in its glow's colour. Zen.ModLib's outline when it is installed and passes
/// its check (<see cref="ZenOutline"/>), otherwise the screen marker with the chest's place in its stack
/// (<see cref="ChestMarkers"/>); the choice per chest is <see cref="ThroughWallsChoice.For"/>. The player's
/// ShowThroughWalls setting turns both off. Owned and driven by <see cref="ChestHighlight"/>, the one owner of chest
/// colouring, once a frame from the plugin's LateUpdate. Visual and local only: no ZDO, inventory or rule write. A
/// fault turns off only what failed: the outline falls back to the marker; the marker, or this whole part, turns off for
/// the session with one warning and the Display mark. Unity game thread only.
/// </summary>
internal sealed class ChestThroughWalls
{
    internal const float CheckSeconds = 1f;
    private readonly ManualLogSource log;
    private readonly ZenOutline outline;
    private readonly ChestMarkers markers;
    private readonly Dictionary<Container, ThroughWallsLook> shown = new();
    private readonly Dictionary<Container, ThroughWallsLook> wanted = new();
    private readonly List<Container> keys = new();
    private bool markersFailed, off;
    private float nextCheck;
#if YAVSOS_LAB
    /// <summary>Lab only (ThroughWallsLab): forces one way for every chest to test it, null for the normal choice.</summary>
    internal ThroughWallsWay? ForcedForLab;
#endif

    internal ChestThroughWalls(ManualLogSource log)
    {
        this.log = log;
        outline = new ZenOutline(log);
        markers = new ChestMarkers(MarkersFailed);
    }

    internal bool Off => off;
    /// <summary>Nothing shows through walls, so a frame without glowing chests has nothing to do.</summary>
    internal bool Idle => off || shown.Count == 0;
    internal ZenOutline Outline => outline;

    /// <summary>
    /// Once a frame: brings what shows through walls in line with the chests that glow (<paramref name="lit"/>: the
    /// search's, then the arrivals', a later entry for the same chest winning, as the arrival glow owns a chest).
    /// </summary>
    internal void Frame(IEnumerable<(Container Chest, ChestHighlight.Kind Kind)> lit, PluginSettings? settings)
    {
        if (off) return;
        try
        {
            wanted.Clear();
            var on = settings?.ShowThroughWalls.Value != false;
            foreach (var (chest, kind) in lit)
                if (chest && on) wanted[chest] = new ThroughWallsLook(ThroughWallsWay.None, (int)kind); // Way chosen below.
            if (wanted.Count == 0 && shown.Count == 0) return;
            var now = Time.unscaledTime;
            if (now >= nextCheck) { nextCheck = now + CheckSeconds; outline.Check(); }
            var state = wanted.Count != 0 ? outline.State : Core.Classification.OptionalCapabilityState.Absent;
            keys.Clear(); keys.AddRange(wanted.Keys);
            foreach (var chest in keys)
            {
                var way = ThroughWallsChoice.For(true, state, outline.Failed, outline.Missing(chest), markersFailed);
#if YAVSOS_LAB
                if (ForcedForLab is { } forced)
                    way = forced == ThroughWallsWay.Outline && (outline.Failed || outline.Missing(chest) || state != Core.Classification.OptionalCapabilityState.Supported)
                        ? ThroughWallsWay.None : forced;
#endif
                wanted[chest] = new ThroughWallsLook(way, wanted[chest].Kind);
            }
            // An outline another mod took or ended, or one selected again after that, is no longer YAVSOS's: shown anew.
            keys.Clear();
            foreach (var pair in shown)
                if (!pair.Key || pair.Value.Way == ThroughWallsWay.Outline && !outline.Holds(pair.Key!)) keys.Add(pair.Key!);
            foreach (var chest in keys)
            {
                var way = shown[chest].Way; shown.Remove(chest);
                if (way == ThroughWallsWay.Outline) outline.Hide(chest); else markers.Hide(chest); // A chest gone: drops what YAVSOS kept for it.
                if (!chest || !wanted.ContainsKey(chest!)) outline.Forget(chest!);
            }
            var changes = ThroughWallsChanges<Container>.Between(shown, wanted);
            if (!changes.None) Apply(changes);
            markers.Frame(now);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            off = true; DisplayFaults.Add(DisplayFaults.ThroughWalls);
            log.LogWarning($"Showing glowing chests through walls turned off after {error.GetType().Name}: {error.Message}; chests still glow.");
            try { Clear(); } catch (Exception again) when (OptionalFailure.Recoverable(again)) { }
        }
    }

    private void Apply(ThroughWallsChanges<Container> changes)
    {
        foreach (var chest in changes.Hide)
        {
            var way = shown[chest].Way; shown.Remove(chest);
            if (way == ThroughWallsWay.Outline) outline.Hide(chest); else markers.Hide(chest);
            if (!wanted.ContainsKey(chest)) outline.Forget(chest);
        }
        foreach (var pair in changes.Recolor)
        {
            var color = ChestHighlight.ColorOf((ChestHighlight.Kind)pair.Value.Kind);
            if (pair.Value.Way == ThroughWallsWay.Outline) outline.Recolor(pair.Key, color); else markers.Recolor(pair.Key, color);
            shown[pair.Key] = pair.Value;
        }
        Dictionary<Container, StackPlace>? stacks = null;
        foreach (var pair in changes.Show)
        {
            var chest = pair.Key; var color = ChestHighlight.ColorOf((ChestHighlight.Kind)pair.Value.Kind);
            if (pair.Value.Way == ThroughWallsWay.Outline)
            {
                outline.Show(chest, color);
                if (!outline.Holds(chest)) continue; // Not drawn: the next frame shows the marker.
            }
            else
            {
                if (markersFailed) continue;
                // X65: the places of the chests getting a marker this frame, grouped once.
                if (stacks == null)
                {
                    var marked = new List<Container>();
                    foreach (var show in changes.Show) if (show.Value.Way == ThroughWallsWay.Marker) marked.Add(show.Key);
                    stacks = ChestStackPlaces.Of(marked);
                }
                stacks.TryGetValue(chest, out var place);
                markers.Show(chest, color, ChestPlaceText.Marker(place));
            }
            shown[chest] = pair.Value;
        }
    }

    private void MarkersFailed(Exception error)
    {
        if (markersFailed) return;
        markersFailed = true; DisplayFaults.Add(DisplayFaults.ThroughWalls);
        log.LogWarning($"Chest markers turned off after {error.GetType().Name}: {error.Message}; chests still glow.");
        markers.Clear();
        foreach (var chest in new List<Container>(shown.Keys)) if (shown[chest].Way == ThroughWallsWay.Marker) shown.Remove(chest);
    }

    /// <summary>The world ended: YAVSOS's outlines and markers go.</summary>
    internal void Clear()
    {
        outline.Clear(); markers.Clear(); shown.Clear(); wanted.Clear();
    }

    internal string Status() => $"shown={shown.Count}; markers={markers.Count}; {outline.Status()}{(markersFailed ? "; markers failed" : "")}{(off ? "; off" : "")}";
}
