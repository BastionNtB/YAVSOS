using System;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P8-18 (D100): how long the search highlight stays on. Held while a search lights chests; a new search holds again;
/// clearing the search ends it; closing the chest (also by walking away) starts the countdown, 0 ending it at once.
/// Times are the caller's clock in seconds (Unity's unscaled time in game).
/// </summary>
public sealed class HighlightLinger
{
    public const int DefaultSeconds = 20, MaximumSeconds = 60;
    private bool held;
    private float until = float.NegativeInfinity;

    /// <summary>A search lights chests: on until it is cleared or the chest closes.</summary>
    public void Hold() { held = true; until = float.NegativeInfinity; }

    /// <summary>The chest closed: on for <paramref name="seconds"/> more (clamped to 0–60), if it was held.</summary>
    public void Release(float now, float seconds)
    {
        if (!held) return;
        held = false;
        var linger = float.IsNaN(seconds) ? DefaultSeconds : Math.Max(0, Math.Min(MaximumSeconds, seconds));
        until = linger == 0 ? float.NegativeInfinity : now + linger;
    }

    /// <summary>The search was cleared or replaced by nothing.</summary>
    public void End() { held = false; until = float.NegativeInfinity; }

    public bool Held => held;
    public bool On(float now) => held || now < until;
}
