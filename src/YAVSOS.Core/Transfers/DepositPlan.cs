using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YAVSOS.Core.Transfers;

public enum TransferRefusal
{
    None, AccessDenied, Busy, UnsupportedAuthority, UnsupportedCapability, Protected, PolicyChanged,
    InventoryChanged, OwnerChanged, NoCapacity, Expired, Disconnected, ProtocolMismatch,
    InvariantFailed, Unconfirmed, RuleMismatch,
    /// <summary>The server turned the feature off mid-run (A01); the rest of the run's moves are refused.</summary>
    TurnedOff
}

/// <summary>Freshness tokens are compared for equality, including owner revision wrap/reset.</summary>
public sealed class TransferInventoryStamp : IEquatable<TransferInventoryStamp>
{
    public string InventoryId { get; }
    public Guid InstanceGeneration { get; }
    public ulong ChangeEpoch { get; }
    public ulong LayoutProtectionRevision { get; }
    public long OwnerPeer { get; }
    public ushort NativeOwnerRevision { get; }
    public string AuthoritativePayloadFingerprint { get; }
    public TransferInventoryStamp(string inventoryId, Guid instanceGeneration, ulong changeEpoch,
        ulong layoutProtectionRevision, long ownerPeer, ushort nativeOwnerRevision,
        string authoritativePayloadFingerprint)
    {
        InventoryId = TransferCanonical.Text(inventoryId, 2048, false);
        if (instanceGeneration == Guid.Empty) throw new ArgumentException("Missing adapter instance generation.");
        InstanceGeneration = instanceGeneration; ChangeEpoch = changeEpoch; LayoutProtectionRevision = layoutProtectionRevision;
        OwnerPeer = ownerPeer; NativeOwnerRevision = nativeOwnerRevision;
        AuthoritativePayloadFingerprint = TransferCanonical.Text(authoritativePayloadFingerprint, 128, false);
    }
    public bool Equals(TransferInventoryStamp? other) => other != null &&
        StringComparer.Ordinal.Equals(InventoryId, other.InventoryId) && InstanceGeneration == other.InstanceGeneration &&
        ChangeEpoch == other.ChangeEpoch && LayoutProtectionRevision == other.LayoutProtectionRevision && OwnerPeer == other.OwnerPeer &&
        NativeOwnerRevision == other.NativeOwnerRevision &&
        StringComparer.Ordinal.Equals(AuthoritativePayloadFingerprint, other.AuthoritativePayloadFingerprint);
    public override bool Equals(object? obj) => obj is TransferInventoryStamp other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(InventoryId);
}

public sealed class TransferStack
{
    public Guid Handle { get; }
    public int X { get; }
    public int Y { get; }
    public int Quantity { get; }
    public int MaximumStack { get; }
    public TransferItemIdentity Identity { get; }
    public bool Protected { get; }
    public bool TransferSupported { get; }
    /// <summary>
    /// D061 Q11: the game's own stacking class, assigned by the adapter from Valheim's <c>ItemData.IsSameType</c>
    /// (as patched by any mod): stackables of one class may merge, whoever crafted them. Null: merge only identical stacks.
    /// </summary>
    public string? MergeClass { get; }
    public TransferStack(Guid handle, int x, int y, int quantity, int maximumStack, TransferItemIdentity identity,
        bool isProtected = false, bool transferSupported = true, string? mergeClass = null)
    {
        if (handle == Guid.Empty || x < 0 || y < 0 || maximumStack < 1 || quantity < 1 || quantity > maximumStack)
            throw new ArgumentException("Invalid selected stack.");
        Handle = handle; X = x; Y = y; Quantity = quantity; MaximumStack = maximumStack;
        Identity = identity ?? throw new ArgumentNullException(nameof(identity)); Protected = isProtected; TransferSupported = transferSupported;
        if (mergeClass != null) MergeClass = TransferCanonical.Text(mergeClass, 256, false);
    }

    /// <summary>What decides merging, and what a move of this stack is counted by: the game's class for stackables, else the full identity.</summary>
    public string MergeKey => MaximumStack > 1 && MergeClass != null ? "class:" + MergeClass : "item:" + Identity.Fingerprint;
}

