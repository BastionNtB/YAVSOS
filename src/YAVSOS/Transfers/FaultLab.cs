using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using YAVSOS.Integrations;

namespace YAVSOS.Transfers;

/// <summary>
/// D088 (Q10): lab-only faults for runtime checks of failure paths a normal game can't produce (P8-09). Built only into
/// the Lab configuration (D077 B09); the release DLL has no fault hooks. Runtime faults are called through the test
/// bridge's debug/call_method. Startup faults are read once, at plugin start, from the file
/// <c>BepInEx/config/com.bastionntb.yavsos.lab-faults</c>: one fault name per line (<c>epicloot-shim</c>).
/// </summary>
internal static class FaultLab
{
    internal const string StartupFile = "com.bastionntb.yavsos.lab-faults";
    /// <summary>Startup: the EpicLoot recipe shim reports a failed install (D086 D1).</summary>
    internal static bool EpicLootShim { get; private set; }
    /// <summary>Stacks of this prefab fail capture (D086 D16): the registration reads as unverified.</summary>
    internal static string Uncapturable { get; private set; } = "";
    /// <summary>A lower chest limit for the runners (D086 D9); 0 keeps the real one.</summary>
    internal static int ChestLimit { get; private set; }

    internal static void LoadStartupFaults(Action<string> log)
    {
        try
        {
            var path = Path.Combine(Paths.ConfigPath, StartupFile);
            if (!File.Exists(path)) return;
            var faults = File.ReadAllLines(path).Select(x => x.Trim()).Where(x => x.Length != 0 && !x.StartsWith("#", StringComparison.Ordinal)).ToArray();
            EpicLootShim = faults.Contains("epicloot-shim");
            log("Lab faults at startup: " + (faults.Length == 0 ? "none" : string.Join(", ", faults)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log("Lab faults file unreadable: " + error.Message); }
    }

    public static string Status() =>
        $"epiclootShim={EpicLootShim}; registration={Plugin.Instance?.EpicLootRegistration}; uncapturable={Uncapturable}; chestLimit={ChestLimit}; " +
        $"azuExclusions={AzuExclusions.State}; azuErrors={AzuExclusions.Errors}; azuPause={AzuExclusions.PauseState}; " +
        $"categoriesGaveUp={Plugin.Instance?.CategoriesGaveUp ?? "-"}";

    /// <summary>D086 D16: stacks of <paramref name="prefab"/> can't be captured; "" turns it off.</summary>
    public static string SetUncapturable(string prefab) { Uncapturable = prefab ?? ""; return "uncapturable=" + Uncapturable; }

    /// <summary>D086 D9: the runners handle at most <paramref name="chests"/> chests (0: the real 256).</summary>
    public static string SetChestLimit(int chests) { ChestLimit = Math.Max(0, chests); return "chestLimit=" + ChestLimit; }

    /// <summary>
    /// D086 D2, M7, D088: <c>throw</c> (each check throws, retried), <c>limited</c> (Azu's check has another shape),
    /// <c>unavailable</c> (nothing can be read), <c>pause</c> (pause flag unreadable), <c>none</c> (bind again as at startup).
    /// </summary>
    public static string Azu(string mode)
    {
        if (!Chainloader.PluginInfos.TryGetValue(AzuAutoStoreIntegration.AzuGuid, out var info) || info?.Instance == null) return "AzuAutoStore not installed";
        switch (mode)
        {
            case "none": AzuExclusions.LabReset(info.Instance.GetType().Assembly); break;
            case "throw": AzuExclusions.LabThrow = true; break;
            case "limited": AzuExclusions.LabLimited(); break;
            case "unavailable": AzuExclusions.LabUnavailable(); break;
            case "pause": AzuExclusions.LabPauseUnreadable(); break;
            default: return "unknown mode: " + mode;
        }
        return Status();
    }
}
