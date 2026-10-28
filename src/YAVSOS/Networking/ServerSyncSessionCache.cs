using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using ServerSync;

namespace YAVSOS.Networking;

// The v1.20 fragment cache is not session-scoped and has no public clear API.
// Resolve only our pinned, internalized dependency; fail initialization if its shape changes.
internal sealed class ServerSyncSessionCache
{
    private readonly IDictionary fragments;
    private readonly IList expirations;
    private readonly FieldInfo incomingRpc;
    public ZRpc? IncomingRpc => incomingRpc.GetValue(null) as ZRpc;
    public ServerSyncSessionCache(ConfigSync sync)
    {
        fragments = AccessTools.Field(typeof(ConfigSync), "configValueCache")?.GetValue(sync) as IDictionary
            ?? throw new NotSupportedException("Pinned ServerSync fragment-cache contract is unavailable.");
        expirations = AccessTools.Field(typeof(ConfigSync), "cacheExpirations")?.GetValue(sync) as IList
            ?? throw new NotSupportedException("Pinned ServerSync cache-expiration contract is unavailable.");
        incomingRpc = AccessTools.Field(AccessTools.Inner(typeof(ConfigSync), "SnatchCurrentlyHandlingRPC"), "currentRpc")
            ?? throw new NotSupportedException("Pinned ServerSync routed-message ingress contract is unavailable.");
        if (incomingRpc.FieldType != typeof(ZRpc) || !incomingRpc.IsStatic)
            throw new NotSupportedException("Pinned ServerSync routed-message ingress type is incompatible.");
    }
    public void Clear() { fragments.Clear(); expirations.Clear(); }
}
