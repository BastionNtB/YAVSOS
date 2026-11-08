using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using YAVSOS.Core.Classification;
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
    /// <summary>X67: a lower cleanup source limit (the work-first pick, D156), so a small base can show it; 0 keeps the server's.</summary>
    internal static int SourceLimit { get; private set; }
    /// <summary>SF3: every chest the stash gathers fails to be read.</summary>
    internal static bool UnreadableChests { get; private set; }
    /// <summary>P10-25 hand test: every rule write and the opt-out are refused as if another player had taken the chest, so each control's refusal shows in the strip.</summary>
    internal static bool RefuseEdits { get; private set; }
    /// <summary>
    /// X62 (D145): every classification build fails, so a server holds its categories at a failed stage and keeps retrying
    /// after growing pauses (D144); a client then shows the server's failure. From the startup file (<c>classification</c>)
    /// or <see cref="Fault"/>.
    /// </summary>
    internal static bool FailClassification { get; private set; }
    /// <summary>
    /// X32 (D154): this game, as a chest's owner, answers remote rule writes this many seconds late (above 0) or never
    /// (below 0); 0 answers at once. The write itself happens at once either way, so the requester sees its value arrive.
    /// </summary>
    internal static float RuleWriteReplyDelay { get; private set; }
    // SF1, SF4: the one-shot fault armed for the next frame that reaches that part ("panel", "explanations", "search", "highlight", "glow").
    private static string armed = "";

    public static string Status() =>
        $"uncapturable={Uncapturable}; chestLimit={ChestLimit}; sourceLimit={SourceLimit}; armed={armed}; chestsUnbound={ChestRuleStore.LabUnbound}; unreadableChests={UnreadableChests}; refuseEdits={RefuseEdits}; failClassification={FailClassification}; ruleWriteReplyDelay={RuleWriteReplyDelay}; " +
        $"display={string.Join(",", Interface.DisplayFaults.Off)}; " +
        $"azuExclusions={AzuExclusions.State}; azuErrors={AzuExclusions.Errors}; azuPause={AzuExclusions.PauseState}; " +
        $"categoriesGaveUp={Plugin.Instance?.CategoriesGaveUp ?? "-"}; categoriesServerFailed={Plugin.Instance?.CategoriesServerFailed == true}; " +
        $"categoriesWait={Plugin.Instance?.CategoriesWait}; categoriesServerRebuildFailed={Plugin.Instance?.CategoriesServerRebuildFailed == true}";

    private static HashSet<string>? startup;
    /// <summary>
    /// D088: startup checks forced to fail, one name per line in <c>BepInEx/config/yavsos-lab-faults.txt</c>, read once
    /// when the first check asks (<c>azu-store-order</c>: AzuAutoStore's store-key order, for the
    /// <c>panel-compatibilitychecks.png</c> capture; <c>classification</c>: every classification build fails, X62, which holds a
    /// dedicated server at a failed stage from its start; <c>classification-rebuild</c>: every build after the first good one
    /// fails, X71, see <see cref="Update"/>). Delete the file and restart to end them.
    /// </summary>
    internal static bool StartupFault(string name)
    {
        if (startup == null)
        {
            startup = new HashSet<string>(StringComparer.Ordinal);
            var file = Path.Combine(Paths.ConfigPath, "yavsos-lab-faults.txt");
            if (File.Exists(file)) foreach (var line in File.ReadAllLines(file)) if (line.Trim().Length != 0) startup.Add(line.Trim());
        }
        return startup.Contains(name);
    }

    /// <summary>
    /// X62: the classification cache the plugin uses in the Lab build. Its builds run as in the release build, unless
    /// <see cref="FailClassification"/> is set when one starts; then that build fails with a Lab fault, through the
    /// cache's own failure and retry path.
    /// </summary>
    internal static ClassificationCache ClassificationCache()
    {
        FailClassification = StartupFault("classification");
        failRebuild = !FailClassification && StartupFault("classification-rebuild");
        return new ClassificationCache(schedule: (work, token) => FailClassification
            ? Task.FromException<ClassifiedCatalog>(new InvalidOperationException("Lab fault (D088): classification is forced to fail."))
            : Task.Run(work, token));
    }

    private static bool failRebuild;

    /// <summary>
    /// X71 (D159) lab check on a dedicated server, which has no test bridge: with the startup fault
    /// <c>classification-rebuild</c>, the first classification build runs as usual; once categories are ready, every build
    /// fails (<see cref="FailClassification"/>) and the catalog is rebuilt once, so the server keeps its last good
    /// categories while its rebuild fails and retries (D144). Called each frame from <c>Plugin.Update</c>; Unity thread only.
    /// </summary>
    internal static void Update(bool categoriesReady, ManualLogSource log)
    {
        if (!failRebuild || !categoriesReady) return;
        failRebuild = false; FailClassification = true;
        log.LogWarning("Lab fault (D088): categories are ready; every classification build now fails, starting with a rebuild (classification-rebuild).");
        Plugin.Instance?.InvalidateLoadedCatalog();
    }

    /// <summary>D086 D16: stacks of <paramref name="prefab"/> can't be captured; "" turns it off.</summary>
    public static string SetUncapturable(string prefab) { Uncapturable = prefab ?? ""; return "uncapturable=" + Uncapturable; }

    /// <summary>X32: answer remote rule writes <paramref name="seconds"/> late (below 0: never; 0: at once).</summary>
    public static string SetRuleWriteReplyDelay(float seconds) { RuleWriteReplyDelay = seconds; return "ruleWriteReplyDelay=" + RuleWriteReplyDelay; }

    /// <summary>D086 D9: the runners handle at most <paramref name="chests"/> chests (0: the real 256).</summary>
    public static string SetChestLimit(int chests) { ChestLimit = Math.Max(0, chests); return "chestLimit=" + ChestLimit; }

    /// <summary>X67: cleanup takes items out of at most <paramref name="sources"/> chests (0: the server's MaximumSources).</summary>
    public static string SetSourceLimit(int sources) { SourceLimit = Math.Max(0, sources); return "sourceLimit=" + SourceLimit; }

    /// <summary>
    /// D087 silent-failure checks (SF1-SF4). One-shot: <c>panel</c> (the rule panel's drawing throws once, SF1),
    /// <c>explanations</c> or <c>search</c> (that display part throws once, SF4). Until <c>none</c>: <c>chests</c> (every
    /// chest reads as if Container.m_nview/CheckAccess were not bound, SF2), <c>unreadable</c> (every chest the stash
    /// gathers fails to be read, SF3), <c>edits</c> (every rule write and the opt-out refused as "another player is using
    /// this chest", for the P10-25 strip), <c>classification</c> (every classification build fails, X62; the current
    /// categories are rebuilt at once so the fault applies). A part turned off by a one-shot fault stays off until the game restarts, as in a
    /// real failure.
    /// </summary>
    public static string Fault(string name)
    {
        switch (name)
        {
            case "panel": case "explanations": case "search": case "highlight": case "glow": armed = name; break;
            case "chests": ChestRuleStore.LabUnbound = true; break;
            case "unreadable": UnreadableChests = true; break;
            case "edits": RefuseEdits = true; break;
            case "classification": FailClassification = true; Plugin.Instance?.InvalidateLoadedCatalog(); break;
            case "none": armed = ""; ChestRuleStore.LabUnbound = false; UnreadableChests = false; RefuseEdits = false; FailClassification = false; failRebuild = false; RuleWriteReplyDelay = 0; break;
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
