using System;
using System.Collections;
using System.Reflection;
using ServerSync;
using YAVSOS.Core;

namespace YAVSOS.Networking;

// D044: did the server send YAVSOS's version check on this connection? The pinned ServerSync copy bundled in
// YAVSOS.dll records it in a private field, cleared for each new connection, so read it by name after a shape
// check (D058). If the shape differs, detection is off and clients keep waiting for managed policy.
internal static class ServerYavsosProbe
{
    private static readonly FieldInfo? Checks = typeof(VersionCheck).GetField("versionChecks", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo? Received = typeof(VersionCheck).GetField("ReceivedCurrentVersion", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool Supported => Checks != null && typeof(IEnumerable).IsAssignableFrom(Checks.FieldType)
        && Received != null && Received.FieldType == typeof(string);

    /// <summary>False when the shape check fails or YAVSOS's own check is not registered.</summary>
    internal static bool TryVersionReceived(out bool received)
    {
        received = false;
        if (!Supported) return false;
        try
        {
            if (Checks!.GetValue(null) is not IEnumerable all) return false;
            foreach (var check in all)
                if (check is VersionCheck version && version.Name == ProjectIdentity.Guid)
                {
                    received = Received!.GetValue(version) != null;
                    return true;
                }
        }
        catch (Exception error) when (error is TargetException || error is FieldAccessException || error is InvalidOperationException) { }
        return false;
    }
}
