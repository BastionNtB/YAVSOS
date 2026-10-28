using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Cleanup;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Integrations;
using YAVSOS.Transfers;

namespace YAVSOS.Interface;

/// <summary>
/// P6-06 (D062 Q4): lights up nearby chests (within the player's cleanup range) whose rules take a searched category
/// or items. P8-18 (D100): on while the search is active, then for the player's <c>HighlightSecondsAfterClose</c> once
/// the chest closes (<see cref="HighlightLinger"/>); the Items tab's search asks for the items its grid shows. VNEI 0.17.6 has no chest highlight or find-item API to adopt (decompiled
/// 2026-10-20), so this is YAVSOS's own: a uniform colour through the game's <c>MaterialMan</c> property blocks, the
/// mechanism of vanilla <c>WearNTear.Highlight</c>, set and reset by YAVSOS. Visual only: no ZDO, inventory or rule
/// write. Rules are checked a few chests per frame, and at most <see cref="MaximumLit"/> chests are lit. Unity game
/// thread only.
/// </summary>
internal sealed class ChestHighlight
{
    internal const float Refresh = 0.5f;
    internal const int MaximumLit = 32, ChecksPerFrame = 16, MaximumItems = 64;
    private static readonly Color Gold = new Color(1f, 0.78f, 0.25f);
    private readonly ManualLogSource log;
    private bool probed, supported;
    private readonly List<Container> queue = new List<Container>();
    private readonly List<(Container Chest, GameObject Target)> lit = new List<(Container, GameObject)>();
    private string category = "", key = "";
    private readonly List<string> items = new List<string>();
    private readonly HighlightLinger linger = new HighlightLinger();
    private float nextRefresh;
    /// <summary>The chests found so far for the current request, nearest first; read by the panel.</summary>
    internal readonly List<Container> Found = new List<Container>();
    internal bool Scanning => queue.Count != 0;
    internal int Revision { get; private set; }
    internal string Category => category;
    /// <summary>A search is lighting (or has listed) chests: there is a status to show.</summary>
    internal bool Active => key.Length != 0;

    internal ChestHighlight(ManualLogSource log) { this.log = log; }

    /// <summary>Whether chests can be coloured; when not, the panel only lists them.</summary>
    internal bool Supported
    {
        get
        {
            if (probed) return supported;
            probed = true;
            // By name across the loaded assemblies (MaterialMan: assembly_valheim, ShaderProps: assembly_utils), so a
            // missing type is a failed check rather than a load error here.
            var capability = ChestHighlightCapability.Probe(name => name == ChestHighlightCapability.GameObject ? typeof(GameObject)
                : typeof(WearNTear).Assembly.GetType(name, false) ?? AccessTools.TypeByName(name));
            supported = capability.Supported;
            if (!supported) log.LogWarning("Chest highlight turned off; the rule panel still lists matching chests. " + capability.Witness);
            return supported;
        }
    }

    /// <summary>
    /// Starts a new search for chests taking <paramref name="focus"/> (a category, or "") or any of <paramref name="wanted"/>
    /// (at most <see cref="MaximumItems"/>). The same request while it is still on only holds it again, unless
    /// <paramref name="rescan"/> says a rule changed since (P6-13).
    /// </summary>
    internal void Request(string focus, IReadOnlyList<string> wanted, bool rescan = false)
    {
        focus ??= ""; wanted ??= Array.Empty<string>();
        var asked = focus + "\n" + string.Join("\n", wanted.Take(MaximumItems));
        if (!rescan && asked == key && (Scanning || linger.On(Time.unscaledTime))) { linger.Hold(); return; }
        Clear();
        var player = Player.m_localPlayer; var settings = Plugin.Instance?.Settings;
        if (!player || settings == null || focus.Length == 0 && wanted.Count == 0) return;
        category = focus; key = asked; items.AddRange(wanted.Take(MaximumItems));
        // One scene query per request; rules are then read a few chests per frame in Update.
        var started = UiCost.Start();
        queue.AddRange(CleanupRunner.Nearby(player!, settings.EffectiveCleanupRange).Take(256));
        UiCost.Stop("highlight scene query", started);
        queue.Reverse(); // Popped from the end: nearest first.
        linger.Hold();
        Revision++;
    }

