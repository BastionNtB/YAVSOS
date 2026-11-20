using System;
using System.Collections.Generic;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>One method's original IL facts: callees as "Namespace.Type::Method", and declared local types.</summary>
public sealed class AzuMethodShape
{
    public string Name { get; }
    public IReadOnlyList<string> Calls { get; }
    public IReadOnlyList<string> LocalTypes { get; }
    public AzuMethodShape(string name, IEnumerable<string> calls, IEnumerable<string>? localTypes = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Calls = Array.AsReadOnly((calls ?? throw new ArgumentNullException(nameof(calls))).ToArray());
        LocalTypes = Array.AsReadOnly((localTypes ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>What the loaded AzuAutoStore assembly looks like, gathered by the game-side adapter.</summary>
public sealed class AzuAssemblyShape
{
    /// <summary>
    /// Members the whole integration needs, resolved by signature (check 1); missing ones are listed by name. Members
    /// only one hook needs show in that hook's shape instead, and the ones YAVSOS doesn't need aren't required (D171).
    /// </summary>
    public IReadOnlyList<string> MissingMembers { get; }
    public IReadOnlyList<string> ContainerImplementers { get; }
    public AzuMethodShape? TryStore { get; }
    public AzuMethodShape? AnyContainerWants { get; }
    /// <summary>Every method in Azu's own namespaces with a direct call to a write sink.</summary>
    public IReadOnlyList<string> SinkCallers { get; }
    public AzuAssemblyShape(IEnumerable<string> missingMembers, IEnumerable<string> containerImplementers,
        AzuMethodShape? tryStore, AzuMethodShape? anyContainerWants, IEnumerable<string> sinkCallers)
    {
        MissingMembers = Array.AsReadOnly(missingMembers.ToArray());
        ContainerImplementers = Array.AsReadOnly(containerImplementers.ToArray());
        TryStore = tryStore; AnyContainerWants = anyContainerWants;
        SinkCallers = Array.AsReadOnly(sinkCallers.ToArray());
    }
}

/// <summary>
/// D171 (#102): what the startup checks allow, one hook at a time (D058). <see cref="Rules"/> false turns the whole
/// integration off; <see cref="EmptyChests"/> (H2 in TryStore) and <see cref="Pickup"/> (H2 and H3 in
/// AnyContainerWants) turn off alone, while rejection (H1) and the backstop keep working.
/// </summary>
public sealed class AzuCompatibility
{
    /// <summary>Problems that turn the whole integration off.</summary>
    public IReadOnlyList<string> Whole { get; }
    /// <summary>Problems that turn off only empty-chest acceptance on the stash keys (H2 in TryStore).</summary>
    public IReadOnlyList<string> EmptyChestProblems { get; }
    /// <summary>Problems that turn off only ground pickup's rule hooks (H2 and H3 in AnyContainerWants).</summary>
    public IReadOnlyList<string> PickupProblems { get; }
    /// <summary>Differences from the reviewed lists, logged only.</summary>
    public IReadOnlyList<string> Notes { get; }
    /// <summary>Write paths not on the list; each gets the backstop's log-only guard (D046's required check, D171).</summary>
    public IReadOnlyList<string> NewWritePaths { get; }
    public bool Rules => Whole.Count == 0;
    public bool EmptyChests => Rules && EmptyChestProblems.Count == 0;
    public bool Pickup => Rules && PickupProblems.Count == 0;
    public AzuCompatibility(IEnumerable<string> whole, IEnumerable<string> emptyChests, IEnumerable<string> pickup, IEnumerable<string> notes,
        IEnumerable<string> newWritePaths)
    {
        Whole = Array.AsReadOnly(whole.ToArray()); EmptyChestProblems = Array.AsReadOnly(emptyChests.ToArray());
        PickupProblems = Array.AsReadOnly(pickup.ToArray()); Notes = Array.AsReadOnly(notes.ToArray());
        NewWritePaths = Array.AsReadOnly(newWritePaths.ToArray());
    }
}

/// <summary>
/// AzuAutoStore startup checks 1–2 (docs/AZUAUTOSTORE-INTEGRATION.md) over a gathered shape, and the hook
/// decisions H1–H3 plus the backstop. The expected deposit paths are AzuAutoStore 3.1.7's, compared for the log only
/// (D171); each hook needs only the shape it patches. Pure: no game, Harmony or Azu references.
/// </summary>
public static class AzuStoreCompatibility
{
    public const string HaveItem = "Inventory::HaveItem";
    public const string CanItemBeStored = "AzuAutoStore.Util.Boxes::CanItemBeStored";
    public const string GetInventory = "Container::GetInventory";
    private static readonly string[] OwnerWrites = { "AzuAutoStore.Util.InventoryMove::MoveStack", "AzuAutoStore.Util.Functions::TryStoreToOtherOwner" };

    public static IReadOnlyList<string> ExpectedImplementers { get; } = Array.AsReadOnly(new[]
    {
        "AzuAutoStore.Interfaces.BackpackContainer", "AzuAutoStore.Interfaces.BowsBeforeHoesQuiver",
        "AzuAutoStore.Interfaces.VanillaContainers", "AzuAutoStore.Interfaces.kgDrawer", "AzuAutoStore.Interfaces.mkzDrawer"
    });

    /// <summary>
    /// Methods in AzuAutoStore 3.1.7's own namespaces that call a write sink directly, whose refusal safety was checked by
    /// hand (D046). A method not on it gets the backstop's log-only guard at startup (D171). Regenerate with
    /// <c>scripts/azu-release-check</c> <c>--runtime-sink-callers</c> when a reviewed Azu release changes them.
    /// </summary>
    public static IReadOnlyList<string> ExpectedSinkCallers { get; } = Array.AsReadOnly(new[]
    {
        "AzuAutoStore.APIs.MUC.MUCSrc.ContainerRPCHandler::AddToAnySlot",
        "AzuAutoStore.APIs.MUC.MUCSrc.Data.SlotPreview::Add",
        "AzuAutoStore.APIs.MUC.MUCSrc.Helper.InventoryHelper::AddItemToInventory",
        "AzuAutoStore.APIs.MUC.MUCSrc.Helper.InventoryHelper::LoadItemFromPackage",
        "AzuAutoStore.APIs.MUC.MUCSrc.Helper.InventoryHelper::MoveItem",
        "AzuAutoStore.APIs.MUC.MUCSrc.InventoryHandler::RPC_RequestItemRemoveResponse",
        "AzuAutoStore.APIs.MUC.MUCSrc.RequestRefund::Give",
        "AzuAutoStore.Interfaces.BackpackContainer::TryStore",
        "AzuAutoStore.Interfaces.BowsBeforeHoesQuiver::TryStoreItem",
        "AzuAutoStore.Interfaces.kgDrawer::TryStoreToKG",
        "AzuAutoStore.Interfaces.mkzDrawer::TryStore",
        "AzuAutoStore.Interfaces.mkzDrawer::TryStoreThisItem",
        "AzuAutoStore.Util.Functions::TryStore",
        "AzuAutoStore.Util.Functions::TryStoreToOtherOwner",
        "AzuAutoStore.Util.InventoryMove::MoveStack"
    });

    /// <summary>Write sinks, matching the release check's list: (declaring type, method name), any overload.</summary>
    public static bool IsWriteSink(string callee)
    {
        if (callee == null) throw new ArgumentNullException(nameof(callee));
        var split = callee.IndexOf("::", StringComparison.Ordinal);
        if (split < 0) return false;
        var type = callee.Substring(0, split); var name = callee.Substring(split + 2);
        return type switch
        {
            "Inventory" => name == "AddItem" || name == "MoveItemToThis",
            "AzuAutoStore.Util.InventoryMove" => name == "MoveStack",
            "AzuAutoStore.APIs.ItemDrawers_API+Drawer" or "AzuAutoStore.APIs.MkzItemDrawers_API+Drawer" or "AzuAutoStore.APIs.BowsBeforeHoesCompat" => name == "Add",
            "Backpacks.API" => name == "AddItemToBackpack",
            _ => false
        };
    }

    /// <summary>
    /// D171 (#102): which of YAVSOS's changes to AzuAutoStore can run, decided one hook at a time (D058). Only a missing
    /// required member or <c>TryStore</c> (H1's target) turns the whole integration off. The lists of write paths and
    /// chest types are compared for the log only; the backstop logs a new write path's writes into ruled chests (D046).
    /// </summary>
    public static AzuCompatibility Assess(AzuAssemblyShape shape, IReadOnlyList<string>? expectedSinkCallers = null)
    {
        if (shape == null) throw new ArgumentNullException(nameof(shape));
        var expected = expectedSinkCallers ?? ExpectedSinkCallers;
        var whole = new List<string>(); var emptyChests = new List<string>(); var pickup = new List<string>(); var notes = new List<string>();
        foreach (var member in shape.MissingMembers) whole.Add("Missing member: " + member);
        if (shape.TryStore == null) whole.Add("TryStore(Container, ref ItemData, bool, bool) not found.");
        else
        {
            // H2 swaps TryStore's one "already holds it" test, which must come before its writes.
            RequireOnce(shape.TryStore, HaveItem, emptyChests);
            var calls = shape.TryStore.Calls;
            var firstWrite = calls.Select((call, index) => (call, index)).Where(x => OwnerWrites.Contains(x.call, StringComparer.Ordinal))
                .Select(x => x.index).DefaultIfEmpty(-1).Min();
            if (firstWrite < 0) emptyChests.Add("TryStore no longer writes through MoveStack/TryStoreToOtherOwner.");
            else if (IndexOf(calls, HaveItem) > firstWrite) emptyChests.Add("TryStore's HaveItem check no longer precedes its writes.");
        }
        // H2 and H3 in AnyContainerWants: one test each, and one chest local to pass to the rule.
        if (shape.AnyContainerWants == null) pickup.Add("AnyContainerWants(ItemDrop) not found.");
        else
        {
            RequireOnce(shape.AnyContainerWants, HaveItem, pickup); RequireOnce(shape.AnyContainerWants, CanItemBeStored, pickup);
            RequireOnce(shape.AnyContainerWants, GetInventory, pickup);
            if (shape.AnyContainerWants.LocalTypes.Count(x => x == "Container") != 1)
                pickup.Add("AnyContainerWants no longer keeps exactly one Container local.");
        }
        var implementers = new HashSet<string>(shape.ContainerImplementers, StringComparer.Ordinal);
        foreach (var added in implementers.Except(ExpectedImplementers, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
            notes.Add("New deposit target type: " + added);
        foreach (var removed in ExpectedImplementers.Except(implementers, StringComparer.Ordinal))
            notes.Add("Missing deposit target type: " + removed);
        var callers = new HashSet<string>(shape.SinkCallers, StringComparer.Ordinal);
        var newPaths = callers.Except(expected, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var added in newPaths) notes.Add("New write path: " + added);
        foreach (var removed in expected.Except(callers, StringComparer.Ordinal))
            notes.Add("Write path moved or removed: " + removed);
        return new AzuCompatibility(whole, emptyChests, pickup, notes, newPaths);
    }

    // D070: the store-key loops YAVSOS runs in its own order. Generic arguments are dropped from type names.
    private static readonly string[] StoreLoopCalls =
    {
        "UnityEngine.Object::op_Implicit", "AzuAutoStore.Util.Functions::LogDebug", "System.Collections.Generic.List`1::Clear",
        "AzuAutoStore.Util.Functions::ClearPendingSends", "BepInEx.Configuration.ConfigEntry`1::get_Value",
        "AzuAutoStore.Util.Boxes::GetNearbyContainers", "System.Collections.Generic.List`1::GetEnumerator",
        "System.Collections.Generic.List`1+Enumerator::get_Current", "System.Collections.Generic.List`1+Enumerator::MoveNext",
        "System.IDisposable::Dispose", "AzuAutoStore.Util.Functions::StoreToContainer", "AzuAutoStore.Util.Functions::StoreSuccess"
    };
    private static readonly string[] SingleItemExtraCalls = { "Humanoid::GetInventory", "System.String::Concat" };
    private static readonly string[] StoreLoopRequired =
    {
        "AzuAutoStore.Util.Functions::ClearPendingSends", "AzuAutoStore.Util.Boxes::GetNearbyContainers",
        "AzuAutoStore.Util.Functions::StoreToContainer", "AzuAutoStore.Util.Functions::StoreSuccess"
    };

    /// <summary>
    /// D070: YAVSOS replaces AzuAutoStore's store key loop (<c>Functions.TryStore()</c>, and with
    /// <paramref name="singleItem"/> <c>TryStoreThisItem</c>) with the same steps in its own chest order. That is only
    /// sound while the loop does nothing else: clear the pings and pending sends, list the nearby containers, store to
    /// each and report. Empty when the loop is as AzuAutoStore 3.1.7 has it; otherwise one line per difference.
    /// </summary>
    public static IReadOnlyList<string> CheckStoreLoop(AzuMethodShape? loop, bool singleItem)
    {
        var problems = new List<string>();
        if (loop == null) { problems.Add((singleItem ? "TryStoreThisItem" : "TryStore()") + " not found."); return problems; }
        var calls = loop.Calls.Select(WithoutGenericArguments).ToList();
        var allowed = new HashSet<string>(singleItem ? StoreLoopCalls.Concat(SingleItemExtraCalls) : StoreLoopCalls, StringComparer.Ordinal);
        foreach (var call in calls.Distinct(StringComparer.Ordinal).Where(x => !allowed.Contains(x)).OrderBy(x => x, StringComparer.Ordinal))
            problems.Add(loop.Name + " now also calls " + call + ".");
        foreach (var call in StoreLoopRequired.Where(x => !calls.Contains(x, StringComparer.Ordinal)))
            problems.Add(loop.Name + " no longer calls " + call + ".");
        return problems;
    }

    /// <summary>"System.Collections.Generic.List`1[[X, A]]::Clear" → "System.Collections.Generic.List`1::Clear".</summary>
    public static string WithoutGenericArguments(string callee)
    {
        if (callee == null) throw new ArgumentNullException(nameof(callee));
        var builder = new System.Text.StringBuilder(callee.Length); var depth = 0;
        foreach (var c in callee)
        {
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (depth == 0) builder.Append(c);
        }
        return builder.ToString();
    }

    private static void RequireOnce(AzuMethodShape method, string callee, List<string> problems)
    {
        var count = method.Calls.Count(x => x == callee);
        if (count != 1) problems.Add($"{method.Name} calls {callee} {count} times; expected 1.");
    }
    private static int IndexOf(IReadOnlyList<string> calls, string callee)
    { for (var i = 0; i < calls.Count; i++) if (calls[i] == callee) return i; return -1; }

    public static bool Rejects(ChestRuleDecision decision) =>
        decision == ChestRuleDecision.Reject || decision == ChestRuleDecision.RejectCategoriesPending || decision == ChestRuleDecision.RejectUnreadable;

    /// <summary>H1: Azu may try this chest unless its rule rejects the item.</summary>
    public static bool MayTry(ChestRuleDecision decision) => !Rejects(decision);

    /// <summary>H2: the must-have-existing-item gate. A ruled chest that accepts, or a reserved drawer (D047), counts as having it.</summary>
    public static bool HasItem(bool original, ChestRuleDecision decision, bool reservedDrawerWants) =>
        original || decision == ChestRuleDecision.Accept || reservedDrawerWants;

    /// <summary>H3: Azu's own exclusions and the chest rule must both allow the item.</summary>
    public static bool CanStore(bool original, ChestRuleDecision decision) => original && !Rejects(decision);

    /// <summary>Backstop: refuse a write into a chest whose rule rejects the item while an Azu entry runs (D046).</summary>
    public static bool RefuseWrite(int azuDepth, ChestRuleDecision decision) => azuDepth > 0 && Rejects(decision);
}

/// <summary>
/// D047, workaround for AzuAutoStore and RossItemDrawers: a drawer assigned to an item wants it even while empty.
/// Exact ordinal prefab identity, as Ross stores it. Removal condition: Azu treats Ross reservations as wanting their
/// item, or Ross exposes a reservation that passes Azu's existing-item check.
/// </summary>
public static class ReservedDrawer
{
    public static bool Wants(string? assignedItem, string? itemPrefab) =>
        !string.IsNullOrEmpty(assignedItem) && !string.IsNullOrEmpty(itemPrefab) && StringComparer.Ordinal.Equals(assignedItem, itemPrefab);
}
