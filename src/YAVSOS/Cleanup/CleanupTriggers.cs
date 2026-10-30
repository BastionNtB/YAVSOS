using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Core.Interface;

namespace YAVSOS.Cleanup;

/// <summary>
/// D057 O7: area cleanup on Ctrl plus AzuAutoStore's own store key (Ctrl+. by default), following the player's Azu
/// binding; without Azu, YAVSOS's own shortcut. Also the console command <c>yavsos cleanup [radius]</c> for testing.
/// Unity game thread only.
/// </summary>
internal static class CleanupTriggers
{
    private static ConfigEntry<KeyboardShortcut>? azuStore, azuSingle;

    /// <summary>
    /// Binds Azu's private store shortcut by shape; on any mismatch YAVSOS's own shortcut applies. Also Azu's "Store Single
    /// Item Shortcut", for the tooltip's label only (P8-17); unbound, the label carries no key.
    /// </summary>
    internal static void BindAzuStoreKey(Assembly azu, BepInEx.Logging.ManualLogSource? log = null)
    {
        string? storeProblem = null, singleProblem = null;
        try
        {
            var plugin = azu.GetType("AzuAutoStore.AzuAutoStorePlugin", false);
            azuStore = Shortcut(plugin, "_storeShortcut", out storeProblem);
            azuSingle = Shortcut(plugin, "SingleItemShortcut", out singleProblem);
        }
        catch (Exception error)
        {
            azuStore = null; azuSingle = null;
            storeProblem = singleProblem = error.GetType().Name + ": " + error.Message;
        }
        // SF7 (D087): what the player's keys do instead, once at load.
        if (storeProblem != null)
            log?.LogWarning("AzuAutoStore's store key could not be read (" + storeProblem + "); cleanup uses YAVSOS's own Cleanup shortcut" +
                (Plugin.Instance is { } yavsos ? " (" + yavsos.Settings.CleanupShortcut.Value + ")" : "") + " instead of Ctrl plus AzuAutoStore's store key.");
        if (singleProblem != null)
            log?.LogWarning("AzuAutoStore's single-item store key could not be read (" + singleProblem + "); the \"Stash stack\" tooltip line shows no key.");
    }

    private static ConfigEntry<KeyboardShortcut>? Shortcut(Type? plugin, string name, out string? problem)
    {
        var field = plugin == null ? null : AccessTools.DeclaredField(plugin, name);
        problem = plugin == null ? "AzuAutoStorePlugin not found" : field == null ? name + " not found" : !field.IsStatic ? name + " is not static"
            : field.FieldType != typeof(ConfigEntry<KeyboardShortcut>) ? name + " is " + field.FieldType.Name : null;
        if (problem != null) return null;
        var entry = field!.GetValue(null) as ConfigEntry<KeyboardShortcut>;
        if (entry == null) problem = name + " is not set";
        return entry;
    }

    internal static string Binding(ConfigEntry<KeyboardShortcut> own) =>
        azuStore != null ? "Ctrl+" + azuStore.Value : own.Value.ToString();

    /// <summary>
    /// P8-17 (Q12, revised 2026-10-28): the single-stack key as a player reads it ("Middle mouse"): Azu's "Store Single
    /// Item Shortcut" when Azu stores (its single-item store follows YAVSOS's order too, <c>TryStoreThisItem</c>), else
    /// YAVSOS's own; "" when Azu's couldn't be read or is unset, so the tooltip says "Stash stack" without a key.
    /// </summary>
    internal static string StackKey(bool azuStores, ConfigEntry<KeyboardShortcut> own)
    {
        var shortcut = azuStores ? azuSingle?.Value : own.Value;
        return shortcut is { } key && key.MainKey != KeyCode.None ? KeyNames.Format(key.MainKey.ToString(), key.Modifiers.Select(x => x.ToString())) : "";
    }

    /// <summary>
    /// The cleanup key as a player reads it ("Ctrl + ."), for the Clean up nearby tooltip (D101): Ctrl plus Azu's store key
    /// while it is bound, as <see cref="Pressed"/> checks, else YAVSOS's own; "" when unset.
    /// </summary>
    internal static string KeyName(ConfigEntry<KeyboardShortcut> own)
    {
        if (azuStore != null)
        {
            var azu = azuStore.Value;
            return azu.MainKey == KeyCode.None ? "" : KeyNames.Format(azu.MainKey.ToString(), new[] { "LeftControl" }.Concat(azu.Modifiers.Select(x => x.ToString())));
        }
        return own.Value is { } key && key.MainKey != KeyCode.None ? KeyNames.Format(key.MainKey.ToString(), key.Modifiers.Select(x => x.ToString())) : "";
    }

    internal static bool Pressed(ConfigEntry<KeyboardShortcut> own) => !InventoryGui.IsVisible() && Chord(own);

    /// <summary>F7 (DOCS-CODE-COMPARISON U2): the cleanup key pressed with the inventory open, which runs nothing; the caller says so.</summary>
    internal static bool PressedInInventory(ConfigEntry<KeyboardShortcut> own) => InventoryGui.IsVisible() && Chord(own);

    private static bool Chord(ConfigEntry<KeyboardShortcut> own)
    {
        if (!Player.m_localPlayer || Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() ||
            Chat.instance && Chat.instance.HasFocus() || Interface.TypingBlock.Active) return false;
        if (azuStore == null) return own.Value.IsDown();
        var shortcut = azuStore.Value;
        if (shortcut.MainKey == KeyCode.None || !UnityInput.Current.GetKeyDown(shortcut.MainKey)) return false;
        if (!UnityInput.Current.GetKey(KeyCode.LeftControl) && !UnityInput.Current.GetKey(KeyCode.RightControl)) return false;
        foreach (var modifier in shortcut.Modifiers) if (!UnityInput.Current.GetKey(modifier)) return false;
        return true;
    }
}

[HarmonyPatch(typeof(Terminal), "InitTerminal")]
internal static class CleanupConsoleCommand
{
    [HarmonyPostfix]
    private static void Register()
    {
        new Terminal.ConsoleCommand("yavsos", "cleanup [radius] - move misplaced items between nearby chests by their YAVSOS rules", args =>
        {
            if (args.Args.Length < 2 || !args.Args[1].Equals("cleanup", StringComparison.OrdinalIgnoreCase))
            { args.Context?.AddString("Usage: yavsos cleanup [radius]"); return; }
            float? radius = args.Args.Length > 2 && float.TryParse(args.Args[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
            var runner = Plugin.Instance?.Cleanup;
            args.Context?.AddString(runner == null ? "YAVSOS is not loaded." : runner.Start(radius));
        });
    }
}
