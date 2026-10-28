using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;

namespace YAVSOS.Integrations;

/// <summary>
/// P6-21 "Item origin" (maintainer, 2026-10-23): which mod registered an item, beyond Jotunn's own registry, and which
/// items are the base game's. Two read-only adapters, each with its own capability, feeding the same "mod" facet:
/// <list type="bullet">
/// <item><c>item-manager</c>: blaxxun's ItemManager, which many mods (Therzie's, Azumatt's and others) embed as their own
/// copy of <c>ItemManager.Item</c> with a static <c>registeredItems</c> list and a <c>Prefab</c> field. Read by that
/// shape (D058), per plugin assembly; a copy with another shape is skipped and named in the witness.</item>
/// <item><c>base-game</c>: the first non-empty item list the game hands an ObjectDB, before any mod adds to it:
/// first-priority prefixes on <c>ObjectDB.Awake</c> (the world's own ObjectDB) and on <c>ObjectDB.CopyOtherDB</c> (the
/// main menu adds an empty ObjectDB, which wakes with no items, then copies the game's list in; mods add theirs in
/// postfixes, runtime 2026-10-23). An item a mod adapter claims is never counted as the base game's.</item>
/// </list>
/// Items no adapter can place stay "origin not known". Unity game thread only.
/// </summary>
[HarmonyPatch(typeof(ObjectDB), "Awake")]
internal static class ItemOrigins
{
    internal const string ManagerAdapter = "item-manager", BaseAdapter = "base-game";
    private static HashSet<string>? baseGame;

    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Record(ObjectDB __instance) => Note(__instance);

    internal static void Note(ObjectDB? db)
    {
        if (baseGame != null || !db || db!.m_items == null || db.m_items.Count == 0) return;
        try { baseGame = new HashSet<string>(db.m_items.Where(x => x).Select(x => x.name), StringComparer.Ordinal); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { baseGame = null; }
    }

    /// <summary>Item registrations through each mod's own ItemManager copy, as (prefab, plugin GUID).</summary>
    internal static OptionalCapability Managers(List<(string Item, string Owner)> output)
    {
        var read = 0; var skipped = new List<string>(); var assemblies = new HashSet<Assembly>();
        try
        {
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                if (info?.Instance == null || info.Metadata == null) continue;
                var assembly = info.Instance.GetType().Assembly;
                if (!assemblies.Add(assembly)) continue;
                Type? type;
                try { type = assembly.GetType("ItemManager.Item", false); }
                catch (Exception error) when (error is ArgumentException or TypeLoadException or BadImageFormatException) { type = null; }
                if (type == null) continue;
                var list = AccessTools.DeclaredField(type, "registeredItems");
                var prefab = AccessTools.DeclaredField(type, "Prefab");
                if (list == null || !list.IsStatic || !typeof(IEnumerable).IsAssignableFrom(list.FieldType) || prefab == null || prefab.FieldType != typeof(GameObject))
                { skipped.Add(info.Metadata.GUID); continue; }
                if (list.GetValue(null) is not IEnumerable items) continue;
                read++;
                foreach (var item in items)
                {
                    if (item == null || prefab.GetValue(item) is not GameObject go || !go) continue;
                    output.Add((go.name, info.Metadata.GUID));
                    if (output.Count > OptionalDefinitionEvidence.MaximumObservations) break;
                }
            }
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            output.Clear();
            return OptionalCapability.Incompatible(ManagerAdapter, "", "registry read failed: " + error.GetType().Name);
        }
        var witness = "mods read=" + read + (skipped.Count == 0 ? "" : "; other shape (skipped)=" + string.Join(",", skipped));
        return new OptionalCapability(ManagerAdapter, OptionalCapabilityState.Supported, "", witness.Length <= 500 ? witness : witness.Substring(0, 499) + "…");
    }

    /// <summary>The base game's items, or an incompatible capability when the list was not recorded this session.</summary>
    internal static OptionalCapability BaseGame(out IReadOnlyCollection<string> items)
    {
        items = baseGame ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        return baseGame == null || baseGame.Count == 0
            ? OptionalCapability.Incompatible(BaseAdapter, "", "the game's item list was not recorded before mods added to it")
            : new OptionalCapability(BaseAdapter, OptionalCapabilityState.Supported, "", "items=" + baseGame.Count + "; value=" + RuleText.Vanilla);
    }
}

/// <summary>P6-21: the main menu's copy of the game's item list, before mods' postfixes add to it (see <see cref="ItemOrigins"/>).</summary>
[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class BaseGameCopy
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Record(ObjectDB other) => ItemOrigins.Note(other);
}
