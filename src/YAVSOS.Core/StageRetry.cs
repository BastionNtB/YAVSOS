using System;
using System.Globalization;

namespace YAVSOS.Core;

/// <summary>
/// When a failed loading stage tries again (D144, issue #4). A few quick retries cover another mod still changing its
/// registries; after them the stage keeps trying with a growing pause (60 s, doubling, at most 5 min), so a slow or busy
/// host recovers on its own instead of waiting until its inputs change. Times are in the caller's clock unit, given as
/// <paramref name="unitsPerSecond"/>. Main-thread owned, like the caches that use it.
/// </summary>
public sealed class StageRetry
{
    public const double SlowFirstSeconds = 60, SlowMaximumSeconds = 300;
    private readonly int quickRetries;
    private readonly double quickSeconds, unitsPerSecond;
    /// <summary>Failures since the inputs last changed.</summary>
    public int Attempts { get; private set; }
    /// <summary>A retry is scheduled and has not been taken yet.</summary>
    public bool Scheduled { get; private set; }
    /// <summary>The last scheduled retry comes after the quick ones: the stage has given up for now (shown, D087).</summary>
    public bool Slow => Scheduled && Attempts > quickRetries;
    public double RetryAt { get; private set; }

    public StageRetry(int quickRetries, double quickSeconds, double unitsPerSecond)
    {
        if (quickRetries < 0 || quickRetries > 10 || double.IsNaN(quickSeconds) || quickSeconds < 0 || quickSeconds > 60 ||
            double.IsNaN(unitsPerSecond) || double.IsInfinity(unitsPerSecond) || unitsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(quickRetries));
        this.quickRetries = quickRetries; this.quickSeconds = quickSeconds; this.unitsPerSecond = unitsPerSecond;
    }

    /// <summary>The inputs changed: start counting again, with nothing scheduled.</summary>
    public void Reset() { Attempts = 0; Scheduled = false; RetryAt = 0; }

    /// <summary>A failure that must not be retried (such as a cleanup fault): nothing scheduled, count kept.</summary>
    public void Hold() { Scheduled = false; }

    /// <summary>Schedules the next attempt after a failure at <paramref name="now"/>; returns the diagnostic suffix.</summary>
    public string Schedule(double now)
    {
        if (Attempts < int.MaxValue) Attempts++;
        var quick = Attempts <= quickRetries;
        var seconds = quick ? quickSeconds
            : Math.Min(SlowFirstSeconds * Math.Pow(2, Math.Min(Attempts - quickRetries - 1, 16)), SlowMaximumSeconds);
        RetryAt = now + seconds * unitsPerSecond; Scheduled = true;
        var shown = seconds.ToString("0.#", CultureInfo.InvariantCulture);
        return quick ? $" Trying again in {shown} s ({Attempts} of {quickRetries})."
            : $" Trying again in {shown} s (failure {Attempts.ToString(CultureInfo.InvariantCulture)}).";
    }

    /// <summary>True once when a scheduled retry is due; the caller then starts the next attempt.</summary>
    public bool Take(double now)
    {
        if (!Scheduled || now < RetryAt) return false;
        Scheduled = false; return true;
    }
}
