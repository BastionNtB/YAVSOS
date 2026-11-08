using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// X66 (D163): Zen.ModLib's blinking outline on a glowing chest, the look ZenSign's search shows (QuickOutline in its
/// "outline all" mode, drawn over everything, blinking every 0.5 s): <c>Highlight.Select(piece, -1, blink: true,
/// colour)</c>, later <c>Unselect(piece)</c>. Reflection behind <see cref="ZenHighlightCapability"/>; no reference to the
/// library. A failure logs once and turns the outline off for the session; the marker then shows instead.
/// <para>
/// Zen.ModLib keeps one highlight per piece and doesn't say whose it is (decompiled 1.14.22 and ZenSign 1.9.6,
/// 2026-11-07): a later Select replaces an earlier one, any Unselect ends it, and ZenSign's search ends every highlight
/// (<c>Highlight.None</c>) when its own time runs out. So, when the capability can read <c>Highlight.Count</c> and the
/// outline's colour: YAVSOS never unselects an outline whose colour is no longer its own (another mod took it, or ended
/// it), and treats such a chest as <see cref="Missing"/> (the marker shows it) until no other mod highlights anything,
/// then selects it again. A chest whose outline Zen.ModLib doesn't draw (its own setting EnableHighlightOutline is off)
/// is missing too. Zen.ModLib adds the outline component once and keeps it, with two extra materials on each renderer
/// of the chest; YAVSOS destroys the component it added when it unselects its own outline, as Zen.ModLib does itself
/// when a piece breaks, so chests lit once don't keep drawing the hidden outline. Visual and local only.
/// Unity game thread only.
/// </para>
/// </summary>
internal sealed class ZenOutline
{
    private sealed class Held
    {
        internal Piece Piece = null!;
        internal Color Color;
        internal bool Added;
    }
    private readonly ManualLogSource log;
    private ZenHighlightCapability.Api? api;
    private OptionalCapability? capability;
    private bool failed;
    private readonly Dictionary<Container, Held> held = new();
    // Missing outlines: true when another mod took or ended it (selected again once no other highlight is on), false
    // when Zen.ModLib didn't draw it (its setting), which isn't tried again for that chest while it glows.
    private readonly Dictionary<Container, bool> missing = new();

    internal ZenOutline(ManualLogSource log) { this.log = log; }

    /// <summary>Zen.ModLib's capability, checked once per game.</summary>
    internal OptionalCapabilityState State => Capability.State;