public sealed class TransferInventorySnapshot
{
    public TransferInventoryStamp Stamp { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<TransferStack> Stacks { get; }
    public IReadOnlyList<int> ProtectedCells { get; }
    public string ContentFingerprint { get; }
    public TransferInventorySnapshot(TransferInventoryStamp stamp, int width, int height,
        IEnumerable<TransferStack> stacks, IEnumerable<int> protectedCells)
    {
        Stamp = stamp ?? throw new ArgumentNullException(nameof(stamp));
        if (width < 1 || height < 1 || (long)width * height > 65536) throw new ArgumentException("Unsupported grid bounds.");
        Width = width; Height = height;
        if (stacks == null || protectedCells == null) throw new ArgumentNullException();
        var own = new List<TransferStack>(); var cells = new HashSet<int>(); var handles = new HashSet<Guid>();
        foreach (var stack in stacks)
        {
            if (stack == null || stack.X >= width || stack.Y >= height || !cells.Add(stack.Y * width + stack.X) || !handles.Add(stack.Handle))
                throw new ArgumentException("Invalid or duplicate stack/cell.");
            own.Add(stack);
        }
        Stacks = Array.AsReadOnly(own.OrderBy(s => s.Y).ThenBy(s => s.X).ToArray());
        var blocked = new HashSet<int>();
        foreach (var cell in protectedCells)
        {
            if (cell < 0 || cell >= width * height) throw new ArgumentException("Protected cell outside grid.");
            blocked.Add(cell);
        }
        ProtectedCells = Array.AsReadOnly(blocked.OrderBy(x => x).ToArray());
        ContentFingerprint = TransferCanonical.Hash(WriteCanonical, TransferLimits.MaximumInventoryPayloadBytes);
    }
    private void WriteCanonical(BinaryWriter w)
    {
        w.Write("yavsos-transfer-inventory-1"); w.Write(Width); w.Write(Height); w.Write(ProtectedCells.Count);
        foreach (var cell in ProtectedCells) w.Write(cell);
        w.Write(Stacks.Count);
        foreach (var item in Stacks)
        {
            w.Write(item.Handle.ToByteArray()); w.Write(item.X); w.Write(item.Y); w.Write(item.Quantity);
            w.Write(item.MaximumStack); w.Write(item.Protected); w.Write(item.TransferSupported); w.Write(item.MergeClass ?? ""); item.Identity.WriteCanonical(w);
        }
    }
}

/// <summary>One requested stack, one destination cell. This is an intention, never a permit.</summary>
public sealed class DepositPlan
{
    public Guid OperationId { get; }
    public Guid SessionNonce { get; }
    public string PolicyStamp { get; }
    public string RuleStamp { get; }
    public TransferInventorySnapshot Source { get; }
    public TransferInventorySnapshot Destination { get; }
    public TransferStack Selection { get; }
    public int RequestedUnits { get; }
    public int PlannedUnits { get; }
    public int DestinationX { get; }
    public int DestinationY { get; }
    public Guid? MergeHandle { get; }
    public double AdmissionDeadline { get; }
    private DepositPlan(Guid operationId, Guid sessionNonce, string policyStamp, string ruleStamp,
        TransferInventorySnapshot source, TransferInventorySnapshot destination, TransferStack selection,
        int requestedUnits, int plannedUnits, int x, int y, Guid? mergeHandle, double deadline)
    {
        OperationId = operationId; SessionNonce = sessionNonce; PolicyStamp = policyStamp; RuleStamp = ruleStamp;
        Source = source; Destination = destination; Selection = selection; RequestedUnits = requestedUnits; PlannedUnits = plannedUnits;
        DestinationX = x; DestinationY = y; MergeHandle = mergeHandle; AdmissionDeadline = deadline;
    }

    /// <summary>
    /// A plan for a cell the caller already chose (a cleanup move), where the caller has made the rule decision. The
    /// fresh snapshots decide: the units shrink to the stack's current quantity and the cell's current room, never grow.
    /// The cell must still be the same strictly identical partial stack (<paramref name="mergeHandle"/>), or still empty.
    /// </summary>
    public static TransferRefusal TryCreateAt(Guid operationId, Guid sessionNonce, string policyStamp, string ruleStamp,
        TransferInventorySnapshot source, TransferInventorySnapshot destination, Guid selection, int requestedUnits,
        int x, int y, Guid? mergeHandle, double now, double admissionDeadline, out DepositPlan? plan)
    {
        plan = null;
        if (operationId == Guid.Empty || sessionNonce == Guid.Empty || requestedUnits < 1 || source == null || destination == null ||
            source.Stamp.InventoryId == destination.Stamp.InventoryId || double.IsNaN(now) || double.IsInfinity(now) || now < 0 ||
            double.IsNaN(admissionDeadline) || double.IsInfinity(admissionDeadline) || admissionDeadline <= now)
            return TransferRefusal.Expired;
        TransferCanonical.Text(policyStamp, 2048, false); TransferCanonical.Text(ruleStamp, 2048, false);
        var item = source.Stacks.SingleOrDefault(s => s.Handle == selection);
        if (item == null) return TransferRefusal.InventoryChanged;
        if (item.Protected || source.ProtectedCells.Contains(item.Y * source.Width + item.X)) return TransferRefusal.Protected;
        if (!item.TransferSupported) return TransferRefusal.UnsupportedCapability;
        if (x < 0 || y < 0 || x >= destination.Width || y >= destination.Height) return TransferRefusal.NoCapacity;
        var cell = y * destination.Width + x;
        if (destination.ProtectedCells.Contains(cell)) return TransferRefusal.NoCapacity;
        var occupant = destination.Stacks.SingleOrDefault(s => s.X == x && s.Y == y);
        int room;
        if (mergeHandle == null)
        {
            if (occupant != null) return TransferRefusal.NoCapacity;
            room = item.MaximumStack;
        }
        else
        {
            if (occupant == null || occupant.Handle != mergeHandle || occupant.Protected || !occupant.TransferSupported ||
                occupant.MaximumStack != item.MaximumStack || !StringComparer.Ordinal.Equals(item.MergeKey, occupant.MergeKey)) return TransferRefusal.InventoryChanged;
            room = occupant.MaximumStack - occupant.Quantity;
            if (room < 1) return TransferRefusal.NoCapacity;
        }
        var units = Math.Min(Math.Min(requestedUnits, item.Quantity), room);
        plan = new DepositPlan(operationId, sessionNonce, policyStamp, ruleStamp, source, destination, item, units,
            units, x, y, mergeHandle, admissionDeadline);
        return TransferRefusal.None;
    }
}
