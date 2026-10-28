using System;

namespace YAVSOS.Networking;

internal sealed class PeerIdentityGate
{
    private object? current;
    public void Replace(object rpc) => current = rpc ?? throw new ArgumentNullException(nameof(rpc));
    public void Clear() => current = null;
    public bool Accepts(object rpc) => current != null && ReferenceEquals(current, rpc);
}
