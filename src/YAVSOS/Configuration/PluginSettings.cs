using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ServerSync;
using YAVSOS.Cleanup;
using YAVSOS.Core.Messages;
using YAVSOS.Interface;

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
    private readonly List<(ConfigurationManagerAttributes Tag, Message Description)> descriptions = new List<(ConfigurationManagerAttributes, Message)>();
    public ConfigEntryBase[] SharedEntries => sharedEntries.ToArray();
    public Type? SharedType(string section, string key)
    {
        foreach (var entry in sharedEntries)
            if (entry.Definition.Section == section && entry.Definition.Key == key) return entry.SettingType;
        return null;
    }

    public PluginSettings(ConfigFile file, ConfigSync sync, Action sharedChanged)
    {
        // P10-04: each description is the message yavsos_setting_<section>_<key>, in lower case; scripts/settings_doc.py
        // derives the same key and fails when a call passes another. Values are literals or Message.Of(key).
        LockSharedPolicy = file.Bind("Server", "LockSharedPolicy", true,
            Describe(Message.Of("yavsos_setting_server_locksharedpolicy")));
        sync.AddLockingConfigEntry(LockSharedPolicy).SynchronizedConfig = true;
        sharedEntries.Add(LockSharedPolicy);
        LockSharedPolicy.SettingChanged += (_, _) => sharedChanged();
        // D042: settings arrive with their working features; unimplemented placeholders are not registered.
        ShowRuleControls = Bind(file, sync, "Controls", "ShowRuleControls", true,
            Message.Of("yavsos_setting_controls_showrulecontrols"), false, sharedChanged);
        // P6-04: the result panel adds item names and reasons to the one-line message, which is shown either way (X14: Azu's too).
        ShowResultPanel = Bind(file, sync, "Controls", "ShowResultPanel", true,
            Message.Of("yavsos_setting_controls_showresultpanel"), false, sharedChanged);
        // P9-23: the panel closes by itself; holding it while the inventory is open (always so before 1.1) is opt-in.
        ResultPanelSeconds = Bind(file, sync, "Controls", "ResultPanelSeconds", YAVSOS.Core.Interface.ResultCountdown.DefaultSeconds,
            Message.Of("yavsos_setting_controls_resultpanelseconds").With("button", Message.Of("yavsos_result_close")),
            false, sharedChanged, new AcceptableValueRange<int>(YAVSOS.Core.Interface.ResultCountdown.MinimumSeconds, YAVSOS.Core.Interface.ResultCountdown.MaximumSeconds));
        ResultPanelHoldInInventory = Bind(file, sync, "Controls", "ResultPanelHoldInInventory", false,
            Message.Of("yavsos_setting_controls_resultpanelholdininventory").With("setting", "ResultPanelSeconds"),
            false, sharedChanged);
        // P6-07 (D062 Q3): an automatic label from the chest's rule; no typed names.
        ShowChestLabels = Bind(file, sync, "Controls", "ShowChestLabels", true,
            Message.Of("yavsos_setting_controls_showchestlabels"), false, sharedChanged);
        ShowItemCategories = Bind(file, sync, "Controls", "ShowItemCategories", true,
            Message.Of("yavsos_setting_controls_showitemcategories"), false, sharedChanged);
        // P8-18 (D100): the search highlight is on while the search is; this is how long it stays once the chest closes.
        HighlightSecondsAfterClose = Bind(file, sync, "Controls", "HighlightSecondsAfterClose", YAVSOS.Core.Interface.HighlightLinger.DefaultSeconds,
            Message.Of("yavsos_setting_controls_highlightsecondsafterclose").With("setting", "ChestArrivalEffect"),
            false, sharedChanged, new AcceptableValueRange<int>(0, YAVSOS.Core.Interface.HighlightLinger.MaximumSeconds));
        // D121: YAVSOS's own moves only; AzuAutoStore's store key shows AzuAutoStore's own ping.
        ChestArrivalEffect = Bind(file, sync, "Controls", "ChestArrivalEffect", YAVSOS.Core.Interface.ChestArrivalEffect.SmallItemPuff,
            Message.Of("yavsos_setting_controls_chestarrivaleffect"),
            false, sharedChanged);
        // D122: the search highlight's two looks, each player's own; alpha is ignored.
        HoldsColor = Bind(file, sync, "Controls", "HoldsColor", new UnityEngine.Color(1f, 0.78f, 0.25f, 1f),
            Message.Of("yavsos_setting_controls_holdscolor"), false, sharedChanged);
        TakesColor = Bind(file, sync, "Controls", "TakesColor", new UnityEngine.Color(0.5f, 0.72f, 1f, 1f),
            Message.Of("yavsos_setting_controls_takescolor").With("button", Message.Of("yavsos_search_takers")), false, sharedChanged);
        // D123: cleanup marks the chests it left something in that the player can act on.
        StaysColor = Bind(file, sync, "Controls", "StaysColor", new UnityEngine.Color(1f, 0.42f, 0.2f, 1f),
            Message.Of("yavsos_setting_controls_stayscolor"), false, sharedChanged);
        SearchShowsTakers = Bind(file, sync, "Controls", "SearchShowsTakers", false,
            Message.Of("yavsos_setting_controls_searchshowstakers").With("button", Message.Of("yavsos_search_takers")), false, sharedChanged);
        // P6-03 (D062): the panel follows the chest window, so its position is an offset from there. The earlier absolute IMGUI
        // PanelX/PanelY keys are no longer read; a stale value would have pushed the panel off its window.
        RulePanelOffsetX = Bind(file, sync, "Controls", "PanelOffsetX", 0f,
            Message.Of("yavsos_setting_controls_paneloffsetx"), false, sharedChanged);
        RulePanelOffsetY = Bind(file, sync, "Controls", "PanelOffsetY", 0f,
            Message.Of("yavsos_setting_controls_paneloffsety"), false, sharedChanged);
        // X2 (D116): one fold for every chest, not per chest; the panel's arrow sets it.
        RulePanelFolded = Bind(file, sync, "Controls", "PanelFolded", false,
            Message.Of("yavsos_setting_controls_panelfolded"), false, sharedChanged);
        // D079: with AzuAutoStore installed, it does the stashing, so YAVSOS's Stash settings show read-only (values kept for
        // when it is removed). Server settings stay bound on every side for ServerSync and say what applies in each case.
        var azu = !StashRunner.Enabled;
        Message Stash(Message text) => azu ? Message.Of("yavsos_setting_stash_azuautostore").With("description", text) : text;
        // D057 O1/O9: the server decides whether cleanup runs and how far it may reach.
        AllowCleanup = Bind(file, sync, "Server", "AllowCleanup", true,
            Message.Of("yavsos_setting_server_allowcleanup"), true, sharedChanged);
        // D078 B06: one range cap for cleanup and YAVSOS's own stash, under a name that says so.
        MaximumRange = Bind(file, sync, "Server", "MaximumRange", 50f,
            Message.Of("yavsos_setting_server_maximumrange"), true, sharedChanged, new AcceptableValueRange<float>(1f, 100f));
        MigrateRenamed(file, new ConfigDefinition("Server", "CleanupMaximumRange"), MaximumRange);
        // D095: one chest limit for cleanup and YAVSOS's own stash; 256 is also the planners' bound, so it can only be lowered.
        MaximumChests = Bind(file, sync, "Server", "MaximumChests", 256,
            Message.Of("yavsos_setting_server_maximumchests"), true, sharedChanged, new AcceptableValueRange<int>(16, 256));
        CleanupRange = Bind(file, sync, "Cleanup", "Range", 10f,
            Message.Of("yavsos_setting_cleanup_range").With("setting", "MaximumRange"), false, sharedChanged,
            new AcceptableValueRange<float>(1f, 100f));
        // D126: Ctrl, as before 1.1. With Azu only the modifier counts (CleanupTriggers, CleanupKey).
        CleanupShortcut = Bind(file, sync, "Cleanup", "Shortcut", new KeyboardShortcut(UnityEngine.KeyCode.Period, UnityEngine.KeyCode.LeftControl),
            Message.Of("yavsos_setting_cleanup_shortcut").With("button", Message.Of("yavsos_cleanup_button")),
            false, sharedChanged);
        // D061: YAVSOS's own stash, used only when AzuAutoStore is not installed.
        AllowStashing = Bind(file, sync, "Server", "AllowStashing", true,
            Message.Of("yavsos_setting_server_allowstashing"), true, sharedChanged);
        StashRange = Bind(file, sync, "Stash", "Range", 10f,
            Stash(Message.Of("yavsos_setting_stash_range").With("setting", "MaximumRange")),
            false, sharedChanged, new AcceptableValueRange<float>(1f, 100f), readOnly: azu);
        StashShortcut = Bind(file, sync, "Stash", "Shortcut", new KeyboardShortcut(UnityEngine.KeyCode.Period),
            Stash(Message.Of("yavsos_setting_stash_shortcut")),
            false, sharedChanged, readOnly: azu);
        StashItemShortcut = Bind(file, sync, "Stash", "SingleItemShortcut", new KeyboardShortcut(UnityEngine.KeyCode.Mouse2),
            Stash(Message.Of("yavsos_setting_stash_singleitemshortcut")), false, sharedChanged, readOnly: azu);
        StashHotbar = Bind(file, sync, "Stash", "IncludeHotbar", false,
            Stash(Message.Of("yavsos_setting_stash_includehotbar").With("setting", "Player Ignore Hotbar")), false, sharedChanged, readOnly: azu);
        StashQuickSlots = Bind(file, sync, "Stash", "IncludeQuickSlots", false,
            Stash(Message.Of("yavsos_setting_stash_includequickslots")), false, sharedChanged, readOnly: azu);
        Texts.Changed += Translate;
    }

    /// <summary>
    /// D078 B06: reads a setting's value from its old key once, then removes the old key from the file. Only an old key
    /// still in the file is read, so a value set under the new key afterwards is never overwritten. Never synchronized.
    /// The old key is removed before any Configuration Manager lists it, so its description stays English.
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
        Message description, bool shared, Action sharedChanged, AcceptableValueBase? acceptable = null, bool readOnly = false)
    {
        var entry = file.Bind(section, key, value, Describe(description, acceptable, readOnly));
        sync.AddConfigEntry(entry).SynchronizedConfig = shared;
        if (shared) sharedEntries.Add(entry);
        if (shared) entry.SettingChanged += (_, _) => sharedChanged();
        return entry;
    }

    /// <summary>
    /// P10-04 (EXTRACTION-TOOL-CHECK §3, route A): the description in English, so the <c>.cfg</c> file stays English, with
    /// YAVSOS's own tag holding it in the player's language for the Configuration Manager. Safe in <c>Awake</c>: the lookup
    /// reads the language from <c>PlayerPrefs</c>, never <c>Localization.instance</c>.
    /// </summary>
    private ConfigDescription Describe(Message description, AcceptableValueBase? acceptable = null, bool readOnly = false)
    {
        var tag = new ConfigurationManagerAttributes { ReadOnly = readOnly ? true : null, Description = Texts.Plain(description) };
        descriptions.Add((tag, description));
        return new ConfigDescription(Texts.English(description), acceptable, tag);
    }

    /// <summary>
    /// After a language change, and after Jotunn adds the loose translation files at the main menu: every description in
    /// the player's language again. The Configuration Manager copies the tags each time its window opens, so the next
    /// opening shows the new text.
    /// </summary>
    private void Translate()
    {
        foreach (var (tag, description) in descriptions) tag.Description = Texts.Plain(description);
    }
}
