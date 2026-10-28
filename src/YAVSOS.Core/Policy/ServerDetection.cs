namespace YAVSOS.Core.Policy;

public enum ServerYavsos { Unknown, Present, Absent }

/// <summary>
/// D044: whether the server has YAVSOS, decided once when the server's peer info arrives. A server with YAVSOS sends
/// its version check and its initial settings before that point, so either one means present. Unknown (the check
/// could not be read) keeps the client waiting for managed policy, which is the safe default.
/// </summary>
public static class ServerDetection
{
    public static ServerYavsos AtHandshake(bool readable, bool versionReceived, bool settingsReceived) =>
        !readable ? ServerYavsos.Unknown : versionReceived || settingsReceived ? ServerYavsos.Present : ServerYavsos.Absent;
}
