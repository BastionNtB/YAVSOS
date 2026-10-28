using System;
using System.Collections.Generic;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// Detached, mutable copy of one inventory's cells for planning. The cell rule is D005's, shared with
/// <see cref="DepositPlan"/>: the first partial stack it may merge into (same <see cref="TransferStack.MergeKey"/> and
/// maximum, unprotected: the game's own stacking rule for stackables, D061 Q11),
/// else the first empty unprotected cell, scanning rows top to bottom and cells left to right. Planned moves update
/// the grid so no cell is used twice. A stack a plan creates takes merges only in a later run, unless the grid is built
/// with <c>mergePlanned</c>: then a later move of the same run may merge into it and the writer finds that stack's
/// handle in its fresh capture (D070: a stash leaves nothing for cleanup). Advisory only; every write rechecks against a fresh snapshot.
/// </summary>
public sealed class TransferGrid
{
    private sealed class Cell
    {
        internal Guid? Handle;
        internal string Key = "";
        internal int Quantity;
        internal int Maximum;
        internal bool Mergeable;
        internal bool Planned;
    }

    private readonly Cell?[] cells;
    private readonly bool[] blocked;
    private readonly bool mergePlanned;
    public int Width { get; }
    public int Height { get; }

    public TransferGrid(TransferInventorySnapshot snapshot, bool mergePlanned = false)
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        this.mergePlanned = mergePlanned;
        Width = snapshot.Width; Height = snapshot.Height;
        cells = new Cell?[Width * Height]; blocked = new bool[Width * Height];
        foreach (var cell in snapshot.ProtectedCells) blocked[cell] = true;
        foreach (var stack in snapshot.Stacks)
        {
            var at = stack.Y * Width + stack.X;
            cells[at] = new Cell
            {
                Handle = stack.Handle, Key = stack.MergeKey, Quantity = stack.Quantity, Maximum = stack.MaximumStack,
                Mergeable = !stack.Protected && stack.TransferSupported && !blocked[at]
            };
        }
    }

    /// <summary>Finds the D005 cell for an item; <paramref name="room"/> is how many units that one cell takes.</summary>
    public bool TryFind(string mergeKey, int maximumStack, out int x, out int y, out Guid? merge, out int room) =>
        TryFind(mergeKey, maximumStack, out x, out y, out merge, out room, out _);

    /// <summary>As above; <paramref name="planned"/> is a merge into a stack this plan created, which has no handle yet.</summary>
    public bool TryFind(string mergeKey, int maximumStack, out int x, out int y, out Guid? merge, out int room, out bool planned)
    {
        planned = false;
        if (mergeKey == null) throw new ArgumentNullException(nameof(mergeKey));
        if (maximumStack < 1) throw new ArgumentOutOfRangeException(nameof(maximumStack));
        for (var at = 0; at < cells.Length; at++)
        {
            var cell = cells[at];
            if (cell == null || !cell.Mergeable || cell.Maximum != maximumStack || cell.Quantity >= cell.Maximum ||
                !StringComparer.Ordinal.Equals(mergeKey, cell.Key)) continue;
            x = at % Width; y = at / Width; merge = cell.Handle; room = cell.Maximum - cell.Quantity; planned = cell.Planned;
            return true;
        }
        for (var at = 0; at < cells.Length; at++)
        {
            if (cells[at] != null || blocked[at]) continue;
            x = at % Width; y = at / Width; merge = null; room = maximumStack;
            return true;
        }
        x = y = -1; merge = null; room = 0;
        return false;
    }

    /// <summary>Records units planned into a cell found by <see cref="TryFind"/>.</summary>
    public void Add(int x, int y, string mergeKey, int maximumStack, int units)
    {
        var at = Index(x, y);
        var cell = cells[at];
        if (cell == null)
        {
            if (blocked[at] || units < 1 || units > maximumStack) throw new InvalidOperationException("Planned cell is unavailable.");
            // A stack this plan creates has no handle yet; a later move of the same run merges into it only when allowed.
            cells[at] = new Cell { Key = mergeKey, Quantity = units, Maximum = maximumStack, Mergeable = mergePlanned, Planned = true };
            return;
        }
        if (!cell.Mergeable || !StringComparer.Ordinal.Equals(mergeKey, cell.Key) || cell.Maximum != maximumStack ||
            units < 1 || cell.Quantity + units > cell.Maximum)
            throw new InvalidOperationException("Planned merge is unavailable.");
        cell.Quantity += units;
    }

    /// <summary>
    /// Records units planned out of a stack. A stack left partly in place stops taking merges, so a later plan
    /// never merges into a stack that is moving out. A stack planned out entirely frees its cell for later plans.
    /// </summary>
    public void Remove(int x, int y, int units)
    {
        var at = Index(x, y);
        var cell = cells[at] ?? throw new InvalidOperationException("No planned stack in that cell.");
        if (units < 1 || units > cell.Quantity) throw new InvalidOperationException("Planned removal exceeds the stack.");
        cell.Quantity -= units;
        if (cell.Quantity == 0) cells[at] = null; else cell.Mergeable = false;
    }

    /// <summary>
    /// The stack a planned move merges into, from the writer's fresh captures: <paramref name="mergeHandle"/>, or for a
    /// merge into a stack the same run created (<paramref name="planned"/>) the stack now in that cell when it is the
    /// moving stack's kind. False: the inventory changed, refuse. <see cref="DepositPlan"/> checks the rest (room, maximum, protection).
    /// </summary>
    public static bool TryMergeTarget(bool planned, Guid? mergeHandle, TransferInventorySnapshot source, Guid handle,
        TransferInventorySnapshot destination, int x, int y, out Guid? merge)
    {
        merge = mergeHandle;
        if (!planned) return true;
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        TransferStack? moving = null, occupant = null;
        foreach (var stack in source.Stacks) if (stack.Handle == handle) moving = stack;
        foreach (var stack in destination.Stacks) if (stack.X == x && stack.Y == y) occupant = stack;
        if (moving == null || occupant == null || !StringComparer.Ordinal.Equals(moving.MergeKey, occupant.MergeKey)) return false;
        merge = occupant.Handle;
        return true;
    }

    private int Index(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException(nameof(x));
        return y * Width + x;
    }
}
