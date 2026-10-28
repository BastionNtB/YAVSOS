using System;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
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
/// writes each request in the other player's format (D061 Q8). Unity game thread only.
/// </summary>
internal static class MucRoute
{
    internal const string StandaloneGuid = MultiUserChestCopies.StandaloneGuid;
    /// <summary>Player ZDO key: 2 = the peer's copy writes <c>m_cheated</c> (0.6.2), 1 = it does not (Azu 3.1.7), 0 = no copy or unknown.</summary>
    internal const string FormatKeyName = "com.bastionntb.yavsos.muc-format"; // D078 B07 (was YAVSOS_MucFormat before 1.0.0)
    private static readonly int FormatKey = FormatKeyName.GetStableHashCode();
    private static int localFormat;
    private static Player? published;
    internal static string BundledStatus { get; private set; } = "not checked";

    /// <summary>
    /// After the chainloader has loaded every plugin, before <c>Game.Start</c>: activates YAVSOS's copy when no other
    /// copy is present, then works out the local copy's item format. Any failure leaves YAVSOS's copy off with one message.
    /// </summary>
    internal static void Initialize(ManualLogSource log)
    {
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
        localFormat = source switch
        {
            "" => 0,
            "YAVSOS's MultiUserChest" => 2,
            "MultiUserChest" => WriterFormat(AccessTools.TypeByName("MultiUserChest.InventoryHelper")),
            _ => WriterFormat(AccessTools.TypeByName("AzuAutoStore.APIs.MUC.MUCSrc.Helper.InventoryHelper"))
        };
        log.LogInfo($"YAVSOS's MultiUserChest: {BundledStatus}. In use: {(source.Length == 0 ? "none" : source)}; item format {Describe(localFormat)}.");
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

    /// <summary>Publishes the local copy's item format on the local player's object once per spawned player.</summary>
    internal static void Update()
    {
        var player = Player.m_localPlayer;
        if (!player || ReferenceEquals(player, published)) return;
        var zdo = player.GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null || !zdo.IsOwner()) return;
        zdo.Set(FormatKey, localFormat);
        published = player;
    }

    private static bool? Resolve(long peer)
    {
        foreach (var player in Player.GetAllPlayers())
        {
            var zdo = player ? player.GetComponent<ZNetView>()?.GetZDO() : null;
            if (zdo == null || zdo.GetOwner() != peer) continue;
            return zdo.GetInt(FormatKey) switch { 2 => true, 1 => false, _ => (bool?)null };
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
    private static int WriterFormat(Type? helper)
    {
        try
        {
            var writer = helper == null ? null : AccessTools.DeclaredMethod(helper, "WriteItemToPackage");
            var cheated = AccessTools.DeclaredField(typeof(ItemDrop.ItemData), "m_cheated");
            if (writer == null || cheated == null) return 0;
            var instructions = PatchProcessor.ReadMethodBody(writer).ToList();
            var writes = instructions.Count(x => x.Key == OpCodes.Call || x.Key == OpCodes.Callvirt);
            if (writes == 0) return 0;
            return instructions.Any(x => x.Key == OpCodes.Ldfld && Equals(x.Value, cheated)) ? 2 : 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return 0; }
    }

    private static string Describe(int format) => format switch { 2 => "0.6.2 (with cheated flag)", 1 => "older (without cheated flag)", _ => "unknown" };
}
