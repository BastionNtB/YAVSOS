using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using YAVSOS.Classification;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;
using YAVSOS.Transfers;
using YAVSOS.Cleanup;
using YAVSOS.Core.Messages;

namespace YAVSOS.Interface;

internal enum RulePanelMode { Ineligible, Unreadable, ReadOnly, Edit }

/// <summary>
/// P3-05 controls (D048), styled in P6-03 (D062): on an open chest, view, set and clear its rule. Categories and exact
/// items only; other stored fields are preserved by the envelope. Items are picked from lists of what the player and
/// the chest hold, so no drag (which can drop the item) or hotkey (which can collide with typing) is involved.
/// This class holds the rule being edited and applies changes; <see cref="RulePanel"/> draws it with Jotunn widgets.
/// Unity game thread only.
/// </summary>
internal sealed class RuleControls
{
    private static readonly AccessTools.FieldRef<InventoryGui, Container>? CurrentContainer = Field();
    private readonly ConfigEntry<bool> enabled;
    internal ConfigEntry<float> OffsetX { get; }
    internal ConfigEntry<float> OffsetY { get; }
    /// <summary>X2 (D116): the panel is folded, for every chest and across sessions.</summary>
    internal ConfigEntry<bool> Folded { get; }
    private readonly ManualLogSource log;
    internal Container? Chest { get; private set; }
    internal string Observed { get; private set; } = "";
    /// <summary>S1 (audit D01): the rule as edited; the members below show it to the panel's sections.</summary>
    private readonly RuleEditState edit = new RuleEditState();
    internal SortedSet<string> Categories => edit.Categories;
    internal SortedSet<string> Includes => edit.Includes;
    internal SortedSet<string> Excludes => edit.Excludes;
    internal SortedSet<string> Unknown => edit.Unknown;
    private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal), collapsed = new HashSet<string>(StringComparer.Ordinal);
    internal bool Unreadable { get; private set; }
    internal bool Stored => edit.Stored;
    internal bool PickingCategories { get; private set; }
    internal bool PickingItems { get; private set; }
    internal bool PickingFilters { get; private set; }
    /// <summary>Filters of the edited alternative (P6-07b): values within a dimension are OR, dimensions AND.</summary>
    internal SortedDictionary<string, SortedSet<string>> Filters => edit.Filters;
    /// <summary>D101, D132: a refusal or summary in the strip under the header, the control it came from outlined; successes say nothing.</summary>
    internal PanelMessages Messages { get; } = new PanelMessages();
    internal int Priority => edit.Priority;
    /// <summary>Bumped on every change to what the panel shows, so sections rebuild their rows only then.</summary>
    internal int Revision { get; private set; }
    private Action? pending; // Clicks queue here and run in LateUpdate, outside the UI's event dispatch.
    private ClassificationTaxonomy? loadedTaxonomy;
    private RulePanel? panel;
    private ResultPanel? resultPanel;
    // D058, per feature: the capability probe turns off both panels; a runtime failure turns off only the panel, the
    // result view, the search or the explanations that failed, so one broken view never removes the rule editor.
    private bool styled = true, resultStyled = true, capable = true, probed, searchOff, explainOff, eligible;
    private readonly ConfigEntry<bool> showResults, holdResults;
    private readonly ConfigEntry<int> resultSeconds;
    private readonly RunResults results;
    private readonly StashRunner stash;
    internal ChestHighlight Highlight { get; }

    internal CleanupRunner Cleanup { get; }
    internal RuleControls(ConfigEntry<bool> enabled, ConfigEntry<bool> showResults, ConfigEntry<int> resultSeconds, ConfigEntry<bool> holdResults,
        ConfigEntry<float> offsetX, ConfigEntry<float> offsetY, ConfigEntry<bool> folded, CleanupRunner cleanup, StashRunner stash, RunResults results, ChestHighlight highlight, ManualLogSource log)
    {
        this.enabled = enabled; this.showResults = showResults; this.resultSeconds = resultSeconds; this.holdResults = holdResults; OffsetX = offsetX; OffsetY = offsetY; Folded = folded; Cleanup = cleanup; this.stash = stash;
        this.results = results; Highlight = highlight; this.log = log;
        Texts.Changed += LanguageChanged;
        // B1.17: without the game's open-chest field the panel can never open; say so once instead of staying silent.
        if (CurrentContainer == null)
            log.LogWarning("Chest rule panel turned off: the game's open-chest field (InventoryGui.m_currentContainer) could not be read. Chests open as usual without YAVSOS's panel.");
    }

    /// <summary>
    /// P10-04: the game's language changed. Both panels are rebuilt on their next frame, so every label, button and
    /// tooltip set when they were built is set again in the new language; the message spot formats its message when it
    /// draws. Cached text (the label preview, the where-line) is built again.
    /// </summary>
    private void LanguageChanged()
    {
        try { panel?.Destroy(); } catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        try { resultPanel?.Destroy(); } catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        panel = null; resultPanel = null; labelPayload = null;
        Revision++; ruleRevision++;
    }

    private static AccessTools.FieldRef<InventoryGui, Container>? Field()
    {
        try { return AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer"); }
        catch { return null; }
    }

    private void SearchFailed(Exception error)
    {
        searchOff = true; DisplayFaults.Add(DisplayFaults.Search); Search = CategorySearchResult.None; SearchRevision++; rowHover = rowLit = "";
        try { Highlight.Clear(); } catch (Exception again) when (OptionalFailure.Recoverable(again)) { }
        log.LogWarning($"Category search turned off after {error.GetType().Name}: {error.Message}; the rule panel keeps working.");
    }

    internal void LateUpdate()
    {
        var plugin = Plugin.Instance;
        var gui = InventoryGui.instance;
        if (!ZNet.instance) LeftWorld(); // X2: a copied rule lasts until leaving the world; X26, X22: so do undo records and dismissals.
        Results();
        if (!searchOff)
            try
            {
#if YAVSOS_LAB
                FaultLab.Raise("highlight"); // D088: lab-only display fault (X51 B2-01).
#endif
                var lit = UiCost.Start();
                Highlight.Update(); // Runs on with the inventory closed, so the lit chests can be seen.
                UiCost.Stop("highlight frame", lit);
            }
            // X51 B2-01: the highlight reads chests and rules (D122); a fault turns off the search, never the panel (D058).
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { SearchFailed(error); }
        var shown = plugin != null && CurrentContainer != null && gui && InventoryGui.IsVisible() ? CurrentContainer(gui) : null;
        if (shown) Notice();
        var open = enabled.Value && styled ? shown : null;
        if (!open)
        {
            var closed = Chest != null; Chest = null; pending = null; panel?.Hide();
            // P8-18 (D100): what the chest's search lit stays lit for the player's setting, then goes.
            if (closed) Highlight.Release(plugin != null ? plugin.Settings.HighlightSecondsAfterClose.Value : 0);
            // D086 D12 (M23): with only the inventory open, a stack's tooltip still says where the stash key would send it.
            if (closed || explainOff || !enabled.Value || !styled || plugin == null || !gui || !InventoryGui.IsVisible()) Forget();
            else
                try
                {
#if YAVSOS_LAB
                    FaultLab.Raise("explanations"); // D088: lab-only display fault (SF4).
#endif
                    Hover(gui!, inventoryOnly: true); Step();
                }
                catch (Exception error) when (OptionalFailure.Recoverable(error))
                {
                    explainOff = true; Forget(); DisplayFaults.Add(DisplayFaults.Where);
                    log.LogWarning($"Item explanations turned off after {error.GetType().Name}: {error.Message}; the rule panel keeps working.");
                }
            return;
        }
        var action = pending; pending = null; action?.Invoke();
        if (!ReferenceEquals(open, Chest)) Load(open!);
        else if (!StringComparer.Ordinal.Equals(ChestRuleStore.Payload(Chest!), Observed))
            Load(Chest!); // Someone else's change, or ours confirmed: show the stored rule.
        else if (!ReferenceEquals(loadedTaxonomy, plugin!.RuleTaxonomy)) Load(Chest!);
        // Tombstones, drawers, creature packs and the like take no rule: no panel at all (runtime 2026-10-21).
        if (!eligible) { panel?.Hide(); return; }
        if (!Styled(gui!)) return;
        if (UndoKey()) Undo(); // X26 (D130): queued like a click, so it runs at the next frame's start.
        if (!explainOff)
            try
            {
#if YAVSOS_LAB
                FaultLab.Raise("explanations"); // D088: lab-only display fault (SF4).
#endif
                Hover(gui!); Step();
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                explainOff = true; Forget(); DisplayFaults.Add(DisplayFaults.Where);
                log.LogWarning($"Item explanations turned off after {error.GetType().Name}: {error.Message}; the rule panel keeps working.");
            }
        if (!searchOff)
            try
            {
#if YAVSOS_LAB
                FaultLab.Raise("search"); // D088: lab-only display fault (SF4).
#endif
                Searching(); Lighting();
            }
            catch (Exception error) when (OptionalFailure.Recoverable(error)) { SearchFailed(error); }
        try
        {
#if YAVSOS_LAB
            FaultLab.Raise("panel"); // D088: lab-only drawing fault (SF1).
#endif
            panel!.Show();
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { TurnOff("drawing failed: " + error.GetType().Name + ": " + error.Message + Where(error)); }
    }

    // SF1, SF2 (D087): with the panel off, or no chest readable, the status marks can't show either, so the first chest
    // opened says so once per session; the log has the cause.
    private bool noticedChests, noticedPanel;
    private void Notice()
    {
        if (!ChestRuleStore.Bound)
        {
            if (noticedChests) return;
            noticedChests = true;
            GameText.Say(Message.Of(StashRunner.Enabled ? "yavsos_notice_chests_unreadable" : "yavsos_notice_chests_unreadable_azu"), overInventory: true);
        }
        else if (!styled && enabled.Value && !noticedPanel)
        {
            noticedPanel = true;
            GameText.Say(Message.Of("yavsos_notice_panel_off"), overInventory: true);
        }
    }

    // P6-04: the result panel, on Jotunn's overlay canvas, so it shows with or without the inventory screen. P9-23: it
    // closes after the player's seconds, also with the inventory open unless they hold it there.
    private void Results()
    {
        var visible = results.Visible(Time.unscaledDeltaTime, resultSeconds.Value, InventoryGui.IsVisible(), holdResults.Value);
        if (!visible || !showResults.Value || !resultStyled || !Probe()) { resultPanel?.Hide(); return; }
        try
        {
            if (resultPanel == null || !resultPanel.Alive)
            {
                resultPanel = null;
                var widgets = new JotunnWidgets(); var front = JotunnWidgets.Front;
                if (!widgets.Ready || !front) return; // Jotunn has not made its sprites or canvas yet; try next frame.
                resultPanel = new ResultPanel(widgets, front!, results);
            }
            resultPanel.Show();
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { TurnOffResults(error.GetType().Name + ": " + error.Message + Where(error)); }
    }

    // D058: the styled panel needs the GUIManager members it calls. A failed check or build turns off the panel only.
    private bool Styled(InventoryGui gui)
    {
        if (!styled || !Probe()) return false;
        if (panel != null && panel.Alive) return true;
        panel = null;
        try
        {
            var widgets = new JotunnWidgets();
            if (!widgets.Ready) return false; // Jotunn has not loaded the game's sprites yet; try next frame.
            var root = gui.m_inventoryRoot as RectTransform ?? (gui.m_container ? gui.m_container.parent as RectTransform : null);
            if (!root) { TurnOff("inventory screen has no UI rectangle to attach to"); return false; }
            panel = new RulePanel(widgets, root!, this);
            return true;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            TurnOff("building failed: " + error.GetType().Name + ": " + error.Message + Where(error));
            return false;
        }
    }

    /// <summary>Run once at startup, after every plugin has loaded (Plugin.Start); later calls return the result.</summary>
    internal bool Probe()
    {
        if (probed) return capable;
        probed = true;
        var capability = JotunnGuiCapability.Probe(JotunnVersion(out var resolve), resolve);
        if (capability.Supported) return true;
        capable = styled = resultStyled = false;
        log.LogWarning("Chest rule and result panels turned off; rules, stashing and cleanup keep working, results still show as one line. " + capability.Witness);
        return false;
    }

    // The rule panel only; the result panel keeps working.
    // The first frames of the trace, so a one-time warning from the field names its cause.
    internal static string Where(Exception error) =>
        " at " + string.Join(" < ", (error.StackTrace ?? "").Split('\n').Take(3).Select(x => x.Trim()));

    private void TurnOff(string reason)
    {
        if (!styled) return;
        styled = false;
        try { panel?.Destroy(); } catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        panel = null; Chest = null; pending = null; Forget();
        try { Highlight.Clear(); } catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        log.LogWarning("Chest rule panel turned off; rules, stashing and cleanup keep working. " + reason);
    }

    // The result panel only; results still show as one line and the rule panel keeps working.
    private void TurnOffResults(string reason)
    {
        if (!resultStyled) return;
        resultStyled = false;
        try { resultPanel?.Destroy(); } catch (Exception error) when (OptionalFailure.Recoverable(error)) { }
        resultPanel = null; DisplayFaults.Add(DisplayFaults.Results);
        log.LogWarning("Result panel turned off; results still show as one line. " + reason);
    }

    private static string? JotunnVersion(out OptionalTypeResolver? resolve)
    {
        resolve = null;
        if (!Chainloader.PluginInfos.TryGetValue(Jotunn.Main.ModGuid, out var info) || info?.Instance == null) return null;
        var assembly = info.Instance.GetType().Assembly;
        resolve = name => name switch
        {
            "UnityEngine.Transform" => typeof(Transform), "UnityEngine.Vector2" => typeof(Vector2), "UnityEngine.GameObject" => typeof(GameObject),
            "UnityEngine.Color" => typeof(Color), "UnityEngine.UI.Text" => typeof(Text), "UnityEngine.UI.ScrollRect" => typeof(ScrollRect),
            "UnityEngine.UI.InputField+ContentType" => typeof(InputField.ContentType), "UnityEngine.UI.DefaultControls+Resources" => typeof(DefaultControls.Resources),
            _ => assembly.GetType(name, false)
        };
        return info.Metadata.Version?.ToString() ?? "";
    }

    /// <summary>A click: it runs on the next LateUpdate, and what the panel said before it goes (D101).</summary>
    internal void Queue(Action action) => pending = () => { Messages.Action(); action(); };

    private void Load(Container container)
    {
        var fresh = !ReferenceEquals(container, Chest);
        if (fresh)
        {
            Forget(); Messages.Action();
            // A search narrows the tree to its hits; carried over to another chest it hid most categories (runtime 2026-10-21).
            // The Items search too (maintainer, 2026-10-28): carried over, it narrowed the next chest's grid and lit its chests again.
            typed = ""; typedAt = float.NegativeInfinity; gridQuery = ""; gridLit = ""; rowHover = rowLit = "";
            opened++; // P8-18: a newly opened chest decides the highlight afresh.
        }
        Chest = container; Observed = ChestRuleStore.Payload(container); Unreadable = false;
        eligible = ChestRuleStore.Eligibility(container) == RuleEligibility.Eligible;
        loadedTaxonomy = Plugin.Instance!.RuleTaxonomy; Revision++; ruleRevision++;
        // D071: one tab is always open; a chest with a rule opens on its items, one without on the categories to pick.
        if (fresh) { PickingItems = Readable(container); PickingCategories = !PickingItems; PickingFilters = false; }
        edit.Clear();
        var read = Plugin.Instance!.ChestRules.Read(container).Policy;
        if (read == null) return;
        if (read.State == ContainerPolicyReadState.Unsupported) { Unreadable = true; return; }
        if (!read.HasRule) return;
        // P6-07b: the editor shows one alternative with its filters and saves the filter set it replaces.
        edit.Load(read.Rules!);
    }

    private static bool Readable(Container container) => Plugin.Instance!.ChestRules.Read(container).Policy is { HasRule: true };

    internal RulePanelMode Mode
    {
        get
        {
            if (ChestRuleStore.Eligibility(Chest) != RuleEligibility.Eligible) return RulePanelMode.Ineligible;
            if (Unreadable) return RulePanelMode.Unreadable;
            return Owner() ? RulePanelMode.Edit : RulePanelMode.ReadOnly;
        }
    }

    internal bool Owner() => Chest != null && ChestRuleStore.View(Chest)?.IsOwner() == true;

    // ---- Actions; each runs from the queue on the next LateUpdate.
    // D071: the tabs choose which one is open; clicking the open tab keeps it open.
    internal void ShowCategories() => Queue(() => { PickingCategories = true; PickingItems = PickingFilters = false; Revision++; });
    internal void ShowItems() => Queue(() => { PickingItems = true; PickingCategories = PickingFilters = false; Revision++; });
    internal void ShowFilters() => Queue(() => { PickingFilters = true; PickingCategories = PickingItems = false; Revision++; });
    internal void ToggleFilter(string dimension, string value) => Apply(() => edit.ToggleFilter(dimension, value));
    internal void ClearRule() => Queue(() => Save(null, PanelControl.Clear));

    // ---- X2 (D116): copy and paste a rule, the chest's label, folding the panel.
    /// <summary>The copied rule, in this game only; nothing is sent until it is pasted.</summary>
    internal RuleCopy? Copied { get; private set; }
    internal bool CanCopy => RuleCopy.CanCopy(edit);
    internal void CopyRule() => Queue(() =>
    {
        // The label goes into Paste's tooltip, which is rich text (P10-04: names escaped).
        if (RuleCopy.Of(edit, Label() is { } shown ? Texts.Escape(shown) : null) is not { } copy) return;
        Copied = copy; Revision++;
    });
    /// <summary>Replaces the open chest's rule at once, saved like any edit: compare-and-set, refused while another player uses the chest.</summary>
    internal void PasteRule() => Queue(() =>
    {
        if (Copied == null || Mode != RulePanelMode.Edit) return;
        edit.Paste(Copied, Plugin.Instance!.RuleTaxonomy);
        Save(edit.Edit(), PanelControl.Paste);
    });
    internal void ToggleFold() => Queue(() => { Folded.Value = !Folded.Value; Revision++; });

    private string? labelPayload, label;
    private object? labelTaxonomy;
    /// <summary>The chest's hover label (S3), plain text, as other players see it; null without a rule. Built once per rule.</summary>
    internal string? Label()
    {
        var plugin = Plugin.Instance!; var taxonomy = plugin.RuleTaxonomy;
        if (labelPayload != null && ReferenceEquals(labelTaxonomy, taxonomy) && StringComparer.Ordinal.Equals(labelPayload, Observed)) return label;
        labelPayload = Observed; labelTaxonomy = taxonomy;
        return label = Observed.Length == 0 ? null : ChestLabels.Label(plugin, Observed, taxonomy);
    }
    /// <summary>The Rule heading's and chips' tooltip: the label (variant C).</summary>
    internal Tooltip? LabelTooltip() =>
        ChestLabelPreview.Of(Label() is { } shown ? Texts.Escape(shown) : null, Plugin.Instance?.Settings.ShowChestLabels.Value != false);
    internal void RemoveUnknown(string id) => Apply(() => edit.RemoveUnknown(id));
    /// <summary>Filters of the edited alternative this version doesn't know (D077 B01); kept on save unless removed.</summary>
    internal IReadOnlyList<string> UnknownFilters => edit.UnknownFilters;
    internal void RemoveFilter(string dimension) => Apply(() => edit.RemoveFilter(dimension));
    /// <summary>Values of known filters this version doesn't know (D086 D6): kept on save, match nothing, until removed.</summary>
    internal IReadOnlyList<(string Dimension, string Value)> UnknownFilterValues => edit.UnknownFilterValues;
    internal void RemoveFilterValue(string dimension, string value) => Apply(() => edit.RemoveFilterValue(dimension, value));
    /// <summary>Stored parts of the rule the panel can't show or edit (D086 D5); kept on save, removed by Clear rule.</summary>
    internal int HiddenParts => edit.HiddenParts;
    internal void ToggleCategory(ClassificationTaxonomy taxonomy, string id) => Apply(() => edit.ToggleCategory(taxonomy, id));
    internal void Fold(string id, bool open) => Queue(() =>
    {
        if (open) { collapsed.Add(id); expanded.Remove(id); }
        else { expanded.Add(id); collapsed.Remove(id); }
        Revision++;
    });
    internal void Choose(string prefab, bool always) => Apply(() => edit.Choose(prefab, always));
    // D057, D061: priority orders chests (specificity breaks ties), shown as levels 1-10 (D069); the opt-out keeps cleanup from taking items out.
    internal void ChangeLevel(int step)
    {
        if (ChestPriority.Step(Priority, step) is not int next) return;
        Apply(() => edit.Priority = next, PanelControl.Priority);
    }
    /// <summary>A click on the 1–10 bar (D071).</summary>
    internal void SetLevel(int level)
    {
        var rank = ChestPriority.Rank(level);
        // D101: the bar is shown without a rule too, faint; priority belongs to the rule, so there it does nothing.
        if (!Stored || ChestPriority.Level(Priority) == ChestPriority.Level(rank)) return;
        Apply(() => edit.Priority = rank, PanelControl.Priority);
    }
    internal void ToggleCleanupOptOut()
    {
        var target = Chest!; var value = !ChestRuleStore.CleanupOptOut(target);
        Queue(() =>
        {
            var result = Plugin.Instance!.ChestRules.TrySetCleanupOptOut(target, value);
            if (!result.Succeeded) Messages.Say(PanelControl.OptOut, result.Message);
            Revision++; ruleRevision++;
        });
    }
    // D101: "Cleanup started." goes (the button turns to "Cleaning up…" and the result panel follows); a refusal, or the
    // summary of a run with nothing to move (no result panel follows it), shows in the strip (D132).
    internal void CleanUpNearby() => Queue(() =>
    {
        var said = Cleanup.Start();
        if (said.Outcome != RunOutcome.Started)
            Messages.Say(PanelControl.Cleanup, said.Message, said.Outcome == RunOutcome.Summary ? MessageKind.Summary : MessageKind.Refusal);
        Revision++;
    });

    // D051: each change is written at once (RuleEditState.Edit says what an emptied rule becomes).
    private void Apply(Action change, PanelControl control = PanelControl.Edit) => Queue(() =>
    {
        change();
        Save(edit.Edit(), control);
    });

    // D101: a saved edit says nothing (the chips and ticks change at the click); a refusal stays in the strip until the
    // next action, its control outlined (D132). X26: every saved change becomes this chest's undo record.
    private void Save(ChestRuleEdit? edit, PanelControl control)
    {
        var before = Observed;
        var result = Plugin.Instance!.ChestRules.TryEdit(Chest!, before, edit);
        Load(Chest!);
        if (result.Succeeded && ChestKey() is { } key) history.Changed(key, before, Observed);
        if (!result.Succeeded) Messages.Say(control, result.Message);
    }

    // ---- X26 (P10-07, D130): undo the last rule change on this chest, then redo it. Local to this game, per chest, until
    // the world ends. The write is the owner's compare-and-set: refused when the rule changed since (A2-09: the stored
    // payload is put back whole, so Clear rule can always be undone, hidden parts included).
    private readonly RuleHistory<ZDOID> history = new RuleHistory<ZDOID>();
    private static readonly MessageList? UndoKeyName = KeyNames.Format(nameof(KeyCode.Z), new[] { nameof(KeyCode.LeftControl) });

    /// <summary>The open chest's undo record, or null.</summary>
    internal RuleUndo? UndoRecord => ChestKey() is { } key ? history.For(key) : null;

    /// <summary>The ↶ button's tooltip: what a click puts back, by its chest label.</summary>
    internal Tooltip UndoTooltip()
    {
        var record = UndoRecord;
        var label = record == null || record.Before.Length == 0 ? null : ChestLabels.Label(Plugin.Instance!, record.Before, Plugin.Instance!.RuleTaxonomy);
        return UndoButton.Tip(record, Mode == RulePanelMode.Edit || Mode == RulePanelMode.Unreadable && Owner(), label == null ? null : Texts.Escape(label), UndoKeyName);
    }

    internal void Undo() => Queue(() =>
    {
        if (Chest == null || ChestKey() is not { } key || history.For(key) is not { } record) return;
        var result = Plugin.Instance!.ChestRules.TryRestore(Chest, record.After, record.Before, record.Redo);
        Load(Chest);
        if (result.Succeeded) { history.Restored(key, Observed); Revision++; return; }
        // The rule changed since: this record can't be undone any more (the wireframe drops it).
        if (result.Reason == TransferRefusal.PolicyChanged) history.Drop(key);
        Messages.Say(PanelControl.Undo, result.Message);
    });

    // Ctrl + Z, unless the player is typing: in a YAVSOS search box, any other focused text field, the chat or the console.
    private static bool UndoKey()
    {
        var input = BepInEx.UnityInput.Current;
        if (!input.GetKeyDown(KeyCode.Z) || !input.GetKey(KeyCode.LeftControl) && !input.GetKey(KeyCode.RightControl)) return false;
        if (TypingBlock.Active || global::Console.IsVisible() || TextInput.IsVisible() || Chat.instance && Chat.instance.HasFocus()) return false;
        var events = UnityEngine.EventSystems.EventSystem.current;
        var selected = events ? events!.currentSelectedGameObject : null;
        return !(selected && selected!.GetComponent<InputField>() is { isFocused: true });
    }

    /// <summary>The open chest's identity in this world (its object ID), for its undo record and dismissed suggestions.</summary>
    private ZDOID? ChestKey() => Chest != null && ChestRuleStore.View(Chest)?.GetZDO() is { } zdo ? zdo.m_uid : null;

    private void LeftWorld()
    {
        Copied = null;
        if (history.Count != 0) history.Clear();
        if (dismissed.Count != 0) dismissed.Clear();
    }

    // ---- P10-25 (D132, variant B): the strip under the header.
    /// <summary>What the strip shows now: the message, a banner, or the chest label.</summary>
    internal StripLine Strip(RulePanelMode mode)
    {
        var label = Label();
        return PanelStrip.Of(Messages, mode switch { RulePanelMode.ReadOnly => StripBanner.InUse, RulePanelMode.Unreadable => StripBanner.Unreadable, _ => StripBanner.None },
            Stasher, label == null ? null : Texts.Escape(label), Folded.Value);
    }

    // ---- X22 (P10-08, D130 variant A, D132): suggestions from what the open chest holds (B2-11: the chest only), as chips
    // under the rule's chips. Each saves through the normal rule write, so it is undoable and refused like any edit.
    private readonly Dictionary<ZDOID, string> dismissed = new();
    private string suggestedKey = "\0";
    private RuleSuggestions suggested = RuleSuggestions.None;

    /// <summary>What to suggest for the open chest; none while categories load, in a read-only panel, or once dismissed.</summary>
    internal RuleSuggestions Suggestions()
    {
        var plugin = Plugin.Instance!; var answers = plugin.CategoryAnswers; var taxonomy = plugin.RuleTaxonomy;
        if (Chest == null || Mode != RulePanelMode.Edit || answers == null || !answers.CategoriesReady) return RuleSuggestions.None;
        var inventory = Chest.GetInventory();
        var key = Observed + "|" + TooltipContext.Contents(inventory) + "|" + answers.Generation + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(taxonomy);
        if (key != suggestedKey)
        {
            suggestedKey = key;
            var timed = UiCost.Start();
            suggested = RuleSuggestion.Find(taxonomy, ChestStacks(plugin, answers, taxonomy, inventory), Stored, plugin.CategoryMemberCount);
            UiCost.Stop("suggestions", timed);
        }
        return ChestKey() is { } chest && dismissed.TryGetValue(chest, out var hidden) && hidden == suggested.Signature ? RuleSuggestions.None : suggested;
    }

    // B2-11: the open chest's own stacks (not the player's), each with its item's categories and whether the stored rule
    // accepts it (the evaluator, as the kept-out line asks it) or marks it Never.
    private List<SuggestionStack> ChestStacks(Plugin plugin, ICategoryAnswers answers, ClassificationTaxonomy taxonomy, Inventory? inventory)
    {
        var stacks = new List<SuggestionStack>();
        if (inventory == null) return stacks;
        var accepts = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in inventory.GetAllItems())
        {
            var prefab = item?.m_dropPrefab ? item!.m_dropPrefab.name : null;
            if (prefab == null) continue;
            if (!accepts.TryGetValue(prefab, out var accepted))
                accepts[prefab] = accepted = Stored && plugin.RuleEvaluator.Evaluate(Observed, prefab, taxonomy, answers) == ChestRuleDecision.Accept;
            stacks.Add(new SuggestionStack(prefab, answers.Resolve(prefab)?.Memberships.Select(x => x.Category), accepted, Excludes.Contains(prefab)));
        }
        return stacks;
    }

    /// <summary>"✦ Set rule: {category}": ticks the category on a chest without a rule.</summary>
    internal void SuggestCategory(string category) => Apply(() =>
    {
        if (!Stored && Plugin.Instance!.RuleTaxonomy.Categories.ContainsKey(category)) edit.ToggleCategory(Plugin.Instance!.RuleTaxonomy, category);
    }, PanelControl.Suggestion);

    /// <summary>"✓ Always: these items" (D139): marks each item the rule misses Always (D132).</summary>
    internal void AcceptContents(IReadOnlyList<string> items) => Apply(() => edit.AcceptAll(items), PanelControl.Suggestion);

    /// <summary>✕: hides these suggestions on this chest until they change or the world ends.</summary>
    internal void DismissSuggestions(string signature) => Queue(() =>
    {
        if (ChestKey() is { } key) dismissed[key] = signature;
        Revision++;
    });

    // ---- P6-05: where a stack would go and why, for the stack under the mouse or one picked in the item list. Read only:
    // the runners gather the same live inputs a run would and hand them to the Core explainer; nothing is planned to act on.
    // X16 (P9-13): the inputs are gathered once per open inventory (TooltipContext), spread over frames, and each
    // stack pointed at is answered against them at once. Until the first context is ready, a stack is answered only once
    // pointed at for HoverDelay, as before X16, so sweeping the mouse asks nothing.
    private const float HoverDelay = 0.5f, HoverPoll = 0.1f;
    /// <summary>X16: the line follows the player once they have moved this far since it was built, at most every <see cref="FollowEvery"/>.</summary>
    private const float FollowStep = 1f, FollowEvery = 1f;
    private ItemDrop.ItemData? hovered, subject;
    private float hoveredSince, nextHover;
    // ruleRevision changes only with what an answer depends on (a loaded or saved rule, the opt-out), never with
    // folding or tabs, since each context captures every chest in range.
    private int ruleRevision;
    /// <summary>X16: the context answers are read from, the one being gathered (it replaces the first when ready), and its gather.</summary>
    private TooltipContext? context, building;
    private IEnumerator<bool>? gather;
    private double gatherWork;
    private long gatherStarted;
    private float lineAt;
    private Vector3 lineFrom;
    /// <summary>S2: the typed answer the line was built from, with its chosen chest; null before the first and after forgetting.</summary>
    internal ExplanationAnswer? Answer { get; private set; }
    /// <summary>One short line (P6-23): what the stash key or "Clean up nearby" would do with the stack, "Stash stack [Middle mouse]: …".</summary>
    internal WhereLine? Explanation { get; private set; }
    /// <summary>Why there is no answer, or a qualifier ("This server has turned off stashing"); null for none.</summary>
    internal Message? ExplanationContext { get; private set; }
    /// <summary>Bumped when the explanation changes, so its section rebuilds its lines only then.</summary>
    internal int ExplanationRevision { get; private set; }

    // Polled every HoverPoll: rebuilds the context when it is stale, answers the stack pointed at, and lets the line follow the player.
    private void Hover(InventoryGui gui, bool inventoryOnly = false)
    {
        var now = Time.unscaledTime;
        if (now < nextHover) return;
        nextHover = now + HoverPoll;
        if (!inventoryOnly && Mode is not (RulePanelMode.Edit or RulePanelMode.ReadOnly)) return;
        Freshen(now);
        var item = inventoryOnly ? GridHover.Item(gui.m_playerGrid) : GridHover.Item(gui.m_playerGrid) ?? GridHover.Item(gui.ContainerGrid);
        if (!ReferenceEquals(item, hovered)) { hovered = item; hoveredSince = now; }
        if (item != null && !ReferenceEquals(item, subject) && (context != null || now - hoveredSince >= HoverDelay)) Explain(item);
        Follow(now);
    }

    internal void ExplainItem(string prefab) => Queue(() =>
    {
        if (explainOff) return;
        var player = Player.m_localPlayer;
        bool Is(ItemDrop.ItemData? x) => x?.m_dropPrefab && x!.m_dropPrefab.name == prefab;
        var item = (player ? player.GetInventory().GetAllItems().FirstOrDefault(Is) : null) ?? Chest?.GetInventory()?.GetAllItems().FirstOrDefault(Is);
        if (item != null) Explain(item);
    });

    // A new subject: answered at once against a current context, otherwise when the context being gathered is ready.
    private void Explain(ItemDrop.ItemData item)
    {
        subject = item; ExplanationRevision++;
        Answer = null; Explanation = null; ExplanationContext = null;
        if (gather == null && context != null) Answered(context);
    }

    // The subject's answer from ctx. The shown line is replaced only once the new one is there, so it never blinks out.
    private void Answered(TooltipContext ctx)
    {
        var item = subject!; ExplanationAnswer answer;
        var player = Player.m_localPlayer;
        try
        {
            if (player && player.GetInventory().ContainsItem(item)) answer = ctx.Stash(stash, item);
            else if (Chest && ReferenceEquals(ctx.Open, Chest) && Chest!.GetInventory()?.ContainsItem(item) == true) answer = ctx.Cleanup(Cleanup, item);
            else { subject = null; Answer = null; Explanation = null; ExplanationContext = null; ExplanationRevision++; return; } // Moved or used up since.
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            answer = ExplanationAnswer.Without(TooltipContext.Failed(item));
            log.LogWarning($"Explanation failed for {item.m_dropPrefab?.name}: {error.GetType().Name}: {error.Message}");
        }
        var line = answer.Line;
        lineAt = Time.unscaledTime; lineFrom = player ? player.transform.position : lineFrom;
        if (Answer != null && Equals(line, Explanation) && Equals(answer.Note, ExplanationContext)) { Answer = answer; return; } // Same text: no redraw.
        Answer = answer; Explanation = line; ExplanationContext = answer.Note; ExplanationRevision++;
    }

    // X16: text only, no re-plan; the answer's chest names read the player's position as the line is built.
    private void Follow(float now)
    {
        var player = Player.m_localPlayer;
        if (Answer?.Explanation == null || !player || now - lineAt < FollowEvery) return;
        var at = player.transform.position;
        if ((at - lineFrom).sqrMagnitude < FollowStep * FollowStep) return;
        lineAt = now; lineFrom = at;
        var line = Answer.Line;
        if (Equals(line, Explanation)) return;
        Explanation = line; ExplanationRevision++;
    }

    // X16: starts gathering a new context when there is none or the current one is stale (TooltipContext.Stale).
    private void Freshen(float now)
    {
        if (gather != null) return;
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        if (plugin == null || !player) return;
        var stamp = TooltipContext.Stamp(player!, Chest, results.Revision, ruleRevision, plugin, now);
        if (context != null && !context.Stale(stamp)) return;
        building = new TooltipContext(stamp);
        gather = building.Gather(stash, Cleanup); gatherWork = 0; gatherStarted = Stopwatch.GetTimestamp();
    }

    // P6-11 cost check: a context reads every chest in range, so it is gathered a few chests per frame within StepBudget.
    private const double StepBudget = 2.0;

    private void Step()
    {
        if (gather == null) return;
        var started = UiCost.Start(); var clock = Stopwatch.StartNew(); var finished = false;
        try
        {
            var more = true;
            while (gather != null && (more = gather.MoveNext())) if (clock.Elapsed.TotalMilliseconds >= StepBudget) break;
            finished = !more;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            // Every answer says so until the context is rebuilt (at the latest when it is TooltipContext.MaximumAge old).
            StopGather(); building!.Fail(); finished = true;
            if (CleanupRunner.LogLimit("explanation context|" + error.GetType().Name) is { } more)
                log.LogWarning($"Explanations could not gather the chests nearby: {error.GetType().Name}: {error.Message}" + more);
        }
        gatherWork += clock.Elapsed.TotalMilliseconds;
        if (finished) Ready();
        UiCost.Stop("explanation", started);
    }

    // The gathered context replaces the old one, which is disposed; the subject is answered again against it.
    private void Ready()
    {
        UiCost.Record("explain context", gatherWork); // X16: the gather's work over all its frames,
        UiCost.Record("explain context wall", (Stopwatch.GetTimestamp() - gatherStarted) * 1000.0 / Stopwatch.Frequency); // and the time it took.
        var old = context; context = building; building = null; gather = null;
        old?.Dispose();
        if (subject != null) Answered(context!);
    }

    private void StopGather()
    {
        var running = gather; gather = null;
        running?.Dispose();
    }

    private int tooltipShown = -1, tooltipLanguage = -1;
    private string tooltipLine = "";

    /// <summary>
    /// P6-23 (D071): the answer for the game's tooltip of <paramref name="item"/>, or "" if it is not the stack explained
    /// (or not yet). Built once per answer and language: the game asks for the tooltip every frame while it is open.
    /// B2-07: the lead is colored from the answer's parts (<see cref="WhereLine"/>), not by finding ": " in the text.
    /// </summary>
    internal string TooltipLine(ItemDrop.ItemData item)
    {
        if (explainOff || !ReferenceEquals(item, subject)) return ""; // With or without a chest open (D086 D12).
        if (tooltipShown == ExplanationRevision && tooltipLanguage == Texts.Generation) return tooltipLine;
        tooltipShown = ExplanationRevision; tooltipLanguage = Texts.Generation;
        var line = Explanation == null ? "" : Texts.Get(Explanation.Fit(Texts.Plain, Texts.Escape));
        if (ExplanationContext != null)
            line += (line.Length == 0 ? "" : "\n") + "<color=" + Texts.Color(Tone.Faint) + ">" + Texts.Escape(Texts.Plain(ExplanationContext)) + "</color>";
        return tooltipLine = line;
    }

    // X16 (memory): the contexts and their snapshot adapters go when the inventory closes or another chest opens.
    private void Forget()
    {
        StopGather();
        building?.Dispose(); context?.Dispose(); building = context = null;
        if (subject == null && hovered == null && Explanation == null && Answer == null) return;
        subject = hovered = null; Answer = null; Explanation = null; ExplanationContext = null; ExplanationRevision++;
    }

    // ---- Read-only views for the panel's sections.
    internal IReadOnlyList<CategoryTreeRow> CategoryRows(ClassificationTaxonomy taxonomy)
    {
        var plugin = Plugin.Instance!;
        // Before the server's answers arrive, member counts are unknown, so every category is listed.
        Func<string, bool> hasMembers = plugin.ClassificationEvidenceReady ? id => plugin.CategoryMemberCount(id) > 0 : _ => true;
        return CategorySearch.Rows(CategoryText.For(taxonomy), Categories, hasMembers, expanded, collapsed, Search);
    }

    // ---- P6-06: search in the category picker, and the nearby chests that hold (D122; with the toggle, take) what was found. Read only.
    private const float SearchDelay = 0.25f;
    private string typed = "", searched = "";
    private float typedAt;
    private long searchedGeneration = long.MinValue;
    private object? searchedTaxonomy;
    private int searchedLanguage = -1;
    private List<SearchableItem>? searchable;
    private string searchableKey = "";
    internal CategorySearchResult Search { get; private set; } = CategorySearchResult.None;
    /// <summary>Bumped when the search result changes; separate from <see cref="Revision"/>, so typing never re-explains.</summary>
    internal int SearchRevision { get; private set; }

    internal void SearchFor(string text) { typed = text ?? ""; typedAt = Time.unscaledTime; }
    /// <summary>The query as typed, for the search box to show (a lab or reset change must not leave the box out of step).</summary>
    internal string Typed => typed;

    // Runs a typed query once typing pauses, and again when the category answers change.
    private void Searching()
    {
        var plugin = Plugin.Instance!; var answers = plugin.CategoryAnswers; var taxonomy = plugin.RuleTaxonomy;
        var generation = answers?.Generation ?? long.MinValue;
        // S13: category names follow the language, so a language change searches again.
        var current = typed == searched && generation == searchedGeneration && ReferenceEquals(taxonomy, searchedTaxonomy) && searchedLanguage == Texts.Generation;
        // A saved or reloaded rule that changes which chests take the focused hit is rescanned by Lighting (P6-13).
        if (current || Time.unscaledTime - typedAt < SearchDelay) return;
        searched = typed; searchedGeneration = generation; searchedTaxonomy = taxonomy; searchedLanguage = Texts.Generation;
        var started = UiCost.Start();
        Search = answers == null || !answers.CategoriesReady ? CategorySearchResult.None
            : CategorySearch.Find(CategoryText.For(taxonomy), typed, Searchable(plugin, answers), plugin.CategoryMemberCount);
        UiCost.Stop("search", started);
        SearchRevision++;
    }

    // P8-18 (D100): the highlight follows the visible tab's search. Categories (D101 C9): the tree row under the pointer,
    // else the first match. Items: every item the grid shows for its search (Q13), or the one icon under the pointer;
    // the Filters tab has no search. Typing and pointing settle for SearchDelay first, so a rescan does not start on each
    // keystroke, row or icon passed over.
    private int litRuleRevision, opened;
    private string litKey = "\0", gridLit = "", gridHover = "", hoverLit = "", rowHover = "", rowLit = "";
    private float gridTypedAt, gridHoverAt, rowHoverAt;
    /// <summary>What the highlight asked about, named for the status line's tooltip (X54: the items, not "these items"); null: nothing named.</summary>
    internal MessageList? LitSubject { get; private set; }

    internal string RowHovered => rowHover;

    /// <summary>The pointer entered (<paramref name="on"/>) or left a category tree row while searching (D101).</summary>
    internal void RowPointer(string id, bool on)
    {
        var next = on ? id : rowHover == id ? "" : rowHover; // A row left after the next was entered keeps the next.
        if (next != rowHover) { rowHover = next; rowHoverAt = Time.unscaledTime; }
    }

    /// <summary>The Items tab's icon under the pointer, from the panel's Items section refresh (null: none).</summary>
    internal void GridPointer(GridEntry? entry)
    {
        var prefab = entry?.Prefab ?? "";
        if (prefab != gridHover) { gridHover = prefab; gridHoverAt = Time.unscaledTime; }
    }

    private void Lighting()
    {
        var now = Time.unscaledTime;
        if (gridQuery != gridLit && now - gridTypedAt >= SearchDelay) gridLit = gridQuery;
        if (gridHover != hoverLit && now - gridHoverAt >= SearchDelay) hoverLit = gridHover;
        if (rowHover != rowLit && now - rowHoverAt >= SearchDelay) rowLit = Search.Active ? rowHover : "";
        var answers = Plugin.Instance?.CategoryAnswers; var edit = Mode == RulePanelMode.Edit;
        var key = !edit ? "-" : PickingCategories ? "c|" + SearchRevision + "|" + rowLit
            : PickingItems && gridLit.Trim().Length != 0 ? "i|" + gridLit + "|" + hoverLit + "|" + (answers?.Generation ?? long.MinValue) : "-";
        key += "|" + ruleRevision + "|" + opened + "|" + ShowsTakers;
        if (key == litKey) return;
        litKey = key;
        Light(edit);
    }

    private void Light(bool edit)
    {
        var rescan = litRuleRevision != ruleRevision;
        litRuleRevision = ruleRevision;
        var taxonomy = Plugin.Instance!.RuleTaxonomy;
        if (edit && PickingCategories && Search.Active)
        {
            var items = Search.Items; var categories = Search.Categories;
            if (rowLit.Length != 0) LightCategory(taxonomy, rowLit, rescan);
            else if (items.Count != 0)
            {
                var name = Texts.Escape(items[0].Name);
                LitSubject = HighlightText.Names(new[] { name });
                Highlight.Request(items[0].Category, new[] { items[0].Prefab }, rescan, takes: ShowsTakers, about: name);
            }
            else if (categories.Count != 0) LightCategory(taxonomy, categories[0], rescan);
            else Highlight.Clear();
        }
        else if (edit && PickingItems && gridLit.Trim().Length != 0)
        {
            var wanted = GridLit();
            // X54: the items by name ("Copper, Tin +3 more"); look-alike copies share one name.
            var names = wanted.Select(x => Texts.Escape(ItemName(x))).ToList();
            LitSubject = HighlightText.Names(names);
            Highlight.Request("", wanted, rescan, takes: ShowsTakers, named: names.Distinct(StringComparer.Ordinal).Count());
        }
        else Highlight.Clear();
    }

    // D101 (Q16, bible B3.20): a category lights the chests whose full rule could take its items (Never and filters count),
    // judged on its first members, the search's finds first.
    private void LightCategory(ClassificationTaxonomy taxonomy, string category, bool rescan)
    {
        var name = Texts.Escape(CategoryName(taxonomy, category));
        LitSubject = HighlightText.Names(new[] { name });
        var found = Search.Items.Select(x => x.Prefab);
        Highlight.Request(category, CategorySearch.Members(searchable ?? new List<SearchableItem>(), category, found, ChestHighlight.MaximumItems), rescan,
            holdsOf: category, takes: ShowsTakers, about: name);
    }

    // The grid's items for its search, in the order shown, at most ChestHighlight.MaximumItems; just the hovered icon's
    // copies while one is pointed at.
    private List<string> GridLit()
    {
        var entries = GridEntries(); var wanted = new List<string>();
        var hovered = hoverLit.Length == 0 ? null : entries.FirstOrDefault(x => x.Prefab == hoverLit);
        foreach (var entry in hovered != null ? new[] { hovered } : entries)
            foreach (var prefab in entry.Copies)
            {
                if (wanted.Count == ChestHighlight.MaximumItems) return wanted;
                wanted.Add(prefab);
            }
        return wanted;
    }

    // Every item in the loaded catalog with its current categories and shown name, built once per catalog and answers.
    private List<SearchableItem> Searchable(Plugin plugin, ICategoryAnswers answers)
    {
        var catalog = plugin.LoadedCatalog;
        var key = (catalog?.Fingerprint ?? "") + "|" + answers.Generation;
        if (searchable != null && key == searchableKey) return searchable;
        searchableKey = key; searchable = new List<SearchableItem>();
        if (catalog == null) return searchable;
        foreach (var prefab in catalog.Items.Keys)
        {
            var result = answers.Resolve(prefab);
            if (result != null) searchable.Add(new SearchableItem(prefab, ItemName(prefab), result.Memberships.Select(x => x.Category)));
        }
        return searchable;
    }

    /// <summary>
    /// P6-14 (D065): the filters and values that apply to the ticked categories' items, plus any the rule already uses.
    /// Rebuilt only when the answers, the categories or the rule's filters change. Instance filters are judged from the
    /// game's own item data: upgradable (max quality above 1), durability, craftable (a recipe makes it), and EpicLoot
    /// rarity only while EpicLoot's verified reader works.
    /// </summary>
    internal IReadOnlyList<FilterChoice> FilterOptions()
    {
        var plugin = Plugin.Instance!; var answers = plugin.CategoryAnswers; var catalog = plugin.LoadedCatalog;
        var ready = catalog != null && answers != null && answers.CategoriesReady;
        var teleportAll = TeleportAll();
        var key = (catalog?.Fingerprint ?? "") + "|" + (answers?.Generation ?? long.MinValue) + "|" + ready + "|" + plugin.EpicLootRarityOffered + "|" + teleportAll + "|" +
            string.Join(",", Categories) + "|" + string.Join(";", Filters.Select(x => x.Key + "=" + string.Join(",", x.Value)));
        if (key == choicesKey) return choices;
        choicesKey = key;
        var saved = Filters.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToList(), StringComparer.Ordinal);
        var items = ready ? catalog!.Items.Keys.Select(answers!.Resolve).Where(x => x != null).Select(x => x!) : null;
        var rarity = plugin.EpicLootRarityOffered; craftable = null;
        var timed = UiCost.Start();
        var me = Player.m_localPlayer;
        var crafter = me ? new[] { ClassificationFilters.CrafterValue(true, me!.GetPlayerID(), me.GetPlayerName()), ClassificationFilters.CrafterValue(false, me.GetPlayerID(), me.GetPlayerName()) } : null;
        choices = FilterChoices.Compute(items, Categories, (item, dimension) => InstanceApplies(item.Prefab, dimension, rarity), saved, crafter,
            teleportAll ? new[] { "teleportable" } : null);
        UiCost.Stop("filter choices", timed);
        return choices;
    }
    private string choicesKey = "\0";

    /// <summary>The world setting that lets every item through portals (world modifier "Portals"); the game then ignores teleportability.</summary>
    internal static bool TeleportAll() => ZoneSystem.instance && ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll);
    private IReadOnlyList<FilterChoice> choices = Array.Empty<FilterChoice>();
    private HashSet<string>? craftable;

    // Items an enabled recipe makes; rebuilt with the filter choices (a recipe mod may change them).
    private HashSet<string> Craftable()
    {
        if (craftable != null) return craftable;
        craftable = new HashSet<string>(StringComparer.Ordinal);
        var db = ObjectDB.instance;
        if (db) foreach (var recipe in db!.m_recipes) if (recipe && recipe.m_enabled && recipe.m_item) craftable.Add(recipe.m_item.gameObject.name);
        return craftable;
    }

    private bool InstanceApplies(string prefab, string dimension, bool rarity)
    {
        if (dimension == "rarity") return rarity;
        var db = ObjectDB.instance;
        var shared = db ? db.GetItemPrefab(prefab)?.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
        if (shared == null) return false;
        switch (dimension)
        {
            // B3.21: the stack fact "upgraded" needs an upgrade recipe since B3.6, as "upgradable" and "quality" do.
            case "upgraded": case "upgradable": case "quality": return UpgradeRecipes.Can(prefab); // P6-21: a recipe upgrades it.
            case "damaged": return shared.m_useDurability;
            case "crafter": return Craftable().Contains(prefab);
            default: return false;
        }
    }

    internal Message? HighlightStatus() => Highlight.Status();

    /// <summary>The status line's tooltip (D101 C7): the chests found, with their distances.</summary>
    internal Tooltip? HighlightTooltip() => HighlightText.Tooltip(LitSubject,
        Highlight.Chests(ChestHighlight.Kind.Holds, LitChestName), Highlight.Chests(ChestHighlight.Kind.Takes, LitChestName));

    private Message LitChestName(Container chest)
    {
        if (ReferenceEquals(chest, Chest)) return Message.Of("yavsos_search_tip_this_chest");
        // The panel is open only with a player in the world, which the highlight also needs (it goes dark without one).
        var player = Player.m_localPlayer;
        var meters = player ? Math.Round(Vector3.Distance(player.transform.position, chest.transform.position), MidpointRounding.AwayFromZero) : 0;
        return Message.Of("yavsos_search_tip_chest").With("chest", Texts.Escape(GameText.Localize(chest.m_name))).With("meters", (long)meters);
    }

    /// <summary>D122: the search also lights the chests whose rules accept a match ("Also show chests that accept it"; remembered).</summary>
    internal bool ShowsTakers => Plugin.Instance?.Settings.SearchShowsTakers.Value == true;
    internal void ToggleTakers() => Queue(() =>
    {
        if (Plugin.Instance?.Settings is { } settings) settings.SearchShowsTakers.Value = !settings.SearchShowsTakers.Value;
        Revision++;
    });

    // ---- P6-18: the Items tab's icon grid. The list is rebuilt only when the rule, the answers, the search or what is
    // in the chest and inventory changes; the view itself draws only the rows in sight.
    private string gridQuery = "", gridKey = "\0";
    private List<GridSource>? gridSources;
    private string gridSourcesKey = "";
    private IReadOnlyList<GridEntry> gridEntries = Array.Empty<GridEntry>();
    private readonly Dictionary<string, Sprite?> icons = new(StringComparer.Ordinal);
    /// <summary>Bumped when <see cref="GridEntries"/> gives a new list, so the view refills only then.</summary>
    internal int GridRevision { get; private set; }
    internal string GridQuery => gridQuery;
    internal void SearchItems(string text) { gridQuery = text ?? ""; gridTypedAt = Time.unscaledTime; }

    /// <summary>Click on an icon: none → Always → Never → none (maintainer, 2026-10-23), for every look-alike copy it stands for.</summary>
    internal void CycleMark(GridEntry entry)
    {
        var next = ItemGrid.Next(entry.Mark);
        Apply(() =>
        {
            foreach (var prefab in entry.Copies)
            {
                Includes.Remove(prefab); Excludes.Remove(prefab);
                if (next == GridMark.Always) Includes.Add(prefab);
                else if (next == GridMark.Never) Excludes.Add(prefab);
            }
        });
    }

    internal IReadOnlyList<GridEntry> GridEntries()
    {
        var plugin = Plugin.Instance!; var answers = plugin.CategoryAnswers; var catalog = plugin.LoadedCatalog;
        var here = Here();
        KeptOutUpdate();
        var key = Revision + "|" + keptOutKey + "|" + gridQuery + "|" + (catalog?.Fingerprint ?? "") + "|" + (answers?.Generation ?? long.MinValue) + "|" + string.Join(",", here);
        if (key == gridKey) return gridEntries;
        gridKey = key;
        var timed = UiCost.Start();
        var rarity = plugin.EpicLootRarityOffered;
        gridEntries = ItemGrid.Build(GridSources(plugin, answers), Categories, GridFilters(), Includes, Excludes, here, gridQuery,
            (item, dimension) => InstanceApplies(item.Prefab, dimension, rarity && item.InstanceRarity));
        UiCost.Stop("item grid", timed);
        GridRevision++;
        return gridEntries;
    }

    private Dictionary<string, IReadOnlyList<string>> GridFilters() =>
        Filters.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToList(), StringComparer.Ordinal);

    /// <summary>A grid section's title in the player's language (the panel is rebuilt on a language change).</summary>
    internal static string GridTitle(GridSection section, int count) => Texts.Get(ItemGrid.Title(section, count));

    internal Sprite? GridIcon(string prefab)
    {
        if (icons.TryGetValue(prefab, out var known)) return known;
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        Sprite? sprite = null;
        try { sprite = drop ? drop!.m_itemData.GetIcon() : null; } catch (Exception error) when (error is IndexOutOfRangeException or NullReferenceException) { }
        icons[prefab] = sprite;
        return sprite;
    }

    /// <summary>The game's own tooltip for the item, then what its mark means here.</summary>
    internal (string Topic, string Text) GridTooltip(GridEntry entry)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(entry.Prefab)?.GetComponent<ItemDrop>() : null;
        var plugin = Plugin.Instance!; var rarity = plugin.EpicLootRarityOffered;
        var byCategory = plugin.CategoryAnswers?.Resolve(entry.Prefab) is { } item &&
            ItemGrid.Takes(item, Categories, GridFilters(), (row, dimension) => InstanceApplies(row.Prefab, dimension, rarity && row.InstanceRarity));
        // P10-04: the notes are messages, one per line, each in its tone; no markup in the text.
        var lines = new List<object> { Tinted.Of(ItemGrid.MarkText(entry.Mark, byCategory), Tone.Warning), Tinted.Of(ItemGrid.ClickHint, Tone.Warning) };
        if (entry.Copies.Count >= 2)
            lines.Add(Tinted.Of(Message.Of("yavsos_grid_copies").With("prefabs", MessageList.Of(ListKind.Comma, entry.Copies.Select(Texts.Escape))).Count(entry.Copies.Count), Tone.Faint));
        if (KeptOut(entry)) lines.Add(Tinted.Of(Message.Of("yavsos_grid_kept_out"), Tone.Caution));
        var note = Texts.Get(MessageList.Of(ListKind.Lines, lines));
        if (!drop) return (entry.Name, Texts.Get("yavsos_grid_missing") + "\n\n" + note);
        try { return (drop!.m_itemData.m_shared.m_name, drop.m_itemData.GetTooltip() + "\n\n" + note); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { return (entry.Name, note); }
    }

    // Every catalog item with its shown name and current answers, built once per catalog and answers generation.
    private List<GridSource> GridSources(Plugin plugin, ICategoryAnswers? answers)
    {
        var catalog = plugin.LoadedCatalog;
        var key = (catalog?.Fingerprint ?? "") + "|" + (answers?.Generation ?? long.MinValue);
        if (gridSources != null && key == gridSourcesKey) return gridSources;
        gridSourcesKey = key; gridSources = new List<GridSource>();
        if (catalog == null) return gridSources;
        foreach (var prefab in catalog.Items.Keys)
        {
            // Look-alikes (P6-24, 2026-10-23): the same Sprite object; the game's NPC copies of a cape share the player's.
            var icon = GridIcon(prefab);
            gridSources.Add(new GridSource(prefab, ItemName(prefab), answers?.CategoriesReady == true ? answers.Resolve(prefab) : null, icon != null,
                icon ? icon!.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) : null, Craftable().Contains(prefab)));
        }
        return gridSources;
    }

    // What this chest and the player's inventory hold, by prefab.
    private List<string> Here()
    {
        var player = Player.m_localPlayer;
        return (player ? player.GetInventory().GetAllItems() : new List<ItemDrop.ItemData>())
            .Concat(Chest?.GetInventory()?.GetAllItems() ?? new List<ItemDrop.ItemData>())
            .Select(x => x?.m_dropPrefab ? x!.m_dropPrefab.name : null).Where(x => x != null).Select(x => x!)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>What stashes in this game (P10-04: a typed value; each sentence that names it has one message per stasher).</summary>
    internal static Stasher Stasher => StashRunner.Enabled ? Stasher.Yavsos : Stasher.AzuAutoStore;

    // ---- D071: the header's status marks and the Rule section's chips.
    private const float MarksEvery = 1f; // The MultiUserChest check reads Harmony's patch list; once a second is plenty.
    private float marksAt = float.NegativeInfinity;
    private IReadOnlyList<IntegrationMark> marks = Array.Empty<IntegrationMark>();
    private Message? marksKeptOut;

    /// <summary>One mark per integration that is installed (D071), refreshed at most once a second.</summary>
    internal IReadOnlyList<IntegrationMark> Marks()
    {
        var now = Time.unscaledTime;
        var line = KeptOutLine(); // Per chest, so a change shows at once rather than on the next once-a-second refresh.
        if (now - marksAt < MarksEvery && Equals(line, marksKeptOut)) return marks;
        marksAt = now; marksKeptOut = line;
        var plugin = Plugin.Instance!; var settings = plugin.Settings;
        var azu = Chainloader.PluginInfos.TryGetValue(AzuAutoStoreIntegration.AzuGuid, out var info) ? Texts.Escape(info?.Metadata?.Version?.ToString() ?? "") : null;
        var reader = NativeInventoryProbe.Supported || !NativeInventoryProbe.Checked; // Not checked yet is not broken.
        var muc = MucRoute.InUse(out var copy);
        var epic = plugin.EpicLootState;
        marks = PanelMarks.Shown(
            PanelMarks.Azu(azu, AzuAutoStoreIntegration.State == AzuIntegrationState.Active, AzuStoreOrder.Active, AzuStoreOrder.PickupActive,
                AzuExclusions.State != AzuExclusionState.Unavailable, AzuExclusions.PauseState != AzuExclusionState.Unavailable, line,
                AzuExclusions.State == AzuExclusionState.Limited ? Message.Of("yavsos_mark_azu_exclusions_changed")
                : AzuExclusions.RecentErrors ? Message.Of("yavsos_mark_azu_exclusions_errors").Count(AzuExclusions.Errors) : null),
            azu == null ? PanelMarks.Stashing(settings.AllowStashing.Value, reader, MarkKey(settings.StashShortcut.Value), MarkKey(settings.StashItemShortcut.Value),
                ChestRuleStore.Bound) : null,
            PanelMarks.Cleanup(settings.AllowCleanup.Value, reader, azu != null && AzuExclusions.State == AzuExclusionState.Unavailable, ChestRuleStore.Bound),
            PanelMarks.Server(plugin.ServerWithoutYavsos),
            PanelMarks.Categories(plugin.ClassificationEvidenceReady, plugin.LoadedCatalog?.Items.Count ?? 0, plugin.PolicyFromServer, plugin.CategoriesGaveUp != null,
                plugin.ServerPolicyRejected),
            PanelMarks.MultiUserChest(copy, muc),
            PanelMarks.EpicLoot(epic != OptionalCapabilityState.Absent, epic == OptionalCapabilityState.Supported),
            PanelMarks.Drawers(Chainloader.PluginInfos.ContainsKey(RossItemDrawersIntegration.RossGuid), AzuAutoStoreIntegration.ReservedDrawerWants != null,
                AzuAutoStoreIntegration.State == AzuIntegrationState.Active),
            PanelMarks.Display(DisplayFaults.Off));
        return marks;
    }

    // A key as a player reads it ("Ctrl + .", the friendly names the panel uses elsewhere), safe inside rich text.
    // "" when the key is unset, so the mark picks a sentence without it.
    private static string MarkKey(KeyboardShortcut key) =>
        KeyNames.Format(key.MainKey.ToString(), key.Modifiers.Select(x => x.ToString())) is { } name ? Texts.Escape(Texts.Get(name)) : "";

    private string chipsKey = "\0";
    private IReadOnlyList<RuleChip> chips = Array.Empty<RuleChip>();

    /// <summary>
    /// What the rule takes, as chips (D071), or for a chest without a rule what stashing does with it (D101); rebuilt only
    /// when the rule, the category set or AzuAutoStore's existing-item setting changes.
    /// </summary>
    internal IReadOnlyList<RuleChip> Chips(ClassificationTaxonomy taxonomy)
    {
        var none = NoRule();
        var key = Revision + "|" + none + "|" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(taxonomy);
        if (key == chipsKey) return chips;
        chipsKey = key;
        if (!Stored) return chips = new[] { RuleChips.NoRule(none) };
        var names = CategoryText.For(taxonomy);
        var categories = CategorySelection.Normalize(taxonomy, Categories.Where(taxonomy.Categories.ContainsKey))
            .OrderBy(x => taxonomy.Categories[x].DisplayRank).ThenBy(names.Shown, StringComparer.CurrentCulture)
            .Select(names.Shown);
        // Unknown filters and values are listed as ignored, not as what the rule takes (D077 B01, D086 D6).
        var filters = RuleText.Short(Filters.Where(x => ClassificationFilters.IsKnown(x.Key))
            .ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.Where(v => ClassificationFilters.IsKnownRuleValue(x.Key, v)).ToList(), StringComparer.Ordinal), OpenValueName);
        return chips = RuleChips.Build(categories, filters, Includes.Select(ItemName).OrderBy(x => x, StringComparer.CurrentCulture),
            Excludes.Select(ItemName).OrderBy(x => x, StringComparer.CurrentCulture), Unknown.Count, Stasher);
    }

    /// <summary>
    /// D101: YAVSOS's own stash adds to a chest without a rule only items it holds (D061 Q2); AzuAutoStore does so while
    /// its "Must Have Existing Item To Pull" is on. Unreadable: not guessed.
    /// </summary>
    private static NoRuleStash NoRule() => StashRunner.Enabled ? NoRuleStash.ExistingOnly : AzuAutoStoreIntegration.ExistingItemOnly switch
    {
        true => NoRuleStash.ExistingOnly, false => NoRuleStash.Anything, _ => NoRuleStash.Unknown
    };

    // P4-00f (D056): AzuAutoStore's own config can keep items this rule asks for out of this chest type. Shown as a
    // caution on the AzuAutoStore mark and on those items in the grid, not as a banner (maintainer, 2026-10-24).
    private string keptOutKey = "";
    private Message? keptOutLine;
    private HashSet<string> keptOut = new(StringComparer.Ordinal);

    /// <summary>The AzuAutoStore mark's line for this chest's kept-out items, or null when there are none or it can't tell yet.</summary>
    internal Message? KeptOutLine() { KeptOutUpdate(); return keptOutLine; }

    /// <summary>The rule takes this grid item, but AzuAutoStore's config keeps it out of this chest type.</summary>
    internal bool KeptOut(GridEntry entry) => entry.Taken && keptOut.Count != 0 && entry.Copies.Any(keptOut.Contains);

    private void KeptOutUpdate()
    {
        var plugin = Plugin.Instance!;
        var answers = plugin.CategoryAnswers; var catalog = plugin.LoadedCatalog;
        if (!Stored || Chest == null || answers == null || catalog == null || Categories.Count != 0 && !answers.CategoriesReady || !AzuExclusions.HasExclusions(Chest))
        { keptOutKey = ""; keptOutLine = null; if (keptOut.Count != 0) keptOut = new HashSet<string>(StringComparer.Ordinal); return; }
        var key = Observed + "|" + answers.Generation + "|" + catalog.Fingerprint + "|" + AzuExclusions.ChestType(Chest);
        if (StringComparer.Ordinal.Equals(key, keptOutKey)) return;
        keptOutKey = key;
        var container = Chest; var observed = Observed; var taxonomy = plugin.RuleTaxonomy;
        // F10: only the items the grid lists (with an icon), so creature attacks ("Lox bite") aren't counted or named.
        var found = StasherExclusionConflicts.Find(catalog.Items.Keys.Where(prefab => GridIcon(prefab) != null),
            prefab => plugin.RuleEvaluator.Evaluate(observed, prefab, taxonomy, answers) == ChestRuleDecision.Accept,
            prefab => AzuExclusions.Allows(container, prefab) != false);
        keptOut = new HashSet<string>(found.Items, StringComparer.Ordinal);
        // Different prefabs can share a shown name (two "Lox bite"s); list each name once. Names go into rich text.
        var examples = found.Examples.Select(ItemName).Distinct(StringComparer.CurrentCulture).Select(Texts.Escape).ToList();
        // P8-17: one item is named ("keeps Bronze out"); several are counted, with examples and how many more there are.
        keptOutLine = found.Count == 0 ? null : found.Count == 1 && found.Examples.Count == 1
            ? Message.Of("yavsos_mark_azu_kept_out_item").With("item", examples[0]).With("tab", Message.Of("yavsos_tab_items"))
            : Message.Of("yavsos_mark_azu_kept_out").Count(found.Count)
                .With("items", MessageList.Of(ListKind.Comma, examples).WithMore(Math.Max(0, found.Count - found.Examples.Count)))
                .With("tab", Message.Of("yavsos_tab_items"));
    }

    /// <summary>
    /// An open filter value in the player's language: a creature's name token (P6-15), a mod by its own name, the
    /// crafter ("me" for the local player). Formatted here, so it is a name like the others for <see cref="RuleText"/>;
    /// the caches that keep it key on <see cref="Texts.Generation"/>.
    /// </summary>
    internal static string OpenValueName(string dimension, string value)
    {
        if (dimension == "eaten-by") return Localization.instance != null ? Localization.instance.Localize(value) : value;
        // P6-21: the mod's own name rather than its plugin ID; the base game by its display name.
        if (dimension == "mod")
            return value == RuleText.Vanilla ? Shown(RuleText.ValueName(dimension, value))
                : Chainloader.PluginInfos.TryGetValue(value, out var plugin) && !string.IsNullOrEmpty(plugin?.Metadata?.Name) ? plugin!.Metadata.Name : value;
        if (dimension == "crafter" && ClassificationFilters.TryParseCrafter(value, out var by, out var id, out var name))
            return Shown(RuleText.CrafterName(by, name, Player.m_localPlayer && Player.m_localPlayer.GetPlayerID() == id));
        return value;
    }

    /// <summary>A filter value as its tick box shows it, in the player's language: an open value's name or a closed token's display name.</summary>
    internal static string ValueText(string dimension, string value) =>
        value != "unknown" && ClassificationFilters.Values(dimension) == null ? OpenValueName(dimension, value) : Shown(RuleText.ValueName(dimension, value));

    /// <summary>A filter's name in the player's language, or its ID when this version doesn't name it.</summary>
    internal static string FilterNameText(string dimension) => RuleText.FilterName(dimension) is { } name ? Texts.Get(name) : dimension;

    /// <summary>A value <see cref="RuleText"/> shows (a name as is, or a message) as text in the player's language.</summary>
    private static string Shown(object value) => value is Message message ? Texts.Get(message) : value as string ?? "";

    /// <summary>A category's name as the player sees it (S13).</summary>
    internal static string CategoryName(ClassificationTaxonomy taxonomy, string id) => CategoryText.Name(taxonomy, id);

    internal static string ItemName(ItemDrop.ItemData item) =>
        Localization.instance != null && !string.IsNullOrEmpty(item.m_shared?.m_name) ? Localization.instance.Localize(item.m_shared!.m_name) : item.m_dropPrefab?.name ?? "";

    internal static string ItemName(string prefab)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        var token = drop ? drop!.m_itemData?.m_shared?.m_name : null;
        return string.IsNullOrEmpty(token) || Localization.instance == null ? prefab : Localization.instance.Localize(token);
    }
}
