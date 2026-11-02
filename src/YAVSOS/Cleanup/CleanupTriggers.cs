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
/// D057 O7, D125, D126: area cleanup on the modifiers of <c>Cleanup.Shortcut</c> (Ctrl by default) plus AzuAutoStore's
/// own store key (Ctrl + . by default), following the player's Azu binding; without Azu, YAVSOS's own shortcut. Also the
/// console command <c>yavsos cleanup [radius]</c> for testing. Unity game thread only.
/// </summary>
internal static class CleanupTriggers
{
    private static ConfigEntry<KeyboardShortcut>? azuStore, azuSingle, azuPause;
    private static ConfigEntry<KeyboardShortcut>? watchedOwn;
    private static BepInEx.Logging.ManualLogSource? log;
    private static bool pauseWarned;

    /// <summary>
    /// Binds Azu's private store shortcut by shape; on any mismatch YAVSOS's own shortcut applies. Also Azu's "Store Single
    /// Item Shortcut", for the tooltip's label only (P8-17); unbound, the label carries no key. And Azu's "Pause Shortcut",
    /// only to warn when it is also the cleanup key (D125); unread, there is no check.
    /// </summary>
    internal static void BindAzuStoreKey(Assembly azu, BepInEx.Logging.ManualLogSource? log = null)
    {
        CleanupTriggers.log = log;
        string? storeProblem = null, singleProblem = null;
        try
        {
            var plugin = azu.GetType("AzuAutoStore.AzuAutoStorePlugin", false);
            azuStore = Shortcut(plugin, "_storeShortcut", out storeProblem);
            azuSingle = Shortcut(plugin, "SingleItemShortcut", out singleProblem);
            azuPause = Shortcut(plugin, "_pauseShortcut", out _);
        }
        catch (Exception error)
        {
            azuStore = null; azuSingle = null; azuPause = null;
            storeProblem = singleProblem = error.GetType().Name + ": " + error.Message;
        }
        // SF7 (D087): what the player's keys do instead, once at load.
        var own = Plugin.Instance?.Settings.CleanupShortcut;
        if (storeProblem != null)
            log?.LogWarning("AzuAutoStore's store key could not be read (" + storeProblem + "); cleanup uses YAVSOS's own Cleanup shortcut" +
                (own != null ? " (" + own.Value + ")" : "") + " instead of its modifier plus AzuAutoStore's store key.");
        if (singleProblem != null)
            log?.LogWarning("AzuAutoStore's single-item store key could not be read (" + singleProblem + "); the \"Stash stack\" tooltip line shows no key.");
        if (azuStore == null || azuPause == null || own == null) return;
        watchedOwn = own;
        own.SettingChanged += (_, _) => WarnIfPause();
        azuStore.SettingChanged += (_, _) => WarnIfPause();
        azuPause.SettingChanged += (_, _) => WarnIfPause();
        WarnIfPause();
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

    /// <summary>
    /// D125, D126: when the cleanup chord is also Azu's Pause Shortcut (Shift + . by default, so a Shift modifier in
    /// <c>Cleanup.Shortcut</c>), each cleanup would also pause or resume Azu's storing. Said once per session, at load or
    /// when one of the keys changes to match.
    /// </summary>
    private static void WarnIfPause()
    {
        if (pauseWarned || azuStore == null || azuPause == null || watchedOwn == null) return;
        var chord = CleanupKey.WithAzu(Chord(watchedOwn.Value), Chord(azuStore.Value));
        if (!CleanupKey.Same(chord, Chord(azuPause.Value))) return;
        pauseWarned = true;
        log?.LogWarning("YAVSOS's cleanup key (" + chord + ") is also AzuAutoStore's Pause Shortcut, so each cleanup also pauses or resumes " +
            "AzuAutoStore's storing. Change AzuAutoStore's Pause Shortcut, or the modifier in YAVSOS's Cleanup Shortcut.");
    }

    private static KeyChord Chord(KeyboardShortcut shortcut) =>
        new KeyChord(shortcut.MainKey.ToString(), shortcut.Modifiers.Select(x => x.ToString()));

    /// <summary>The cleanup chord while Azu's store key is bound (D125), else null: YAVSOS's own shortcut applies.</summary>
    private static KeyChord? AzuChord(ConfigEntry<KeyboardShortcut> own) =>
        azuStore == null ? null : CleanupKey.WithAzu(Chord(own.Value), Chord(azuStore.Value));

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
    /// The cleanup key as a player reads it ("Ctrl + ."), for the Clean up nearby tooltip (D101): the chord
    /// <see cref="Pressed"/> checks, with Azu's store key while it is bound, else YAVSOS's own; "" when unset.
    /// </summary>
    internal static string KeyName(ConfigEntry<KeyboardShortcut> own) =>
        azuStore != null ? AzuChord(own)?.ToString() ?? "" : Chord(own.Value).ToString();

    internal static bool Pressed(ConfigEntry<KeyboardShortcut> own) => !InventoryGui.IsVisible() && Down(own);

    /// <summary>F7 (DOCS-CODE-COMPARISON U2): the cleanup key pressed with the inventory open, which runs nothing; the caller says so.</summary>
    internal static bool PressedInInventory(ConfigEntry<KeyboardShortcut> own) => InventoryGui.IsVisible() && Down(own);

    private static bool Down(ConfigEntry<KeyboardShortcut> own)
    {
        if (!Player.m_localPlayer || Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() ||
            Chat.instance && Chat.instance.HasFocus() || Interface.TypingBlock.Active) return false;
        if (azuStore == null) return own.Value.IsDown();
        // Every frame: the chord's main key is Azu's, so the chord is built only on the frame it goes down. Extra held keys
        // don't block the chord; Azu's own store key (BepInEx IsDown) doesn't fire while a modifier is held.
        var main = azuStore.Value.MainKey;
        if (main == KeyCode.None || !UnityInput.Current.GetKeyDown(main) || AzuChord(own) is not { } chord) return false;
        foreach (var modifier in chord.Modifiers)
            if (!Held(modifier) && !(CleanupKey.OtherSide(modifier) is { } other && Held(other))) return false;
        return true;
    }

    private static bool Held(string key) => Enum.TryParse<KeyCode>(key, out var code) && UnityInput.Current.GetKey(code);
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
