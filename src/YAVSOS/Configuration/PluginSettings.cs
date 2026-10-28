using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ServerSync;
using YAVSOS.Cleanup;

namespace YAVSOS.Configuration;

internal sealed class PluginSettings
{
    public ConfigEntry<bool> LockSharedPolicy { get; }
    public ConfigEntry<bool> ShowRuleControls { get; }
    public ConfigEntry<bool> ShowResultPanel { get; }
    public ConfigEntry<bool> ShowChestLabels { get; }
    public ConfigEntry<bool> ShowItemCategories { get; }
    public ConfigEntry<int> HighlightSecondsAfterClose { get; }
    public ConfigEntry<float> RulePanelOffsetX { get; }
    public ConfigEntry<float> RulePanelOffsetY { get; }
    public ConfigEntry<bool> AllowCleanup { get; }
    public ConfigEntry<float> MaximumRange { get; }
    public ConfigEntry<int> MaximumChests { get; }
    public ConfigEntry<float> CleanupRange { get; }
    public ConfigEntry<KeyboardShortcut> CleanupShortcut { get; }
    public ConfigEntry<bool> AllowStashing { get; }
    public ConfigEntry<float> StashRange { get; }
    public ConfigEntry<KeyboardShortcut> StashShortcut { get; }
    public ConfigEntry<KeyboardShortcut> StashItemShortcut { get; }
    public ConfigEntry<bool> StashHotbar { get; }
    public ConfigEntry<bool> StashQuickSlots { get; }
    /// <summary>The player's stash range, capped by the server's maximum (D061 Q6).</summary>
    public float EffectiveStashRange => Math.Max(1f, Math.Min(StashRange.Value, MaximumRange.Value));
    /// <summary>The player's cleanup range, capped by the server's maximum (D057 O1, O9).</summary>
    public float EffectiveCleanupRange => Math.Max(1f, Math.Min(CleanupRange.Value, MaximumRange.Value));
    /// <summary>D095: the server's chest limit for one stash or cleanup run, never above the planners' own bound.</summary>
    public int EffectiveMaximumChests => Math.Max(1, Math.Min(MaximumChests.Value, YAVSOS.Core.Transfers.CleanupPlanner.MaximumChests));
    private readonly List<ConfigEntryBase> sharedEntries = new List<ConfigEntryBase>();
    public ConfigEntryBase[] SharedEntries => sharedEntries.ToArray();
    public Type? SharedType(string section, string key)
    {
        foreach (var entry in sharedEntries)
            if (entry.Definition.Section == section && entry.Definition.Key == key) return entry.SettingType;
        return null;
    }

