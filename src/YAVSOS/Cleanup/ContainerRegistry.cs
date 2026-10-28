using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Integrations;
using YAVSOS.Transfers;

namespace YAVSOS.Cleanup;

/// <summary>
/// P6-11 cost check: every live <see cref="Container"/>, kept by a postfix on its Awake, so finding nearby chests needs
/// no scene-wide search (FindObjectsByType took about 9 ms in a 60-chest base, on every explanation, highlight and run).
/// Destroyed ones are dropped when listed. The first listing after the game starts is checked once against a real scene search;
/// if a chest that can hold a rule is missing (a container type whose own Awake skips the game's), the scene search is
/// used from then on (D058). Unity main thread only.
/// </summary>
[HarmonyPatch(typeof(Container), "Awake")]
internal static class ContainerRegistry
{
    private static readonly List<Container> Live = new();
    private static bool active, verified;
    private static ManualLogSource? log;

    [HarmonyPostfix]
    private static void Added(Container __instance)
    {
        if (__instance) Live.Add(__instance);
    }

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        try { harmony.PatchAll(typeof(ContainerRegistry)); active = true; }
        catch (System.Exception error) when (OptionalFailure.Recoverable(error))
        { logger.LogWarning("Container list turned off (" + error.GetType().Name + "); nearby chests are found by a scene search."); }
    }

    /// <summary>The active containers in the scene, as the scene search would find them.</summary>
    internal static Container[] Scene()
    {
        if (!active) return Search();
        Live.RemoveAll(x => !x);
        var listed = Live.Where(x => x.gameObject.activeInHierarchy).ToArray();
        if (verified) return listed;
        verified = true;
        var found = Search();
        var known = new HashSet<Container>(listed);
        var missing = found.Where(x => !known.Contains(x) && ChestRuleStore.Eligibility(x) == RuleEligibility.Eligible).ToArray();
        if (missing.Length == 0) return listed;
        active = false;
        log?.LogWarning($"Container list missed {missing.Length} chest(s) such as {missing[0].GetType().Name} '{missing[0].name}'; " +
                       "nearby chests are found by a scene search until the game restarts.");
        return found;
    }

    private static Container[] Search() => Object.FindObjectsByType<Container>(FindObjectsSortMode.None);
}
