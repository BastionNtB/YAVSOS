using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class AzuStoreCompatibilityTests
{
    private const string Have = AzuStoreCompatibility.HaveItem, CanStore = AzuStoreCompatibility.CanItemBeStored;
    private const string Move = "AzuAutoStore.Util.InventoryMove::MoveStack", Route = "AzuAutoStore.Util.Functions::TryStoreToOtherOwner";

    private static AzuAssemblyShape Shape(IEnumerable<string>? missing = null, IEnumerable<string>? implementers = null,
        IEnumerable<string>? tryStore = null, IEnumerable<string>? wants = null, IEnumerable<string>? wantsLocals = null,
        IEnumerable<string>? sinkCallers = null, bool noTryStore = false) =>
        new AzuAssemblyShape(missing ?? Array.Empty<string>(), implementers ?? AzuStoreCompatibility.ExpectedImplementers,
            noTryStore ? null : new AzuMethodShape("TryStore", tryStore ?? new[] { Have, CanStore, "Container::CheckAccess", Move, Route }),
            new AzuMethodShape("AnyContainerWants", wants ?? new[] { AzuStoreCompatibility.GetInventory, Have, CanStore },
                wantsLocals ?? new[] { "ItemData", "Container", "Vector3" }),
            sinkCallers ?? AzuStoreCompatibility.ExpectedSinkCallers);

    private static AzuCompatibility Assess(AzuAssemblyShape shape) => AzuStoreCompatibility.Assess(shape);

    [Fact]
    public void Shape317_IsCompatible()
    {
        var check = Assess(Shape());
        Assert.True(check.Rules); Assert.True(check.EmptyChests); Assert.True(check.Pickup);
        Assert.Empty(check.Notes); Assert.Empty(check.NewWritePaths);
    }

    [Fact]
    public void MissingRequiredMemberOrTryStore_TurnsEverythingOff()
    {
        var member = Assess(Shape(missing: new[] { "Container::Awake" }));
        Assert.False(member.Rules); Assert.False(member.EmptyChests); Assert.False(member.Pickup);
        Assert.Contains(member.Whole, x => x.Contains("Missing member"));
        var tryStore = Assess(Shape(noTryStore: true));
        Assert.False(tryStore.Rules); Assert.Contains(tryStore.Whole, x => x.Contains("TryStore"));
    }

    [Fact]
    public void NewOrMissingDepositTargetType_IsOnlyNoted()
    {
        // D171 (#102, option b): a new chest type still stores through TryStore, which H1 and the backstop guard.
        var added = Assess(Shape(implementers: AzuStoreCompatibility.ExpectedImplementers.Concat(new[] { "AzuAutoStore.Interfaces.NewChest" })));
        Assert.True(added.EmptyChests && added.Pickup); Assert.Contains(added.Notes, x => x.Contains("NewChest"));
        var missing = Assess(Shape(implementers: AzuStoreCompatibility.ExpectedImplementers.Skip(1)));
        Assert.True(missing.EmptyChests && missing.Pickup); Assert.Contains(missing.Notes, x => x.Contains("Missing deposit target"));
    }

    [Theory]
    [InlineData("no-have")]
    [InlineData("two-have")]
    [InlineData("check-after-write")]
    [InlineData("no-write")]
    public void TryStoreShapeChanges_TurnOffOnlyEmptyChests(string kind)
    {
        var calls = kind switch
        {
            "no-have" => new[] { CanStore, Move },
            "two-have" => new[] { Have, Have, CanStore, Move },
            "check-after-write" => new[] { CanStore, Move, Have },
            _ => new[] { Have, CanStore }
        };
        var check = Assess(Shape(tryStore: calls));
        Assert.True(check.Rules); Assert.False(check.EmptyChests); Assert.True(check.Pickup);
        Assert.NotEmpty(check.EmptyChestProblems);
    }

    [Fact]
    public void TryStore_AzuOwnExclusionCheckMayMove()
    {
        // H2 swaps only the "already holds it" test; where Azu checks its own exclusions is Azu's (D171).
        var check = Assess(Shape(tryStore: new[] { Have, Move, CanStore }));
        Assert.True(check.EmptyChests);
    }

    [Fact]
    public void AnyContainerWantsShapeChanges_TurnOffOnlyPickup()
    {
        foreach (var shape in new[]
        {
            Shape(wants: new[] { AzuStoreCompatibility.GetInventory, CanStore }),
            Shape(wantsLocals: new[] { "Container", "Container" }),
            Shape(wants: new[] { Have, CanStore }),
            new AzuAssemblyShape(Array.Empty<string>(), AzuStoreCompatibility.ExpectedImplementers,
                new AzuMethodShape("TryStore", new[] { Have, CanStore, Move }), null, AzuStoreCompatibility.ExpectedSinkCallers)
        })
        {
            var check = Assess(shape);
            Assert.True(check.Rules); Assert.True(check.EmptyChests); Assert.False(check.Pickup);
            Assert.NotEmpty(check.PickupProblems);
        }
    }

    [Fact]
    public void NewWritePath_IsGuardedAndMovedPathNoted()
    {
        // D046's own example: a future Azu adds a store-all button. It no longer turns rules off; the backstop logs its
        // writes into ruled chests, since refusing a path nobody reviewed could lose the item.
        var added = Assess(Shape(sinkCallers: AzuStoreCompatibility.ExpectedSinkCallers.Concat(new[] { "AzuAutoStore.Util.Functions::StoreAllButton" })));
        Assert.True(added.Rules && added.EmptyChests && added.Pickup);
        Assert.Equal(new[] { "AzuAutoStore.Util.Functions::StoreAllButton" }, added.NewWritePaths);
        Assert.Contains(added.Notes, x => x.Contains("StoreAllButton"));
        var moved = Assess(Shape(sinkCallers: AzuStoreCompatibility.ExpectedSinkCallers.Skip(1)));
        Assert.True(moved.Rules); Assert.Empty(moved.NewWritePaths); Assert.Contains(moved.Notes, x => x.Contains("moved or removed"));
    }

    [Theory]
    [InlineData("Inventory::AddItem", true)]
    [InlineData("Inventory::MoveItemToThis", true)]
    [InlineData("Inventory::HaveItem", false)]
    [InlineData("AzuAutoStore.Util.InventoryMove::MoveStack", true)]
    [InlineData("AzuAutoStore.APIs.ItemDrawers_API+Drawer::Add", true)]
    [InlineData("AzuAutoStore.APIs.ItemDrawers_API+Drawer::AddListener", false)]
    [InlineData("Backpacks.API::AddItemToBackpack", true)]
    [InlineData("NoSeparator", false)]
    public void WriteSinks_MatchTheReleaseCheckList(string callee, bool sink) => Assert.Equal(sink, AzuStoreCompatibility.IsWriteSink(callee));

    [Theory]
    [InlineData(ChestRuleDecision.NoRule, false, false, false, true, true, false)]
    [InlineData(ChestRuleDecision.NoRule, true, false, true, true, true, false)]
    [InlineData(ChestRuleDecision.NoRule, false, true, true, true, true, false)]
    [InlineData(ChestRuleDecision.Accept, false, false, true, true, true, false)]
    [InlineData(ChestRuleDecision.Reject, true, false, true, false, false, true)]
    [InlineData(ChestRuleDecision.RejectCategoriesPending, false, false, false, false, false, true)]
    [InlineData(ChestRuleDecision.RejectUnreadable, false, false, false, false, false, true)]
    public void HookDecisions(ChestRuleDecision decision, bool hasItem, bool reserved, bool h2, bool h1, bool h3, bool backstop)
    {
        Assert.Equal(h1, AzuStoreCompatibility.MayTry(decision));
        Assert.Equal(h2, AzuStoreCompatibility.HasItem(hasItem, decision, reserved));
        Assert.Equal(h3, AzuStoreCompatibility.CanStore(true, decision));
        Assert.False(AzuStoreCompatibility.CanStore(false, decision)); // Azu's own exclusions always hold.
        Assert.Equal(backstop, AzuStoreCompatibility.RefuseWrite(1, decision));
        Assert.False(AzuStoreCompatibility.RefuseWrite(0, decision)); // Outside Azu entries nothing is refused.
    }
}

