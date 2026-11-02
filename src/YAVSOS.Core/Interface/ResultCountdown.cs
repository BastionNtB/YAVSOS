using System;

namespace YAVSOS.Core.Interface;

/// <summary>
/// P9-23: how long the stash or cleanup result panel stays up. A finished run's result restarts it and the Close button
/// stops it. It counts the time it has been shown against the player's setting, read each frame, so a changed setting
/// applies to the panel already up. With the hold setting on, it doesn't count while the inventory is open; by default it
/// counts either way. Times are seconds of the caller's clock (Unity's unscaled frame time in game).
/// </summary>
public sealed class ResultCountdown
{
    public const int DefaultSeconds = 10, MinimumSeconds = 3, MaximumSeconds = 60;
    private bool running;
    private float shown;

    /// <summary>A run published a result with lines: shown again for the full time.</summary>
    public void Restart() { running = true; shown = 0; }

    /// <summary>Closed with its button, at the world's end, or a result with nothing to list.</summary>
    public void Stop() => running = false;

    /// <summary>
    /// Advances by one frame and says whether the panel is still up. <paramref name="seconds"/> is clamped to 3–60
    /// (NaN reads as the default); <paramref name="holdWhileOpen"/> is the hold setting.
    /// </summary>
    public bool Tick(float elapsed, float seconds, bool inventoryOpen, bool holdWhileOpen)
    {
        if (!running) return false;
        if (!(inventoryOpen && holdWhileOpen) && elapsed > 0) shown += elapsed;
        if (shown >= Limit(seconds)) running = false;
        return running;
    }

    public bool Running => running;

    public static float Limit(float seconds) =>
        float.IsNaN(seconds) ? DefaultSeconds : Math.Max(MinimumSeconds, Math.Min(MaximumSeconds, seconds));
}
