using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;
using YAVSOS.Vendor.MultiUserChest;

namespace YAVSOS.Cleanup;

/// <summary>
/// P4-03, P5-03 (D043, D057 O11, D061): whether a move with one side in another player's chest can go through
/// MultiUserChest, and which copy does it: standalone MultiUserChest, else the copy bundled with AzuAutoStore (active
/// unless one of the mods Azu defers to is installed), else YAVSOS's own bundled copy, which steps aside for all of
/// those. Before any request, a MultiUserChest-style intercept must actually be present on
/// <c>Inventory.AddItem(ItemData, int, int, int, bool)</c>, patched by that copy's own Harmony id (D058: a capability,
/// not a version). Every peer publishes the item format of its copy on its player object, and YAVSOS's copy reads and
/// writes each request in the other player's format (D061 Q8). For a player that publishes none (AzuAutoStore without
/// YAVSOS), YAVSOS's copy measures it with an echo probe (D116, <see cref="PeerProbe"/>). Unity game thread only.
/// </summary>
internal static class MucRoute
{
    internal const string StandaloneGuid = MultiUserChestCopies.StandaloneGuid;
    /// <summary>
    /// Player ZDO key: 2 = the peer's copy writes <c>m_cheated</c> (0.6.2), 1 = it does not (Azu 3.1.7), 0 = no copy or
    /// one that can't take requests, <see cref="UnreadableFormat"/> = the copy takes requests but its format couldn't be read.
    /// </summary>
    internal const string FormatKeyName = "com.bastionntb.yavsos.muc-format"; // D078 B07 (was YAVSOS_MucFormat before 1.0.0)
    private static readonly int FormatKey = FormatKeyName.GetStableHashCode();
    // B4.33: kept apart from 0 so other players read "format is unknown", not "can't take requests". Every YAVSOS since
    // 1.0.0 reads any value but 1 and 2 as unknown and refuses, so older players treat it as they treated 0.
    internal const int UnreadableFormat = -1;
    private static int localFormat;
    private static Player? published;
    /// <summary>D116: how often the players in range are checked for one that publishes no format and needs a probe.</summary>
    internal const float ProbeScanEvery = 1f;
    private static float scannedAt = float.MinValue;
    private static ZNet? probeWorld;
    internal static string BundledStatus { get; private set; } = "not checked";
    private static ManualLogSource? logger;

    /// <summary>
    /// After the chainloader has loaded every plugin, before <c>Game.Start</c>: activates YAVSOS's copy when no other
    /// copy is present, then works out the local copy's item format. Any failure leaves YAVSOS's copy off with one message.
    /// </summary>
    internal static void Initialize(ManualLogSource log)
    {
        logger = log;
        var plugins = Chainloader.PluginInfos;
        var choice = MultiUserChestCopies.Choose(plugins.ContainsKey);
        var other = choice == MultiUserChestCopy.Yavsos ? null : new[] { StandaloneGuid, AzuAutoStoreIntegration.AzuGuid }
            .Concat(MultiUserChestCopies.Conflicting).First(plugins.ContainsKey);
        PeerFormat.Resolve = Resolve;
        if (other != null) BundledStatus = "stepped aside for " + other;
        else
        {
            try
            {
                BundledCopy.Activate(log);
                BundledStatus = Intercepted(BundledCopy.HarmonyId) ? "active" : "off: its intercept is missing after patching";
                if (BundledStatus != "active") BundledCopy.Deactivate();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                BundledStatus = "off: " + error.GetType().Name + ": " + error.Message;
            }
        }
        Available(out var source);
        string? cause = null;
        localFormat = source switch
        {
            "" => 0,
            "YAVSOS's MultiUserChest" => 2,
            "MultiUserChest" => WriterFormat(AccessTools.TypeByName("MultiUserChest.InventoryHelper"), out cause),
            _ => WriterFormat(AccessTools.TypeByName("AzuAutoStore.APIs.MUC.MUCSrc.Helper.InventoryHelper"), out cause)
        };
        if (cause != null) localFormat = UnreadableFormat;
        log.LogInfo($"YAVSOS's MultiUserChest: {BundledStatus}. In use: {(source.Length == 0 ? "none" : source)}; item format {Describe(localFormat)}.");
        // SF11 (D087): other players can't tell this copy's format, so their YAVSOS doesn't send to this player's chests.
        if (cause != null)
            log.LogWarning($"{source}'s item format could not be read ({cause}). Other players whose YAVSOS uses its own MultiUserChest " +
                "don't move items into or out of chests your game handles; their result says your MultiUserChest format is unknown. " +
                "Everything else works.");
    }