public sealed class ReservedDrawerTests
{
    [Theory]
    [InlineData("Copper", "Copper", true)]
    [InlineData("Copper", "Tin", false)]
    [InlineData("Copper", "copper", false)]
    [InlineData("", "Copper", false)]
    [InlineData(null, "Copper", false)]
    [InlineData("Copper", null, false)]
    public void ReservedDrawer_WantsOnlyItsAssignedItem(string? assigned, string? item, bool wants) =>
        Assert.Equal(wants, ReservedDrawer.Wants(assigned, item));

    // D070: Azu 3.1.7's store-key loops as Harmony reports them (generic arguments included).
    private static readonly string[] HotkeyLoop =
    {
        "UnityEngine.Object::op_Implicit", "AzuAutoStore.Util.Functions::LogDebug",
        "System.Collections.Generic.List`1[[AzuAutoStore.Interfaces.IContainer, AzuAutoStore, Version=3.1.7.0, Culture=neutral, PublicKeyToken=null]]::Clear",
        "AzuAutoStore.Util.Functions::ClearPendingSends",
        "BepInEx.Configuration.ConfigEntry`1[[System.Single, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089]]::get_Value",
        "AzuAutoStore.Util.Boxes::GetNearbyContainers",
        "System.Collections.Generic.List`1[[AzuAutoStore.Interfaces.IContainer, AzuAutoStore]]::GetEnumerator",
        "System.Collections.Generic.List`1+Enumerator[[AzuAutoStore.Interfaces.IContainer, AzuAutoStore]]::get_Current",
        "AzuAutoStore.Util.Functions::StoreToContainer",
        "System.Collections.Generic.List`1+Enumerator[[AzuAutoStore.Interfaces.IContainer, AzuAutoStore]]::MoveNext",
        "System.IDisposable::Dispose", "AzuAutoStore.Util.Functions::StoreSuccess"
    };