    internal OptionalCapability Capability
    {
        get
        {
            if (capability != null) return capability;
            string? version = null; Assembly? assembly = null;
            try
            {
                if (Chainloader.PluginInfos.TryGetValue(ZenHighlightCapability.PluginGuid, out var info) && info?.Instance != null)
                { version = info.Metadata.Version.ToString(); assembly = info.Instance.GetType().Assembly; }
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { version = null; }
            capability = ZenHighlightCapability.Probe(version, name => name switch
            {
                ZenHighlightCapability.Piece => typeof(Piece),
                ZenHighlightCapability.Color => typeof(Color),
                _ => assembly?.GetType(name, false)
            }, out api);
            if (capability.State != OptionalCapabilityState.Absent)
                log.LogInfo("X66 Zen.ModLib outline: " + capability.State + (capability.Diagnostic.Length != 0 ? ": " + capability.Diagnostic : ""));
            return capability;
        }
    }

    /// <summary>The outline turned itself off after an error this session.</summary>
    internal bool Failed => failed;

    /// <summary>This chest's outline isn't drawn now: the marker shows it instead.</summary>
    internal bool Missing(Container chest) => missing.ContainsKey(chest);

    /// <summary>YAVSOS's own outline is on this chest (selected, and not taken or ended by another mod as far as it can tell).</summary>
    internal bool Holds(Container chest) => !failed && held.ContainsKey(chest);

    internal void Show(Container chest, Color color)
    {
        if (failed || api == null || !chest) return;
        var piece = chest.GetComponentInParent<Piece>();
        if (!piece) { missing[chest] = false; return; }
        color.a = 1;
        Try(() =>
        {
            var added = api.Outline != null && !piece!.GetComponent(api.Outline);
            api.Select.Invoke(null, new object?[] { piece, -1f, true, color, null });
            var mine = new Held { Piece = piece!, Color = color, Added = added };
            // Zen.ModLib's outlines are off (its EnableHighlightOutline): Select made no outline, or left an old one's colour.
            if (api.Outline != null && !Mine(mine)) { if (!failed) missing[chest] = false; return; }
            held[chest] = mine;
        });
    }

    /// <summary>The chest's kind changed: selected again in the new colour (one highlight per piece, so this replaces it).</summary>
    internal void Recolor(Container chest, Color color)
    {
        if (failed || api == null || !held.TryGetValue(chest, out var mine) || !mine.Piece) return;
        if (!Mine(mine)) { Lose(chest); return; }
        color.a = 1;
        Try(() => { api.Select.Invoke(null, new object?[] { mine.Piece, -1f, true, color, null }); mine.Color = color; });
    }

    /// <summary>The chest no longer glows, or shows another way: its own outline ends; another mod's stays.</summary>
    internal void Hide(Container chest)
    {
        if (!held.TryGetValue(chest, out var mine)) return;
        held.Remove(chest);
        if (failed || api == null || !mine.Piece || !Mine(mine)) return;
        Try(() =>
        {
            api.Unselect.Invoke(null, new object?[] { mine.Piece });
            if (mine.Added && api.Outline != null && mine.Piece.GetComponent(api.Outline) is { } outline) UnityEngine.Object.Destroy(outline);
        });
    }

    /// <summary>The chest stopped glowing: forget that its outline was missing, so it is tried again next time.</summary>
    internal void Forget(Container chest) => missing.Remove(chest);

    /// <summary>
    /// Now and then: outlines another mod took or ended become missing, and missing ones that another mod ended are
    /// selected again once no other mod highlights anything. Returns whether anything changed.
    /// </summary>
    internal bool Check()
    {
        if (failed || api == null || !api.Reads || held.Count == 0 && missing.Count == 0) return false;
        var changed = false;
        List<Container>? lost = null;
        foreach (var pair in held) if (!pair.Key || !pair.Value.Piece || !Mine(pair.Value)) (lost ??= new List<Container>()).Add(pair.Key!);
        if (lost != null) foreach (var chest in lost) { Lose(chest); changed = true; }
        if (failed || !missing.ContainsValue(true)) return changed;
        int count = -1;
        Try(() => count = (int)api.Count!.Invoke(null, null));
        if (failed || !ThroughWallsChoice.CanReclaim(count, held.Count)) return changed;
        List<Container>? free = null;
        foreach (var pair in missing) if (pair.Value) (free ??= new List<Container>()).Add(pair.Key);
        if (free != null) foreach (var chest in free) { missing.Remove(chest); changed = true; } // Shown again as outlines next frame.
        return changed;
    }

    // Another mod took or ended this outline: never unselect it; the marker shows the chest until it can be had again.
    private void Lose(Container chest)
    {
        held.Remove(chest);
        if (chest) missing[chest] = true;
    }

    // Whether the piece's outline still has YAVSOS's colour: Zen.ModLib sets the colour on every Select and Unselect.
    private bool Mine(Held mine)
    {
        if (api?.Outline == null || api.OutlineColor == null) return true; // Can't tell: as the prototype, it is ours.
        var outline = mine.Piece.GetComponent(api.Outline);
        if (!outline) return false;
        Color color = default;
        Try(() => color = (Color)api.OutlineColor.Invoke(outline, null));
        return !failed && Near(color, mine.Color);
    }

    private static bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f;

    private void Try(Action action)
    {
        if (failed) return;
        try { action(); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            failed = true;
            log.LogWarning($"Zen.ModLib outline turned off after {cause.GetType().Name}: {cause.Message}; glowing chests show a marker instead.");
        }
    }

    /// <summary>The world ended: YAVSOS's own outlines end.</summary>
    internal void Clear()
    {
        foreach (var chest in new List<Container>(held.Keys)) Hide(chest);
        held.Clear(); missing.Clear();
    }

    internal string Status() => $"zen={State}; held={held.Count}; missing={missing.Count}{(failed ? "; failed" : "")}";
}
