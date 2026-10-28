using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using YAVSOS.Integrations;

namespace YAVSOS.Core.Tests;

public sealed class OptionalEpicLootRegistrationTests : IDisposable
{
    public OptionalEpicLootRegistrationTests() { Loader.Reset(); Manager.Instance.Items.Clear(); }
    public void Dispose() { Loader.Reset(); Manager.Instance.Items.Clear(); }
    private static Type? Types(string name) => name switch
    {
        "Common.ItemBatchLoader" => typeof(Loader), "Common.ItemDefinition" => typeof(Definition),
        "Jotunn.Managers.ItemManager" => typeof(Manager), _ => null
    };
    private static OptionalEpicLootRegistration Open(Action? invalidate = null) =>
        OptionalEpicLootRegistration.TryInstall(true, "0.14.13", "2.30.2", Types, invalidate ?? (() => { }), _ => { }, out _)!;

    [Theory]
    [InlineData(true, null, "2.30.2")]
    [InlineData(true, "0.14.13", null)]
    public void Install_AbsentEpicLootOrJotunnDoesNotReadOrMutateRegistries(bool headless, string? epic, string? jotunn)
    {
        var reads = 0;
        using var adapter = OptionalEpicLootRegistration.TryInstall(headless, epic, jotunn,
            name => { reads++; return Types(name); }, () => { }, _ => { }, out _);
        Assert.Null(adapter); Assert.Equal(0, reads); Assert.Equal(0, Loader.Adds); Assert.Empty(Manager.Instance.Items);
    }

    // D058: other versions install when the loader and manager shapes match.
    [Theory]
    [InlineData("0.14.14", "2.30.2")]
    [InlineData("0.14.13", "2.31.0")]
    public void Install_OtherVersionsWithMatchingShapeInstall(string epic, string jotunn)
    {
        using var adapter = OptionalEpicLootRegistration.TryInstall(true, epic, jotunn, Types, () => { }, _ => { }, out _);
        Assert.NotNull(adapter); Assert.Equal(1, Loader.Adds);
    }

