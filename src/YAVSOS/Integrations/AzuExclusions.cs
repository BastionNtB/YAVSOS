using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace YAVSOS.Integrations;

/// <summary>
/// Active: Azu's own check answers. Limited (D088): its check can't be used, but its config can, so only chest types
/// without exclusion entries count as allowed. Unavailable: neither can be read, so cleanup is off (D056 never bypasses).
/// </summary>
internal enum AzuExclusionState { Absent, Active, Limited, Unavailable }

/// <summary>
/// P4-00f (D056): reads AzuAutoStore's own per-chest-type exclusions (<c>Azumatt.AzuAutoStore.yml</c>) through Azu's
/// own <c>Boxes.CanItemBeStored(chestType, prefab)</c>, with the chest type computed by Azu's own
/// <c>MiscFunctions.GetPrefabName(root.name)</c>, exactly as Azu's TryStore does. Bound by signature, never by version
/// (D058). Read-only: it never changes Azu's config. Each part is bound on its own (D086 D2, D088): when Azu's check has
/// another shape but its config and chest-type name can be read, a chest type without exclusion entries is allowed and
/// any other is unknown. A call that throws is retried on the next call, and meanwhile answers the same way; it is logged
/// once per kind (D087). Unknown keeps the item where it is. Unity game thread only.
/// </summary>
internal static class AzuExclusions
{
    private static Func<string, string, bool>? canStore;
    private static Func<string, string>? prefabName;
    private static FieldInfo? yamlData;
    private static ManualLogSource? log;
    private static readonly HashSet<string> reported = new(StringComparer.Ordinal);
    internal static AzuExclusionState State { get; private set; } = AzuExclusionState.Absent;
    /// <summary>Calls into Azu that threw this session (D086 D2); each was retried on the next check.</summary>
    internal static int Errors { get; private set; }
    /// <summary>A call threw in the last few minutes (B4.21): the mark is amber only then, not for the rest of the session.</summary>
    internal static bool RecentErrors => Errors != 0 && Time.realtimeSinceStartup - lastError < 300f;
    private static float lastError = float.NegativeInfinity;
    private static int? pausedKey;
    /// <summary>
    /// D059: Azu's "storing paused" ZDO flag can be read. Azu's pause key sets it on every chest that player's Azu has
    /// loaded. Bound separately from the exclusions: when it cannot be read, only this check is off (D058, D086 M7).
    /// </summary>
    internal static AzuExclusionState PauseState { get; private set; } = AzuExclusionState.Absent;

    internal static void Initialize(Assembly azu, ManualLogSource logger)
    {
        log = logger;
        try
        {
            var boxes = azu.GetType("AzuAutoStore.Util.Boxes", false);
            var misc = azu.GetType("AzuAutoStore.Util.MiscFunctions", false);
            var plugin = azu.GetType("AzuAutoStore.AzuAutoStorePlugin", false);
            var store = boxes == null ? null : AccessTools.DeclaredMethod(boxes, "CanItemBeStored", new[] { typeof(string), typeof(string) });
            var name = misc == null ? null : AccessTools.DeclaredMethod(misc, "GetPrefabName", new[] { typeof(string) });
            var data = plugin == null ? null : AccessTools.DeclaredField(plugin, "yamlData");
            // Each part binds on its own (B1.22): a check that can't be bound leaves the others, so the state is Limited.
            try { if (name != null && name.IsStatic && name.ReturnType == typeof(string)) prefabName = AccessTools.MethodDelegate<Func<string, string>>(name); }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { prefabName = null; }
            if (data != null && data.IsStatic && typeof(IDictionary).IsAssignableFrom(data.FieldType)) yamlData = data;
            try { if (store != null && store.IsStatic && store.ReturnType == typeof(bool)) canStore = AccessTools.MethodDelegate<Func<string, string, bool>>(store); }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { canStore = null; }
            if (prefabName == null || yamlData == null)
                Unavailable("MiscFunctions.GetPrefabName or yamlData has a different shape");
            else if (canStore == null)
            {
                State = AzuExclusionState.Limited;
                log.LogWarning("AzuAutoStore's Boxes.CanItemBeStored has a different shape; cleanup reads its exclusion config instead and moves items only " +
                    "into chest types that have no exclusions there. AzuAutoStore and chest rules keep working.");
            }
            else State = AzuExclusionState.Active;
        }
        catch (Exception error) { Unavailable(error.GetType().Name); }
        // D086 M7: the pause flag is bound whatever happened above.
        try
        {
            var patch = azu.GetType("AzuAutoStore.Patches.ContainerAwakePatch", false);
            var key = patch == null ? null : AccessTools.DeclaredField(patch, "storingPausedHash");
            if (key == null || !key.IsStatic || key.FieldType != typeof(int)) { FailPause("ContainerAwakePatch.storingPausedHash has a different shape"); return; }
            pausedKey = (int)key.GetValue(null);
            PauseState = AzuExclusionState.Active;
        }
        catch (Exception error) { FailPause(error.GetType().Name); }
    }