    public PluginSettings(ConfigFile file, ConfigSync sync, Action sharedChanged)
    {
        LockSharedPolicy = file.Bind("Server", "LockSharedPolicy", true,
            "Lock shared settings for nonadministrators. ServerSync permissions apply; this grants no chest access.");
        sync.AddLockingConfigEntry(LockSharedPolicy).SynchronizedConfig = true;
        sharedEntries.Add(LockSharedPolicy);
        LockSharedPolicy.SettingChanged += (_, _) => sharedChanged();
        // D042: settings arrive with their working features; unimplemented placeholders are not registered.
        ShowRuleControls = Bind(file, sync, "Controls", "ShowRuleControls", true,
            "Show the chest rule panel while a chest is open.", false, sharedChanged);
        // P6-04: the result panel adds item names and reasons to the one-line message, which is shown either way.
        ShowResultPanel = Bind(file, sync, "Controls", "ShowResultPanel", true,
            "After a cleanup, or a stash with YAVSOS's own store keys (without AzuAutoStore), show a short result panel with what moved and what stayed, with item names and reasons.", false, sharedChanged);
        // P6-07 (D062 Q3): an automatic label from the chest's rule; no typed names.
        ShowChestLabels = Bind(file, sync, "Controls", "ShowChestLabels", true,
            "Add the chest's rule to its hover text, such as \"Food › eaten by Boar · priority 2 (Urgent)\". Only chests with a rule that you can open.", false, sharedChanged);
        ShowItemCategories = Bind(file, sync, "Controls", "ShowItemCategories", true,
            "Add an item's categories to its tooltip, such as \"Raw food, Animal feed · eaten by Boar, Hen\".", false, sharedChanged);
        // P8-18 (D100): the search highlight is on while the search is; this is how long it stays once the chest closes.
        HighlightSecondsAfterClose = Bind(file, sync, "Controls", "HighlightSecondsAfterClose", YAVSOS.Core.Interface.HighlightLinger.DefaultSeconds,
            "After you close a chest, how many seconds the chests lit by its search stay lit, so you can walk to them. 0 turns them off when the chest closes. While the search is active they stay lit.",
            false, sharedChanged, new AcceptableValueRange<int>(0, YAVSOS.Core.Interface.HighlightLinger.MaximumSeconds));
        // P6-03 (D062): the panel follows the chest window, so its position is an offset from there. The earlier absolute IMGUI
        // PanelX/PanelY keys are no longer read; a stale value would have pushed the panel off its window.
        RulePanelOffsetX = Bind(file, sync, "Controls", "PanelOffsetX", 0f,
            "Rule panel offset to the right of its place beside the chest window, in the game's UI units (1080p-scaled); negative moves it left. Drag the panel's title to change it, for example if it overlaps another mod's window.", false, sharedChanged);
        RulePanelOffsetY = Bind(file, sync, "Controls", "PanelOffsetY", 0f,
            "Rule panel offset downwards from the top of the chest window, in the game's UI units (1080p-scaled); negative moves it up.", false, sharedChanged);
        // D079: with AzuAutoStore installed, it does the stashing, so YAVSOS's Stash settings show read-only (values kept for
        // when it is removed). Server settings stay bound on every side for ServerSync and say what applies in each case.
        var azu = !StashRunner.Enabled;
        const string AzuOwns = "AzuAutoStore is installed and does the stashing, so this is read-only and kept for when it is removed. ";
        object[]? stash = azu ? new object[] { new ConfigurationManagerAttributes { ReadOnly = true } } : null;
        string Stash(string text) => azu ? AzuOwns + text : text;
        // D057 O1/O9: the server decides whether cleanup runs and how far it may reach.
        AllowCleanup = Bind(file, sync, "Server", "AllowCleanup", true,
            "Allow players to run misplaced-item cleanup. Set by the server.", true, sharedChanged);
        // D078 B06: one range cap for cleanup and YAVSOS's own stash, under a name that says so.
        MaximumRange = Bind(file, sync, "Server", "MaximumRange", 50f,
            "Largest range in metres a player may use for cleanup and for YAVSOS's own stash. With AzuAutoStore installed, its store key uses its own range, so this then limits cleanup only. Set by the server.", true, sharedChanged, new AcceptableValueRange<float>(1f, 100f));
        MigrateRenamed(file, new ConfigDefinition("Server", "CleanupMaximumRange"), MaximumRange);
        // D095: one chest limit for cleanup and YAVSOS's own stash; 256 is also the planners' bound, so it can only be lowered.
        MaximumChests = Bind(file, sync, "Server", "MaximumChests", 256,
            "Most chests one cleanup or YAVSOS stash looks at, the nearest first; the result says when more were in range. With AzuAutoStore installed, its store key searches chests itself, so this then limits cleanup only. Set by the server.", true, sharedChanged, new AcceptableValueRange<int>(16, 256));
        CleanupRange = Bind(file, sync, "Cleanup", "Range", 10f,
            "How far from you, in metres, cleanup looks for chests. Capped by the server's MaximumRange.", false, sharedChanged,
            new AcceptableValueRange<float>(1f, 100f));
        CleanupShortcut = Bind(file, sync, "Cleanup", "Shortcut", new KeyboardShortcut(UnityEngine.KeyCode.Period, UnityEngine.KeyCode.LeftControl),
            "Runs cleanup of nearby chests when AzuAutoStore is not installed. With AzuAutoStore, cleanup is Ctrl plus AzuAutoStore's own store key.",
            false, sharedChanged);
        // D061: YAVSOS's own stash, used only when AzuAutoStore is not installed.
        AllowStashing = Bind(file, sync, "Server", "AllowStashing", true,
            "Allow players to stash with YAVSOS's store keys. With AzuAutoStore installed, its own store key stashes and this has no effect. Set by the server.", true, sharedChanged);
        StashRange = Bind(file, sync, "Stash", "Range", 10f,
            Stash("How far from you, in metres, the stash key looks for chests. Capped by the server's MaximumRange. Used only without AzuAutoStore."),
            false, sharedChanged, new AcceptableValueRange<float>(1f, 100f), tags: stash);
        StashShortcut = Bind(file, sync, "Stash", "Shortcut", new KeyboardShortcut(UnityEngine.KeyCode.Period),
            Stash("Stores items from your inventory into nearby chests whose rules accept them, and into unruled chests that already hold them. Used only without AzuAutoStore."),
            false, sharedChanged, tags: stash);
        StashItemShortcut = Bind(file, sync, "Stash", "SingleItemShortcut", new KeyboardShortcut(UnityEngine.KeyCode.Mouse2),
            Stash("With your inventory open, stores the stack under the mouse into nearby chests. Used only without AzuAutoStore."), false, sharedChanged, tags: stash);
        StashHotbar = Bind(file, sync, "Stash", "IncludeHotbar", false,
            Stash("Also store items from your hotbar (the first inventory row), with the store key and the single-item key. Equipped and quest items are never stored. Used only without AzuAutoStore, which applies its own \"Player Ignore Hotbar\"."), false, sharedChanged, tags: stash);
        StashQuickSlots = Bind(file, sync, "Stash", "IncludeQuickSlots", false,
            Stash("Also store items from AzuExtendedPlayerInventory's quick slots, with the store key and the single-item key. Used only without AzuAutoStore, which always keeps them."), false, sharedChanged, tags: stash);
    }

    /// <summary>
    /// D078 B06: reads a setting's value from its old key once, then removes the old key from the file. Only an old key
    /// still in the file is read, so a value set under the new key afterwards is never overwritten. Never synchronized.
    /// </summary>
    private static void MigrateRenamed(ConfigFile file, ConfigDefinition old, ConfigEntry<float> renamed)
    {
        const float Absent = float.NegativeInfinity;
        var legacy = file.Bind(old, Absent, new ConfigDescription("Renamed to " + renamed.Definition.Key + "; read once."));
        if (!float.IsNegativeInfinity(legacy.Value)) renamed.Value = legacy.Value;
        file.Remove(old);
        file.Save();
    }

    private ConfigEntry<T> Bind<T>(ConfigFile file, ConfigSync sync, string section, string key, T value,
        string description, bool shared, Action sharedChanged, AcceptableValueBase? acceptable = null, object[]? tags = null)
    {
        var entry = file.Bind(section, key, value, new ConfigDescription(description, acceptable, tags ?? System.Array.Empty<object>()));
        sync.AddConfigEntry(entry).SynchronizedConfig = shared;
        if (shared) sharedEntries.Add(entry);
        if (shared) entry.SettingChanged += (_, _) => sharedChanged();
        return entry;
    }
}
