using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace YAVSOS.Transfers;

/// <summary>
/// F14 (D129) and the X60 spike (D143): asks the game whether an inventory takes one unit of an item, through
/// <c>Inventory.CanAddItem(item, 1)</c>, which other mods patch to refuse items (Storehouse's stores take only their own
/// kind). Capability-checked (D058): the method's shape is checked once, and a missing or changed method, or any
/// exception from the call, switches off only this check for the session. The planners then plan as without it. No
/// answer (<c>null</c>) means "not asked": callers treat it as "takes". Unity game thread only; changes nothing.
/// </summary>
internal static class GameTakes
{
    private static ManualLogSource? log;
    private static bool? usable;
    private static string? fault;

    internal static void Initialize(ManualLogSource logger) => log = logger;
    /// <summary>The plugin's log, for the Lab's X60 pass.</summary>
    internal static ManualLogSource? Log => log;

    /// <summary>False once the shape check or a call failed; the planners then don't ask.</summary>
    internal static bool Usable => usable ??= Probe();

    /// <summary>Why the check is off, for the Lab; null while it works.</summary>
    internal static string? Fault => Usable ? null : fault;

    /// <summary>The game's answer, or null when the check is off or <paramref name="item"/> can't be asked about.</summary>
    internal static bool? Takes(Inventory? inventory, ItemDrop.ItemData? item)
    {
        if (inventory == null || item?.m_shared == null || !Usable) return null;
        try { return Ask(inventory, item); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Disable($"{error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    // Its own method, so a changed signature fails here (MissingMethodException) and is caught above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Ask(Inventory inventory, ItemDrop.ItemData item) => inventory.CanAddItem(item, 1);

    private static bool Probe()
    {
        var method = AccessTools.DeclaredMethod(typeof(Inventory), "CanAddItem", new[] { typeof(ItemDrop.ItemData), typeof(int) });
        if (method != null && !method.IsStatic && method.ReturnType == typeof(bool)) return true;
        fault = "Inventory.CanAddItem(ItemData, int) returning bool was not found";
        log?.LogWarning($"YAVSOS doesn't ask chests whether they take an item ({fault}); cleanup and stash plan without that check.");
        return false;
    }

    private static void Disable(string why)
    {
        usable = false; fault = why;
        log?.LogWarning($"YAVSOS stopped asking chests whether they take an item ({why}); cleanup and stash plan without that check this session.");
    }
}
