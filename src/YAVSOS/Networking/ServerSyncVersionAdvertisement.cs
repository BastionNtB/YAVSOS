using System;
using System.IO;
using YAVSOS.Core;

namespace YAVSOS.Networking;

// ServerSync v1.20 only advertises from clients when ModRequired is true.
// Send the pinned wire format explicitly for optional clients, keeping their
// ability to join unmanaged servers without weakening managed-server checks.
internal static class ServerSyncVersionAdvertisement
{
    public static bool TrySend(bool isServer, bool modRequired, string currentVersion,
        string minimumVersion, Action<string, byte[]> send)
    {
        if (isServer || modRequired) return false; // The library already sends for these cases.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            // ZPackage.Write(string) uses BinaryWriter.Write(string). CheckVersion reads
            // name, minimum, then current; this order is a pinned dependency contract.
            writer.Write(ProjectIdentity.Guid);
            writer.Write(minimumVersion);
            writer.Write(currentVersion);
        }
        send("ServerSync VersionCheck", stream.ToArray());
        return true;
    }
}
