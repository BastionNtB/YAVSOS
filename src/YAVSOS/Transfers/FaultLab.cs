using System;
using BepInEx.Bootstrap;
using YAVSOS.Integrations;

namespace YAVSOS.Transfers;

/// <summary>
/// D088 (Q10): lab-only faults for runtime checks of failure paths a normal game can't produce (P8-09). Built only into
/// the Lab configuration (D077 B09); the release DLL has no fault hooks. Faults are called through the test bridge's
/// debug/call_method.
/// </summary>
internal static class FaultLab
{
    /// <summary>Stacks of this prefab fail capture (D086 D16): the registration reads as unverified.</summary>
    internal static string Uncapturable { get; private set; } = "";
    /// <summary>A lower chest limit for the runners (D086 D9); 0 keeps the real one.</summary>
    internal static int ChestLimit { get; private set; }
    /// <summary>SF3: every chest the stash gathers fails to be read.</summary>
    internal static bool UnreadableChests { get; private set; }
    // SF1, SF4: the one-shot fault armed for the next frame that reaches that part ("panel", "explanations", "search").
    private static string armed = "";

    public static string Status() =>
        $"uncapturable={Uncapturable}; chestLimit={ChestLimit}; armed={armed}; chestsUnbound={ChestRuleStore.LabUnbound}; unreadableChests={UnreadableChests}; " +
        $"display={string.Join(",", Interface.DisplayFaults.Off)}; " +
        $"azuExclusions={AzuExclusions.State}; azuErrors={AzuExclusions.Errors}; azuPause={AzuExclusions.PauseState}; " +
        $"categoriesGaveUp={Plugin.Instance?.CategoriesGaveUp ?? "-"}";

    /// <summary>D086 D16: stacks of <paramref name="prefab"/> can't be captured; "" turns it off.</summary>
    public static string SetUncapturable(string prefab) { Uncapturable = prefab ?? ""; return "uncapturable=" + Uncapturable; }

    /// <summary>D086 D9: the runners handle at most <paramref name="chests"/> chests (0: the real 256).</summary>
    public static string SetChestLimit(int chests) { ChestLimit = Math.Max(0, chests); return "chestLimit=" + ChestLimit; }

    /// <summary>
    /// D087 silent-failure checks (SF1-SF4). One-shot: <c>panel</c> (the rule panel's drawing throws once, SF1),
    /// <c>explanations</c> or <c>search</c> (that display part throws once, SF4). Until <c>none</c>: <c>chests</c> (every
    /// chest reads as if Container.m_nview/CheckAccess were not bound, SF2), <c>unreadable</c> (every chest the stash
    /// gathers fails to be read, SF3). A part turned off by a one-shot fault stays off until the game restarts, as in a
    /// real failure.
    /// </summary>
    public static string Fault(string name)
    {
        switch (name)
        {
            case "panel": case "explanations": case "search": armed = name; break;
            case "chests": ChestRuleStore.LabUnbound = true; break;
            case "unreadable": UnreadableChests = true; break;
            case "none": armed = ""; ChestRuleStore.LabUnbound = false; UnreadableChests = false; break;
            default: return "unknown fault: " + name;
        }
        return Status();
    }

    /// <summary>Throws once where <paramref name="part"/>'s fault is armed (<see cref="Fault"/>).</summary>
    internal static void Raise(string part)
    {
        if (armed != part) return;
        armed = "";
        throw new InvalidOperationException("Lab fault: " + part + " (D088)");
    }

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
