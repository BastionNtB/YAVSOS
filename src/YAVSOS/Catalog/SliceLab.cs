using System;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;

namespace YAVSOS.Catalog;

/// <summary>
/// X63 (D145): lab-only measurement of larger catalog and loaded-source slices on a dedicated server. Built only into
/// the Lab configuration (D077 B09); the release DLL reads no such file and keeps the production slices.
/// <para>
/// The file <c>BepInEx/config/yavsos-lab-slices.txt</c> turns it on. It is read once in <c>Awake</c>; each line is
/// <c>catalog|sources &lt;steps per frame&gt; &lt;milliseconds per frame&gt;</c>, and a stage without a line keeps
/// today's slice (catalog 64 steps or 2 ms, sources 32 steps or 1 ms). An empty file measures today's slices.
/// </para>
/// <para>
/// With the file present, a dedicated server logs its frames from world start until categories are ready, then for
/// <see cref="SettleSeconds"/> plus <see cref="IdleSeconds"/> more to give the idle frame rate. Every line starts with
/// <see cref="Tag"/> and an event word, followed by <c>key=value</c> fields (invariant culture), for
/// <c>scripts/lab/x63_slices.py</c>: <c>setting</c>, <c>begin</c>, <c>second</c> (frames ending in that second of the
/// run), <c>phase</c> (one stretch of a phase, closed when the phase changes), <c>long</c> (a frame over
/// <see cref="LongFrameMilliseconds"/>), <c>ready</c> and <c>end</c>. A frame is the time between two calls of the
/// plugin's Update, counted in the phase of the slices that ran at its start.
/// </para>
/// </summary>
internal static class SliceLab
{
    internal const string FileName = "yavsos-lab-slices.txt";
    internal const string Tag = "YAVSOS-LAB-FRAMES";
    private const double LongFrameMilliseconds = 50, SettleSeconds = 5, IdleSeconds = 60, GiveUpSeconds = 900;
    private const int MaxLongFrames = 1000;
    private static ManualLogSource? log;
    private static string setting = "";
    /// <summary>The catalog's limits from the file, or null without it (the cache then uses its defaults).</summary>
    internal static CatalogLimits? Catalog { get; private set; }
    /// <summary>The loaded sources' limits from the file, or null without it.</summary>
    internal static LoadedSourceLimits? Sources { get; private set; }

    private static bool sampling;
    private static double origin, last, readyAt, frameStart;
    private static CatalogState catalogBefore; private static LoadedSourceState sourcesBefore;
    private static string phase = "";
    private static double phaseStart, phaseLongest; private static int phaseFrames, phaseOver;
    private static int second, secondFrames, secondOver; private static double secondLongest;
    private static int longFrames;

    /// <summary>Reads the file, before the catalog and source hosts are created.</summary>
    internal static void Load(ManualLogSource logger)
    {
        log = logger;
        var file = Path.Combine(Paths.ConfigPath, FileName);
        if (!File.Exists(file)) return;
        var catalog = new CatalogLimits(); var sources = new LoadedSourceLimits();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw; var comment = line.IndexOf('#');
            if (comment >= 0) line = line.Substring(0, comment);
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            if (parts.Length == 3 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            {
                try
                {
                    if (parts[0] == "catalog") { catalog = catalog.WithLabSlice(steps, milliseconds); continue; }
                    if (parts[0] == "sources") { sources = sources.WithLabSlice(steps, milliseconds); continue; }
                }
                catch (ArgumentOutOfRangeException) { }
            }
            logger.LogWarning($"{Tag} ignored line in {FileName}: \"{raw.Trim()}\" (expected: catalog|sources <steps 1-" +
                $"{CatalogLimits.LabMaxStepsPerTick}> <milliseconds up to {F(CatalogLimits.LabMaxSliceMilliseconds)}>).");
        }
        Catalog = catalog; Sources = sources;
        setting = $"catalog={catalog.StepsPerTick}/{F(catalog.SliceMilliseconds)} sources={sources.StepsPerTick}/{F(sources.SliceMilliseconds)}";
        logger.LogInfo($"{Tag} setting {setting}");
    }

    /// <summary>A world starts: a dedicated server begins sampling.</summary>
    internal static void BeginWorld(bool dedicated, double now)
    {
        if (log == null || Catalog == null) return;
        if (!dedicated) { log.LogInfo($"{Tag} off reason=not-dedicated"); return; }
        sampling = true; origin = now; last = -1; readyAt = -1; phase = ""; longFrames = 0;
        second = 0; secondFrames = secondOver = 0; secondLongest = 0;
        log.LogInfo($"{Tag} begin {setting} cpu={CpuLimit()} processors={Environment.ProcessorCount} " +
            $"long={F(LongFrameMilliseconds)} settle={F(SettleSeconds)} idle={F(IdleSeconds)}");
    }

