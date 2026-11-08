using System;
using System.Collections.Generic;

namespace YAVSOS.Core.Templates;

/// <summary>
/// X23 (D157): whether this player is on the server's admin list, for which template buttons the panel shows. Valheim's
/// client check (<c>ZNet.PlayerIsAdmin</c>) looks only for the platform form ("Steam_7656…"), while its server check
/// (<c>ZNet.ListContainsId</c>) also accepts the bare Steam number, which is how most admin lists are written; so a real
/// admin's panel hid the buttons (batch 2 session B, 2026-11-07). This matches the server's rule. The server still
/// decides each change.
/// </summary>
public static class AdminListMatch
{
    /// <param name="platformId">The player's ID in its platform form, such as "Steam_76561190000000001".</param>
    /// <param name="bareId">The same ID without the platform, such as "76561190000000001".</param>
    /// <param name="steam">The ID is a Steam one (the server accepts the bare form only for Steam).</param>
    public static bool Contains(IReadOnlyCollection<string>? adminList, string? platformId, string? bareId, bool steam)
    {
        if (adminList == null || adminList.Count == 0) return false;
        foreach (var entry in adminList)
        {
            var id = entry?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (!string.IsNullOrEmpty(platformId) && string.Equals(id, platformId, StringComparison.Ordinal)) return true;
            if (steam && !string.IsNullOrEmpty(bareId) && string.Equals(id, bareId, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