    [Fact]
    public void Install_UsesStructuredDefinitionsAndReappliesOnlyAfterRegistrationOrConfigChange()
    {
        Loader.resourceDefinitions.Add(new Definition { Prefab = "NewStructuredRing" });
        var invalidations = 0;
        using var adapter = Open(() => invalidations++);
        Assert.NotNull(adapter); Assert.Equal(1, Loader.Adds); Assert.Equal(0, Loader.Applies);
        Assert.Equal(new[] { "NewStructuredRing", "RingA", "RingB" }, Manager.Instance.Items.Keys.OrderBy(x => x));
        Manager.Register(); Assert.Equal(1, Loader.Applies); Assert.Equal(2, invalidations);
        Loader.resourceDefinitions[0].CraftAmountCfg.Change();
        Loader.resourceDefinitions[0].Recipe.RecipeConfig.Change();
        Assert.Equal(1, Loader.Applies); // File/config callbacks only queue bounded main-thread work.
        adapter!.Update(false); Assert.Equal(1, Loader.Applies);
        adapter.Update(true); Assert.Equal(2, Loader.Applies);
        adapter.Update(true); Assert.Equal(2, Loader.Applies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_ExistingCompleteOrPartialRegistrationIsNeverOverwritten(bool complete)
    {
        var existing = new object(); Manager.Instance.Items["RingA"] = existing;
        if (complete) Manager.Instance.Items["RingB"] = new object();
        using var adapter = OptionalEpicLootRegistration.TryInstall(true, "0.14.13", "2.30.2", Types, () => { }, _ => { }, out var diagnostic);
        Assert.Null(adapter); Assert.Equal(0, Loader.Adds); Assert.Same(existing, Manager.Instance.Items["RingA"]);
        Manager.Register(); Assert.Equal(0, Loader.Applies);
        // D086 D1 (b): complete registration by EpicLoot itself is the retirement case, not a failure.
        Assert.StartsWith(complete ? "not needed:" : "unavailable:", diagnostic);
    }

    [Theory]
    [InlineData(0)] // Duplicate definition.
    [InlineData(1)] // Unsupported item-stat callbacks would exceed this adapter's recipe-only scope.
    [InlineData(2)] // Missing recipe/config shape.
    [InlineData(3)] // Bounded work.
    public void Install_InvalidDefinitionsFailBeforeAnyRegistration(int scenario)
    {
        if (scenario == 0) Loader.resourceDefinitions[1].Prefab = "RingA";
        if (scenario == 1) Loader.resourceDefinitions[0].ModifableStats.Add("weight", new object());
        if (scenario == 2) Loader.resourceDefinitions[0].Recipe.RecipeConfig = null!;
        if (scenario == 3) for (var i = 0; i < 64; i++) Loader.resourceDefinitions.Add(new Definition { Prefab = "Extra" + i });
        using var adapter = Open();
        Assert.Null(adapter); Assert.Equal(0, Loader.Adds); Assert.Empty(Manager.Instance.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Install_PartialOrThrowingUpstreamBatchStopsWithoutRetryOrLiveRemoval(bool throws)
    {
        Loader.Partial = true; Loader.Throw = throws;
        using var adapter = Open();
        Assert.Null(adapter); Assert.Equal(1, Loader.Adds); Assert.Single(Manager.Instance.Items);
        Manager.Register(); Assert.Equal(0, Loader.Applies);
    }

    [Fact]
    public void Dispose_RemovesOwnedCallbacksAndLeavesRegistrationsToTheGameLifecycle()
    {
        var invalidations = 0; var adapter = Open(() => invalidations++); Assert.NotNull(adapter);
        adapter!.Dispose(); adapter.Dispose();
        Manager.Register(); Loader.resourceDefinitions[0].CraftableCfg.Change(); adapter.Update(true);
        Assert.Equal(0, Loader.Applies); Assert.Equal(0, invalidations); Assert.Equal(2, Manager.Instance.Items.Count);
    }

    [Fact]
    public void Reapply_FailureInvalidatesEvidenceAndDisablesFurtherCallbacks()
    {
        var invalidations = 0; var faults = new List<string>();
        using var adapter = OptionalEpicLootRegistration.TryInstall(true, "0.14.13", "2.30.2", Types,
            () => invalidations++, faults.Add, out _);
        Assert.NotNull(adapter); Loader.ThrowApply = true; Manager.Register();
        Assert.Equal(2, invalidations); Assert.Single(faults); Assert.False(adapter!.Pending); // Recaptured without the shim (D086 D1).
        Loader.ThrowApply = false; Manager.Register(); adapter!.Update(true);
        Assert.Equal(1, Loader.Applies); // A failed adapter cannot silently resume.
    }

    [Fact]
    public void Install_GraphicalClientObservesRecipesWithoutRegisteringAgain()
    {
        Manager.Instance.Items["RingA"] = new object(); Manager.Instance.Items["RingB"] = new object();
        using var adapter = OptionalEpicLootRegistration.TryInstall(false, "0.14.13", "2.30.2", Types,
            () => { }, _ => { }, out _);
        Assert.NotNull(adapter); Assert.Equal(0, Loader.Adds);
        Loader.resourceDefinitions[0].CraftedAtCfg.Change(); Assert.True(adapter!.Pending);
        adapter.Update(true); Assert.Equal(0, Loader.Applies); Assert.True(adapter.Pending);
        Manager.Register(); Assert.Equal(1, Loader.Applies); Assert.False(adapter.Pending);
    }

    [Fact]
    public void Install_AssetPreflightFailureDoesNotInvokeTheUpstreamBatch()
    {
        using var adapter = OptionalEpicLootRegistration.TryInstall(true, "0.14.13", "2.30.2", Types,
            () => { }, _ => { }, out _, (_, _) => throw new InvalidOperationException("missing asset"));
        Assert.Null(adapter); Assert.Equal(0, Loader.Adds); Assert.Empty(Manager.Instance.Items);
    }

    [Fact]
    public void Reapply_RegistryPostconditionFailureTurnsOnlyTheShimOff()
    {
        using var adapter = OptionalEpicLootRegistration.TryInstall(true, "0.14.13", "2.30.2", Types,
            () => { }, _ => { }, out _, verify: _ => throw new InvalidOperationException("unresolved recipe"));
        // D086 D1: a failed shim is off; it no longer holds every category back.
        Manager.Register(); Assert.NotNull(adapter); Assert.True(adapter!.Faulted); Assert.False(adapter.Pending);
    }

    public sealed class Config
    {
        public event EventHandler? SettingChanged;
        public void Change() => SettingChanged?.Invoke(this, EventArgs.Empty);
    }
    public sealed class RecipeDefinition { public Config RecipeConfig { get; set; } = new(); }
    public sealed class Definition
    {
        public string Prefab { get; set; } = "";
        public Dictionary<string, object> ModifableStats { get; set; } = new();
        public Dictionary<string, object>? DamageMods { get; set; }
        public RecipeDefinition Recipe { get; set; } = new();
        public Config CraftableCfg { get; set; } = new();
        public Config StationLVLCfg { get; set; } = new();
        public Config CraftAmountCfg { get; set; } = new();
        public Config CraftedAtCfg { get; set; } = new();
    }
    public sealed class Loader
    {
        public static List<Definition> resourceDefinitions = new();
        public static int Adds, Applies;
        public static bool Partial, Throw, ThrowApply;
        public static void Reset()
        {
            resourceDefinitions = new() { new() { Prefab = "RingA" }, new() { Prefab = "RingB" } };
            Adds = Applies = 0; Partial = Throw = ThrowApply = false;
        }
        private static bool BatchAddItems()
        {
            Adds++;
            foreach (var item in resourceDefinitions)
            {
                Manager.Instance.Items.Add(item.Prefab, new object());
                if (Throw) throw new InvalidOperationException("fixture registration failure");
                if (Partial) break;
            }
            return true;
        }
        private static void ReapplyAllRecipeConfig() { Applies++; if (ThrowApply) throw new InvalidOperationException("fixture recipe failure"); }
    }
    public sealed class Manager
    {
        public static Manager Instance { get; } = new();
        public Dictionary<string, object> Items { get; } = new();
        public object? GetItem(string name) => Items.TryGetValue(name, out var item) ? item : null;
        public void RemoveItem(string name) => Items.Remove(name);
        public static event Action? OnItemsRegistered;
        public static void Register() => OnItemsRegistered?.Invoke();
    }
}