    internal static void EndWorld(double now) { if (sampling) Finish(now - origin, "world-ended"); }

    /// <summary>
    /// At the start of the plugin's Update: closes the previous frame (counted in its phase) and records the stages'
    /// states before this frame's slices run.
    /// </summary>
    internal static void Before(double now, bool ready, CatalogState catalog, LoadedSourceState sources)
    {
        if (!sampling) return;
        var t = now - origin;
        if (last >= 0) Count(t, (now - last) * 1000);
        last = now; frameStart = t; catalogBefore = catalog; sourcesBefore = sources;
        if (ready && readyAt < 0) { readyAt = t; log!.LogInfo($"{Tag} ready t={F(t)}"); }
        if (readyAt >= 0 && t >= readyAt + SettleSeconds + IdleSeconds) Finish(t, "idle-done");
        else if (readyAt < 0 && t >= GiveUpSeconds) Finish(t, "not-ready");
    }

    /// <summary>
    /// After the hosts' update: this frame's phase, from the states before and after it, so a slice that opens or
    /// finishes a capture counts as reading.
    /// </summary>
    internal static void After(CatalogState catalog, LoadedSourceState sources)
    {
        if (!sampling) return;
        var next = Phase(catalog, sources);
        if (next != phase) { ClosePhase(frameStart); phase = next; phaseStart = frameStart; }
    }

    // Reading is a stage capturing on the main thread; a build runs detached. "wait" covers the rest before categories
    // are ready (no stage started yet, classification, a retry's pause).
    private static string Phase(CatalogState catalog, LoadedSourceState sources)
    {
        if (catalog == CatalogState.Capturing || catalogBefore == CatalogState.Capturing) return "catalog";
        if (sources == LoadedSourceState.Capturing || sourcesBefore == LoadedSourceState.Capturing) return "sources";
        if (catalog == CatalogState.Building || catalogBefore == CatalogState.Building) return "catalog-build";
        if (sources == LoadedSourceState.Building || sourcesBefore == LoadedSourceState.Building) return "sources-build";
        if (readyAt < 0) return "wait";
        return frameStart < readyAt + SettleSeconds ? "settle" : "idle";
    }

    private static void Count(double t, double milliseconds)
    {
        var index = (int)Math.Floor(t);
        while (index > second) { FlushSecond(); second++; }
        secondFrames++; secondLongest = Math.Max(secondLongest, milliseconds);
        phaseFrames++; phaseLongest = Math.Max(phaseLongest, milliseconds);
        if (milliseconds <= LongFrameMilliseconds) return;
        secondOver++; phaseOver++;
        if (++longFrames <= MaxLongFrames) log!.LogInfo($"{Tag} long t={F(t)} ms={F(milliseconds)} phase={phase}");
        else if (longFrames == MaxLongFrames + 1) log!.LogInfo($"{Tag} long-capped after={MaxLongFrames}");
    }

    private static void FlushSecond()
    {
        log!.LogInfo($"{Tag} second n={second} frames={secondFrames} longest={F(secondLongest)} over={secondOver} phase={phase}");
        secondFrames = secondOver = 0; secondLongest = 0;
    }

    private static void ClosePhase(double t)
    {
        if (phase.Length != 0)
            log!.LogInfo($"{Tag} phase name={phase} start={F(phaseStart)} end={F(t)} frames={phaseFrames} longest={F(phaseLongest)} over={phaseOver}");
        phaseFrames = phaseOver = 0; phaseLongest = 0;
    }

    private static void Finish(double t, string reason)
    {
        FlushSecond(); ClosePhase(t); phase = "";
        log!.LogInfo($"{Tag} end reason={reason} t={F(t)} ready={(readyAt < 0 ? "none" : F(readyAt))} cpu={CpuLimit()} long-frames={longFrames}");
        sampling = false;
    }

    // The container's CPU limit (cgroup v2, else v1), so each run's log says how it ran: "0.25", "none" or "unknown".
    private static string CpuLimit()
    {
        try
        {
            const string v2 = "/sys/fs/cgroup/cpu.max";
            const string quota = "/sys/fs/cgroup/cpu/cpu.cfs_quota_us";
            const string period = "/sys/fs/cgroup/cpu/cpu.cfs_period_us";
            if (File.Exists(v2))
            {
                var parts = File.ReadAllText(v2).Trim().Split(' ');
                if (parts.Length == 2) return parts[0] == "max" ? "none" : Ratio(parts[0], parts[1]);
            }
            else if (File.Exists(quota) && File.Exists(period))
            {
                var value = File.ReadAllText(quota).Trim();
                return value.StartsWith("-", StringComparison.Ordinal) ? "none" : Ratio(value, File.ReadAllText(period).Trim());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return "unknown";
    }

    private static string Ratio(string quota, string period) =>
        double.TryParse(quota, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) &&
        double.TryParse(period, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0 ? F(q / p) : "unknown";

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
