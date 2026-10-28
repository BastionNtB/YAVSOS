using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class TransferPlanningTests
{
    private static readonly Guid Instance = Guid.Parse("18a499ef-25e2-4d38-bbb7-e7810bfe7671");
    private static readonly Guid Session = Guid.Parse("8cb34bd1-4c4b-47e7-b9ae-66d249917eee");
    private static readonly Guid Selected = Guid.Parse("c4a90b5c-70ef-4e1e-8a09-be036ff31b37");
    private static readonly Guid Target = Guid.Parse("7dbb3f04-4fc0-4092-bbf1-9e16f53f9928");
    private static TransferItemIdentity Item(string prefab = "Wood", string registration = "catalog-a", int quality = 1,
        int variant = 0, int worldLevel = 0, float durability = 100, long crafterId = 42, string crafterName = "Crafter",
        bool pickedUp = true, bool cheated = false, Dictionary<string, string>? data = null,
        string adapter = "vanilla-v1", byte[]? extension = null) =>
        new(prefab, registration, quality, variant, worldLevel, durability, crafterId, crafterName, pickedUp, cheated,
            data ?? new Dictionary<string, string> { ["owner-mod"] = "distinct-value" }, adapter, extension ?? Array.Empty<byte>());
    private static TransferInventorySnapshot Snapshot(string id, IEnumerable<TransferStack>? items = null, int width = 2,
        int height = 1, IEnumerable<int>? blocked = null, Guid? instance = null, ulong epoch = 0, ulong protections = 0,
        long owner = 12, ushort ownerRevision = 3, string payload = "payload-a") =>
        new(new TransferInventoryStamp(id, instance ?? Instance, epoch, protections, owner, ownerRevision, payload),
            width, height, items ?? Array.Empty<TransferStack>(), blocked ?? Array.Empty<int>());
    private static TransferStack Stack(Guid? handle = null, TransferItemIdentity? item = null, int count = 10,
        int maximum = 50, int x = 0, int y = 0, bool protectedItem = false, bool supported = true, string? mergeClass = null) =>
        new(handle ?? Selected, x, y, count, maximum, item ?? Item(), protectedItem, supported, mergeClass);
    private sealed record Found(Guid? MergeHandle, int DestinationX, int PlannedUnits, int RequestedUnits);
    // The planners' own cell choice (D005): the first strictly compatible partial stack, then the first empty unprotected cell.
    private static Found Plan(TransferInventorySnapshot? source = null, TransferInventorySnapshot? destination = null)
    {
        var item = (source ?? Snapshot("player", new[] { Stack() })).Stacks.Single(x => x.Handle == Selected);
        Assert.True(new TransferGrid(destination ?? Snapshot("chest")).TryFind(item.MergeKey, item.MaximumStack, out var x, out _, out var merge, out var room));
        return new Found(merge, x, Math.Min(10, room), 10);
    }

    [Fact]
    public void Identity_OwnsMetadataAndExtensionBytes_AndCanonicalizesDictionaryOrder()
    {
        var data = new Dictionary<string, string> { ["b"] = "two", ["a"] = "one" }; var bytes = new byte[] { 1, 2 };
        var item = Item(data: data, extension: bytes);
        var reordered = Item(data: new Dictionary<string, string> { ["a"] = "one", ["b"] = "two" }, extension: new byte[] { 1, 2 });
        Assert.Equal(item, reordered); Assert.Equal(item.Fingerprint, reordered.Fingerprint);
        data["a"] = "mutated"; bytes[0] = 99; var copy = item.CopyExtensionData(); copy[0] = 77;
        Assert.Equal("one", item.CustomData["a"]); Assert.Equal(new byte[] { 1, 2 }, item.CopyExtensionData());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)item.CustomData)["a"] = "changed");
        Assert.Equal(reordered, item);
    }

    [Theory]
    [InlineData("prefab")]
    [InlineData("registration")]
    [InlineData("quality")]
    [InlineData("variant")]
    [InlineData("world-level")]
    [InlineData("durability")]
    [InlineData("crafter-id")]
    [InlineData("crafter-name")]
    [InlineData("pickup")]
    [InlineData("cheated")]
    [InlineData("custom-value")]
    [InlineData("custom-key-case")]
    [InlineData("adapter")]
    [InlineData("extension")]
    public void Identity_AnyPreservedFieldDifference_PreventsMerge(string field)
    {
        var changed = field switch
        {
            "prefab" => Item(prefab: "OtherWood"), "registration" => Item(registration: "catalog-b"),
            "quality" => Item(quality: 2), "variant" => Item(variant: 1), "world-level" => Item(worldLevel: 1),
            "durability" => Item(durability: 99.999f), "crafter-id" => Item(crafterId: 43), "crafter-name" => Item(crafterName: "Other"),
            "pickup" => Item(pickedUp: false), "cheated" => Item(cheated: true),
            "custom-value" => Item(data: new() { ["owner-mod"] = "other" }),
            "custom-key-case" => Item(data: new() { ["Owner-mod"] = "distinct-value" }),
            "adapter" => Item(adapter: "other-v1"), "extension" => Item(extension: new byte[] { 1 }),
            _ => throw new ArgumentException(nameof(field))
        };
        Assert.NotEqual(Item(), changed);
        var destination = Snapshot("chest", new[] { Stack(Target, changed, 49) });
        var plan = Plan(destination: destination);
        Assert.Null(plan.MergeHandle); Assert.Equal(1, plan.DestinationX); Assert.Equal(10, plan.PlannedUnits);
    }

    [Fact]
    public void MergeClass_FromTheGame_DecidesWhichStackablesMerge()
    {
        // D061 Q11: the adapter assigns the game's IsSameType class; within it, who crafted the stack does not matter.
        var source = Snapshot("player", new[] { Stack(mergeClass: "wood") });
        var plan = Plan(source, Snapshot("chest", new[] { Stack(Target, Item(crafterId: 43, crafterName: "Other", data: new() { ["owner-mod"] = "other" }), 45, mergeClass: "wood") }));
        Assert.Equal(Target, plan.MergeHandle); Assert.Equal(5, plan.PlannedUnits);
        // Another class, or no class on either side, never merges.
        Assert.Null(Plan(source, Snapshot("chest", new[] { Stack(Target, Item(crafterId: 43), 45, mergeClass: "fine-wood") })).MergeHandle);
        Assert.Null(Plan(source, Snapshot("chest", new[] { Stack(Target, Item(crafterId: 43), 45) })).MergeHandle);
        // A non-stackable never merges, and is counted by its full identity.
        Assert.StartsWith("item:", Stack(count: 1, maximum: 1, mergeClass: "sword").MergeKey);
    }

    [Fact]
    public void Plan_StrictCompatiblePartialStack_ReportsOnlySafeCellCapacity()
    {
        var plan = Plan(destination: Snapshot("chest", new[] { Stack(Target, count: 48) }));
        Assert.Equal(Target, plan.MergeHandle); Assert.Equal(2, plan.PlannedUnits); Assert.Equal(10, plan.RequestedUnits);
    }

    [Fact]
    public void Plan_ProtectedCompatibleStack_UsesNextEmptyUnprotectedCell()
    {
        var plan = Plan(destination: Snapshot("chest", new[] { Stack(Target, count: 48, protectedItem: true) }));
        Assert.Null(plan.MergeHandle); Assert.Equal(1, plan.DestinationX);
    }

    [Theory]
    [InlineData("full")]
    [InlineData("protected-empty-cell")]
    public void Grid_FullOrProtectedDestination_HasNoCell(string kind)
    {
        var destination = Snapshot("chest", kind == "full" ? new[] { Stack(Target, count: 50) } : Array.Empty<TransferStack>(),
            width: 1, blocked: kind == "protected-empty-cell" ? new[] { 0 } : Array.Empty<int>());
        Assert.False(new TransferGrid(destination).TryFind(Stack().MergeKey, 50, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("protected-item", TransferRefusal.Protected)]
    [InlineData("protected-slot", TransferRefusal.Protected)]
    [InlineData("unsupported-extension", TransferRefusal.UnsupportedCapability)]
    [InlineData("selection-replaced", TransferRefusal.InventoryChanged)]
    public void TryCreateAt_IneligibleSource_RefusesWithoutPlan(string kind, TransferRefusal expected)
    {
        var source = Snapshot("player", new[] { Stack(protectedItem: kind == "protected-item", supported: kind != "unsupported-extension") },
            blocked: kind == "protected-slot" ? new[] { 0 } : Array.Empty<int>());
        var result = DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "policy-a", "rule-a", source, Snapshot("chest"),
            kind == "selection-replaced" ? Guid.NewGuid() : Selected, 10, 0, 0, null, 10, 15, out var plan);
        Assert.Equal(expected, result); Assert.Null(plan);
    }

    [Fact]
    public void Snapshot_OwnsInputCollections_AndRejectsDuplicateCellsOrHandles()
    {
        var list = new List<TransferStack> { Stack() }; var blocked = new List<int> { 1 };
        var snapshot = Snapshot("player", list, blocked: blocked); var fingerprint = snapshot.ContentFingerprint;
        list.Clear(); blocked.Clear();
        Assert.Single(snapshot.Stacks); Assert.Equal(1, Assert.Single(snapshot.ProtectedCells)); Assert.Equal(fingerprint, snapshot.ContentFingerprint);
        Assert.Throws<ArgumentException>(() => Snapshot("player", new[] { Stack(), Stack(Target) }));
        Assert.Throws<ArgumentException>(() => Snapshot("player", new[] { Stack(), Stack(x: 1) }));
    }

    [Fact]
    public void Identity_OversizedAggregateMetadata_IsRejectedWithoutTruncation()
    {
        var data = Enumerable.Range(0, 20).ToDictionary(i => "key-" + i, _ => new string('v', 65536));
        Assert.Throws<InvalidDataException>(() => Item(data: data));
        Assert.Equal(20, data.Count); Assert.All(data.Values, value => Assert.Equal(65536, value.Length));
    }

    [Fact]
    public void TryCreateAt_ShrinksToTheFreshStackAndCellButNeverGrows()
    {
        var source = Snapshot("chest-a", new[] { Stack(count: 6) });
        var destination = Snapshot("chest-b", new[] { Stack(Target, count: 47, x: 1) });
        Assert.Equal(TransferRefusal.None, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "policy-a", "rule-a", source, destination,
            Selected, 10, 1, 0, Target, 10, 15, out var merge));
        Assert.Equal(3, merge!.PlannedUnits); Assert.Equal(Target, merge.MergeHandle);
        Assert.Equal(TransferRefusal.None, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "policy-a", "rule-a", source, destination,
            Selected, 4, 0, 0, null, 10, 15, out var empty));
        Assert.Equal(4, empty!.PlannedUnits);
        Assert.Equal(TransferRefusal.None, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "policy-a", "rule-a", source, destination,
            Selected, 50, 0, 0, null, 10, 15, out var fresh));
        Assert.Equal(6, fresh!.PlannedUnits);
    }

    [Fact]
    public void TryCreateAt_RefusesAChangedOrUnavailableCell()
    {
        var source = Snapshot("chest-a", new[] { Stack(count: 6) });
        var other = Snapshot("chest-b", new[] { Stack(Target, item: Item(data: new() { ["owner-mod"] = "other" }), count: 10, x: 1) });
        Assert.Equal(TransferRefusal.InventoryChanged, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "p", "r", source, other,
            Selected, 6, 1, 0, Target, 10, 15, out _));
        Assert.Equal(TransferRefusal.NoCapacity, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "p", "r", source, other,
            Selected, 6, 1, 0, null, 10, 15, out _));
        Assert.Equal(TransferRefusal.NoCapacity, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "p", "r", source,
            Snapshot("chest-c", blocked: new[] { 0 }), Selected, 6, 0, 0, null, 10, 15, out _));
        Assert.Equal(TransferRefusal.InventoryChanged, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "p", "r", source, other,
            Guid.NewGuid(), 6, 0, 0, null, 10, 15, out _));
        Assert.Equal(TransferRefusal.Protected, DepositPlan.TryCreateAt(Guid.NewGuid(), Session, "p", "r",
            Snapshot("chest-a", new[] { Stack(protectedItem: true) }), other, Selected, 6, 0, 0, null, 10, 15, out _));
    }
}
