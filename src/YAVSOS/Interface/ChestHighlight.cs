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
/// P6-06 (D062 Q4): lights up nearby chests (within the player's cleanup range) for a searched category or items.
/// D122: chests that hold a match, ruled or not, in the player's <c>HoldsColor</c>; with the takes toggle on, also the
/// other chests whose rule would take one, in <c>TakesColor</c>; each lit chest keeps why, for its label. P8-18 (D100): on while the search is active, then for the player's <c>HighlightSecondsAfterClose</c> once
/// the chest closes (<see cref="HighlightLinger"/>); the Items tab's search asks for the items its grid shows. VNEI 0.17.6 has no chest highlight or find-item API to adopt (decompiled
/// 2026-10-20), so this is YAVSOS's own: a uniform colour through the game's <c>MaterialMan</c> property blocks, the
/// mechanism of vanilla <c>WearNTear.Highlight</c>, set and reset by YAVSOS. Visual only: no ZDO, inventory or rule
/// write. Rules are checked a few chests per frame, and at most <see cref="MaximumLit"/> chests are lit. Unity game
/// thread only.
/// </summary>
internal sealed class ChestHighlight
{
    internal const float Refresh = 0.5f, RecheckSeconds = 1f;
    internal const int MaximumLit = 32, ChecksPerFrame = 16, MaximumItems = 64;
    private static readonly Color Gold = new Color(1f, 0.78f, 0.25f);
    /// <summary>Holds and Takes come from the search (D122); Stays only from cleanup (D123, <see cref="ChestArrivals"/>).</summary>
    internal enum Kind { Holds, Takes, Stays }
    private readonly ManualLogSource log;
    private bool probed, supported;
    private readonly List<Container> queue = new List<Container>();
    private readonly List<(Container Chest, GameObject Target, Kind Kind)> lit = new List<(Container, GameObject, Kind)>();
    private string category = "", key = "", holdsCategory = "", subject = "";
    private bool withTakes;
    private readonly List<string> items = new List<string>();
    private readonly HashSet<string> wantedSet = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> inCategory = new Dictionary<string, bool>(StringComparer.Ordinal);
    private readonly Dictionary<Container, (Kind Kind, string Why)> why = new Dictionary<Container, (Kind, string)>();
    private readonly HighlightLinger linger = new HighlightLinger();
    private float nextRefresh, nextRecheck;
    /// <summary>The chests found so far for the current request, nearest first, with what lit them; read by the panel.</summary>
    internal readonly List<(Container Chest, Kind Kind)> Found = new List<(Container, Kind)>();
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
    /// Starts a new search for chests that hold or take <paramref name="focus"/> (a category, or "") or any of
    /// <paramref name="wanted"/> (at most <see cref="MaximumItems"/>). D122: <paramref name="holdsOf"/> is a category whose
    /// every member counts as held (a category search; its first members alone may miss some); <paramref name="takes"/>
    /// also lights the chests whose rule would take a match; <paramref name="about"/> names the search for labels. The same
    /// request while it is still on only holds it again, unless <paramref name="rescan"/> says a rule changed since (P6-13).
    /// </summary>
    internal void Request(string focus, IReadOnlyList<string> wanted, bool rescan = false, string holdsOf = "", bool takes = false, string about = "")
    {
        focus ??= ""; wanted ??= Array.Empty<string>(); holdsOf ??= "";
        var asked = focus + "\n" + holdsOf + "\n" + takes + "\n" + about + "\n" + string.Join("\n", wanted.Take(MaximumItems));
        if (!rescan && asked == key && (Scanning || linger.On(Time.unscaledTime))) { linger.Hold(); return; }
        Clear();
        var player = Player.m_localPlayer; var settings = Plugin.Instance?.Settings;
        if (!player || settings == null || focus.Length == 0 && wanted.Count == 0) return;
        category = focus; key = asked; holdsCategory = holdsOf; withTakes = takes; subject = about ?? "";
        items.AddRange(wanted.Take(MaximumItems)); wantedSet.UnionWith(items);
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
        queue.Clear(); Found.Clear(); items.Clear(); wantedSet.Clear(); inCategory.Clear(); why.Clear();
        category = key = holdsCategory = subject = ""; withTakes = false; linger.End();
        Revision++;
    }

    // The colours go; what was found stays listed until the next request. Painted chests are reset even after a
    // failure turned painting off, each on its own, so YAVSOS never leaves its colour behind.
    private void Expire()
    {
        var arrivals = Plugin.Instance?.Arrivals;
        // A chest the arrival glow is colouring is left to it; it resets the chest when its glow ends.
        for (var i = 0; i < lit.Count; i++) if (arrivals?.Owns(lit[i].Target) != true) Reset(lit[i].Target);
        why.Clear();
        lit.Clear();
    }

