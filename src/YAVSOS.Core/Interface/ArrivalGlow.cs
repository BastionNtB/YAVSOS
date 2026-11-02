using System;
using System.Collections.Generic;

namespace YAVSOS.Core.Interface;

/// <summary>
/// D121: the small game effect a chest plays when YAVSOS's own stash or cleanup puts items into it, picked in the
/// player's settings. Only these tested effects are offered: short, quiet and chest-sized.
/// </summary>
public enum ChestArrivalEffect { None, SmallItemPuff, SmelterPuff, FermenterPuff, LootSparkle, PickSparkle, AutoPickupShimmer }

public static class ChestArrivalEffects
{
    /// <summary>The game's effect prefab for the choice; "" for <see cref="ChestArrivalEffect.None"/>.</summary>
    public static string Prefab(ChestArrivalEffect effect) => effect switch
    {
        ChestArrivalEffect.SmallItemPuff => "vfx_Place_smallitem",
        ChestArrivalEffect.SmelterPuff => "vfx_smelter_addore",
        ChestArrivalEffect.FermenterPuff => "vfx_fermenter_add",
        ChestArrivalEffect.LootSparkle => "vfx_lootspawn",
        ChestArrivalEffect.PickSparkle => "vfx_pickable_pick",
        ChestArrivalEffect.AutoPickupShimmer => "vfx_auto_pickup",
        _ => "",
    };
}

/// <summary>
/// D121: when chests lit by an arrival glow, and how brightly. Each arrival flashes the chest (emission
/// <see cref="FlashGain"/>), settles to the search highlight's steady glow (<see cref="SteadyGain"/>, D100) and fades out
/// over the last <see cref="FadeSeconds"/> of the player's highlight time; a new arrival restarts it. An effect plays at
/// most once per chest every <see cref="EffectEverySeconds"/>, so a chest taking several stacks in one run puffs once.
/// Times are the caller's clock in seconds (Unity's unscaled time in game).
/// </summary>
public sealed class ArrivalGlow<TKey> where TKey : notnull
{
    public const float FlashSeconds = 0.6f, FadeSeconds = 0.5f, EffectEverySeconds = 1f;
    public const float FlashGain = 1f, SteadyGain = 0.4f;
    private readonly Dictionary<TKey, (float Start, float Until, float Effect, bool Lit)> entries = new();

    private readonly List<TKey> ended = new(), forget = new();

    /// <summary>
    /// Items arrived in the chest: it glows for <paramref name="seconds"/> (clamped to 0–<see cref="HighlightLinger.MaximumSeconds"/>;
    /// 0 = no glow). True when its effect should play now. <paramref name="withEffect"/> false (D123's stays mark, which
    /// has no effect) lights the chest without using up its effect window.
    /// </summary>
    public bool Arrive(TKey key, float now, float seconds, bool withEffect = true)
    {
        var glow = float.IsNaN(seconds) ? HighlightLinger.DefaultSeconds : Math.Max(0, Math.Min(HighlightLinger.MaximumSeconds, seconds));
        var known = entries.TryGetValue(key, out var old);
        var effect = withEffect && (!known || now - old.Effect >= EffectEverySeconds);
        // A chest still lit stays lit through a puff-only arrival, so its end is still reported once.
        entries[key] = (now, now + glow, effect ? now : known ? old.Effect : float.NegativeInfinity, glow > 0 || known && old.Lit);
        return effect;
    }

    /// <summary>
    /// How strongly the chest is lit now: the emission gain and how far its colour is tinted (0–1); null when it is not lit.
    /// </summary>
    public (float Gain, float Tint)? Light(TKey key, float now)
    {
        if (!entries.TryGetValue(key, out var e) || !e.Lit || now >= e.Until) return null;
        var age = now - e.Start;
        var gain = age < FlashSeconds ? FlashGain + (SteadyGain - FlashGain) * (age / FlashSeconds) : SteadyGain;
        var left = e.Until - now;
        var tint = left < FadeSeconds ? left / FadeSeconds : 1f;
        return (gain * tint, tint);
    }

    /// <summary>
    /// Chests whose glow ended since the last call, each reported once, for the caller to reset; chests are forgotten
    /// once their effect window has passed too. The list is reused: read it before the next call.
    /// </summary>
    public IReadOnlyList<TKey> Expire(float now)
    {
        ended.Clear(); forget.Clear();
        foreach (var pair in entries)
        {
            var e = pair.Value;
            if (e.Lit && now >= e.Until) ended.Add(pair.Key);
            if (now >= e.Until && now - e.Effect >= EffectEverySeconds) forget.Add(pair.Key);
        }
        foreach (var key in ended) { var e = entries[key]; entries[key] = (e.Start, e.Until, e.Effect, false); }
        foreach (var key in forget) entries.Remove(key);
        return ended;
    }

    /// <summary>Chests lit now, for painting.</summary>
    public IEnumerable<TKey> Lit
    {
        get { foreach (var pair in entries) if (pair.Value.Lit) yield return pair.Key; }
    }

    public int Count => entries.Count;
    public void Clear() => entries.Clear();
}
