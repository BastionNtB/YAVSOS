using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Configuration;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;
using YAVSOS.Integrations;

namespace YAVSOS.Interface;

/// <summary>
/// D121: shows where YAVSOS's own stash and cleanup put items. Each chest a move lands in plays the player's
/// <c>ChestArrivalEffect</c> (a game effect prefab) and glows gold for <c>HighlightSecondsAfterClose</c> seconds
/// (<see cref="ArrivalGlow{TKey}"/>), coloured through <see cref="ChestHighlight"/>, which owns chest painting and its
/// D058 check. AzuAutoStore's stash key keeps AzuAutoStore's own ping; YAVSOS adds nothing to it. D123: a chest where
/// something stays after cleanup that the player can act on is lit in <c>StaysColor</c> for the same time, with no
/// effect, and its label says why (<see cref="Stayed"/>; B2-06: kept as a message, formatted by ChestLabels).
/// Visual and local only: the effect is created with the game's <c>ZNetView.m_forceDisableInit</c>, so it never becomes
/// a networked object, and is destroyed after <see cref="EffectLifetime"/> seconds at most. No ZDO, inventory or rule
/// write. A failure turns off only the part that failed. Unity game thread only.
/// </summary>
internal sealed class ChestArrivals
{
    internal const float EffectLifetime = 5f, EffectHeight = 0.4f;
    private readonly ManualLogSource log;
    private readonly ChestHighlight highlight;
    private readonly PluginSettings settings;
    private readonly ArrivalGlow<Container> glow = new();
    private readonly Dictionary<Container, GameObject> targets = new();
    private readonly Dictionary<Container, Message> kept = new();
    private bool effectsOff, glowOff;
    private string missing = "";

    internal ChestArrivals(ManualLogSource log, ChestHighlight highlight, PluginSettings settings)
    { this.log = log; this.highlight = highlight; this.settings = settings; }

    /// <summary>A YAVSOS move put items into the chest.</summary>
    internal void Arrived(Container chest)
    {
        try
        {
            if (!chest || glowOff) return;
            kept.Remove(chest); // A new arrival replaces an earlier run's stays mark.
            highlight.Recheck(chest); // D122: a search may now find it holding a match.
            var seconds = highlight.Supported ? settings.HighlightSecondsAfterClose.Value : 0;
            if (glow.Arrive(chest, Time.unscaledTime, seconds)) Effect(chest);
            if (seconds > 0 && !targets.ContainsKey(chest)) targets[chest] = ChestHighlight.Target(chest);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { log.LogWarning("Chest arrival effect failed: " + error.GetType().Name + ": " + error.Message); }
    }

    /// <summary>D123: items stay in the chest after cleanup because no chest accepts them or those chests are full; <paramref name="why"/> is its label line.</summary>
    internal void Stayed(Container chest, Message why)
    {
        try
        {
            if (!chest || glowOff || !highlight.Supported) return;
            var seconds = settings.HighlightSecondsAfterClose.Value;
            if (seconds <= 0) return;
            glow.Arrive(chest, Time.unscaledTime, seconds, withEffect: false); // Nothing arrived; a later arrival still plays its effect.
            kept[chest] = why;
            if (!targets.ContainsKey(chest)) targets[chest] = ChestHighlight.Target(chest);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { log.LogWarning("Chest stays mark failed: " + error.GetType().Name + ": " + error.Message); }
    }

    /// <summary>D123: why a chest where something stays after cleanup is lit, for its label; null otherwise.</summary>
    internal (ChestHighlight.Kind Kind, Message Why)? Reason(Container chest) =>
        kept.TryGetValue(chest, out var why) && glow.Light(chest, Time.unscaledTime) != null ? (ChestHighlight.Kind.Stays, why) : null;

    /// <summary>The glow is colouring this chest now; the search highlight leaves it alone until the glow ends.</summary>
    internal bool Owns(GameObject target) => targets.Count != 0 && targets.ContainsValue(target);

    /// <summary>
    /// X51 B2-02: runs in the plugin's core update, before the cleanup and stash keys; a fault turns off only the glow
    /// and stays mark (D058), never the keys or the settings publish.
    /// </summary>
    internal void Update()
    {
        if (glowOff || glow.Count == 0) return;
        try
        {
#if YAVSOS_LAB
            YAVSOS.Transfers.FaultLab.Raise("glow"); // D088: lab-only display fault.
#endif
            Paint();
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            glowOff = true; DisplayFaults.Add(DisplayFaults.Glow);
            try { Clear(); } catch (Exception again) when (OptionalFailure.Recoverable(again)) { }
            log.LogWarning($"Chest glow after stash and cleanup turned off after {error.GetType().Name}: {error.Message}; stashing and cleanup keep working.");
        }
    }

    private void Paint()
    {
        var now = Time.unscaledTime;
        foreach (var chest in glow.Expire(now))
        {
            kept.Remove(chest);
            if (targets.TryGetValue(chest, out var target)) { targets.Remove(chest); highlight.ResetArrival(target); }
        }
        foreach (var chest in glow.Lit)
        {
            if (!chest || !targets.TryGetValue(chest, out var target) || !target) continue;
            var kind = kept.ContainsKey(chest) ? ChestHighlight.Kind.Stays : ChestHighlight.Kind.Holds;
            if (glow.Light(chest, now) is { } light && !highlight.PaintArrival(target, light.Gain, light.Tint, kind)) { Clear(); return; }
        }
    }

    /// <summary>The world ended or painting failed: every glow goes now.</summary>
    internal void Clear()
    {
        foreach (var target in targets.Values) highlight.ResetArrival(target);
        targets.Clear(); glow.Clear(); kept.Clear();
    }

    private void Effect(Container chest)
    {
        var name = ChestArrivalEffects.Prefab(settings.ChestArrivalEffect.Value);
        if (effectsOff || name.Length == 0 || !ZNetScene.instance) return;
        var prefab = ZNetScene.instance.GetPrefab(name);
        if (!prefab)
        {
            if (missing != name) { missing = name; log.LogWarning($"Chest arrival effect {name} not found in this game; chests still light up."); }
            return;
        }
        try { Spawn(prefab, chest.transform.position + Vector3.up * EffectHeight); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            effectsOff = true; // D058: a missing ZNetView.m_forceDisableInit lands here too; the glow keeps working.
            log.LogWarning("Chest arrival effects turned off after " + error.GetType().Name + ": " + error.Message + "; chests still light up.");
        }
    }

    // Local only, as the game makes its building ghost (Player.SetupPlacementGhost): with m_forceDisableInit the copy's
    // ZNetView removes itself in Awake, so no ZDO is made, and its TimedDestruction then destroys it locally. (The game's
    // EffectList, used by AzuAutoStore's ping, makes networked copies.)
    private static void Spawn(GameObject prefab, Vector3 position)
    {
        ZNetView.m_forceDisableInit = true;
        GameObject copy;
        try { copy = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity); }
        finally { ZNetView.m_forceDisableInit = false; }
        UnityEngine.Object.Destroy(copy, EffectLifetime);
    }
}