    internal bool Lit => lit.Count != 0;

    internal void Update()
    {
        if (lit.Count == 0 && queue.Count == 0 && !Active) return; // An active search with nothing lit still rechecks (D122).
        var now = Time.unscaledTime; var plugin = Plugin.Instance;
        if (!linger.On(now) || !Player.m_localPlayer || plugin == null) { queue.Clear(); Expire(); Revision++; return; }
        for (var i = 0; i < ChecksPerFrame && queue.Count != 0; i++)
        {
            var chest = queue[queue.Count - 1]; queue.RemoveAt(queue.Count - 1);
            if (queue.Count == 0) Revision++; // Done looking: the status says so.
            if (chest) Judge(plugin, chest);
        }
        // D122: contents change while the search is on (a drag, a stash, another player), so the chests found are
        // judged again now and then, and the open chest too; only what changed is repainted.
        if (Active && queue.Count == 0 && now >= nextRecheck)
        {
            nextRecheck = now + RecheckSeconds;
            for (var i = Found.Count - 1; i >= 0; i--) if (i < Found.Count && Found[i].Chest) Judge(plugin, Found[i].Chest);
            var open = plugin.RuleEditor?.Chest; // The chest open in the inventory screen.
            if (open && InRange(open!)) Judge(plugin, open!);
        }
        if (now < nextRefresh || !supported) return;
        // Re-applied now and then: the hammer's own support highlight resets the same values when it ends.
        nextRefresh = now + Refresh;
        for (var i = lit.Count - 1; i >= 0 && supported; i--)
            if (!lit[i].Chest || !lit[i].Target) lit.RemoveAt(i); else PaintUnlessGlowing(lit[i].Target, lit[i].Kind);
    }

    /// <summary>D122: items arrived in the chest (<see cref="ChestArrivals"/>); a search on now judges it again next frame.</summary>
    internal void Recheck(Container chest)
    {
        if (Active && chest && !queue.Contains(chest) && InRange(chest)) queue.Add(chest);
    }

    private bool InRange(Container chest)
    {
        var player = Player.m_localPlayer; var settings = Plugin.Instance?.Settings;
        if (!player || settings == null) return false;
        var range = settings.EffectiveCleanupRange;
        return (chest.transform.position - player.transform.position).sqrMagnitude <= range * range &&
               ChestRuleStore.Eligibility(chest) == RuleEligibility.Eligible && ChestRuleStore.HasAccess(chest, player);
    }

    // D122: judges one chest now and brings what was found, its label and its colour in line; a chest that no longer
    // matches goes dark. Only a change is repainted.
    private void Judge(Plugin plugin, Container chest)
    {
        (Kind Kind, string Why)? now = null;
        if (Holds(plugin, chest) is { } held) now = (Kind.Holds, held);
        else if (withTakes && Takes(plugin, chest)) now = (Kind.Takes, HighlightText.Takes(subject));
        var found = Found.FindIndex(x => ReferenceEquals(x.Chest, chest));
        var index = lit.FindIndex(x => ReferenceEquals(x.Chest, chest));
        if (now == null)
        {
            if (found < 0) return;
            Found.RemoveAt(found); why.Remove(chest);
            if (index >= 0)
            {
                var target = lit[index].Target; lit.RemoveAt(index);
                if (Plugin.Instance?.Arrivals?.Owns(target) != true) Reset(target);
            }
            Revision++;
            return;
        }
        var (kind, reason) = now.Value;
        if (found >= 0 && Found[found].Kind == kind && why.TryGetValue(chest, out var known) && known.Why == reason) return;
        if (found >= 0) Found[found] = (chest, kind); else Found.Add((chest, kind));
        why[chest] = (kind, reason);
        if (index >= 0) { lit[index] = (chest, lit[index].Target, kind); PaintUnlessGlowing(lit[index].Target, kind); }
        else if (lit.Count < MaximumLit && Supported) { var target = Target(chest); lit.Add((chest, target, kind)); PaintUnlessGlowing(target, kind); }
        Revision++;
    }

    // The arrival glow (D121, D123) owns a chest while it lasts; painting it here would flash the search colour over it.
    private void PaintUnlessGlowing(GameObject target, Kind kind)
    {
        if (Plugin.Instance?.Arrivals?.Owns(target) == true) return;
        Paint(target, kind);
    }