    internal static bool Available(out string source)
    {
        var (copy, owner) = MultiUserChestCopies.Choose(Chainloader.PluginInfos.ContainsKey) switch
        {
            MultiUserChestCopy.Standalone => ("MultiUserChest", StandaloneGuid),
            MultiUserChestCopy.AzuAutoStore => ("AzuAutoStore's MultiUserChest", AzuAutoStoreIntegration.AzuGuid),
            MultiUserChestCopy.Yavsos when BundledCopy.Active => ("YAVSOS's MultiUserChest", BundledCopy.HarmonyId),
            _ => ("", "")
        };
        source = copy;
        // The intercept must come from the MultiUserChest copy in use, not merely from some other mod's prefix.
        return owner.Length != 0 && Intercepted(owner);
    }

    /// <summary>
    /// D061 Q8: a request to this peer can be read correctly. YAVSOS's copy adapts to the peer's published format and
    /// refuses unknown ones; the other copies write their own format, which a YAVSOS copy on the other side adapts to.
    /// </summary>
    internal static bool OwnerFormatKnown(long owner) => !BundledCopy.Active || PeerFormat.Known(owner);

    /// <summary>
    /// F3: <see cref="OwnerFormatKnown"/> with the cause when it is false, so the result line says why the chest was skipped:
    /// a published 0, an unrecognised published format, or the probe's state for a peer that publishes none.
    /// </summary>
    internal static OwnerMucState OwnerState(long owner)
    {
        if (OwnerFormatKnown(owner)) return OwnerMucState.Usable;
        if (Published(owner) is { } format) return format == 0 ? OwnerMucState.CantTakeRequests : OwnerMucState.FormatUnknown;
        return PeerProbe.Status(owner) switch
        {
            ProbeStatus.NoAnswer => OwnerMucState.NoAnswer,
            ProbeStatus.UnknownAnswer => OwnerMucState.FormatUnknown,
            // A2.11: only loaded players are probed, so an owner out of range stays unknown until they come near;
            // "run again in a moment" is for a probe that is about to go out or is under way (D117).
            ProbeStatus.NotProbed when !Loaded(owner) => OwnerMucState.FormatUnknown,
            _ => OwnerMucState.Checking
        };
    }

    /// <summary>
    /// Publishes the local copy's item format on the local player's object once per spawned player; then, while YAVSOS's
    /// copy is active, probes players that publish none (D116).
    /// </summary>
    internal static void Update()
    {
        Publish();
        Probe();
    }

