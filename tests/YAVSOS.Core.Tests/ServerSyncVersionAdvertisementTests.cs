using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using YAVSOS.Networking;

namespace YAVSOS.Core.Tests;

public sealed class ServerSyncVersionAdvertisementTests
{
    [Fact]
    public void OptionalClient_AdvertisesGuidMinimumAndCurrentVersionInPinnedWireOrder()
    {
        var sent = new List<(string Rpc, byte[] Payload)>();

        Assert.True(ServerSyncVersionAdvertisement.TrySend(false, false, "0.2.9", "0.2.1",
            (rpc, payload) => sent.Add((rpc, payload))));

        var message = Assert.Single(sent);
        Assert.Equal("ServerSync VersionCheck", message.Rpc);
        // Parse independently as the pinned receiver does, rather than round-tripping
        // through another method on the sender. No game/Unity runtime is required.
        using var stream = new MemoryStream(message.Payload);
        using var reader = new BinaryReader(stream);
        Assert.Equal("com.bastionntb.yavsos", reader.ReadString());
        Assert.Equal("0.2.1", reader.ReadString()); // minimum
        Assert.Equal("0.2.9", reader.ReadString()); // current: different to catch swapped fields
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Server_DoesNotDuplicateServerSyncAdvertisement(bool modRequired)
    {
        var sent = new List<byte[]>();

        Assert.False(ServerSyncVersionAdvertisement.TrySend(true, modRequired, "0.2.9", "0.2.1",
            (_, payload) => sent.Add(payload)));

        Assert.Empty(sent);
    }

    [Fact]
    public void RequiredClient_LeavesAdvertisementToServerSync()
    {
        var sent = new List<byte[]>();

        Assert.False(ServerSyncVersionAdvertisement.TrySend(false, true, "0.2.9", "0.2.1",
            (_, payload) => sent.Add(payload)));

        Assert.Empty(sent);
    }
}