    // D122: the label line when the chest holds a match ("holds Copper ×57 +1 more"), else null. Read from the chest's
    // inventory as the game has loaded it; a category search counts every member of the category.
    private string? Holds(Plugin plugin, Container chest)
    {
        var inventory = chest.GetInventory();
        if (inventory == null) return null;
        string? first = null; var count = 0; HashSet<string>? kinds = null;
        foreach (var item in inventory.GetAllItems())
        {
            var prefab = item?.m_dropPrefab ? item!.m_dropPrefab.name : null;
            if (prefab == null || !wantedSet.Contains(prefab) && !Member(plugin, prefab)) continue;
            first ??= prefab;
            if (prefab == first) count += item!.m_stack; else (kinds ??= new HashSet<string>(StringComparer.Ordinal)).Add(prefab);
        }
        return first == null ? null : HighlightText.Holds(CleanupRunner.Name(first), count, kinds?.Count ?? 0);
    }

    private bool Member(Plugin plugin, string prefab)
    {
        if (holdsCategory.Length == 0) return false;
        if (inCategory.TryGetValue(prefab, out var known)) return known;
        var result = plugin.CategoryAnswers?.Resolve(prefab);
        return inCategory[prefab] = result != null && result.Memberships.Any(x => plugin.RuleTaxonomy.IsAncestorOrSelf(holdsCategory, x.Category));
    }

    /// <summary>D122: why the chest is lit by the current search ("holds Copper ×57", "takes Copper"), for its label; null when it isn't.</summary>
    internal (Kind Kind, string Why)? Reason(Container chest) => Active && why.TryGetValue(chest, out var reason) ? reason : null;

    /// <summary>D122: the colour of a kind, from the player's settings, without alpha.</summary>
    internal static Color ColorOf(Kind kind)
    {
        var settings = Plugin.Instance?.Settings;
        var color = settings == null ? Gold : kind switch { Kind.Takes => settings.TakesColor.Value, Kind.Stays => settings.StaysColor.Value, _ => settings.HoldsColor.Value };
        color.a = 1; return color;
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

    /// <summary>
    /// D121: colours a chest items just arrived in (<see cref="ChestArrivals"/>), the same gold with the arrival glow's
    /// emission <paramref name="gain"/> and <paramref name="tint"/> (1 = full gold, 0 = the chest's own colour). False
    /// when chests can't be coloured.
    /// </summary>
    internal bool PaintArrival(GameObject target, float gain, float tint, Kind kind = Kind.Holds)
    {
        if (!Supported || !target) return false;
        if (!MaterialMan.instance) return true; // Leaving the world: nothing to colour, and not a failure.
        try
        {
            var color = ColorOf(kind); // D122: the chests a move lands in hold the item; D123: Stays for what cleanup left.
            MaterialMan.instance.SetValue(target, ShaderProps._EmissionColor, color * gain);
            MaterialMan.instance.SetValue(target, ShaderProps._Color, Color.Lerp(Color.white, color, tint));
            return true;
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Fail(error); return false; }
    }

    /// <summary>D121: an arrival glow ended; a chest the search still lights keeps the search's gold.</summary>
    internal void ResetArrival(GameObject target)
    {
        if (!target) return;
        var index = supported ? lit.FindIndex(x => x.Target == target) : -1;
        if (index >= 0) Paint(target, lit[index].Kind); else Reset(target);
    }

    // The piece root, so every renderer of the chest model is coloured, as WearNTear.Highlight does.
    internal static GameObject Target(Container chest)
    {
        var piece = chest.GetComponentInParent<WearNTear>();
        return piece ? piece!.gameObject : chest.gameObject;
    }

    private void Paint(GameObject target, Kind kind)
    {
        if (!target || !MaterialMan.instance) return; // Leaving the world: not a failure.
        try
        {
            var color = ColorOf(kind);
            MaterialMan.instance.SetValue(target, ShaderProps._EmissionColor, color * 0.4f);
            MaterialMan.instance.SetValue(target, ShaderProps._Color, color);
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

    /// <summary>D101 (C7), D122: "2 hold this · 1 more takes it", "No chest within 10 m holds this.", or nothing while the first chests are checked.</summary>
    internal string Status() =>
        HighlightText.Status(Active, Found.Count(x => x.Kind == Kind.Holds), Found.Count(x => x.Kind == Kind.Takes), Scanning,
            Plugin.Instance?.Settings.EffectiveCleanupRange ?? 0, category.Length == 0 && items.Count > 1, withTakes);

    /// <summary>The chests found of one kind, nearest first, named by <paramref name="name"/>, for the status line's tooltip.</summary>
    internal IReadOnlyList<string> Chests(Kind kind, Func<Container, string> name) => Found.Where(x => x.Chest && x.Kind == kind).Select(x => name(x.Chest)).ToList();
}
