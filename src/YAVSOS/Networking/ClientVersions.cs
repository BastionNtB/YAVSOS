using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using YAVSOS.Core;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

// X48 option C (D130): on a server, remember the YAVSOS version each connection's version check reported, so the log can
// name a player whose YAVSOS can't read a schema-2 categories file. ServerSync keeps only the latest received version in
// one field, which another joining player can overwrite before this player's peer info arrives, so the bundled copy's
// handler is read per package instead. Its private signature is looked up first (D058); if it is missing, only this
// warning turns off.
[HarmonyPatch]
internal static class ClientVersions
{
    private sealed class Reported { internal string Version = ""; }
    private static readonly ConditionalWeakTable<ZRpc, Reported> versions = new ConditionalWeakTable<ZRpc, Reported>();

    private static MethodBase TargetMethod() =>
        AccessTools.DeclaredMethod(typeof(VersionCheck), "CheckVersion", new[] { typeof(ZRpc), typeof(ZPackage), typeof(Action<ZRpc, ZPackage>) })
        ?? throw new MissingMethodException(nameof(VersionCheck), "CheckVersion");

    // Reads the package ahead of ServerSync and puts its position back, so the version check itself sees it unchanged.
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ZRpc rpc, ZPackage pkg)
    {
        if (rpc == null || pkg == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        var position = pkg.GetPos();
        try
        {
            if (pkg.ReadString() != ProjectIdentity.Guid) return;
            pkg.ReadString(); // Minimum version; CheckVersion reads name, minimum, then current (D010).
            var current = pkg.ReadString();
            versions.Remove(rpc);
            versions.Add(rpc, new Reported { Version = current });
        }
        catch (Exception error) when (error is IOException || error is ArgumentException || error is ObjectDisposedException) { }
        finally { pkg.SetPos(position); }
    }

    internal static string? Of(ZRpc rpc) => rpc != null && versions.TryGetValue(rpc, out var reported) ? reported.Version : null;

    /// <summary>After the server accepted a peer's info: warn if its YAVSOS can't read the schema-2 file being served.</summary>
    internal static void Joined(ZNet net, ZRpc rpc, CategoryPolicy? served, ManualLogSource log)
    {
        if (served == null || served.Schema < 2) return;
        foreach (var peer in net.GetPeers())
            if (ReferenceEquals(peer.m_rpc, rpc))
            {
                Warn(peer, served, true, log);
                return;
            }
    }

    /// <summary>After the server accepted a changed file: warn for each connected player whose YAVSOS can't read it.</summary>
    internal static void Changed(ZNet net, CategoryPolicy served, ManualLogSource log)
    {
        if (served.Schema < 2) return;
        foreach (var peer in net.GetPeers()) Warn(peer, served, false, log);
    }

    private static void Warn(ZNetPeer peer, CategoryPolicy served, bool joined, ManualLogSource log)
    {
        if (peer?.m_rpc == null || peer.m_uid == 0) return; // m_uid is set only once the game accepted the peer's info.
        var line = PolicyReaders.OlderClientWarning(string.IsNullOrEmpty(peer.m_playerName) ? "A player" : peer.m_playerName,
            Of(peer.m_rpc), served.Schema, joined);
        if (line != null) log.LogWarning(line);
    }
}
