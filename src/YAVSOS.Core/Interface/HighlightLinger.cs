using System;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P8-18 (D100): how long the search highlight stays on. Held while a search lights chests; a new search holds again;
/// closing the chest (also by walking away) starts the countdown, 0 ending it at once. D169 (#119): clearing the search
/// with the chest still open starts the same countdown, and closing the chest while it runs starts it over, so the
/// player who clears the box first (Esc, then Esc again to close) still has the whole time to walk to the chest.
/// Times are the caller's clock in seconds (Unity's unscaled time in game).
/// </summary>
public sealed class HighlightLinger
{
    public const int DefaultSeconds = 20, MaximumSeconds = 60;
    private bool held, letGo;
    private float until = float.NegativeInfinity;

    /// <summary>A search lights chests: on until it is cleared or the chest closes.</summary>
    public void Hold() { held = true; letGo = false; until = float.NegativeInfinity; }

    /// <summary>
    /// The chest closed: on for <paramref name="seconds"/> more (clamped to 0–60), if it was held, or if the search was
    /// cleared in this chest and its countdown still runs (D169: from now, not from the clearing).
    /// </summary>
    public void Release(float now, float seconds)
    {
        var restart = held || letGo && On(now);
        held = letGo = false;
        if (restart) Count(now, seconds);
    }

    /// <summary>D169: the search was cleared while the chest is open: on for <paramref name="seconds"/> more, if it was held.</summary>
    public void LetGo(float now, float seconds)
    {
        if (!held) return;
        held = false;
        Count(now, seconds);
        letGo = On(now);
    }

    /// <summary>Off now: the search ended for good (a new chest, a fault, the world ended).</summary>
    public void End() { held = letGo = false; until = float.NegativeInfinity; }

    public bool Held => held;
    public bool On(float now) => held || now < until;

    private void Count(float now, float seconds)
    {
        var linger = float.IsNaN(seconds) ? DefaultSeconds : Math.Max(0, Math.Min(MaximumSeconds, seconds));
        until = linger == 0 ? float.NegativeInfinity : now + linger;
    }
}
