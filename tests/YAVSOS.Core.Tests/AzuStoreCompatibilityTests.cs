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

    [Fact]
    public void Shape317_IsCompatible() => Assert.Empty(AzuStoreCompatibility.Check(Shape()));

    [Fact]
    public void MissingMemberOrMethod_Disables()
    {
        Assert.Contains(AzuStoreCompatibility.Check(Shape(missing: new[] { "AzuAutoStore.Util.Boxes::CanItemBeStored" })), x => x.Contains("Missing member"));
        Assert.Contains(AzuStoreCompatibility.Check(Shape(noTryStore: true)), x => x.Contains("TryStore"));
    }

    [Fact]
    public void NewOrMissingDepositTargetType_Disables()
    {
        var added = AzuStoreCompatibility.ExpectedImplementers.Concat(new[] { "AzuAutoStore.Interfaces.NewChest" });
        Assert.Contains(AzuStoreCompatibility.Check(Shape(implementers: added)), x => x.Contains("NewChest"));
        Assert.Contains(AzuStoreCompatibility.Check(Shape(implementers: AzuStoreCompatibility.ExpectedImplementers.Skip(1))), x => x.Contains("Missing deposit target"));
    }

    [Theory]
    [InlineData("no-have")]
    [InlineData("two-have")]
    [InlineData("check-after-write")]
    [InlineData("no-write")]
    public void TryStoreShapeChanges_Disable(string kind)
    {
        var calls = kind switch
        {
            "no-have" => new[] { CanStore, Move },
            "two-have" => new[] { Have, Have, CanStore, Move },
            "check-after-write" => new[] { Have, Move, CanStore },
            _ => new[] { Have, CanStore }
        };
        Assert.NotEmpty(AzuStoreCompatibility.Check(Shape(tryStore: calls)));
    }

    [Fact]
    public void AnyContainerWantsShapeChanges_Disable()
    {
        Assert.NotEmpty(AzuStoreCompatibility.Check(Shape(wants: new[] { AzuStoreCompatibility.GetInventory, CanStore })));
        Assert.NotEmpty(AzuStoreCompatibility.Check(Shape(wantsLocals: new[] { "Container", "Container" })));
        Assert.NotEmpty(AzuStoreCompatibility.Check(Shape(wants: new[] { Have, CanStore })));
    }

    [Fact]
    public void NewOrMovedWritePath_DisablesEvenWithTheSameVersion()
    {
        var added = AzuStoreCompatibility.ExpectedSinkCallers.Concat(new[] { "AzuAutoStore.Util.Functions::StoreAllButton" });
        Assert.Contains(AzuStoreCompatibility.Check(Shape(sinkCallers: added)), x => x.Contains("StoreAllButton"));
        Assert.Contains(AzuStoreCompatibility.Check(Shape(sinkCallers: AzuStoreCompatibility.ExpectedSinkCallers.Skip(1))), x => x.Contains("moved or removed"));
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
