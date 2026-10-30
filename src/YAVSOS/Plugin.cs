using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;
using YAVSOS.Configuration;
using YAVSOS.Core;
using YAVSOS.Core.Policy;
using YAVSOS.Networking;
using YAVSOS.Catalog;
using YAVSOS.Classification;
using YAVSOS.Core.Catalog;
using YAVSOS.Core.Classification;
using YAVSOS.Sources;
using YAVSOS.Integrations;
using YAVSOS.Transfers;
using YAVSOS.Interface;
using YAVSOS.Cleanup;

namespace YAVSOS;

[BepInPlugin(ProjectIdentity.Guid, ProjectIdentity.Name, ProjectIdentity.Version)]
// D062: the rule panel is built on Jotunn's GUIManager. D064: declared explicitly unenforced in Jotunn's join-time mod
// check (the same as having no attribute); YAVSOS's own ServerSync version check governs joins, never a second gate (D058).
[Jotunn.Utils.NetworkCompatibility(Jotunn.Utils.CompatibilityLevel.NotEnforced, Jotunn.Utils.VersionStrictness.None)]
[BepInDependency(Jotunn.Main.ModGuid)]
[BepInDependency("randyknapp.mods.epicloot", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(AzuAutoStoreIntegration.AzuGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(RossItemDrawersIntegration.RossGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin? Instance { get; private set; }
    private ConfigSync sync = null!;
    private PluginSettings settings = null!;
    private PolicyHost policy = null!;
    private Harmony harmony = null!;
    private ZNet? activeNet;
    private ZNetPeer? activeServerPeer;
    private readonly PeerIdentityGate peerGate = new PeerIdentityGate();
    private readonly ServerSyncSettingsBridge settingsBridge = new ServerSyncSettingsBridge();
    private Action<long, ConfigEntryBase[]> broadcastSettings = null!;
    private Action publishSettings = null!;
    private ConfigEntryBase[] sharedEntries = null!;
    private LoadedCatalogHost catalog = null!;
    private ClassificationHost classification = null!;
    private LoadedSourceHost sources = null!;
    private AssignmentHost assignments = null!;
    private readonly ClassificationTaxonomy defaultTaxonomy = DefaultClassificationTaxonomy.Create();
    private CategoryPolicy? taxonomyPolicy;
    private ClassificationTaxonomy? policyTaxonomy;
    internal ChestRuleStore ChestRules { get; private set; } = null!;
    internal ChestRuleEvaluator RuleEvaluator { get; } = new ChestRuleEvaluator();
    private RuleControls? ruleControls;
#if YAVSOS_LAB
    internal RuleControls? RuleControlsForLab => ruleControls; // UiLab only.
    internal PluginSettings SettingsForLab => settings; // CleanupLab.Allow only.
#endif
    /// <summary>The chest rule panel's state, for the item tooltip's "where would it go" line (P6-23).</summary>
    internal RuleControls? RuleEditor => ruleControls;
    internal CleanupRunner Cleanup { get; private set; } = null!;
    internal StashRunner Stash { get; private set; } = null!;
    internal RunResults Results { get; } = new RunResults();
    internal ChestHighlight Highlight { get; private set; } = null!;
    internal PluginSettings Settings => settings;

    private void Awake()
    {
        sync = new ConfigSync(ProjectIdentity.Guid)
        {
            DisplayName = ProjectIdentity.Name, CurrentVersion = ProjectIdentity.Version,
            MinimumRequiredVersion = ProjectIdentity.MinimumManagedVersion,
            ModRequired = false // Set true for managed server sessions before accepting peers.
        };
        policy = new PolicyHost(sync, Paths.ConfigPath, Logger);
        catalog = new LoadedCatalogHost(Logger);
        sources = new LoadedSourceHost(Logger);
        classification = new ClassificationHost(Logger.LogInfo, Logger.LogWarning, policy.State, () => catalog.Current,
            getSources: () => sources.Current, requireSources: true);
        assignments = new AssignmentHost(sync, policy.State, Logger, () => catalog.Current, () => classification.Current, () => sources.Current);
        ChestRules = new ChestRuleStore(() => RuleTaxonomy, () => policy.State.PolicyReady);
        settings = new PluginSettings(Config, sync, policy.State.SharedSettingsChanged);
        sharedEntries = settings.SharedEntries;
        Cleanup = new CleanupRunner(settings, Logger, Results);
        Stash = new StashRunner(settings, Logger, Results);
        Highlight = new ChestHighlight(Logger);
        ruleControls = new RuleControls(settings.ShowRuleControls, settings.ShowResultPanel, settings.RulePanelOffsetX, settings.RulePanelOffsetY,
            Cleanup, Stash, Results, Highlight, Logger);
        var broadcast = AccessTools.DeclaredMethod(typeof(ConfigSync), "Broadcast", new[] { typeof(long), typeof(ConfigEntryBase[]) });
        if (broadcast == null || broadcast.ReturnType != typeof(void))
            throw new NotSupportedException("Pinned ServerSync settings-publication contract is unavailable.");
        broadcastSettings = (Action<long, ConfigEntryBase[]>)Delegate.CreateDelegate(typeof(Action<long, ConfigEntryBase[]>), sync, broadcast);
        publishSettings = () => broadcastSettings(0, sharedEntries);
        Instance = this;
        harmony = new Harmony(ProjectIdentity.Guid + ".policy-lifecycle");
        // B1.19 (M8): the core session and settings hooks have no fallback, so if one can't attach YAVSOS turns off as a
        // whole, says so once and leaves nothing half-patched, instead of running without its views and integrations.
        foreach (var core in new[] { typeof(NetPolicyPatches), typeof(ServerPolicyRpcGuard), typeof(ServerPolicyRoutedGuard),
                     typeof(ServerSettingsAcceptedPatch), typeof(LocalSharedSettingsSavePatch) })
        {
            try { harmony.PatchAll(core); }
            catch (Exception error)
            {
                harmony.UnpatchSelf(); Instance = null; enabled = false;
                Logger.LogError($"YAVSOS is off: its core hook {core.Name} could not attach to the game ({error.GetType().Name}: {error.Message}). " +
                    "Chests and items work as without YAVSOS. Check for a YAVSOS update for this game version.");
                return;
            }
        }
        // D086 B1.3: optional hooks turn off alone. Without the registry hook, the per-frame registry count check still
        // notices most item changes; without the console command, the key and button still clean up.
        PatchView(typeof(CatalogRegistryPatches), "Catalog refresh on ObjectDB.UpdateRegisters");
        PatchView(typeof(CleanupConsoleCommand), "The \"yavsos cleanup\" console command");
        ContainerRegistry.Initialize(harmony, Logger);
        // Display-only patches (D058): a changed game method turns off that view alone.
        ChestLabels.Log = Logger;
        PatchView(typeof(ChestLabels), "Chest labels", DisplayFaults.Labels);
        ItemTooltips.Log = Logger;
        PatchView(typeof(ItemTooltips), "Item category tooltips", DisplayFaults.Categories);
        PatchView(typeof(WhereTooltip), "\"Where would it go?\" in item tooltips", DisplayFaults.Where);
        GridHover.Log = Logger;
        // P6-21: the base game's item list, recorded before mods add theirs (Item origin "Vanilla"); off alone if it fails.
        PatchView(typeof(ItemOrigins), "Base-game item origin");
        PatchView(typeof(BaseGameCopy), "Base-game item origin (menu copy)");
        // SF2 (D087): no chest can be read; said here, since the AzuAutoStore check below runs only with it installed.
        if (ChestRuleStore.BindingFailure.Length != 0) Logger.LogWarning(ChestRuleStore.BindingFailure);
        AzuAutoStoreIntegration.Initialize(Logger);
        RossItemDrawersIntegration.Initialize(Logger);
        Logger.LogInfo($"{ProjectIdentity.Name} {ProjectIdentity.Version} loaded. Policy synchronization enabled; chest rules: {AzuAutoStoreIntegration.Status}");
    }

    // After the chainloader has loaded every plugin (BepInEx loads them all before any Start runs), before Game.Start.
    private void Start()
    {
        MucRoute.Initialize(Logger);
        if (!ServerYavsosProbe.Supported) // D058: only detection is off; clients wait for server settings as before.
            Logger.LogWarning("Server detection turned off: the bundled ServerSync changed. On a server without YAVSOS chest rules stay off.");
        ruleControls?.Probe(); // D058: the styled panel's GUIManager members.
        _ = Highlight.Supported; // D058: the chest highlight's MaterialMan members.
    }

    private void Update()
    {
        var started = UiCost.Start(); // Lab cost scope (UiLab.CostStart); free when off.
        UpdateCore();
        UiCost.Stop("plugin update", started);
    }

    private void UpdateCore()
    {
        policy?.Update(Time.unscaledTime);
        catalog?.Update();
        sources?.Update(catalog?.Current);
        classification?.Update();
        assignments?.Update();
        if (activeNet != null) NativeInventoryProbe.Update(Logger.LogInfo, Logger.LogWarning);
        MucRoute.Update();
        Cleanup?.Update();
        Stash?.Update();
        if (Cleanup != null && !Cleanup.Running && CleanupTriggers.Pressed(settings.CleanupShortcut)) Cleanup.Start();
        else if (Cleanup != null && CleanupTriggers.PressedInInventory(settings.CleanupShortcut))
            CleanupRunner.Say("Close the inventory to clean up, or use Clean up nearby in a chest's panel.", overInventory: true);
        else if (Stash != null && !Stash.Running) StashTriggers.Poll(settings, Stash);
        settingsBridge.Publish(IsManagedServer, ConfigSync.ProcessingServerUpdate,
            publishSettings);
    }

    // After the inventory screen's own Update, so the panel follows the chest window in the same frame.
    // Patches whose failure must turn off only their own feature (D058, D087: named in the log).
    /// <param name="display">SF4: the display part the Display mark names when this patch fails; null for a non-display hook.</param>
    private void PatchView(Type patch, string name, string? display = null)
    {
        try { harmony.PatchAll(patch); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            if (display != null) DisplayFaults.Add(display);
            Logger.LogWarning(name + " turned off: " + error.GetType().Name + ": " + error.Message);
        }
    }

    private void LateUpdate()
    {
        UiCost.Frame();
        var started = UiCost.Start();
        ruleControls?.LateUpdate();
        UiCost.Stop("panel frame", started);
    }

    internal void StartWorld(ZNet net)
    {
        if (ReferenceEquals(activeNet, net)) return;
        activeNet = net;
        catalog.BeginSession(true);
        classification.BeginSession(true);
        sources.BeginSession(true);
        assignments.BeginSession(true);
        settingsBridge.BeginSession();
        NativeInventoryProbe.BeginSession();
        sync.ModRequired = net.IsServer() && !ZNet.IsSinglePlayer;
        var mode = net.IsServer() ? ZNet.IsSinglePlayer ? PolicyMode.Standalone : PolicyMode.ManagedServer : PolicyMode.AwaitingServer;
        policy.BeginSession(mode);
    }

    internal void EndWorld(ZNet net)
    {
        if (!ReferenceEquals(activeNet, net)) return;
        activeNet = null;
        Cleanup?.EndWorld();
        Stash?.EndWorld();
        Results.Dismiss();
        Highlight?.Clear();
        catalog.BeginSession(false);
        classification.BeginSession(false);
        sources.BeginSession(false);
        assignments.BeginSession(false);
        settingsBridge.BeginSession();
        activeServerPeer = null;
        peerGate.Clear();
        policy.BeginSession(PolicyMode.Offline);
        sync.ModRequired = false;
    }

    internal void StartConnection(ZNet net, ZNetPeer peer)
    {
        StartWorld(net);
        if (net.IsServer() || ReferenceEquals(activeServerPeer, peer)) return;
        activeServerPeer = peer;
        catalog.BeginSession(true);
        classification.BeginSession(true);
        sources.BeginSession(true);
        assignments.BeginSession(true);
        peerGate.Replace(peer.m_rpc);
        policy.BeginSession(PolicyMode.AwaitingServer);
    }

    // D044: the server's peer info ends the handshake. A server with YAVSOS has sent its version check and its
    // initial settings by now; with neither, this client runs on its own categories and settings (D074).
    internal void ServerPeerInfo(ZNet net, ZRpc rpc)
    {
        if (!ReferenceEquals(activeNet, net) || net.IsServer() || activeServerPeer == null
            || !peerGate.Accepts(rpc)) return;
        if (policy.State.Mode == PolicyMode.ManagedClient) // Its settings can arrive before its peer info.
        {
            Logger.LogInfo("Server has YAVSOS: its category settings already arrived.");
            return;
        }
        if (policy.State.Mode != PolicyMode.AwaitingServer) return;
        var readable = ServerYavsosProbe.TryVersionReceived(out var version);
        switch (ServerDetection.AtHandshake(readable, version, !sync.IsSourceOfTruth))
        {
            case ServerYavsos.Absent:
                if (policy.State.ServerLacksYavsos(policy.State.Stamp.Session))
                    Logger.LogInfo("Server without YAVSOS: this game's own categories and settings apply (D044).");
                break;
            case ServerYavsos.Present:
                Logger.LogInfo("Server has YAVSOS: waiting for its category settings.");
                break;
            default:
                Logger.LogWarning("Cannot tell whether the server has YAVSOS (ServerSync changed); chest rules wait for server settings.");
                break;
        }
    }

    internal void AdvertiseClientVersion(ZNet net, ZNetPeer peer)
    {
        if (!ReferenceEquals(activeNet, net) || !ReferenceEquals(activeServerPeer, peer)
            || !peerGate.Accepts(peer.m_rpc)) return;
        if (ServerSyncVersionAdvertisement.TrySend(net.IsServer(), sync.ModRequired,
            ProjectIdentity.Version, ProjectIdentity.MinimumManagedVersion,
            (rpcName, payload) => peer.m_rpc.Invoke(rpcName, new object[] { new ZPackage(payload) })))
            Logger.LogInfo($"Advertising YAVSOS version {ProjectIdentity.Version}, minimum {ProjectIdentity.MinimumManagedVersion} to the server; unmanaged-server use remains optional.");
    }

    internal bool IsServerConnection(ZRpc rpc) => activeNet != null && !activeNet.IsServer()
        && peerGate.Accepts(rpc);
    internal bool IsServerSender(long sender) => activeNet != null && !activeNet.IsServer()
        && activeServerPeer != null && activeServerPeer.m_uid != 0 && activeServerPeer.m_uid == sender
        && policy.IncomingRpc != null && peerGate.Accepts(policy.IncomingRpc);
    internal bool IsManagedServer => activeNet != null && activeNet.IsServer();
    internal bool ServerWithoutYavsos => policy.State.Mode == PolicyMode.UnmanagedLocal;
#if YAVSOS_LAB
    internal ConfigSync SyncForLab => sync; // UiLab.JoinSendCost only.
    internal string PolicyStatus => policy.State.Mode + ": " + policy.State.Diagnostic; // UiLab.Mode only.
#endif
    internal bool IsActiveServerPeer(ZNetPeer peer) => ReferenceEquals(activeServerPeer, peer);
    // Optional registration adapters must call this on the Unity thread after in-place item/recipe edits.
    // It is invalidation only: callers cannot publish evidence or open policy/classification readiness.
    internal void InvalidateLoadedCatalog() { catalog?.Invalidate(); sources?.Invalidate(); classification?.InvalidateCatalog(); assignments?.Invalidate(); }
    // B04: no public API on the plugin; other mods have no supported entry point here yet. Unity thread only.
    internal CatalogSnapshot? LoadedCatalog => catalog?.Current;
    /// <summary>
    /// P6-07b: fresh evidence of one item instance for instance filters: EpicLoot rarity (equipment only, through the
    /// verified adapter), condition (quality, durability) and crafter (D064). Read only; Unity thread only. What cannot be read now
    /// (rarity reader not ready or incompatible, a failed read) is <see cref="ClassificationFilters.Unreadable"/>, which
    /// every filter on it rejects; it never reads as "unknown"/plain. Empty when the item has no current answer.
    /// </summary>
    internal IReadOnlyList<FacetEvidence> InstanceFacets(ItemDrop.ItemData? item, ICategoryAnswers answers)
    {
        var prefab = item?.m_dropPrefab;
        if (item == null || !prefab || answers == null) return Array.Empty<FacetEvidence>();
        ClassificationResult? resolved = null;
        try
        {
            resolved = answers.Resolve(prefab!.name);
            if (resolved == null) return Array.Empty<FacetEvidence>();
            var rows = new List<FacetEvidence>(4);
            // The same answer object the planner resolves, so a rarity row matches its InstanceRarity check.
            if (resolved.InstanceRarity)
                rows.AddRange(sources != null ? sources.FilterEquipmentRarity(resolved, item) : new[] { ClassificationFilters.UnreadableRow("rarity") });
            var shared = item.m_shared;
            rows.AddRange(ClassificationFilters.Condition(Math.Max(1, item.m_quality), shared != null && shared.m_useDurability, item.m_durability,
                shared != null ? item.GetMaxDurability() : 0, UpgradeRecipes.Can(prefab.name)));
            // D064/Q12: the crafter's ID; the filter holds the ID of the player who set it.
            var crafter = ClassificationFilters.Crafter(item.m_crafterID);
            if (crafter != null) rows.Add(crafter);
            return rows;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return resolved == null ? Array.Empty<FacetEvidence>() : ClassificationFilters.UnreadableInstance(resolved); }
    }

    /// <summary>Instance evidence for a stack that can no longer be found: all of it unreadable, so instance filters reject it.</summary>
    internal static IReadOnlyList<FacetEvidence> UnreadableFacets(string prefab, ICategoryAnswers answers)
    {
        var resolved = answers?.Resolve(prefab);
        return resolved == null ? Array.Empty<FacetEvidence>() : ClassificationFilters.UnreadableInstance(resolved);
    }

    /// <summary>
    /// Instance evidence only when this chest's rule has an instance filter (P6-07b); a missing <paramref name="item"/>
    /// reads as unreadable, not unknown.
    /// </summary>
    internal IReadOnlyList<FacetEvidence>? InstanceFacetsFor(string payload, string prefab, ItemDrop.ItemData? item, ICategoryAnswers answers) =>
        !RuleEvaluator.UsesInstanceFilters(payload, RuleTaxonomy) ? null
        : item != null ? InstanceFacets(item, answers) : UnreadableFacets(prefab, answers);
    // Per-item server authority (D042): the server row, or Misc for a local item the server did not classify.
    // Null until categories are ready; kept through a catalog/source rebuild (D049). Unity thread only.
    internal ClassificationResult? ResolveCategories(string prefab) => assignments?.Answers.Resolve(prefab);
    internal ICategoryAnswers? CategoryAnswers => assignments?.Answers;
    internal bool EpicLootRarityOffered => sources?.EpicLootRarityOffered == true;
    /// <summary>D071 status mark: EpicLoot's verified reader, or Absent before sources are captured.</summary>
    internal OptionalCapabilityState EpicLootState => sources?.EpicLootState ?? OptionalCapabilityState.Absent;
    /// <summary>D086 B5.36, D087: a category stage that gave up for its current inputs, shown in the status; null otherwise.</summary>
    /// A managed client's own classification is not used (the server's answers are), so its failure is not shown (B4.24).
    /// B4.26: on a managed client the sources feed only that unused classification, so their failure isn't shown either.
    internal string? CategoriesGaveUp => catalog?.GaveUp ?? (ManagedClient ? null : sources?.GaveUp ?? classification?.GaveUp);
    /// <summary>A client of a managed server: the server's settings are announced (accepted or not).</summary>
    internal bool ManagedClient => policy.State.Mode == PolicyMode.ManagedClient;
    /// <summary>D071 status mark, B4.9: the server's category settings apply: managed and accepted, not merely announced.</summary>
    internal bool PolicyFromServer => ManagedClient && policy.State.PolicyReady;
    internal bool ServerPolicyRejected => policy.State.RemoteRejected;
    internal int CategoryMemberCount(string category) => assignments?.CategoryMemberCount(category) ?? 0;
    internal bool CategoriesRebuilding => assignments?.CategoriesRebuilding == true;
    // Chest rules parse against the effective policy's category set, or the default set before the policy
    // arrives; a rule naming a category outside it is unreadable and accepts nothing (D048).
    internal ClassificationTaxonomy RuleTaxonomy
    {
        get
        {
            var effective = policy.State.PolicyReady ? policy.State.Effective : null;
            if (effective == null) return defaultTaxonomy;
            if (!ReferenceEquals(effective, taxonomyPolicy)) { taxonomyPolicy = effective; policyTaxonomy = defaultTaxonomy.WithPolicy(effective); }
            return policyTaxonomy!;
        }
    }
    internal bool ClassificationEvidenceReady => assignments?.Answers.CategoriesReady == true;
    internal long? CaptureSettingsUpdate(long sender, ZPackage package)
    {
        var incoming = policy.IncomingRpc;
        var currentPeer = activeNet != null && incoming != null && activeNet.GetPeers().Any(
            peer => peer.m_uid != 0 && peer.m_uid == sender && ReferenceEquals(peer.m_rpc, incoming));
        var captured = settingsBridge.CaptureUpdate(IsManagedServer, true, currentPeer);
        if (!captured.HasValue) return null;
        if (!ServerSyncSettingsBridge.ValidateRequest(package.GetArray(), sharedEntries.Length, settings.SharedType, out var diagnostic))
        {
            Logger.LogWarning($"Client settings request rejected: {diagnostic}");
            return null;
        }
        return captured;
    }
    internal void CompleteSettingsUpdate(long? captured, bool accepted) => settingsBridge.CompleteUpdate(captured, accepted);
    internal bool TrySerializeLocalShared(ConfigEntryBase entry, out string serialized)
    {
        serialized = "";
        if (!ReferenceEquals(entry.ConfigFile, Config)) return false;
        var data = entry.Description.Tags.OfType<OwnConfigEntryBase>().FirstOrDefault();
        return data != null && ServerSyncSettingsBridge.TrySerializeLocal(!sync.IsSourceOfTruth,
            data.SynchronizedConfig, data.LocalBaseValue, entry.SettingType, TomlTypeConverter.ConvertToString, out serialized);
    }

    private void OnDestroy()
    {
        Instance = null;
        policy?.Dispose();
        catalog?.Dispose();
        classification?.Dispose();
        sources?.Dispose();
        assignments?.Dispose();
        harmony?.UnpatchSelf();
        YAVSOS.Vendor.MultiUserChest.BundledCopy.Deactivate();
    }
}

// Policy lifecycle hooks. No inventory mutation.
internal static class NetPolicyPatches
{
    [HarmonyPatch(typeof(ZNet), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void WorldStarted(ZNet __instance) => Plugin.Instance?.StartWorld(__instance);

    [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Reconnecting(ZNet __instance, ZNetPeer peer) => Plugin.Instance?.StartConnection(__instance, peer);

    // Run after the bundled VersionCheck's normal-priority registration prefix,
    // but before vanilla queues its handshake. Keep the advertisement ahead of
    // peer-info validation and scoped to the current connection.
    [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
    private static void AdvertiseVersion(ZNet __instance, ZNetPeer peer) => Plugin.Instance?.AdvertiseClientVersion(__instance, peer);

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo"), HarmonyPostfix]
    private static void PeerInfo(ZNet __instance, ZRpc rpc) => Plugin.Instance?.ServerPeerInfo(__instance, rpc);

    [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPrefix]
    private static void WorldStopping(ZNet __instance) => Plugin.Instance?.EndWorld(__instance);

    [HarmonyPatch(typeof(ZNet), "OnDestroy"), HarmonyPrefix]
    private static void WorldDestroyed(ZNet __instance) => Plugin.Instance?.EndWorld(__instance);

    [HarmonyPatch(typeof(ZNet), "Disconnect"), HarmonyPrefix]
    private static void PeerDisconnected(ZNet __instance, ZNetPeer peer)
    {
        if (!__instance.IsServer() && Plugin.Instance?.IsActiveServerPeer(peer) == true) Plugin.Instance.EndWorld(__instance);
    }
}

// The pinned library has no public sender hook. Guard our bundled copy's entry points before it
// changes shared values; a callback from an old peer must never be tagged as the current session.
[HarmonyPatch(typeof(ConfigSync), "RPC_FromServerConfigSync")]
internal static class ServerPolicyRpcGuard
{
    [HarmonyPrefix]
    private static bool FromCurrentServer(ConfigSync __instance, ZRpc rpc) => __instance.Name != ProjectIdentity.Guid
        || Plugin.Instance?.IsServerConnection(rpc) == true;
}

[HarmonyPatch(typeof(ConfigSync), "RPC_FromOtherClientConfigSync")]
internal static class ServerPolicyRoutedGuard
{
    [HarmonyPrefix]
    private static bool FromCurrentSession(ConfigSync __instance, long sender) => __instance.Name != ProjectIdentity.Guid
        || Plugin.Instance?.IsManagedServer == true || Plugin.Instance?.IsServerSender(sender) == true;
}

// The game's routed relay retains the original client sender ID and can forward
// even server-refused messages. Keep that relay blocked on clients; publish only
// the canonical settings after the pinned server handler accepts a request.
[HarmonyPatch(typeof(ConfigSync), "HandleConfigSyncRPC")]
internal static class ServerSettingsAcceptedPatch
{
    [HarmonyPrefix]
    private static bool Before(ConfigSync __instance, long sender, ZPackage package, bool clientUpdate, out long? __state)
    {
        __state = null;
        if (__instance.Name != ProjectIdentity.Guid || !clientUpdate || Plugin.Instance?.IsManagedServer != true) return true;
        __state = Plugin.Instance.CaptureSettingsUpdate(sender, package);
        return __state.HasValue;
    }
    [HarmonyPostfix]
    private static void After(ConfigSync __instance, bool __result, long? __state)
    {
        if (__instance.Name == ProjectIdentity.Guid) Plugin.Instance?.CompleteSettingsUpdate(__state, __result);
    }
}

[HarmonyPatch(typeof(ConfigEntryBase), "GetSerializedValue")]
internal static class LocalSharedSettingsSavePatch
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static bool PreserveLocal(ConfigEntryBase __instance, ref string __result)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || !plugin.TrySerializeLocalShared(__instance, out var local)) return true;
        __result = local;
        return false;
    }
}