    /// <summary>True when Azu's "storing paused" flag is set on the chest; null when that cannot be read.</summary>
    internal static bool? Paused(Container chest)
    {
        if (PauseState == AzuExclusionState.Absent) return false;
        if (PauseState != AzuExclusionState.Active || !chest) return null;
        var view = YAVSOS.Transfers.ChestRuleStore.View(chest);
        return view && view!.IsValid() ? view.GetZDO().GetBool(pausedKey!.Value, false) : null;
    }

    private static void FailPause(string reason)
    {
        if (PauseState == AzuExclusionState.Unavailable) return;
        PauseState = AzuExclusionState.Unavailable;
        log?.LogWarning("AzuAutoStore's \"storing paused\" flag cannot be read (" + reason + "); ground pickup may fill chests whose storing is paused. Everything else keeps working.");
    }

    /// <summary>Azu's chest-type key for a container, or null when unavailable.</summary>
    internal static string? ChestType(Container chest)
    {
        if (State is AzuExclusionState.Absent or AzuExclusionState.Unavailable || !chest) return null;
        try { return prefabName!(chest.transform.root.name); }
        catch (Exception error) { Error("MiscFunctions.GetPrefabName", error); return null; }
    }

    /// <summary>True when Azu's config allows the item in this chest's type; null when that cannot be read.</summary>
    internal static bool? Allows(Container chest, string prefab)
    {
        if (State == AzuExclusionState.Absent) return true;
        var type = ChestType(chest);
        if (type == null || prefab == null) return null;
        // Azu logs an error and refuses every item while its config is not loaded; report that as unknown instead.
        if (!Config(out var data) || data == null) return null;
        if (State == AzuExclusionState.Active)
            try
            {
#if YAVSOS_LAB
                if (LabThrow) throw new InvalidOperationException("lab fault (D088)");
#endif
                return canStore!(type, prefab);
            }
            catch (Exception error) { Error("Boxes.CanItemBeStored", error); }
        // D088: without Azu's own answer, a chest type with no exclusion entries takes anything; any other is unknown.
        return Excludes(data, type) == false ? true : null;
    }

    /// <summary>Azu's config lists exclusions for this chest's type (otherwise no conflict is possible).</summary>
    internal static bool HasExclusions(Container chest)
    {
        var type = ChestType(chest);
        return type != null && Config(out var data) && data != null && Excludes(data, type) == true;
    }

    private static bool Config(out IDictionary? data)
    {
        data = null;
        if (yamlData == null) return false;
        try { data = yamlData.GetValue(null) as IDictionary; return true; }
        catch (Exception error) { Error("yamlData", error); return false; }
    }

    // Null when the entry can't be read.
    private static bool? Excludes(IDictionary data, string type)
    {
        try
        {
            if (!data.Contains(type)) return false;
            // Azu refuses every item for a type whose entry isn't a map (an empty "piece_chest_wood:"), so it is unknown here.
            if (data[type] is not IDictionary entry) return null;
            foreach (DictionaryEntry field in entry)
                if (field.Key is string key && key.Equals("exclude", StringComparison.OrdinalIgnoreCase) && field.Value is ICollection { Count: > 0 })
                    return true;
            return false;
        }
        catch (Exception error) { Error("yamlData entry", error); return null; }
    }

#if YAVSOS_LAB
    // D088: lab-only faults (FaultLab.Azu).
    internal static bool LabThrow;
    internal static void LabLimited() { canStore = null; State = AzuExclusionState.Limited; log?.LogWarning("Lab fault: AzuAutoStore's check treated as another shape."); }
    internal static void LabUnavailable() => Unavailable("lab fault (D088)");
    internal static void LabPauseUnreadable() => FailPause("lab fault (D088)");
    internal static void LabReset(Assembly azu)
    {
        LabThrow = false; canStore = null; prefabName = null; yamlData = null; pausedKey = null; Errors = 0; reported.Clear();
        State = PauseState = AzuExclusionState.Absent;
        Initialize(azu, log!);
    }
#endif

    private static void Error(string call, Exception error)
    {
        Errors++; lastError = Time.realtimeSinceStartup;
        if (reported.Add(call + "|" + error.GetType().Name))
            log?.LogWarning("AzuAutoStore's " + call + " failed (" + error.GetType().Name + ": " + error.Message + "); it is tried again on the next check. " +
                "Meanwhile cleanup moves items only into chest types without exclusions in its config. AzuAutoStore and chest rules keep working.");
    }

    private static void Unavailable(string reason)
    {
        if (State == AzuExclusionState.Unavailable) return;
        State = AzuExclusionState.Unavailable;
        log?.LogWarning("AzuAutoStore's exclusion list cannot be read (" + reason + "); conflicts are not shown and cleanup is off, since it can't tell which chests " +
            "AzuAutoStore keeps items out of. AzuAutoStore and chest rules keep working.");
    }
}
