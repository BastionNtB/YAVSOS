using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace YAVSOS.Interface;

/// <summary>
/// The stack under the pointer in one of the inventory screen's grids, through the public
/// <c>InventoryGrid.GetItem(Vector2i)</c> screen-point lookup (works with grids other mods scale or rotate). Null when
/// nothing is there or anything differs. Unity game thread only. (B2-18: moved out of ResultPanel.cs.)
/// </summary>
internal static class GridHover
{
    internal static ManualLogSource? Log;
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    internal static ItemDrop.ItemData? Item(InventoryGrid? grid)
    {
        if (!grid) return null;
        try
        {
            var pointer = ZInput.pointerPosition;
            return grid!.GetItem(new Vector2i((int)pointer.x, (int)pointer.y));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // SF8 (D087): runs every frame, so once per exception type.
            if (Reported.Add(error.GetType().FullName ?? error.GetType().Name))
                Log?.LogWarning($"Inventory slot lookup failed ({error.GetType().Name}: {error.Message}); the single-stack key and hover explanations don't work while it fails. " +
                    "Another mod's inventory grid may cause this.");
            return null;
        }
    }
}
