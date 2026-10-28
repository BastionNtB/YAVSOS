using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace YAVSOS.Interface;

/// <summary>
/// P6-11 cost check (D065 pass mark): wall-clock time of the panel's per-frame work, explanations, searches and
/// highlight scans, plus the longest frame. Off unless the lab turns it on (UiLab.CostStart); then two timestamps per
/// measured call. Unity game thread only.
/// </summary>
internal static class UiCost
{
#if !YAVSOS_LAB
    // D077 B09: the release build measures nothing; the calls compile to constants the JIT drops.
    internal static long Start() => 0;
    internal static void Stop(string what, long started) { }
    internal static void Frame() { }
}
#else
    internal static bool On;
    private static readonly Dictionary<string, (int Count, double Total, double Max)> Rows = new();
    private static double longestFrame;

    internal static long Start() => On ? Stopwatch.GetTimestamp() : 0;
    internal static void Stop(string what, long started)
    {
        if (!On || started == 0) return;
        var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        Rows.TryGetValue(what, out var row);
        Rows[what] = (row.Count + 1, row.Total + ms, row.Max < ms ? ms : row.Max);
    }
    internal static void Frame() { if (On && Time.unscaledDeltaTime * 1000.0 > longestFrame) longestFrame = Time.unscaledDeltaTime * 1000.0; }
    internal static void Reset() { Rows.Clear(); longestFrame = 0; }
    internal static string Report() =>
        $"longest frame {longestFrame:0.00} ms; " + string.Join("; ", Rows.OrderBy(x => x.Key).Select(x =>
            $"{x.Key}: n={x.Value.Count} mean={x.Value.Total / x.Value.Count:0.000} ms max={x.Value.Max:0.000} ms"));
}
#endif
