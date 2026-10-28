using System;
using Xunit;
using YAVSOS.Networking;

namespace YAVSOS.Core.Tests;

public sealed class PeerIdentityGateTests
{
    [Fact]
    public void ReplaceAndDisconnect_RejectOldRpcBeforeItCanChangeSynchronizedValues()
    {
        var gate = new PeerIdentityGate();
        var oldRpc = new object();
        var newRpc = new object();
        Assert.False(gate.Accepts(oldRpc));
        gate.Replace(oldRpc);
        Assert.True(gate.Accepts(oldRpc));
        Assert.False(gate.Accepts(newRpc));
        gate.Replace(newRpc);
        Assert.False(gate.Accepts(oldRpc));
        Assert.True(gate.Accepts(newRpc));
        gate.Clear();
        Assert.False(gate.Accepts(oldRpc));
        Assert.False(gate.Accepts(newRpc));
    }

    [Fact]
    public void Replace_NullPeer_RefusesInvalidConnection()
    {
        var gate = new PeerIdentityGate();
        Assert.Throws<ArgumentNullException>(() => gate.Replace(null!));
        Assert.False(gate.Accepts(new object()));
    }
}