    private static void Publish()
    {
        var player = Player.m_localPlayer;
        if (!player || ReferenceEquals(player, published)) return;
        var zdo = player.GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null || !zdo.IsOwner()) return;
        // D086 D13 (F3): a copy that can't take requests publishes 0, so other players' YAVSOS skips chests this player's
        // game handles instead of sending requests nothing would answer. Checked here, after every plugin has patched.
        var usable = Available(out var source);
        zdo.Set(FormatKey, usable ? localFormat : 0);
        if (!usable && source.Length != 0 && !warnedUnusable)
        {
            warnedUnusable = true;
            logger?.LogWarning($"{source} is installed, but its intercept on Inventory.AddItem is missing, so it can't take requests from other players. " +
                "Their YAVSOS doesn't move items into or out of chests your game handles; their result says your MultiUserChest " +
                "can't take requests. Everything else works.");
        }
        published = player;
    }
    private static bool warnedUnusable;

    // D116: each remote player in range without a format key is probed once per session id (retries bounded by
    // FormatProbeBook). The cache is dropped with the world, and per peer when it has left: no longer in the player
    // list and no player object of its own loaded (the list can lag behind a new player's object).
    private static void Probe()
    {
        if (!ReferenceEquals(ZNet.instance, probeWorld)) { PeerProbe.Clear(); probeWorld = ZNet.instance; scannedAt = float.MinValue; }
        if (!BundledCopy.Active || !ZNet.instance || ZRoutedRpc.instance == null || !Player.m_localPlayer) return;
        var now = Time.unscaledTime;
        if (now - scannedAt < ProbeScanEvery) return;
        scannedAt = now;
        var self = ZDOMan.GetSessionID();
        var loaded = new HashSet<long>();
        foreach (var player in Player.GetAllPlayers())
        {
            var zdo = player ? player.GetComponent<ZNetView>()?.GetZDO() : null;
            var peer = zdo?.GetOwner() ?? 0;
            if (zdo == null || peer == 0 || peer == self) continue;
            loaded.Add(peer);
            if (!zdo.GetInt(FormatKey, out _)) PeerProbe.Probe(peer, now);
        }
        var listed = new HashSet<long>(ZNet.instance.GetPlayerList().Select(x => x.m_characterID.UserID));
        PeerProbe.Update(now, peer => loaded.Contains(peer) || listed.Contains(peer));
    }

    // D116: the format a peer publishes on its player object; else, with no key at all, what its probe measured. A
    // published 0 (no usable copy, D13) is final: such a peer is not probed.
    private static bool? Resolve(long peer) =>
        Published(peer) is { } format ? format switch { 2 => true, 1 => false, _ => (bool?)null } : PeerProbe.Of(peer);

    // The peer's player object is loaded here: only loaded players are probed.
    private static bool Loaded(long peer) =>
        Player.GetAllPlayers().Any(player => player && player.GetComponent<ZNetView>()?.GetZDO()?.GetOwner() == peer);

    // The format key on the peer's loaded player object; null when it has none or its player isn't loaded.
    private static int? Published(long peer)
    {
        foreach (var player in Player.GetAllPlayers())
        {
            var zdo = player ? player.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo == null || zdo.GetOwner() != peer) continue;
            return zdo.GetInt(FormatKey, out var format) ? format : null;
        }
        return null;
    }

    private static bool Intercepted(string owner)
    {
        var add = AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
        var patches = add == null ? null : Harmony.GetPatchInfo(add);
        return patches != null && patches.Prefixes.Any(x => StringComparer.Ordinal.Equals(x.owner, owner));
    }

    // A capability check on the copy's item writer (D058): does it write ItemData.m_cheated? 0 when it cannot be read.
    // cause: why the result is 0, for the log (SF11); null otherwise.
    private static int WriterFormat(Type? helper, out string? cause)
    {
        cause = null;
        try
        {
            var writer = helper == null ? null : AccessTools.DeclaredMethod(helper, "WriteItemToPackage");
            var cheated = AccessTools.DeclaredField(typeof(ItemDrop.ItemData), "m_cheated");
            if (helper == null) { cause = "its InventoryHelper type not found"; return 0; }
            if (writer == null) { cause = "InventoryHelper.WriteItemToPackage not found"; return 0; }
            if (cheated == null) { cause = "the game has no ItemData.m_cheated"; return 0; }
            var instructions = PatchProcessor.ReadMethodBody(writer).ToList();
            var writes = instructions.Count(x => x.Key == OpCodes.Call || x.Key == OpCodes.Callvirt);
            if (writes == 0) { cause = "WriteItemToPackage writes nothing YAVSOS recognises"; return 0; }
            return instructions.Any(x => x.Key == OpCodes.Ldfld && Equals(x.Value, cheated)) ? 2 : 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { cause = "reading WriteItemToPackage failed: " + error.GetType().Name + ": " + error.Message; return 0; }
    }

    private static string Describe(int format) => format switch { 2 => "0.6.2 (with cheated flag)", 1 => "older (without cheated flag)", _ => "unknown" };
}
