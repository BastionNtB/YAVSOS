using System;
using System.Collections.Generic;

namespace YAVSOS.Core.Transfers;

/// <summary>
/// F14 (D129): the game's own answer whether a destination chest takes an item, which other mods can narrow. Storehouse's
/// stores take only their own kind (D143). The adapter asks the game's <c>Inventory.CanAddItem(item, 1)</c> and answers
/// true when it can't ask. A planner asks only when the chest's snapshot has room for one unit of the stack (the D005
/// cell rule of <see cref="TransferGrid"/>), so a "no" there is the chest refusing the item, not the chest being full; a
/// full chest stays a matter of room, as before. Answers are kept per chest and stack kind
/// (<see cref="TransferStack.MergeKey"/>) for one plan or explanation context. A refusal only removes a destination, so a
/// plan never moves more than it would without the check. Advisory only: every write still rechecks.
/// </summary>
internal sealed class ChestTakes
{
    private readonly Func<string, TransferStack, bool>? takes;
    private readonly Dictionary<string, Chest> chests = new(StringComparer.Ordinal);

    private sealed class Chest
    {
        internal readonly TransferGrid Grid;
        internal readonly Dictionary<string, bool> Refused = new(StringComparer.Ordinal);
        internal Chest(TransferInventorySnapshot inventory) => Grid = new TransferGrid(inventory);
    }

    internal ChestTakes(Func<string, TransferStack, bool>? takes) => this.takes = takes;

    /// <summary>
    /// True when chest <paramref name="chestId"/> refuses <paramref name="stack"/>: its snapshot
    /// <paramref name="inventory"/> has room for one unit and the game still says no. False without the check.
    /// </summary>
    internal bool Refuses(string chestId, TransferInventorySnapshot inventory, TransferStack stack)
    {
        if (takes == null) return false;
        if (!chests.TryGetValue(chestId, out var chest)) chests.Add(chestId, chest = new Chest(inventory));
        var key = stack.MergeKey;
        if (chest.Refused.TryGetValue(key, out var refused)) return refused;
        refused = chest.Grid.TryFind(key, stack.MaximumStack, out _, out _, out _, out _) && !takes(chestId, stack);
        chest.Refused.Add(key, refused);
        return refused;
    }
}
