using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace YAVSOS.Integrations;

/// <summary>The EpicLoot recipe shim for the status mark (D086 D1): off when not installed or not needed, else working or failed.</summary>
internal enum EpicLootRegistrationState { Absent, NotNeeded, Working, Failed }

internal sealed class OptionalEpicLootRegistrationHost : IDisposable
{
    private readonly OptionalEpicLootRegistration? adapter;
    private readonly bool failed, notNeeded;
    private ObjectDB? registeredDatabase;
    /// <summary>Catalog capture waits only while a recipe change is applied; a failed shim is off and blocks nothing (D086 D1).</summary>
    internal bool Pending => adapter?.Pending == true;
    internal EpicLootRegistrationState State => adapter != null ? adapter.Faulted ? EpicLootRegistrationState.Failed : EpicLootRegistrationState.Working
        : failed ? EpicLootRegistrationState.Failed : notNeeded ? EpicLootRegistrationState.NotNeeded : EpicLootRegistrationState.Absent;

    internal OptionalEpicLootRegistrationHost(Action invalidate, ManualLogSource log)
    {
        const string epicGuid = "randyknapp.mods.epicloot", jotunnGuid = "com.jotunn.jotunn";
        if (!Chainloader.PluginInfos.TryGetValue(epicGuid, out var epic) || !epic.Instance) return;
        Chainloader.PluginInfos.TryGetValue(jotunnGuid, out var jotunn);
        Type? Resolve(string name) => (name.StartsWith("Jotunn.", StringComparison.Ordinal)
            ? jotunn?.Instance?.GetType().Assembly : epic.Instance.GetType().Assembly)?.GetType(name, false);
        var headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        adapter = OptionalEpicLootRegistration.TryInstall(headless, epic.Metadata.Version.ToString(),
            jotunn?.Metadata.Version.ToString(), Resolve, invalidate, message => log.LogWarning(message), out var diagnostic,
            Preflight, names => { Verify(Resolve, names); registeredDatabase = ObjectDB.instance; });
#if YAVSOS_LAB
        if (Transfers.FaultLab.EpicLootShim) { adapter?.Dispose(); adapter = null; diagnostic = "unavailable: lab fault (D088); actual catalog admission remains strict"; }
#endif
        failed = diagnostic.StartsWith("unavailable:", StringComparison.Ordinal);
        notNeeded = diagnostic.StartsWith("not needed:", StringComparison.Ordinal);
        if (failed) log.LogWarning("EpicLoot recipe compatibility " + diagnostic + "; only this compatibility is off until restart. " +
            "On a dedicated server, the items EpicLoot adds this way get no categories; every other category becomes ready.");
        else log.LogInfo("EpicLoot recipe compatibility " + diagnostic + ".");
    }

    private static void Preflight(Type loader, IReadOnlyList<string> names)
    {
        const BindingFlags fields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        if (loader.GetField("Assets", fields)?.GetValue(null) is not AssetBundle bundle || !bundle ||
            loader.GetField("PrefabPathFormat", fields) == null || loader.GetField("PrefabPathFormat", fields)!.GetValue(null) != null ||
            loader.GetField("IconPathFormat", fields) == null || loader.GetField("IconPathFormat", fields)!.GetValue(null) != null ||
            !Equals(loader.GetField("FixReferences", fields)?.GetValue(null), false))
            throw new InvalidDataException("Unsupported batch asset layout.");
        foreach (var name in names)
        {
            var prefab = bundle.LoadAsset<GameObject>(name);
            if (!prefab || prefab.name != name || prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared == null)
                throw new InvalidDataException("Missing complete batch item asset.");
        }
    }

    private static void Verify(OptionalTypeResolver resolve, IReadOnlyList<string> names)
    {
        var db = ObjectDB.instance;
        if (!db || db.m_items == null || db.m_recipes == null) throw new InvalidDataException("No registered database.");
        var manager = resolve("Jotunn.Managers.ItemManager")!;
        var instance = manager.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)!.GetValue(null);
        var get = manager.GetMethod("GetItem", new[] { typeof(string) })!;
        foreach (var name in names)
        {
            var prefab = db.GetItemPrefab(name);
            var custom = get.Invoke(instance, new object[] { name });
            if (!prefab || custom == null || !ReferenceEquals(custom.GetType().GetProperty("ItemPrefab")?.GetValue(custom), prefab))
                throw new InvalidDataException("Item registration did not resolve exactly.");
            var itemCount = 0; foreach (var item in db.m_items) if (item && item.name == name) itemCount++;
            if (itemCount != 1) throw new InvalidDataException("Duplicate/missing item registration.");
            var recipeCount = 0;
            foreach (var recipe in db.m_recipes)
            {
                if (!recipe || !recipe.m_item || recipe.m_item.gameObject.name != name) continue;
                recipeCount++;
                if (!ReferenceEquals(recipe.m_item.gameObject, prefab) || recipe.m_resources == null || recipe.m_resources.Length > 256)
                    throw new InvalidDataException("Invalid registered recipe.");
                foreach (var slot in recipe.m_resources)
                    if (slot == null || !slot.m_resItem || !ReferenceEquals(db.GetItemPrefab(slot.m_resItem.gameObject.name), slot.m_resItem.gameObject))
                        throw new InvalidDataException("Unresolved recipe resource.");
            }
            if (recipeCount != 1) throw new InvalidDataException("Duplicate/missing recipe registration.");
        }
    }

    internal void Update() => adapter?.Update(ZNet.instance && registeredDatabase &&
        ReferenceEquals(ObjectDB.instance, registeredDatabase));
    public void Dispose() => adapter?.Dispose();
}
