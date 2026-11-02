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
    public ConfigEntry<int> ResultPanelSeconds { get; }
    public ConfigEntry<bool> ResultPanelHoldInInventory { get; }
    public ConfigEntry<bool> ShowChestLabels { get; }
    public ConfigEntry<bool> ShowItemCategories { get; }
    public ConfigEntry<int> HighlightSecondsAfterClose { get; }
    public ConfigEntry<YAVSOS.Core.Interface.ChestArrivalEffect> ChestArrivalEffect { get; }
    public ConfigEntry<UnityEngine.Color> HoldsColor { get; }
    public ConfigEntry<UnityEngine.Color> TakesColor { get; }
    public ConfigEntry<UnityEngine.Color> StaysColor { get; }
    public ConfigEntry<bool> SearchShowsTakers { get; }
    public ConfigEntry<float> RulePanelOffsetX { get; }
    public ConfigEntry<float> RulePanelOffsetY { get; }
    public ConfigEntry<bool> RulePanelFolded { get; }
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
        // P6-04: the result panel adds item names and reasons to the one-line message, which is shown either way (X14: Azu's too).
        ShowResultPanel = Bind(file, sync, "Controls", "ShowResultPanel", true,
            "After a cleanup or a stash, show a short result panel with what moved and what stayed, with item names and reasons. With AzuAutoStore installed, it shows after AzuAutoStore's store key.", false, sharedChanged);
        // P9-23: the panel closes by itself; holding it while the inventory is open (always so before 1.1) is opt-in.
        ResultPanelSeconds = Bind(file, sync, "Controls", "ResultPanelSeconds", YAVSOS.Core.Interface.ResultCountdown.DefaultSeconds,
            "How many seconds the result panel stays up before it closes by itself. Its Close button closes it sooner.",
            false, sharedChanged, new AcceptableValueRange<int>(YAVSOS.Core.Interface.ResultCountdown.MinimumSeconds, YAVSOS.Core.Interface.ResultCountdown.MaximumSeconds));
        ResultPanelHoldInInventory = Bind(file, sync, "Controls", "ResultPanelHoldInInventory", false,
            "Keep the result panel up while your inventory is open; its time counts down only while the inventory is closed. When off, it closes after ResultPanelSeconds whether the inventory is open or not.",
            false, sharedChanged);
        // P6-07 (D062 Q3): an automatic label from the chest's rule; no typed names.
        ShowChestLabels = Bind(file, sync, "Controls", "ShowChestLabels", true,
            "Add the chest's rule to its hover text, such as \"Food › eaten by Boar · priority 2 (Urgent)\". Only chests with a rule that you can open. A chest lit by your search or cleanup says why either way.", false, sharedChanged);
        ShowItemCategories = Bind(file, sync, "Controls", "ShowItemCategories", true,
            "Add an item's categories to its tooltip, such as \"Raw food, Animal feed · eaten by Boar, Hen\".", false, sharedChanged);
        // P8-18 (D100): the search highlight is on while the search is; this is how long it stays once the chest closes.
        HighlightSecondsAfterClose = Bind(file, sync, "Controls", "HighlightSecondsAfterClose", YAVSOS.Core.Interface.HighlightLinger.DefaultSeconds,
            "How many seconds chests stay lit. After you close a chest, the chests lit by its search stay lit this long, so you can walk to them (while the search is active they stay lit). After your stash or cleanup, the chests it put items into, or left something in, light up this long. 0 turns the light off: search lights end when the chest closes, and stash and cleanup only play the ChestArrivalEffect.",
            false, sharedChanged, new AcceptableValueRange<int>(0, YAVSOS.Core.Interface.HighlightLinger.MaximumSeconds));
        // D121: YAVSOS's own moves only; AzuAutoStore's store key shows AzuAutoStore's own ping.
        ChestArrivalEffect = Bind(file, sync, "Controls", "ChestArrivalEffect", YAVSOS.Core.Interface.ChestArrivalEffect.SmallItemPuff,
            "The small effect a chest plays when your stash or cleanup puts items into it. None turns it off. With AzuAutoStore installed, its store key shows AzuAutoStore's own effect instead; cleanup still shows this one.",
            false, sharedChanged);
        // D122: the search highlight's two looks, each player's own; alpha is ignored.
        HoldsColor = Bind(file, sync, "Controls", "HoldsColor", new UnityEngine.Color(1f, 0.78f, 0.25f, 1f),
            "The colour of chests that hold what you searched for, and of chests your stash or cleanup just put items into. Alpha is ignored.", false, sharedChanged);
        TakesColor = Bind(file, sync, "Controls", "TakesColor", new UnityEngine.Color(0.5f, 0.72f, 1f, 1f),
            "The colour of chests whose rule would take what you searched for, shown while the rule-tag button beside the search box is on. Alpha is ignored.", false, sharedChanged);
        // D123: cleanup marks the chests it left something in that the player can act on.
        StaysColor = Bind(file, sync, "Controls", "StaysColor", new UnityEngine.Color(1f, 0.42f, 0.2f, 1f),
            "The colour of chests your cleanup left something in because no chest takes it or the chests that take it are full. Alpha is ignored.", false, sharedChanged);
        SearchShowsTakers = Bind(file, sync, "Controls", "SearchShowsTakers", false,
            "Also light the chests whose rule would take what you search for, not only the chests that hold it. The rule-tag button beside the search box turns this on and off.", false, sharedChanged);
        // P6-03 (D062): the panel follows the chest window, so its position is an offset from there. The earlier absolute IMGUI
        // PanelX/PanelY keys are no longer read; a stale value would have pushed the panel off its window.
        RulePanelOffsetX = Bind(file, sync, "Controls", "PanelOffsetX", 0f,
            "Rule panel offset to the right of its place beside the chest window, in the game's UI units (1080p-scaled); negative moves it left. Drag the panel's title to change it, for example if it overlaps another mod's window.", false, sharedChanged);
        RulePanelOffsetY = Bind(file, sync, "Controls", "PanelOffsetY", 0f,
            "Rule panel offset downwards from the top of the chest window, in the game's UI units (1080p-scaled); negative moves it up.", false, sharedChanged);
        // X2 (D116): one fold for every chest, not per chest; the panel's arrow sets it.
        RulePanelFolded = Bind(file, sync, "Controls", "PanelFolded", false,
            "Show the rule panel folded: only its title, status marks and the chest's label. The arrow left of the panel's title folds and unfolds it.", false, sharedChanged);
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
        // D126: Ctrl, as before 1.1. With Azu only the modifier counts (CleanupTriggers, CleanupKey).
        CleanupShortcut = Bind(file, sync, "Cleanup", "Shortcut", new KeyboardShortcut(UnityEngine.KeyCode.Period, UnityEngine.KeyCode.LeftControl),
            "Runs cleanup of nearby chests. With AzuAutoStore installed, only this shortcut's modifier counts: cleanup is that modifier (Ctrl if there is none) plus AzuAutoStore's own store key. Set it to None for no cleanup key; the Clean up nearby button still works.",
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
