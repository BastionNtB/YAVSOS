using Xunit;
using YAVSOS.Core.Policy;
using YAVSOS.Networking;

namespace YAVSOS.Core.Tests;

public sealed class PolicyMailboxTests
{
    [Fact]
    public void RemoteBurst_KeepsOnlyLatestCompleteSnapshot()
    {
        var mailbox = new PolicyMailbox();
        mailbox.Reset(5);
        mailbox.PostRemote(5, "first");
        mailbox.PostRemote(5, "second");
        mailbox.PostRemote(5, "latest");
        Assert.Equal("latest", mailbox.TakeRemote(5));
        Assert.Null(mailbox.TakeRemote(5));
    }

    [Fact]
    public void SessionSwitch_DropsQueuedAndDelayedOldPeerWork()
    {
        var mailbox = new PolicyMailbox();
        mailbox.Reset(5);
        mailbox.PostRemote(5, "queued-old");
        mailbox.Reset(6);
        mailbox.PostRemote(5, "late-old");
        Assert.Null(mailbox.TakeRemote(6));
        mailbox.PostRemote(6, "new");
        Assert.Null(mailbox.TakeRemote(5));
        Assert.Equal("new", mailbox.TakeRemote(6));
    }

    [Fact]
    public void OversizePayload_QueuesRejectionInsteadOfSilentlyRetainingReadiness()
    {
        var mailbox = new PolicyMailbox();
        mailbox.Reset(1);
        mailbox.PostRemote(1, CategoryPolicyCodec.EmptyDocument);
        mailbox.PostRemote(1, new string('a', CategoryPolicyCodec.MaxPayloadBytes + 1));
        Assert.Equal("", mailbox.TakeRemote(1));
    }

    [Fact]
    public void NullPayload_QueuesRejectionWithoutThrowingInNetworkCallback()
    {
        var mailbox = new PolicyMailbox();
        mailbox.Reset(1);
        mailbox.PostRemote(1, null);
        Assert.Equal("", mailbox.TakeRemote(1));
    }

    [Fact]
    public void WatcherBurst_CoalescesOneReloadFlag()
    {
        var mailbox = new PolicyMailbox();
        for (var i = 0; i < 100; i++) mailbox.MarkFileDirty();
        Assert.True(mailbox.TakeFileDirty());
        Assert.False(mailbox.TakeFileDirty());
        mailbox.MarkFileDirty();
        Assert.True(mailbox.TakeFileDirty());
    }
}