    /// <summary>The chest closed: the lit chests stay on for <paramref name="seconds"/> (0: off now).</summary>
    internal void Release(float seconds) => linger.Release(Time.unscaledTime, seconds);

    internal void Clear()
    {
        Expire();
        queue.Clear(); Found.Clear(); items.Clear(); category = key = ""; linger.End();
        Revision++;
    }

    // The colours go; what was found stays listed until the next request. Painted chests are reset even after a
    // failure turned painting off, each on its own, so YAVSOS never leaves its colour behind.
    private void Expire()
    {
        for (var i = 0; i < lit.Count; i++) Reset(lit[i].Target);
        lit.Clear();
    }

    internal bool Lit => lit.Count != 0;

    internal void Update()
    {
        if (lit.Count == 0 && queue.Count == 0) return;
        var now = Time.unscaledTime; var plugin = Plugin.Instance;
        if (!linger.On(now) || !Player.m_localPlayer || plugin == null) { queue.Clear(); Expire(); Revision++; return; }
        for (var i = 0; i < ChecksPerFrame && queue.Count != 0; i++)
        {
            var chest = queue[queue.Count - 1]; queue.RemoveAt(queue.Count - 1);
            if (queue.Count == 0) Revision++; // Done looking: the status says so.
            if (!chest || !Takes(plugin, chest)) continue;
            Found.Add(chest);
            if (lit.Count < MaximumLit && Supported) { var target = Target(chest); lit.Add((chest, target)); Paint(target); }
            Revision++;
        }
        if (now < nextRefresh || !supported) return;
        // Re-applied now and then: the hammer's own support highlight resets the same values when it ends.
        nextRefresh = now + Refresh;
        for (var i = lit.Count - 1; i >= 0 && supported; i--)
            if (!lit[i].Chest || !lit[i].Target) lit.RemoveAt(i); else Paint(lit[i].Target);
    }

    private bool Takes(Plugin plugin, Container chest)
    {
        var payload = ChestRuleStore.Payload(chest);
        if (payload.Length == 0) return false;
        var taxonomy = plugin.RuleTaxonomy; var answers = plugin.CategoryAnswers;
        // P8-18 (Q14): an item search asks the rule itself (exact items, never-lists, filters), with filters on what a
        // stack is set aside, since a search has no stack; a category search compares categories.
        if (items.Count != 0 && answers != null)
        {
            foreach (var item in items) if (plugin.RuleEvaluator.CouldAccept(payload, item, taxonomy, answers)) return true;
            return false;
        }
        var rules = plugin.RuleEvaluator.Read(payload, taxonomy).Rules;
        return RuleText.TakesCategory(rules, taxonomy, category);
    }

    // The piece root, so every renderer of the chest model is coloured, as WearNTear.Highlight does.
    private static GameObject Target(Container chest)
    {
        var piece = chest.GetComponentInParent<WearNTear>();
        return piece ? piece!.gameObject : chest.gameObject;
    }

    private void Paint(GameObject target)
    {
        try
        {
            MaterialMan.instance.SetValue(target, ShaderProps._EmissionColor, Gold * 0.4f);
            MaterialMan.instance.SetValue(target, ShaderProps._Color, Gold);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Fail(error); }
    }

    private void Reset(GameObject target)
    {
        if (!target || !MaterialMan.instance) return;
        try
        {
            MaterialMan.instance.ResetValue(target, ShaderProps._Color);
            MaterialMan.instance.ResetValue(target, ShaderProps._EmissionColor);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (!supported) return;
        supported = false;
        log.LogWarning($"Chest highlight turned off after {error.GetType().Name}: {error.Message}; the rule panel still lists matching chests.");
        Expire(); // Best effort: undo what was already painted; further failures are silent.
    }

    /// <summary>D101 (C7): "Lit up 3 chests", "No chest within 10 m takes this.", or nothing while the first chests are checked.</summary>
    internal string Status() =>
        HighlightText.Status(Active, Found.Count, Lit, Scanning, Plugin.Instance?.Settings.EffectiveCleanupRange ?? 0, category.Length == 0 && items.Count > 1);

    /// <summary>The chests found, nearest first, named by <paramref name="name"/>, for the status line's tooltip.</summary>
    internal IReadOnlyList<string> Chests(Func<Container, string> name) => Found.Where(x => x).Select(name).ToList();
}
