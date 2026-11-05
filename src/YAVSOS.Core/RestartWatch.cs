using System;
using System.Globalization;

namespace YAVSOS.Core;

/// <summary>
/// Names a loading stage that keeps restarting without finishing (D144, issue #4). A capture restarts, with its retry
/// count reset, whenever a registry it reads changes size; a mod that keeps registering at runtime would otherwise keep
/// the stage from ever finishing with nothing in the log. Normal startup churn stays quiet: the first warning needs
/// 60 s without finishing and at least 3 discarded captures. Later warnings come after 1, 2, 4, 8 and 16 minutes, then
/// every 30 minutes, so a stage that never finishes logs about 6 lines in its first hour and 2 an hour after.
/// Times are in seconds. Main-thread owned, like the hosts that use it.
/// </summary>
public sealed class RestartWatch
{
    public const double QuietSeconds = 60, FirstRepeatSeconds = 60, MaximumRepeatSeconds = 1800;
    public const int MinimumRestarts = 3;
    private readonly string stage;
    private int restarts, sinceWarning;
    private double stalledSince = -1, nextWarning, repeat = FirstRepeatSeconds;
    private string cause = "";
    private bool warned;

    public RestartWatch(string stage) { this.stage = stage ?? throw new ArgumentNullException(nameof(stage)); }

    /// <summary>Restarts since the stage last finished.</summary>
    public int Restarts => restarts;

    /// <summary>A capture or build in progress was thrown away because <paramref name="why"/>.</summary>
    public void Restarted(double now, string why)
    {
        if (stalledSince < 0) stalledSince = now;
        if (restarts < int.MaxValue) restarts++;
        if (sinceWarning < int.MaxValue) sinceWarning++;
        cause = why ?? "";
    }

    /// <summary>A warning when one is due, else null.</summary>
    public string? Poll(double now)
    {
        if (stalledSince < 0 || restarts < MinimumRestarts || now - stalledSince < QuietSeconds || warned && now < nextWarning) return null;
        if (warned && sinceWarning == 0) return null; // Stalled without new restarts: the stage's own warnings explain it.
        var message = $"{stage} restarted {Count(sinceWarning)} without finishing{(warned ? " since the last warning" : "")}" +
            $" ({Count(restarts)} in {Minutes(now - stalledSince)}), most recently because {cause}." +
            " Categories wait until it finishes. A mod that keeps registering items or prefabs while the game runs can cause this; please report it with this log.";
        nextWarning = now + repeat; repeat = Math.Min(repeat * 2, MaximumRepeatSeconds);
        warned = true; sinceWarning = 0;
        return message;
    }

    /// <summary>The stage finished: an info line if it had been warned about, else null. Starts counting again.</summary>
    public string? Finished(double now)
    {
        var message = warned ? $"{stage} finished after {restarts.ToString(CultureInfo.InvariantCulture)} {(restarts == 1 ? "restart" : "restarts")} in {Minutes(now - stalledSince)}." : null;
        Reset();
        return message;
    }

    public void Reset()
    {
        restarts = sinceWarning = 0; stalledSince = -1; nextWarning = 0; repeat = FirstRepeatSeconds; cause = ""; warned = false;
    }

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " time" : " times");
    private static string Minutes(double seconds) => seconds < 120
        ? Math.Max(0, Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + " s"
        : Math.Round(seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
}