    [Fact]
    public void StoreLoop_AcceptsAzu317_AndReportsAnythingElse()
    {
        Assert.Empty(AzuStoreCompatibility.CheckStoreLoop(new AzuMethodShape("Functions::TryStore", HotkeyLoop), false));
        var single = HotkeyLoop.Concat(new[] { "Humanoid::GetInventory", "System.String::Concat" });
        Assert.Empty(AzuStoreCompatibility.CheckStoreLoop(new AzuMethodShape("Functions::TryStoreThisItem", single), true));
        // The single-item extras are not expected in the store key's own loop.
        Assert.Equal(new[] { "Functions::TryStore now also calls Humanoid::GetInventory.", "Functions::TryStore now also calls System.String::Concat." },
            AzuStoreCompatibility.CheckStoreLoop(new AzuMethodShape("Functions::TryStore", single), false));
        var added = HotkeyLoop.Append("AzuAutoStore.Util.Functions::SortContainers").Where(x => !x.EndsWith("::StoreSuccess"));
        Assert.Equal(new[] { "Functions::TryStore now also calls AzuAutoStore.Util.Functions::SortContainers.", "Functions::TryStore no longer calls AzuAutoStore.Util.Functions::StoreSuccess." },
            AzuStoreCompatibility.CheckStoreLoop(new AzuMethodShape("Functions::TryStore", added), false));
        Assert.Equal(new[] { "TryStore() not found." }, AzuStoreCompatibility.CheckStoreLoop(null, false));
        Assert.Equal("System.Collections.Generic.List`1+Enumerator::MoveNext", AzuStoreCompatibility.WithoutGenericArguments(HotkeyLoop[9]));
    }
}
