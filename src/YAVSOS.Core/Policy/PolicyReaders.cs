using System;

namespace YAVSOS.Core.Policy;

/// <summary>
/// X48 option C (D130): which clients can read the categories file the server publishes, and the server's log line for
/// one that can't. A client that refuses the policy is safe (its category rules take nothing until it updates), so this
/// only tells the admin who is affected and why.
/// </summary>
public static class PolicyReaders
{
    private static readonly Version Schema2 = Version.Parse(CategoryPolicyCodec.Schema2ReaderVersion);

    /// <summary>False only when the client reported a readable YAVSOS version older than the schema needs.</summary>
    public static bool Reads(string? clientVersion, int schema)
    {
        if (schema < 2 || !Version.TryParse(clientVersion, out var version)) return true; // Unknown: nothing to report.
        return version >= Schema2;
    }

    /// <summary>The server's warning when <paramref name="player"/>'s YAVSOS can't read a schema-2 file; otherwise null.</summary>
    /// <param name="joined">True when the player is joining; false when the file changed while they were connected.</param>
    public static string? OlderClientWarning(string player, string? clientVersion, int schema, bool joined)
    {
        if (Reads(clientVersion, schema)) return null;
        var who = joined ? $"{player} joined with YAVSOS {clientVersion}" : $"{player} is connected with YAVSOS {clientVersion}";
        return who + $", which can't read this server's categories file: it adds or removes categories (schema {schema}), which needs " +
            $"YAVSOS {CategoryPolicyCodec.Schema2ReaderVersion} or later. Until they update, category rules take nothing for them; " +
            "Always and Never items still work. To serve older versions, use only member lines in the file.";
    }
}
