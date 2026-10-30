using System;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using YAVSOS.Core.Classification;

namespace YAVSOS.Integrations;

/// <summary>
/// P3-10 (D047): lets AzuAutoStore fill an assigned, empty RossItemDrawers drawer with its item, through H2.
/// Reads Ross's public <c>DrawerComponent.Snapshot</c> only; Ross's own deposit and capacity logic still decides
/// what lands. A missing or changed shape switches off drawer support alone, with one log line.
/// </summary>
internal static class RossItemDrawersIntegration
{
    internal const string RossGuid = "com.rossdwest.itemdrawers";
    private static Type? drawerType;
    private static PropertyInfo? snapshot;
    private static FieldInfo? itemName;

    internal static void Initialize(ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.TryGetValue(RossGuid, out var info) || info?.Instance == null) return;
        if (AzuAutoStoreIntegration.State != AzuIntegrationState.Active)
        { log.LogInfo("RossItemDrawers found; reserved-drawer filling needs the AzuAutoStore integration, which is not active."); return; }
        var version = info.Metadata.Version.ToString();
        try
        {
            drawerType = info.Instance.GetType().Assembly.GetType("ItemDrawers.Game.DrawerComponent", false);
            snapshot = drawerType == null ? null : AccessTools.DeclaredProperty(drawerType, "Snapshot");
            var snapshotType = snapshot?.PropertyType;
            itemName = snapshotType == null ? null : AccessTools.DeclaredField(snapshotType, "ItemName");
            if (drawerType == null || !typeof(Container).IsAssignableFrom(drawerType) || snapshot?.GetGetMethod() == null ||
                itemName == null || itemName.FieldType != typeof(string))
            { log.LogWarning($"RossItemDrawers {version} changed shape; reserved empty drawers are not filled by AzuAutoStore. Chest rules are unaffected."); return; }
            AzuAutoStoreIntegration.ReservedDrawerWants = Wants;
            log.LogInfo($"RossItemDrawers {version}: AzuAutoStore fills reserved empty drawers.");
        }
        catch (Exception ex) { log.LogWarning($"RossItemDrawers {version} support disabled: {ex.GetType().Name}. Chest rules are unaffected."); }
    }

    private static bool Wants(Container chest, ItemDrop.ItemData item)
    {
        if (!chest || !drawerType!.IsInstanceOfType(chest)) return false;
        // B1.10: a drawer without a snapshot yet (just built) is not reserved; it must not turn the support off for all drawers.
        if (snapshot!.GetValue(chest) is not { } state) return false;
        var assigned = itemName!.GetValue(state) as string;
        return ReservedDrawer.Wants(assigned, item.m_dropPrefab ? item.m_dropPrefab.name : null);
    }
}
