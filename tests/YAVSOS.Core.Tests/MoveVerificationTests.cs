using System;
using System.Collections.Generic;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

/// <summary>D07: the shared conservation checks of a direct move, as both runners apply them.</summary>
public sealed class MoveVerificationTests
{
    private static readonly Guid Instance = Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671");
    private static TransferItemIdentity Item(string prefab = "Wood", long crafter = 42) =>
        new(prefab, "catalog-a", 1, 0, 0, 100, crafter, "Crafter", true, false, new Dictionary<string, string>(), "vanilla-v1", Array.Empty<byte>());
    private static TransferStack Stack(int x, int count, TransferItemIdentity? item = null, string? mergeClass = "Wood", Guid? handle = null) =>
        new(handle ?? Guid.NewGuid(), x, 0, count, 50, item ?? Item(), mergeClass: mergeClass);
    private static TransferInventorySnapshot Snapshot(string id, params TransferStack[] stacks) =>
        new(new TransferInventoryStamp(id, Instance, 0, 0, 12, 3, "payload"), 4, 1, stacks, Array.Empty<int>());

    [Fact]
    public void Observe_ConservedMoveIntoTheCell_IsCountedByStackKey()
    {
        var handle = Guid.NewGuid();
        var sourceBefore = Snapshot("player", Stack(0, 10, handle: handle), Stack(1, 3, Item("Stone"), "Stone"));
        var destinationBefore = Snapshot("chest", Stack(0, 5, Item(crafter: 7)));
        var key = MoveVerification.MergeKey(sourceBefore, handle, Item());
        Assert.Equal("class:Wood", key);
        // A stack crafted by someone else of the same class merges and still counts (D061 Q11).
        var observed = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(0, 4, handle: handle), Stack(1, 3, Item("Stone"), "Stone")), Snapshot("chest", Stack(0, 11, Item(crafter: 7))), key, 0, 0);
        Assert.Equal(6, observed.Decrease); Assert.Equal(6, observed.Increase);
        Assert.True(observed.OthersUnchanged); Assert.True(observed.CellHoldsKey); Assert.False(observed.NothingChanged);
    }

    [Fact]
    public void Observe_DetectsOtherChangesWrongCellAndNothingChanged()
    {
        var handle = Guid.NewGuid();
        var sourceBefore = Snapshot("player", Stack(0, 10, handle: handle), Stack(1, 3, Item("Stone"), "Stone"));
        var destinationBefore = Snapshot("chest");
        var key = MoveVerification.MergeKey(sourceBefore, handle, Item());
        var otherLost = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 2, Item("Stone"), "Stone")), Snapshot("chest", Stack(0, 10)), key, 0, 0);
        Assert.False(otherLost.OthersUnchanged);
        var wrongCell = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 3, Item("Stone"), "Stone")), Snapshot("chest", Stack(2, 10)), key, 0, 0);
        Assert.False(wrongCell.CellHoldsKey); Assert.True(wrongCell.OthersUnchanged);
        var refused = MoveVerification.Observe(sourceBefore, destinationBefore, sourceBefore, destinationBefore, key, 0, 0);
        Assert.True(refused.NothingChanged); Assert.Equal(0, refused.Decrease);
    }

    private static TransferOperation Applying(TransferInventorySnapshot source, TransferInventorySnapshot destination, Guid handle, int units)
    {
        Assert.Equal(TransferRefusal.None, DepositPlan.TryCreateAt(Guid.NewGuid(), Guid.NewGuid(), "policy", "rule", source, destination,
            handle, units, 0, 0, null, 0, 5, out var plan));
        var operation = new TransferOperation(Assert.IsType<DepositPlan>(plan));
        Assert.Equal(TransferRefusal.None, operation.BeginApplying(TransferRefusal.None));
        return operation;
    }

    [Fact]
    public void Verify_ConservedMoveIntoAnotherCell_IsMoved()
    {
        // F13 (D129): a mod's inventory (Storehouse's typed slots) places the item elsewhere; the counts still prove the move.
        var handle = Guid.NewGuid();
        var sourceBefore = Snapshot("player", Stack(0, 10, handle: handle), Stack(1, 3, Item("Stone"), "Stone"));
        var destinationBefore = Snapshot("chest");
        var key = MoveVerification.MergeKey(sourceBefore, handle, Item());
        var seen = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 3, Item("Stone"), "Stone")), Snapshot("chest", Stack(2, 10)), key, 0, 0);
        Assert.False(seen.CellHoldsKey);
        var operation = Applying(sourceBefore, destinationBefore, handle, 10);
        Assert.Equal(TransferRefusal.None, MoveVerification.Verify(operation, seen, ownersCurrent: true));
        Assert.Equal(TransferExecutionState.Verified, operation.State); Assert.Equal(10, operation.VerifiedMovedUnits);
    }

    [Fact]
    public void Verify_WithoutTheCellCheck_StillRefusesChangedCountsAndOwners()
    {
        var handle = Guid.NewGuid();
        var sourceBefore = Snapshot("player", Stack(0, 10, handle: handle), Stack(1, 3, Item("Stone"), "Stone"));
        var destinationBefore = Snapshot("chest");
        var key = MoveVerification.MergeKey(sourceBefore, handle, Item());
        var moved = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 3, Item("Stone"), "Stone")), Snapshot("chest", Stack(2, 10)), key, 0, 0);
        var otherLost = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 2, Item("Stone"), "Stone")), Snapshot("chest", Stack(2, 10)), key, 0, 0);
        var unitsLost = MoveVerification.Observe(sourceBefore, destinationBefore,
            Snapshot("player", Stack(1, 3, Item("Stone"), "Stone")), Snapshot("chest", Stack(2, 9)), key, 0, 0);
        Assert.Equal(TransferRefusal.Unconfirmed, MoveVerification.Verify(Applying(sourceBefore, destinationBefore, handle, 10), otherLost, true));
        Assert.Equal(TransferRefusal.Unconfirmed, MoveVerification.Verify(Applying(sourceBefore, destinationBefore, handle, 10), unitsLost, true));
        Assert.Equal(TransferRefusal.Unconfirmed, MoveVerification.Verify(Applying(sourceBefore, destinationBefore, handle, 10), moved, false));
        Assert.Throws<ArgumentNullException>(() => MoveVerification.Verify(null!, moved, true));
    }

    [Fact]
    public void MergeKey_FallsBackToTheIdentityWhenTheStackIsGone_AndCountsSplitDelegatedState()
    {
        var gone = MoveVerification.MergeKey(Snapshot("player"), Guid.NewGuid(), Item());
        Assert.Equal(Item().Fingerprint, gone);
        var counts = MoveVerification.Counts(Snapshot("player", Stack(0, 4), Stack(1, 6)), Snapshot("chest", Stack(0, 2), Stack(1, 9, Item("Stone"), "Stone")), "class:Wood");
        Assert.Equal((10, 2, 9), counts);
    }

    [Fact]
    public void SameExcept_IgnoresOnlyTheMovedKey()
    {
        var before = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        Assert.True(MoveVerification.SameExcept(before, new Dictionary<string, int> { ["a"] = 5, ["b"] = 2 }, "a"));
        Assert.False(MoveVerification.SameExcept(before, new Dictionary<string, int> { ["a"] = 1 }, "a"));
        Assert.False(MoveVerification.SameExcept(before, new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 1 }, "a"));
    }
}
