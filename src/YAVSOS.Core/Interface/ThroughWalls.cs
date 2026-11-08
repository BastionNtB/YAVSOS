using System;
using System.Collections.Generic;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Interface;

/// <summary>X66 (D163): how a glowing chest is also shown through walls and other chests, beside the game's glow.</summary>
public enum ThroughWallsWay
{
    /// <summary>The glow only: the player turned it off, or both ways failed this session.</summary>
    None,
    /// <summary>Zen.ModLib's blinking outline, drawn over everything.</summary>
    Outline,
    /// <summary>YAVSOS's screen marker over the chest, with its place in its stack.</summary>
    Marker
}

/// <summary>
/// X66 (D163): which way shows one glowing chest. Zen.ModLib's outline when it is installed, its capability check passed
/// and it has not failed this session, unless this chest's outline isn't drawn (Zen.ModLib's own setting turned
/// outlines off, or another mod's highlight took or ended it); otherwise the marker. Pure and Unity-free.
/// </summary>
public static class ThroughWallsChoice
{
    /// <param name="on">The player's ShowThroughWalls setting.</param>
    /// <param name="outline">Zen.ModLib's highlight capability (absent when it isn't installed).</param>
    /// <param name="outlineFailed">The outline adapter turned itself off after an error this session.</param>
    /// <param name="outlineMissing">This chest's outline isn't drawn now.</param>
    /// <param name="markerFailed">The marker turned itself off after an error this session.</param>
    public static ThroughWallsWay For(bool on, OptionalCapabilityState outline, bool outlineFailed, bool outlineMissing, bool markerFailed)
    {
        if (!on) return ThroughWallsWay.None;
        if (outline == OptionalCapabilityState.Supported && !outlineFailed && !outlineMissing) return ThroughWallsWay.Outline;
        return markerFailed ? ThroughWallsWay.None : ThroughWallsWay.Marker;
    }

    /// <summary>
    /// Whether YAVSOS may select again the outlines another mod ended or took: Zen.ModLib keeps one highlight per piece,
    /// and its public <c>Highlight.Count</c> counts them all, so when it is no more than YAVSOS's own
    /// (<paramref name="held"/>), no other mod (ZenSign's search) highlights anything and selecting takes nothing from it.
    /// </summary>
    public static bool CanReclaim(int zenCount, int held) => zenCount >= 0 && zenCount <= Math.Max(0, held);
}

/// <summary>X66: how one chest is shown through walls: the way and its glow's kind (holds, accepts, stays), which sets its colour.</summary>
public readonly struct ThroughWallsLook : IEquatable<ThroughWallsLook>
{
    public ThroughWallsLook(ThroughWallsWay way, int kind) { Way = way; Kind = kind; }
    public ThroughWallsWay Way { get; }
    public int Kind { get; }
    public bool Equals(ThroughWallsLook other) => Way == other.Way && Kind == other.Kind;
    public override bool Equals(object? obj) => obj is ThroughWallsLook other && Equals(other);
    public override int GetHashCode() => ((int)Way * 397) ^ Kind;
    public override string ToString() => Way + "/" + Kind;
}

/// <summary>
/// X66: what to change so the chests shown through walls match the chests that glow: hide those no longer wanted or
/// shown another way, show the new ones, and recolour those whose kind changed (the outline is selected again in its
/// new colour rather than ended and started, so a component Zen.ModLib is about to destroy is never reused). A
/// <see cref="ThroughWallsWay.None"/> look counts as not shown. Hides come first. Pure.
/// </summary>
public sealed class ThroughWallsChanges<TKey> where TKey : notnull
{
    private ThroughWallsChanges(List<TKey> hide, List<KeyValuePair<TKey, ThroughWallsLook>> show, List<KeyValuePair<TKey, ThroughWallsLook>> recolor)
    { Hide = hide; Show = show; Recolor = recolor; }

    /// <summary>Shown now, and to hide: no longer glowing, or to be shown another way (that way is then in <see cref="Show"/>).</summary>
    public IReadOnlyList<TKey> Hide { get; }
    public IReadOnlyList<KeyValuePair<TKey, ThroughWallsLook>> Show { get; }
    /// <summary>Shown the same way, in another kind's colour.</summary>
    public IReadOnlyList<KeyValuePair<TKey, ThroughWallsLook>> Recolor { get; }
    public bool None => Hide.Count == 0 && Show.Count == 0 && Recolor.Count == 0;

    public static ThroughWallsChanges<TKey> Between(IReadOnlyDictionary<TKey, ThroughWallsLook> shown, IReadOnlyDictionary<TKey, ThroughWallsLook> wanted)
    {
        if (shown == null) throw new ArgumentNullException(nameof(shown));
        if (wanted == null) throw new ArgumentNullException(nameof(wanted));
        var hide = new List<TKey>(); var show = new List<KeyValuePair<TKey, ThroughWallsLook>>(); var recolor = new List<KeyValuePair<TKey, ThroughWallsLook>>();
        foreach (var pair in shown)
        {
            if (pair.Value.Way == ThroughWallsWay.None) continue;
            if (!wanted.TryGetValue(pair.Key, out var want) || want.Way != pair.Value.Way) hide.Add(pair.Key);
            else if (want.Kind != pair.Value.Kind) recolor.Add(new KeyValuePair<TKey, ThroughWallsLook>(pair.Key, want));
        }
        foreach (var pair in wanted)
        {
            if (pair.Value.Way == ThroughWallsWay.None) continue;
            if (!shown.TryGetValue(pair.Key, out var now) || now.Way != pair.Value.Way) show.Add(pair);
        }
        return new ThroughWallsChanges<TKey>(hide, show, recolor);
    }
}
