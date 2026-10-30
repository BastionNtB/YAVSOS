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
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;
using YAVSOS.Transfers;
using YAVSOS.Cleanup;

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
    private readonly ManualLogSource log;
    internal Container? Chest { get; private set; }
    internal string Observed { get; private set; } = "";
    internal readonly SortedSet<string> Categories = new SortedSet<string>(StringComparer.Ordinal);
    internal readonly SortedSet<string> Includes = new SortedSet<string>(StringComparer.Ordinal);
    internal readonly SortedSet<string> Excludes = new SortedSet<string>(StringComparer.Ordinal);
    internal readonly SortedSet<string> Unknown = new SortedSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal), collapsed = new HashSet<string>(StringComparer.Ordinal);
    internal bool Unreadable { get; private set; }
    internal bool Stored { get; private set; }
    internal bool PickingCategories { get; private set; }
    internal bool PickingItems { get; private set; }
    internal bool PickingFilters { get; private set; }
    /// <summary>Filters of the edited alternative (P6-07b): values within a dimension are OR, dimensions AND.</summary>
    internal readonly SortedDictionary<string, SortedSet<string>> Filters = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IReadOnlyList<string>> editedFilters = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>D101: a refusal or summary next to the control that was clicked; successes say nothing.</summary>
    internal PanelMessages Messages { get; } = new PanelMessages();
    internal int Priority { get; private set; }
    /// <summary>Bumped on every change to what the panel shows, so sections rebuild their rows only then.</summary>
    internal int Revision { get; private set; }
    private Action? pending; // Clicks queue here and run in LateUpdate, outside the UI's event dispatch.
    private ClassificationTaxonomy? loadedTaxonomy;
    private RulePanel? panel;
    private ResultPanel? resultPanel;
    // D058, per feature: the capability probe turns off both panels; a runtime failure turns off only the panel, the
    // result view, the search or the explanations that failed, so one broken view never removes the rule editor.
    private bool styled = true, resultStyled = true, capable = true, probed, searchOff, explainOff, eligible;
    private readonly ConfigEntry<bool> showResults;
    private readonly RunResults results;
    private readonly StashRunner stash;
    internal ChestHighlight Highlight { get; }

    internal CleanupRunner Cleanup { get; }
    internal RuleControls(ConfigEntry<bool> enabled, ConfigEntry<bool> showResults, ConfigEntry<float> offsetX, ConfigEntry<float> offsetY,
        CleanupRunner cleanup, StashRunner stash, RunResults results, ChestHighlight highlight, ManualLogSource log)
    {
        this.enabled = enabled; this.showResults = showResults; OffsetX = offsetX; OffsetY = offsetY; Cleanup = cleanup; this.stash = stash;
        this.results = results; Highlight = highlight; this.log = log;
        // B1.17: without the game's open-chest field the panel can never open; say so once instead of staying silent.
        if (CurrentContainer == null)
            log.LogWarning("Chest rule panel turned off: the game's open-chest field (InventoryGui.m_currentContainer) could not be read. Chests open as usual without YAVSOS's panel.");
    }

    private static AccessTools.FieldRef<InventoryGui, Container>? Field()
    {
        try { return AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer"); }
        catch { return null; }
    }

    internal void LateUpdate()
    {
        var plugin = Plugin.Instance;
        var gui = InventoryGui.instance;
        Results();
        var lit = UiCost.Start();
        Highlight.Update(); // Runs on with the inventory closed, so the lit chests can be seen.
        UiCost.Stop("highlight frame", lit);
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
            catch (Exception error) when (OptionalFailure.Recoverable(error))
            {
                searchOff = true; DisplayFaults.Add(DisplayFaults.Search); Search = CategorySearchResult.None; SearchRevision++; rowHover = rowLit = "";
                try { Highlight.Clear(); } catch (Exception again) when (OptionalFailure.Recoverable(again)) { }
                log.LogWarning($"Category search turned off after {error.GetType().Name}: {error.Message}; the rule panel keeps working.");
            }
        try
        {
#if YAVSOS_LAB
            FaultLab.Raise("panel"); // D088: lab-only drawing fault (SF1).
#endif
            panel!.Show();
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { TurnOff("drawing failed: " + error.GetType().Name + ": " + error.Message); }
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
            CleanupRunner.Say("Chests can't be read in this game version, so " + (StashRunner.Enabled ? "chest rules, stashing and cleanup are off"
                : "chest rules and cleanup are off; AzuAutoStore keeps storing on its own") + " (see the log).", overInventory: true);
        }
        else if (!styled && enabled.Value && !noticedPanel)
        {
            noticedPanel = true;
            CleanupRunner.Say("The chest rule panel is off (see the log); chest rules, stashing and cleanup still work.", overInventory: true);
        }
    }

    // P6-04: the result panel, on Jotunn's overlay canvas, so it shows with or without the inventory screen.
    private void Results()
    {
        var visible = results.Visible(InventoryGui.IsVisible(), Time.unscaledDeltaTime);
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
        Chest = container; Observed = ChestRuleStore.Payload(container); Unreadable = false; Stored = false;
        eligible = ChestRuleStore.Eligibility(container) == RuleEligibility.Eligible;
        loadedTaxonomy = Plugin.Instance!.RuleTaxonomy; Revision++; ruleRevision++;
        // D071: one tab is always open; a chest with a rule opens on its items, one without on the categories to pick.
        if (fresh) { PickingItems = Readable(container); PickingCategories = !PickingItems; PickingFilters = false; }
        Categories.Clear(); Includes.Clear(); Excludes.Clear(); Unknown.Clear(); Filters.Clear(); Priority = 0; HiddenParts = 0;
        editedFilters = new Dictionary<string, IReadOnlyList<string>>();
        var read = Plugin.Instance!.ChestRules.Read(container).Policy;
        if (read == null) return;
        if (read.State == ContainerPolicyReadState.Unsupported) { Unreadable = true; return; }
        if (!read.HasRule) return;
        Stored = true;
        // P6-07b: the editor shows one alternative with its filters and saves the filter set it replaces.
        var view = ChestRuleEdit.From(read.Rules!, withFilters: true);
        Categories.UnionWith(view.Categories); Includes.UnionWith(view.Includes); Excludes.UnionWith(view.Excludes);
        foreach (var pair in view.Filters) Filters.Add(pair.Key, new SortedSet<string>(pair.Value, StringComparer.Ordinal));
        // P6-21: a saved "upgraded" (yes/no) shows, and saves on the next edit, as upgrade levels (yes: 2 and up, no: 1).
        if (Filters.TryGetValue("upgraded", out var upgraded))
        {
            Filters.Remove("upgraded");
            if (!Filters.TryGetValue("quality", out var levels)) Filters.Add("quality", levels = new SortedSet<string>(StringComparer.Ordinal));
            levels.UnionWith(ClassificationFilters.LevelsFor(upgraded));
            if (levels.Count == 0) Filters.Remove("quality");
        }
        editedFilters = view.EditedFilters;
        Priority = view.Priority ?? 0; HiddenParts = view.HiddenParts;
        Unknown.UnionWith(view.UnknownCategories);
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
    internal void ToggleFilter(string dimension, string value) => Apply(() =>
    {
        if (!Filters.TryGetValue(dimension, out var values)) Filters.Add(dimension, values = new SortedSet<string>(StringComparer.Ordinal));
        if (!values.Remove(value)) values.Add(value);
        if (values.Count == 0) Filters.Remove(dimension);
    });
    internal void ClearRule() => Queue(() => Save(null, MessageSpot.Clear));
    internal void RemoveUnknown(string id) => Apply(() => Unknown.Remove(id));
    /// <summary>Filters of the edited alternative this version doesn't know (D077 B01); kept on save unless removed.</summary>
    internal IReadOnlyList<string> UnknownFilters => Filters.Keys.Where(x => !ClassificationFilters.IsKnown(x)).ToArray();
    internal void RemoveFilter(string dimension) => Apply(() => Filters.Remove(dimension));
    /// <summary>Values of known filters this version doesn't know (D086 D6): kept on save, match nothing, until removed.</summary>
    internal IReadOnlyList<(string Dimension, string Value)> UnknownFilterValues => Filters
        .Where(x => ClassificationFilters.IsKnown(x.Key)).SelectMany(x => x.Value.Where(v => !ClassificationFilters.IsKnownRuleValue(x.Key, v)).Select(v => (x.Key, v)))
        .ToArray();
    internal void RemoveFilterValue(string dimension, string value) => Apply(() =>
    {
        if (Filters.TryGetValue(dimension, out var values) && values.Remove(value) && values.Count == 0) Filters.Remove(dimension);
    });
    /// <summary>Stored parts of the rule the panel can't show or edit (D086 D5); kept on save, removed by Clear rule.</summary>
    internal int HiddenParts { get; private set; }
    internal void ToggleCategory(ClassificationTaxonomy taxonomy, string id) =>
        Apply(() => { var next = CategorySelection.Toggle(taxonomy, Categories, id); Categories.Clear(); Categories.UnionWith(next); });
    internal void Fold(string id, bool open) => Queue(() =>
    {
        if (open) { collapsed.Add(id); expanded.Remove(id); }
        else { expanded.Add(id); collapsed.Remove(id); }
        Revision++;
    });
    internal void Choose(string prefab, bool always)
    {
        var list = always ? Includes : Excludes; var other = always ? Excludes : Includes;
        var on = list.Contains(prefab);
        Apply(() => { if (on) list.Remove(prefab); else { list.Add(prefab); other.Remove(prefab); } });
    }
    // D057, D061: priority orders chests (specificity breaks ties), shown as levels 1-10 (D069); the opt-out keeps cleanup from taking items out.
    internal void ChangeLevel(int step)
    {
        if (ChestPriority.Step(Priority, step) is not int next) return;
        Apply(() => Priority = next, MessageSpot.Chest);
    }
    /// <summary>A click on the 1–10 bar (D071).</summary>
    internal void SetLevel(int level)
    {
        var rank = ChestPriority.Rank(level);
        // D101: the bar is shown without a rule too, faint; priority belongs to the rule, so there it does nothing.
        if (!Stored || ChestPriority.Level(Priority) == ChestPriority.Level(rank)) return;
        Apply(() => Priority = rank, MessageSpot.Chest);
    }
    internal void ToggleCleanupOptOut()
    {
        var target = Chest!; var value = !ChestRuleStore.CleanupOptOut(target);
        Queue(() =>
        {
            var result = Plugin.Instance!.ChestRules.TrySetCleanupOptOut(target, value);
            if (!result.Succeeded) Messages.Say(MessageSpot.Cleanup, result.Message);
            Revision++; ruleRevision++;
        });
    }
    // D101: "Cleanup started." goes (the button turns to "Cleaning up…" and the result panel follows); a refusal, or the
    // summary of a run with nothing to move (no result panel follows it), shows beside the button.
    internal void CleanUpNearby() => Queue(() =>
    {
        var said = Cleanup.Start();
        if (said != CleanupRunner.Started)
            Messages.Say(MessageSpot.Cleanup, said, ReferenceEquals(said, Cleanup.LastSummary) ? MessageKind.Summary : MessageKind.Refusal);
        Revision++;
    });

    // D051: each change is written at once. An edit that leaves no part of the rule removes it rather than leaving
    // "accepts nothing"; parts the panel can't show stay (D086 D5), so the edit, not the panel, decides that.
    private void Apply(Action change, MessageSpot spot = MessageSpot.Tab) => Queue(() =>
    {
        change();
        Save(new ChestRuleEdit(Categories, Includes, Excludes, Unknown, Priority,
            Filters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray(),
            editedFilters.Select(x => new KeyValuePair<string, IEnumerable<string>>(x.Key, x.Value)).ToArray()), spot);
    });

    // D101: a saved edit says nothing (the chips and ticks change at the click); a refusal stays where it was asked.
    private void Save(ChestRuleEdit? edit, MessageSpot spot)
    {
        var result = Plugin.Instance!.ChestRules.TryEdit(Chest!, Observed, edit);
        Load(Chest!);
        if (!result.Succeeded) Messages.Say(spot, result.Message);
    }

    // ---- P6-05: where a stack would go and why, for the stack under the mouse or one picked in the item list. Read only:
    // the runners gather the same live inputs a run would and hand them to the Core explainer; nothing is planned to act on.
    private const float HoverDelay = 0.5f, HoverPoll = 0.1f;
    private ItemDrop.ItemData? hovered, subject;
    private float hoveredSince, nextHover;
    // ruleRevision changes only with what an answer depends on (a loaded or saved rule, the opt-out), never with
    // folding or tabs, since each explanation captures every chest in range.
    private int explainedResults, explainedRevision, ruleRevision;
    /// <summary>One short line (P6-23): what the stash key or "Clean up nearby" would do with the stack, "Stash stack [Middle mouse]: …".</summary>
    internal string? Explanation { get; private set; }
    /// <summary>Why there is no answer, or a qualifier ("turned off on this server").</summary>
    internal string ExplanationContext { get; private set; } = "";
    /// <summary>Bumped when the explanation changes, so its section rebuilds its lines only then.</summary>
    internal int ExplanationRevision { get; private set; }

    // A stack pointed at for HoverDelay is explained once; the answer is refreshed after a run or a rule change.
    private void Hover(InventoryGui gui, bool inventoryOnly = false)
    {
        var now = Time.unscaledTime;
        if (now < nextHover) return;
        nextHover = now + HoverPoll;
        if (!inventoryOnly && Mode is not (RulePanelMode.Edit or RulePanelMode.ReadOnly)) return;
        var item = inventoryOnly ? GridHover.Item(gui.m_playerGrid) : GridHover.Item(gui.m_playerGrid) ?? GridHover.Item(gui.ContainerGrid);
        if (!ReferenceEquals(item, hovered)) { hovered = item; hoveredSince = now; }
        if (item != null && !ReferenceEquals(item, subject) && now - hoveredSince >= HoverDelay) Explain(item);
        else if (subject != null && (explainedResults != results.Revision || explainedRevision != ruleRevision)) Explain(subject);
    }

    internal void ExplainItem(string prefab) => Queue(() =>
    {
        if (explainOff) return;
        var player = Player.m_localPlayer;
        bool Is(ItemDrop.ItemData? x) => x?.m_dropPrefab && x!.m_dropPrefab.name == prefab;
        var item = (player ? player.GetInventory().GetAllItems().FirstOrDefault(Is) : null) ?? Chest?.GetInventory()?.GetAllItems().FirstOrDefault(Is);
        if (item != null) Explain(item);
    });

    private void Explain(ItemDrop.ItemData item)
    {
        Cancel();
        subject = item; explainedResults = results.Revision; explainedRevision = ruleRevision; ExplanationRevision++;
        Explanation = null; ExplanationContext = "";
        var player = Player.m_localPlayer;
        if (player && player.GetInventory().ContainsItem(item)) job = stash.Explain(item, Chest, Done);
        else if (Chest && Chest!.GetInventory()?.ContainsItem(item) == true) job = Cleanup.Explain(Chest!, item, Done);
        else { subject = null; return; } // Moved or used up since.
        Step();
    }

    // P6-11 cost check: an explanation reads every chest in range, so it runs a few chests per frame within StepBudget.
    private const double StepBudget = 2.0;
    private IEnumerator<bool>? job;

    private void Step()
    {
        if (job == null) return;
        var started = UiCost.Start(); var clock = Stopwatch.StartNew();
        try
        {
            var more = true;
            while (job != null && (more = job.MoveNext())) if (clock.Elapsed.TotalMilliseconds >= StepBudget) break;
            if (!more) Cancel(); // Finished: Done has the answer.
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        {
            Cancel();
            ExplanationContext = RuleControls.ItemName(subject!) + " can't be explained right now."; ExplanationRevision++;
            log.LogWarning($"Explanation failed for {subject?.m_dropPrefab?.name}: {error.GetType().Name}: {error.Message}");
        }
        UiCost.Stop("explanation", started);
    }

    private void Done(string? sentence, string note)
    {
        Explanation = sentence;
        ExplanationContext = note;
        ExplanationRevision++;
    }

    private int tooltipShown = -1;
    private string tooltipLine = "";

    /// <summary>
    /// P6-23 (D071): the answer for the game's tooltip of <paramref name="item"/>, or "" if it is not the stack explained
    /// (or not yet). Built once per answer: the game asks for the tooltip every frame while it is open.
    /// </summary>
    internal string TooltipLine(ItemDrop.ItemData item)
    {
        if (explainOff || !ReferenceEquals(item, subject)) return ""; // With or without a chest open (D086 D12).
        if (tooltipShown == ExplanationRevision) return tooltipLine;
        tooltipShown = ExplanationRevision;
        static string Plain(string text) => text.Replace("<", "‹").Replace(">", "›");
        var line = "";
        if (Explanation != null)
        {
            var colon = Explanation.IndexOf(": ", StringComparison.Ordinal);
            line = colon < 0 ? Plain(Explanation) : "<color=#F0A64A>" + Plain(Explanation.Substring(0, colon + 1)) + "</color>" + Plain(Explanation.Substring(colon + 1));
        }
        if (ExplanationContext.Length != 0) line += (line.Length == 0 ? "" : "\n") + "<color=#A0957F>" + Plain(ExplanationContext) + "</color>";
        return tooltipLine = line;
    }

    private void Cancel()
    {
        var running = job; job = null;
        running?.Dispose();
    }

    private void Forget()
    {
        Cancel();
        if (subject == null && hovered == null && Explanation == null) return;
        subject = hovered = null; Explanation = null; ExplanationContext = ""; ExplanationRevision++;
    }

    // ---- Read-only views for the panel's sections.
    internal IReadOnlyList<CategoryTreeRow> CategoryRows(ClassificationTaxonomy taxonomy)
    {
        var plugin = Plugin.Instance!;
        // Before the server's answers arrive, member counts are unknown, so every category is listed.
        Func<string, bool> hasMembers = plugin.ClassificationEvidenceReady ? id => plugin.CategoryMemberCount(id) > 0 : _ => true;
        return CategorySearch.Rows(taxonomy, Categories, hasMembers, expanded, collapsed, Search);
    }

    // ---- P6-06: search in the category picker, and the nearby chests that take what was found. Read only.
    private const float SearchDelay = 0.25f;
    private string typed = "", searched = "";
    private float typedAt;
    private long searchedGeneration = long.MinValue;
    private object? searchedTaxonomy;
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
        var current = typed == searched && generation == searchedGeneration && ReferenceEquals(taxonomy, searchedTaxonomy);
        // A saved or reloaded rule that changes which chests take the focused hit is rescanned by Lighting (P6-13).
        if (current || Time.unscaledTime - typedAt < SearchDelay) return;
        searched = typed; searchedGeneration = generation; searchedTaxonomy = taxonomy;
        var started = UiCost.Start();
        Search = answers == null || !answers.CategoriesReady ? CategorySearchResult.None
            : CategorySearch.Find(taxonomy, typed, Searchable(plugin, answers), plugin.CategoryMemberCount);
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
    /// <summary>What the highlight asked about, for the status line's tooltip.</summary>
    internal string LitSubject { get; private set; } = "";

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
        key += "|" + ruleRevision + "|" + opened;
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
            else if (items.Count != 0) { LitSubject = items[0].Name; Highlight.Request(items[0].Category, new[] { items[0].Prefab }, rescan); }
            else if (categories.Count != 0) LightCategory(taxonomy, categories[0], rescan);
            else Highlight.Clear();
        }
        else if (edit && PickingItems && gridLit.Trim().Length != 0)
        {
            var wanted = GridLit();
            LitSubject = wanted.Count == 1 ? ItemName(wanted[0]) : hoverLit.Length != 0 ? ItemName(hoverLit) : "these items";
            Highlight.Request("", wanted, rescan);
        }
        else Highlight.Clear();
    }

    // D101 (Q16, bible B3.20): a category lights the chests whose full rule could take its items (Never and filters count),
    // judged on its first members, the search's finds first.
    private void LightCategory(ClassificationTaxonomy taxonomy, string category, bool rescan)
    {
        LitSubject = CategoryName(taxonomy, category);
        var found = Search.Items.Select(x => x.Prefab);
        Highlight.Request(category, CategorySearch.Members(searchable ?? new List<SearchableItem>(), category, found, ChestHighlight.MaximumItems), rescan);
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

    internal string HighlightStatus() => Highlight.Status();

    /// <summary>The status line's tooltip (D101 C7): the chests found, with their distances.</summary>
    internal (string Topic, string Text)? HighlightTooltip() => HighlightText.Tooltip(LitSubject, Highlight.Chests(chest => ReferenceEquals(chest, Chest) ? "This chest" :
        CleanupRunner.Localize(chest.m_name) + (Player.m_localPlayer ? " " + Vector3.Distance(Player.m_localPlayer.transform.position, chest.transform.position).ToString("0") + " m" : "")));

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

    internal static string GridTitle(GridSection section, int count) => section switch
    {
        GridSection.Here => "In this chest or your inventory",
        GridSection.Marked => "Always or Never, outside the categories",
        GridSection.Rule => "Taken by this rule",
        _ => "Other items"
    } + " (" + count + ")";

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
        var note = "\n\n<color=orange>" + ItemGrid.MarkText(entry.Mark, byCategory) + "</color>" + (entry.Copies.Count < 2 ? ""
            : "\n<color=#A0957F>" + entry.Copies.Count + " copies in the game (" + string.Join(", ", entry.Copies) + "), for example NPC gear; a mark applies to all.</color>") +
            (KeptOut(entry) ? "\n<color=#F0C050>AzuAutoStore's config keeps this out of this chest type, so it won't store it here.</color>" : "");
        if (!drop) return (entry.Name, "Not in this game any more." + note);
        try { return (drop!.m_itemData.m_shared.m_name, drop.m_itemData.GetTooltip() + note); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { return (entry.Name, note.TrimStart('\n')); }
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

    internal static string Stasher => StashRunner.Enabled ? "stashing" : "AzuAutoStore";

    // ---- D071: the header's status marks and the Rule section's chips.
    private const float MarksEvery = 1f; // The MultiUserChest check reads Harmony's patch list; once a second is plenty.
    private float marksAt = float.NegativeInfinity;
    private IReadOnlyList<IntegrationMark> marks = Array.Empty<IntegrationMark>();
    private string marksKeptOut = "";

    /// <summary>One mark per integration that is installed (D071), refreshed at most once a second.</summary>
    internal IReadOnlyList<IntegrationMark> Marks()
    {
        var now = Time.unscaledTime;
        var line = KeptOutLine(); // Per chest, so a change shows at once rather than on the next once-a-second refresh.
        if (now - marksAt < MarksEvery && StringComparer.Ordinal.Equals(line, marksKeptOut)) return marks;
        marksAt = now; marksKeptOut = line;
        var plugin = Plugin.Instance!; var settings = plugin.Settings;
        var azu = Chainloader.PluginInfos.TryGetValue(AzuAutoStoreIntegration.AzuGuid, out var info) ? info?.Metadata?.Version?.ToString() ?? "" : null;
        var reader = NativeInventoryProbe.Supported || !NativeInventoryProbe.Checked; // Not checked yet is not broken.
        var muc = MucRoute.Available(out var copy);
        var epic = plugin.EpicLootState;
        marks = PanelMarks.Shown(
            PanelMarks.Azu(azu, AzuAutoStoreIntegration.State == AzuIntegrationState.Active, AzuStoreOrder.Active, AzuStoreOrder.PickupActive,
                AzuExclusions.State != AzuExclusionState.Unavailable, AzuExclusions.PauseState != AzuExclusionState.Unavailable, line,
                AzuExclusions.State == AzuExclusionState.Limited ? "AzuAutoStore changed how it checks exclusions, so clean up moves items only into chest types with no exclusions in its config."
                : AzuExclusions.RecentErrors ? "reading its exclusion list failed in the last few minutes (" + AzuExclusions.Errors + " time" + (AzuExclusions.Errors == 1 ? "" : "s") +
                    " this session, see the log); each check is tried again, and meanwhile clean up moves items only into chest types with no exclusions in its config." : null),
            azu == null ? PanelMarks.Stashing(settings.AllowStashing.Value, reader, settings.StashShortcut.Value.ToString(), settings.StashItemShortcut.Value.ToString(),
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
        var categories = CategorySelection.Normalize(taxonomy, Categories.Where(taxonomy.Categories.ContainsKey))
            .OrderBy(x => taxonomy.Categories[x].DisplayRank).ThenBy(x => taxonomy.Categories[x].Name, StringComparer.CurrentCulture)
            .Select(x => taxonomy.Categories[x].Name);
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
    private string keptOutKey = "", keptOutLine = "";
    private HashSet<string> keptOut = new(StringComparer.Ordinal);

    /// <summary>The AzuAutoStore mark's line for this chest's kept-out items, or "" when there are none or it can't tell yet.</summary>
    internal string KeptOutLine() { KeptOutUpdate(); return keptOutLine; }

    /// <summary>The rule takes this grid item, but AzuAutoStore's config keeps it out of this chest type.</summary>
    internal bool KeptOut(GridEntry entry) => entry.Taken && keptOut.Count != 0 && entry.Copies.Any(keptOut.Contains);

    private void KeptOutUpdate()
    {
        var plugin = Plugin.Instance!;
        var answers = plugin.CategoryAnswers; var catalog = plugin.LoadedCatalog;
        if (!Stored || Chest == null || answers == null || catalog == null || Categories.Count != 0 && !answers.CategoriesReady || !AzuExclusions.HasExclusions(Chest))
        { keptOutKey = ""; keptOutLine = ""; if (keptOut.Count != 0) keptOut = new HashSet<string>(StringComparer.Ordinal); return; }
        var key = Observed + "|" + answers.Generation + "|" + catalog.Fingerprint + "|" + AzuExclusions.ChestType(Chest);
        if (StringComparer.Ordinal.Equals(key, keptOutKey)) return;
        keptOutKey = key;
        var container = Chest; var observed = Observed; var taxonomy = plugin.RuleTaxonomy;
        // F10: only the items the grid lists (with an icon), so creature attacks ("Lox bite") aren't counted or named.
        var found = StasherExclusionConflicts.Find(catalog.Items.Keys.Where(prefab => GridIcon(prefab) != null),
            prefab => plugin.RuleEvaluator.Evaluate(observed, prefab, taxonomy, answers) == ChestRuleDecision.Accept,
            prefab => AzuExclusions.Allows(container, prefab) != false);
        keptOut = new HashSet<string>(found.Items, StringComparer.Ordinal);
        // Different prefabs can share a shown name (two "Lox bite"s); list each name once.
        var examples = found.Examples.Select(ItemName).Distinct(StringComparer.CurrentCulture);
        // P8-17: one item is named ("keeps Bronze out"); several are counted, with examples.
        keptOutLine = found.Count == 0 ? "" : found.Count == 1 && found.Examples.Count == 1
            ? $"its config keeps {examples.First()} out of this chest type, so it won't store it here. " +
              "It has an amber slot under Items. Use another chest type or change its config."
            : $"its config keeps {found.Count} of this rule's items out of this chest type " +
              $"({string.Join(", ", examples)}{(found.Count > found.Examples.Count ? ", …" : "")}), so it won't store them here. " +
              "They have amber slots under Items. Use another chest type or change its config.";
    }

    internal string FilterSummary() =>
        RuleText.Filters(Filters.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.ToList(), StringComparer.Ordinal), OpenValueName);

    /// <summary>A creature's name token (P6-15) shown in the player's language; a mod ID as it is.</summary>
    internal static string OpenValueName(string dimension, string value)
    {
        if (dimension == "eaten-by") return Localization.instance != null ? Localization.instance.Localize(value) : value;
        // P6-21: the mod's own name rather than its plugin ID; the base game as "Vanilla".
        if (dimension == "mod")
            return value == RuleText.Vanilla ? RuleText.ValueName(dimension, value)
                : Chainloader.PluginInfos.TryGetValue(value, out var plugin) && !string.IsNullOrEmpty(plugin?.Metadata?.Name) ? plugin!.Metadata.Name : value;
        if (dimension == "crafter" && ClassificationFilters.TryParseCrafter(value, out var by, out var id, out var name))
        {
            var mine = Player.m_localPlayer && Player.m_localPlayer.GetPlayerID() == id;
            return RuleText.CrafterName(by, mine ? "me" : name.Length != 0 ? name : "the player who set it");
        }
        return value;
    }

    internal static string CategoryName(ClassificationTaxonomy taxonomy, string id) =>
        taxonomy.Categories.TryGetValue(id, out var category) ? category.Name : id;

    internal static string ItemName(ItemDrop.ItemData item) =>
        Localization.instance != null && !string.IsNullOrEmpty(item.m_shared?.m_name) ? Localization.instance.Localize(item.m_shared!.m_name) : item.m_dropPrefab?.name ?? "";

    internal static string ItemName(string prefab)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        var token = drop ? drop!.m_itemData?.m_shared?.m_name : null;
        return string.IsNullOrEmpty(token) || Localization.instance == null ? prefab : Localization.instance.Localize(token);
    }
}
