using System;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class TransferOperationTests
{
    private static TransferOperation Operation()
    {
        var item = new TransferItemIdentity("Wood", "catalog-a", 1, 0, 0, 1, 0, "", true, false,
            Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>(), "vanilla-1", Array.Empty<byte>());
        var selected = Guid.NewGuid();
        var source = new TransferInventorySnapshot(new TransferInventoryStamp("player", Guid.NewGuid(), 0, 0, 1, 1, "payload"),
            1, 1, new[] { new TransferStack(selected, 0, 0, 10, 50, item) }, Array.Empty<int>());
        var dest = new TransferInventorySnapshot(new TransferInventoryStamp("chest", Guid.NewGuid(), 0, 0, 1, 1, "payload"),
            1, 1, Array.Empty<TransferStack>(), Array.Empty<int>());
        Assert.Equal(TransferRefusal.None, DepositPlan.TryCreateAt(Guid.NewGuid(), Guid.NewGuid(), "policy", "rule", source,
            dest, selected, 10, 0, 0, null, 0, 5, out var plan));
        return new TransferOperation(Assert.IsType<DepositPlan>(plan));
    }
    private static TransferOperation Applying()
    {
        var op = Operation(); Assert.Equal(TransferRefusal.None, op.BeginApplying(TransferRefusal.None)); return op;
    }
    [Fact]
    public void Lifecycle_VerifiedLiveResult_ReportsExactUnitsAndIsTerminal()
    {
        var op = Applying(); Assert.False(op.IsTerminal); Assert.True(op.MayHaveExecuted);
        Assert.Equal(TransferRefusal.None, op.Verify(4, 4, true, true, true));
        Assert.Equal(TransferExecutionState.Verified, op.State);
        Assert.Equal(4, op.VerifiedMovedUnits); Assert.Equal(6, op.VerifiedRemainingUnits); Assert.True(op.IsTerminal);
    }
    [Fact]
    public void Lifecycle_FailedFreshValidation_IsADefiniteZeroRefusal()
    {
        var op = Operation(); Assert.Equal(TransferRefusal.InventoryChanged, op.BeginApplying(TransferRefusal.InventoryChanged));
        Assert.Equal(TransferExecutionState.Refused, op.State); Assert.Equal(0, op.VerifiedMovedUnits);
        Assert.False(op.MayHaveExecuted); Assert.Equal(TransferRefusal.InventoryChanged, op.Reason);
        Assert.Throws<ArgumentException>(() => Operation().BeginApplying(TransferRefusal.Unconfirmed));
    }
    [Fact]
    public void Lifecycle_RefusalBeforeTheMove_IsDefinite()
    {
        var op = Operation(); Assert.Equal(TransferRefusal.OwnerChanged, op.Refuse(TransferRefusal.OwnerChanged));
        Assert.Equal(TransferExecutionState.Refused, op.State); Assert.Equal(0, op.VerifiedMovedUnits);
    }
    [Theory]
    [InlineData(TransferRefusal.Expired)]
    [InlineData(TransferRefusal.Disconnected)]
    [InlineData(TransferRefusal.PolicyChanged)]
    [InlineData(TransferRefusal.OwnerChanged)]
    public void Lifecycle_PossibleExecutionFailure_IsUnconfirmedAndNeverRetried(TransferRefusal reason)
    {
        var op = Applying(); Assert.Equal(TransferRefusal.Unconfirmed, op.Refuse(reason));
        Assert.Null(op.VerifiedMovedUnits); Assert.Null(op.VerifiedRemainingUnits); Assert.True(op.IsTerminal);
        Assert.Equal(reason, op.Reason);
        Assert.Throws<InvalidOperationException>(() => op.BeginApplying(TransferRefusal.None));
        Assert.Equal(TransferRefusal.Unconfirmed, op.Refuse(TransferRefusal.Expired)); Assert.Equal(reason, op.Reason);
    }
    [Theory]
    [InlineData(0, 0, true, true, true, 0)]
    [InlineData(-1, -1, true, true, true, 0)]
    [InlineData(11, 11, true, true, true, 0)]
    [InlineData(3, 4, true, true, true, 0)]
    [InlineData(4, 4, false, true, true, 0)]
    [InlineData(4, 4, true, false, true, 0)]
    [InlineData(4, 4, true, true, false, 0)]
    [InlineData(4, 4, true, true, true, 1)]
    public void Lifecycle_UnexplainedDeltasOrChangedMetadataOwnerOrGround_AreUnconfirmed(int source, int destination, bool identity,
        bool owner, bool finalState, int ground)
    {
        var op = Applying(); Assert.Equal(TransferRefusal.Unconfirmed, op.Verify(source, destination, identity, owner, finalState, ground));
        Assert.Equal(TransferExecutionState.Unconfirmed, op.State); Assert.Null(op.VerifiedMovedUnits);
        Assert.Equal(TransferRefusal.InvariantFailed, op.Reason);
    }
    [Fact]
    public void Lifecycle_IllegalOrRepeatedTransitions_DoNotExecuteAgain()
    {
        var op = Operation(); op.BeginApplying(TransferRefusal.None);
        Assert.Throws<InvalidOperationException>(() => op.BeginApplying(TransferRefusal.None));
        op.Verify(10, 10, true, true, true);
        Assert.Throws<InvalidOperationException>(() => op.Verify(10, 10, true, true, true));
        Assert.Throws<InvalidOperationException>(() => op.Refuse(TransferRefusal.Expired));
        Assert.Throws<ArgumentException>(() => Operation().Refuse(TransferRefusal.Unconfirmed));
        Assert.Throws<InvalidOperationException>(() => Operation().Verify(1, 1, true, true, true));
    }
}
