using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YAVSOS.Configuration;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Integrations;
using YAVSOS.Interface;
using YAVSOS.Transfers;

namespace YAVSOS.Cleanup;

/// <summary>
/// P4-02: runs one area cleanup for the local player. Planning is <see cref="CleanupPlanner"/>; every move is then
/// rechecked and written in one game-thread segment as the verified owner of both chests, and verified by recapture
/// (conservation, identity, owner, session). Results are moved, left in place, refused or unconfirmed; nothing is
/// retried, replayed or refunded (D041). A few moves run per frame. A move with one side in another player's open chest
/// goes through MultiUserChest when it is present (P4-03): YAVSOS checks access, range and both rules, calls vanilla
/// <c>MoveItemToThis</c> for MultiUserChest to intercept, and counts it as moved only once the item shows up in the
/// other chest (<see cref="DelegatedMoveObserver"/>). Unity game thread only.
/// </summary>
internal sealed class CleanupRunner : MoveRunner<CleanupMove, CleanupRunner.Run>
{
    internal static readonly AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData>? DragItem = Bind();
    internal static AccessTools.FieldRef<InventoryGui, ItemDrop.ItemData>? Bind()
    { try { return AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem"); } catch { return null; } }

    private readonly PluginSettings settings;
#if YAVSOS_LAB
    /// <summary>Lab fixture (P4-05a): while held, moves run only as <see cref="DiagnosticSteps"/> allows, so a test can act mid-run.</summary>
    internal static bool DiagnosticHold;
    internal static int DiagnosticSteps;
#endif

    internal sealed class Run : MoveRun<CleanupMove>
    {
        internal CleanupPlan Plan = null!;
        internal override IReadOnlyList<CleanupMove> Moves => Plan.Moves;
    }

    internal CleanupRunner(PluginSettings settings, ManualLogSource log, RunResults results) : base(log, results) { this.settings = settings; }

    protected override float PollSeconds => 0.5f;
    protected override string Prefab(CleanupMove move) => move.Identity.Prefab;
    protected override CleanupRoute Route(CleanupMove move) => move.Route;
    protected override string PolicyStamp(Run run) => run.Plan.PolicyStamp;
#if YAVSOS_LAB
    protected override bool HoldNext(Run run)
    {
        if (!DiagnosticHold) return false;
        if (DiagnosticSteps <= 0) return true;
        DiagnosticSteps--; return false;
    }
#endif
    protected override bool PendingCurrent(Run run, MovePending<CleanupMove> pending) => pending.Source && pending.Destination;
    protected override (int Source, int Destination, int OtherDestination) Observe(Run run, MovePending<CleanupMove> pending) =>
        MoveVerification.Counts(Capture(run.Adapter, pending.Source!, pending.Move.SourceId).Snapshot,
            Capture(run.Adapter, pending.Destination, pending.Move.DestinationId).Snapshot, pending.Key);
    protected override string MovedViaMuc(CleanupMove move, DelegatedMoveObserver observer) =>
        $"Cleanup moved {observer.MovedUnits} {Name(move.Identity.Prefab)} via MultiUserChest ({move.Route}, {observer.Detail}).";

    /// <summary>Plans an area cleanup around the local player; returns the message shown to them.</summary>
    internal string Start(float? radius = null)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        if (plugin == null || !player || player.IsDead() || !ZNet.instance) return Say("Cleanup needs a player in the world.");
        // A01: one run at a time across both runners, as the stash already refuses during a cleanup; a MultiUserChest
        // request the stash holds would otherwise be observed against chests cleanup is changing.
        if (active != null || plugin.Stash.Running) return Say("A stash or cleanup is already running.");
        if (!settings.AllowCleanup.Value) return Say("Cleanup is turned off on this server.");
        if (!NativeInventoryProbe.Supported) return Say("Cleanup is unavailable: " + NativeInventoryProbe.Status + ".");
        // D056: with Azu present, its exclusions must be readable; cleanup never bypasses them.
        if (AzuExclusions.State == AzuExclusionState.Unavailable) return Say(AzuUnreadable);
        var answers = plugin.CategoryAnswers;
        if (answers == null || !answers.CategoriesReady) return Say("YAVSOS categories are still loading. Try cleanup again shortly.");
        var range = Math.Min(radius ?? settings.EffectiveCleanupRange, settings.MaximumRange.Value);
        var run = new Run { Session = ZNet.instance, Radius = range, Adapter = new GameInventorySnapshotAdapter(), Chests = new Dictionary<string, Container>(StringComparer.Ordinal) };
        var input = Gather(plugin, answers, player, run, out var unreadable, out var mucSource);
        var plan = CleanupPlanner.Plan(input, plugin.RuleEvaluator);
        if (plan.CategoriesPending) { run.Adapter.Dispose(); return Say("YAVSOS categories are still loading. Try cleanup again shortly."); }
        run.Plan = plan;
        foreach (var rest in plan.Remainders)
        {
            run.Report.Remain(rest.Reason, rest.Identity.Prefab);
            if (rest.Reason == CleanupReason.AzuExcluded && rest.DestinationId != null && run.Chests.TryGetValue(rest.DestinationId, out var excluded))
                log.LogInfo($"Cleanup: AzuAutoStore's config excludes {Name(rest.Identity.Prefab)} from {Localize(excluded.m_name)} chests; left in place.");
        }
        for (var i = 0; i < unreadable; i++) run.Report.Remain(CleanupReason.Unsupported);
        for (var i = 0; i < plan.SkippedChests; i++) run.Report.Remain(CleanupReason.ChestLimit);
        log.LogInfo($"Cleanup planned {plan.Moves.Count} moves over {input.Chests.Count} containers within {range:0.#} m in {run.Clock.Elapsed.TotalMilliseconds:0.0} ms" +
            (input.MucAvailable ? $"; other players' chests through {mucSource}." : "; no MultiUserChest, other players' chests are skipped."));
        active = run;
        if (plan.Moves.Count == 0) { Finish(); return LastSummary; }
        return Started;
    }

    /// <summary>What <see cref="Start"/> returns when a run began; the rule panel says nothing then (D101).</summary>
    internal const string Started = "Cleanup started.";

    // The planner's live inputs, shared by Start and Explain (P6-05), so an explanation sees what a run would see.
    private CleanupInput Gather(Plugin plugin, ICategoryAnswers answers, Player player, Run run, out int unreadable, out string mucSource)
    {
        var chests = new List<CleanupChest>(); unreadable = 0;
        var held = new List<Guid>();
        var drag = DragItem != null && InventoryGui.instance ? DragItem(InventoryGui.instance) : null;
        // D086 D9: the nearest chests (Nearby is nearest first); the report says how many were in range.
        var near = Nearby(player, run.Radius).ToList();
        var limit = settings.EffectiveMaximumChests; // D095
#if YAVSOS_LAB
        if (FaultLab.ChestLimit > 0) limit = Math.Min(limit, FaultLab.ChestLimit); // D088: lab-only lower limit.
#endif
        run.Report.Range(near.Count, Math.Min(near.Count, limit));
        foreach (var container in near.Take(limit))
            if (!GatherChest(run, container, chests, held, drag)) unreadable++;
        return Input(plugin, answers, run, chests, held, out mucSource);
    }

    // One chest of Gather: false when it could not be read (logged); a chest already gathered is skipped.
    private bool GatherChest(Run run, Container container, List<CleanupChest> chests, List<Guid> held, ItemDrop.ItemData? drag)
    {
        var view = ChestRuleStore.View(container)!; var zdo = view.GetZDO();
        var id = zdo.m_uid.ToString();
        if (run.Chests.ContainsKey(id)) return true;
        try
        {
            var capture = Capture(run.Adapter, container, id);
            var player = Player.m_localPlayer;
            chests.Add(new CleanupChest(id, ChestRuleStore.Payload(container), capture.Snapshot, view.IsOwner(), ChestRuleStore.CleanupOptOut(container),
                player ? Vector3.Distance(player.transform.position, container.transform.position) : 0));
            run.Chests.Add(id, container);
            if (drag != null && run.Adapter.TryHandle(container.GetInventory(), drag, out var handle)) held.Add(handle);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogInfo($"Cleanup skipped container {container.m_name} at {container.transform.position}: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    // D088, D087: why cleanup is off when AzuAutoStore's exclusions can't be read at all.
    private const string AzuUnreadable = "Cleanup is off: AzuAutoStore changed, and YAVSOS can't read which chests it keeps items out of. " +
        "AzuAutoStore and chest rules keep working.";

    private CleanupInput Input(Plugin plugin, ICategoryAnswers answers, Run run, List<CleanupChest> chests, List<Guid> held, out string mucSource)
    {
        var found = run.Chests;
        // Active or Limited (D088): unknown counts as excluded, so nothing moves where Azu might keep it out.
        var azu = AzuExclusions.State is AzuExclusionState.Active or AzuExclusionState.Limited
            ? (Func<string, TransferItemIdentity, bool>)((chest, item) => AzuExclusions.Allows(found[chest], item.Prefab) == true) : null;
        var muc = MucRoute.Available(out mucSource);
        // P6-07b: each stack's own rarity and condition, read once per stack for the planner's instance filters.
        var adapter = run.Adapter;
        var facets = new Dictionary<Guid, IReadOnlyList<FacetEvidence>>(); Dictionary<Guid, Inventory>? holders = null;
        IReadOnlyList<FacetEvidence> Facets(TransferStack stack)
        {
            if (facets.TryGetValue(stack.Handle, out var known)) return known;
            if (holders == null)
            {
                holders = new Dictionary<Guid, Inventory>();
                foreach (var chest in chests) { var inventory = found[chest.Id].GetInventory(); foreach (var row in chest.Inventory.Stacks) holders[row.Handle] = inventory; }
            }
            known = holders.TryGetValue(stack.Handle, out var holder) && adapter.TryItem(holder, stack.Handle, out var item)
                ? plugin.InstanceFacets(item, answers) : Plugin.UnreadableFacets(stack.Identity.Prefab, answers);
            facets.Add(stack.Handle, known);
            return known;
        }
        return new CleanupInput(plugin.RuleTaxonomy, answers, chests, plugin.CategoryMemberCount, mucAvailable: muc, azuAllows: azu, held: held, instanceFacets: Facets);
    }

    /// <summary>
    /// P6-05: what "Clean up nearby" would do with stack <paramref name="item"/> of chest <paramref name="source"/> now,
    /// from the inputs a run gathers (<see cref="PlacementExplainer.Cleanup"/>). Read only. Advance one step per frame;
    /// <paramref name="done"/> gets the lines, or null with a note saying why it cannot be explained (the note may also
    /// qualify an answer). Chests are read a few per frame, so the answer may be a few frames older than a run's.
    /// </summary>
    internal IEnumerator<bool> Explain(Container source, ItemDrop.ItemData item, Action<string?, string> done)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        var note = "";
        if (plugin == null || !player || player.IsDead() || !ZNet.instance || !source) { done(null, note); yield break; }
        if (!NativeInventoryProbe.Supported) { done(null, "Cleanup is unavailable: " + NativeInventoryProbe.Status + "."); yield break; }
        if (AzuExclusions.State == AzuExclusionState.Unavailable) { done(null, AzuUnreadable); yield break; }
        var answers = plugin.CategoryAnswers;
        if (answers == null) { done(null, "YAVSOS categories are still loading."); yield break; }
        if (!settings.AllowCleanup.Value) note = "Cleanup is turned off on this server; this is what it would do.";
        var range = Math.Min(settings.EffectiveCleanupRange, settings.MaximumRange.Value);
        var run = new Run { Session = ZNet.instance, Radius = range, Adapter = new GameInventorySnapshotAdapter(), Chests = new Dictionary<string, Container>(StringComparer.Ordinal) };
        try
        {
            // Spread over frames (P6-11 cost check): one scene search, then ExplainBatch chests per step, then the plan.
            var timed = UiCost.Start();
            var scene = Scene();
            var near = Nearby(player, range, scene).Take(settings.EffectiveMaximumChests).ToList();
            UiCost.Stop("explain scan", timed);
            var chests = new List<CleanupChest>(); var held = new List<Guid>();
            var drag = DragItem != null && InventoryGui.instance ? DragItem(InventoryGui.instance) : null;
            for (var at = 0; at < near.Count; at++)
            {
                if (at != 0 && at % ExplainBatch == 0) yield return true;
                if (!player || !source) { done(null, ""); yield break; }
                timed = UiCost.Start();
                if (near[at]) GatherChest(run, near[at], chests, held, drag); // One removed since the search is left out.
                UiCost.Stop("explain chest", timed);
            }
            yield return true;
            if (!player || player.IsDead() || !source) { done(null, ""); yield break; }
            timed = UiCost.Start();
            var input = Input(plugin, answers, run, chests, held, out _);
            UiCost.Stop("explain input", timed);
            yield return true;
            if (!player || player.IsDead() || !source) { done(null, ""); yield break; }
            var id = ChestRuleStore.View(source)?.GetZDO()?.m_uid.ToString();
            if (id == null || !run.Chests.ContainsKey(id)) { done(null, "This chest is beyond cleanup range or can't be cleaned up."); yield break; }
            if (!run.Adapter.TryHandle(source.GetInventory(), item, out var handle) ||
                input.Chests.First(x => x.Id == id).Inventory.Stacks.All(x => x.Handle != handle))
            { done(null, "That item cannot be moved by cleanup."); yield break; }
            // One sentence (R4 review): no per-chest rows and no search for chests out of range.
            var names = ChestNamer(player, source, run.Chests);
            timed = UiCost.Start();
            var answer = PlacementExplainer.Cleanup(input, id, handle, plugin.RuleEvaluator, names, Name, openName: RuleControls.OpenValueName, detail: false);
            UiCost.Stop("explain plan", timed);
            done(ExplanationView.Tooltip(answer, true, names), note);
        }
        finally { run.Adapter.Dispose(); }
    }

    /// <summary>Chests captured per explanation step: a few per frame keeps a large base from stalling the frame.</summary>
    internal const int ExplainBatch = 8;

    /// <summary>Every container in the scene, searched once per explanation.</summary>
    internal static Container[] Scene() => ContainerRegistry.Scene();

    /// <summary>"this chest" for the open one, otherwise the chest's name and distance ("Chest 6 m away"), since many chests share a name.</summary>
    internal static Func<string, string> ChestNamer(Player player, Container? open, IReadOnlyDictionary<string, Container> chests)
    {
        var center = player.transform.position;
        return id =>
        {
            if (!chests.TryGetValue(id, out var container) || !container) return id;
            if (ReferenceEquals(container, open)) return "this chest";
            return Localize(container.m_name) + " " + Vector3.Distance(center, container.transform.position).ToString("0") + " m away";
        };
    }

    protected override void ExecuteChecked(Run run, CleanupMove move)
    {
        // While MultiUserChest holds a request, other moves touching its chests would blur what it is observed to have done.
        if (run.Delegated.Any(x => x.Move.SourceId == move.SourceId || x.Move.DestinationId == move.SourceId ||
                                   x.Move.SourceId == move.DestinationId || x.Move.DestinationId == move.DestinationId))
        { run.Report.Remain(CleanupReason.MucBusy, move.Identity.Prefab); return; }
        if (run.PolicyChanged) { run.Report.Refuse(TransferRefusal.PolicyChanged, move.Identity.Prefab); return; }
        var refusal = Recheck(run, move, out var source, out var destination);
        if (refusal == TransferRefusal.PolicyChanged) { run.PolicyChanged = true; run.Report.Refuse(refusal, move.Identity.Prefab); return; }
        if (refusal == TransferRefusal.Protected) { run.Report.Remain(CleanupReason.AzuExcluded, move.Identity.Prefab); return; }
        if (refusal != TransferRefusal.None) { run.Report.Refuse(refusal, move.Identity.Prefab); return; }
        GameInventoryCapture before, beforeDestination;
        try { before = Capture(run.Adapter, source!, move.SourceId); beforeDestination = Capture(run.Adapter, destination!, move.DestinationId); }
        catch (Exception error) when (error is not OutOfMemoryException) { run.Report.Refuse(TransferRefusal.UnsupportedCapability, move.Identity.Prefab); Log(move, "capture failed: " + error.GetType().Name); return; }
        var sourceInventory = source!.GetInventory(); var destinationInventory = destination!.GetInventory();
        if (!run.Adapter.TryResolve(sourceInventory, before.Snapshot.Stamp.InstanceGeneration, move.Handle, out var item) || item == null ||
            !before.Snapshot.Stacks.Any(x => x.Handle == move.Handle && x.Identity.Equals(move.Identity)))
        { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        var drag = DragItem != null && InventoryGui.instance ? DragItem(InventoryGui.instance) : null;
        if (ReferenceEquals(drag, item)) { run.Report.Remain(CleanupReason.Held, move.Identity.Prefab); return; }
        if (move.Route != CleanupRoute.Direct) { Delegate(run, move, source, destination, before, beforeDestination, item); return; }
        if (!move.TryMergeTarget(before.Snapshot, beforeDestination.Snapshot, out var merge)) { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        var result = WriteDirect(run, move, () => Capture(run.Adapter, source, move.SourceId), () => Capture(run.Adapter, destination, move.DestinationId),
            before, beforeDestination, sourceInventory, destinationInventory, item, move.Handle, move.Identity, move.Units, move.DestinationX, move.DestinationY,
            merge, move.Kind.ToString(), () => (Owner(source), Owner(destination)),
            () => ChestRuleStore.View(source)!.IsOwner() && ChestRuleStore.View(destination)!.IsOwner(), ("source", "destination"));
        if (Report(run, move, result))
            log.LogInfo($"Cleanup moved {result.Units} {Name(move.Identity.Prefab)} from {Localize(source.m_name)} to {Localize(destination.m_name)} ({move.Kind}).");
    }

    // P4-03: MultiUserChest takes the request from here (D043). Its windows (refund after 30 s, slot-only removal) are inherited.
    private void Delegate(Run run, CleanupMove move, Container source, Container destination, GameInventoryCapture before,
        GameInventoryCapture beforeDestination, ItemDrop.ItemData item)
    {
        if (!MucRoute.Available(out _)) { run.Report.Remain(CleanupReason.OtherOwnerNoMuc, move.Identity.Prefab); return; }
        var key = MoveVerification.MergeKey(before.Snapshot, move.Handle, move.Identity);
        var baseline = MoveVerification.Counts(before.Snapshot, beforeDestination.Snapshot, key);
        var units = Math.Min(move.Units, item.m_stack);
        if (units < 1) { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        // The other player's chest must have a live owner to answer, or MultiUserChest does not intercept.
        var remote = move.Route == CleanupRoute.MucAdd ? destination : source;
        if (!ChestRuleStore.View(remote)!.GetZDO().HasOwner()) { run.Report.Refuse(TransferRefusal.OwnerChanged, move.Identity.Prefab); return; }
        // D061 Q8: YAVSOS's copy never sends a request the owner's copy might misread.
        if (!MucRoute.OwnerFormatKnown(ChestRuleStore.View(remote)!.GetZDO().GetOwner())) { run.Report.Remain(CleanupReason.OtherOwnerFormatUnknown, move.Identity.Prefab); return; }
        // Cell (−1, −1) both ways: the owner or MultiUserChest's response picks a free slot, so nothing is ever swapped out
        // of a cell, and if MultiUserChest does not intercept, the game itself refuses the move.
        run.MayHaveWritten = true;
        try { destination.GetInventory().MoveItemToThis(source.GetInventory(), item, units, -1, -1); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { run.Report.Unconfirm(viaMuc: true, move.Identity.Prefab); Log(move, "the MultiUserChest request threw " + error.GetType().Name + "; result unconfirmed"); return; }
        if (move.Route == CleanupRoute.MucAdd)
        {
            var after = Capture(run.Adapter, source, move.SourceId).Snapshot;
            // MultiUserChest takes the item out of the local source at once; if it did not, it never sent a request.
            if (StringComparer.Ordinal.Equals(after.ContentFingerprint, before.Snapshot.ContentFingerprint))
            { run.Report.Refuse(TransferRefusal.Busy, move.Identity.Prefab); Log(move, "MultiUserChest did not take the request"); return; }
        }
        else
        {
            // MultiUserChest only asks the owner; our copy of the other chest and our own chest must not change yet.
            var replica = Capture(run.Adapter, source, move.SourceId).Snapshot;
            var local = Capture(run.Adapter, destination, move.DestinationId).Snapshot;
            if (!StringComparer.Ordinal.Equals(replica.ContentFingerprint, before.Snapshot.ContentFingerprint) ||
                !StringComparer.Ordinal.Equals(local.ContentFingerprint, beforeDestination.Snapshot.ContentFingerprint))
            {
                run.Report.Unconfirm(viaMuc: true, move.Identity.Prefab);
                log.LogError($"Cleanup {Name(move.Identity.Prefab)} x{units} ({move.SourceId} -> {move.DestinationId}): the game changed chests directly instead of sending a MultiUserChest request; result unconfirmed. Not corrected (D041).");
                return;
            }
        }
        run.Delegated.Add(new MovePending<CleanupMove>
        {
            Move = move, Source = source, Destination = destination, Key = key,
            Observer = new DelegatedMoveObserver(move.Route, units, baseline.Source, baseline.Destination, baseline.OtherDestination, Time.realtimeSinceStartupAsDouble)
        });
    }

    private TransferRefusal Recheck(Run run, CleanupMove move, out Container? source, out Container? destination)
    {
        source = destination = null;
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        if (plugin == null || !player || player.IsDead() || !ReferenceEquals(ZNet.instance, run.Session)) return TransferRefusal.Disconnected;
        // A01: the server can turn cleanup off mid-run; the rest of the moves are then refused, as stashing does.
        if (!settings.AllowCleanup.Value) return TransferRefusal.TurnedOff;
        var answers = plugin.CategoryAnswers;
        if (answers == null || !StringComparer.Ordinal.Equals(plugin.RuleTaxonomy.Fingerprint + ":" + answers.Generation, run.Plan.PolicyStamp))
            return TransferRefusal.PolicyChanged;
        if (!run.Chests.TryGetValue(move.SourceId, out source) || !run.Chests.TryGetValue(move.DestinationId, out destination) || !source || !destination ||
            ChestRuleStore.Eligibility(source) != RuleEligibility.Eligible || ChestRuleStore.Eligibility(destination) != RuleEligibility.Eligible)
            return TransferRefusal.InventoryChanged;
        // The planned route fixes which side is local; an ownership change in between refuses the move ("run again").
        var sourceLocal = ChestRuleStore.View(source)!.IsOwner(); var destinationLocal = ChestRuleStore.View(destination)!.IsOwner();
        if (sourceLocal != (move.Route != CleanupRoute.MucRemove) || destinationLocal != (move.Route != CleanupRoute.MucAdd)) return TransferRefusal.OwnerChanged;
        if (!ChestRuleStore.HasAccess(source, player) || !ChestRuleStore.HasAccess(destination, player)) return TransferRefusal.AccessDenied;
        var reach = run.Radius + 2f;
        if ((source.transform.position - player.transform.position).sqrMagnitude > reach * reach ||
            (destination.transform.position - player.transform.position).sqrMagnitude > reach * reach) return TransferRefusal.Expired;
        if (ChestRuleStore.CleanupOptOut(source)) return TransferRefusal.RuleMismatch;
        var prefab = move.Identity.Prefab; var taxonomy = plugin.RuleTaxonomy;
        // The stack's current rarity and condition (P6-07b), as planned; a stack gone since is refused at the write.
        var item = run.Adapter.TryItem(source.GetInventory(), move.Handle, out var live) ? live : null;
        var destinationPayload = ChestRuleStore.Payload(destination); var sourcePayload = ChestRuleStore.Payload(source);
        if (plugin.RuleEvaluator.Evaluate(destinationPayload, prefab, taxonomy, answers, plugin.InstanceFacetsFor(destinationPayload, prefab, item, answers)) != ChestRuleDecision.Accept)
            return TransferRefusal.RuleMismatch;
        var decision = plugin.RuleEvaluator.Evaluate(sourcePayload, prefab, taxonomy, answers, plugin.InstanceFacetsFor(sourcePayload, prefab, item, answers));
        var expected = move.Kind switch { CleanupKind.Rejected => ChestRuleDecision.Reject, CleanupKind.Unruled => ChestRuleDecision.NoRule, _ => ChestRuleDecision.Accept };
        if (decision != expected) return TransferRefusal.RuleMismatch;
        // A re-sort must still go to a strictly better-ranked chest, as the plan decided (D061, D092).
        if (move.Kind == CleanupKind.Resort && !StillBetter(plugin, taxonomy, answers, prefab, source, destination, item)) return TransferRefusal.RuleMismatch;
        // D056; reported by the caller as left in place, not as a refusal.
        if (AzuExclusions.State != AzuExclusionState.Absent && AzuExclusions.Allows(destination, prefab) != true) return TransferRefusal.Protected;
        return TransferRefusal.None;
    }

    private static bool StillBetter(Plugin plugin, ClassificationTaxonomy taxonomy, ICategoryAnswers answers, string prefab, Container source, Container destination,
        ItemDrop.ItemData? live)
    {
        var item = answers.Resolve(prefab);
        var facets = live == null ? Plugin.UnreadableFacets(prefab, answers) : plugin.InstanceFacets(live, answers);
        var sourceRules = plugin.RuleEvaluator.Read(ChestRuleStore.Payload(source), taxonomy).Rules;
        var destinationRules = plugin.RuleEvaluator.Read(ChestRuleStore.Payload(destination), taxonomy).Rules;
        return sourceRules != null && destinationRules != null &&
            CleanupPlanner.ResortStillBetter(item, prefab, sourceRules, destinationRules, facets, taxonomy);
    }

    internal static IEnumerable<Container> Nearby(Player player, float range, Container[]? scene = null)
    {
        var center = player.transform.position; var square = range * range;
        return (scene ?? Scene())
            .Where(x => x && (x.transform.position - center).sqrMagnitude <= square && ChestRuleStore.Eligibility(x) == RuleEligibility.Eligible &&
                        ChestRuleStore.HasAccess(x, player))
            .OrderBy(x => (x.transform.position - center).sqrMagnitude);
    }

    internal static GameInventoryCapture Capture(GameInventorySnapshotAdapter adapter, Container container, string id)
    {
        var zdo = ChestRuleStore.View(container)!.GetZDO();
        // The native ZDO bytes may come from an older save format until the chest next changes, so freshness is
        // the owner, owner revision and the content fingerprint, rechecked at every write.
        return adapter.Capture(container.GetInventory(), id, zdo.GetOwner(), zdo.OwnerRevision, Registration,
            _ => false, Array.Empty<int>(), () => NativeInventoryProbe.Supported);
    }

    // An item's registration is verified when the loaded catalog has it and ObjectDB maps its name to the same prefab.
    internal static string? Registration(ItemDrop.ItemData item)
    {
        var prefab = item.m_dropPrefab; var catalog = Plugin.Instance?.LoadedCatalog;
#if YAVSOS_LAB
        if (prefab && prefab.name == FaultLab.Uncapturable) return null; // D088: lab-only uncapturable stack (D086 D16).
#endif
        if (!prefab || catalog == null || !catalog.Items.ContainsKey(prefab.name) || !ObjectDB.instance ||
            !ReferenceEquals(ObjectDB.instance.GetItemPrefab(prefab.name), prefab)) return null;
        return "catalog:" + prefab.name;
    }

    internal static (long, ushort) Owner(Container container)
    { var zdo = ChestRuleStore.View(container)!.GetZDO(); return (zdo.GetOwner(), zdo.OwnerRevision); }


    protected override void Log(CleanupMove move, string text) => log.LogWarning($"Cleanup {Name(move.Identity.Prefab)} x{move.Units} ({move.SourceId} -> {move.DestinationId}): {text}");

    internal static string Say(string text)
    {
        if (Player.m_localPlayer) Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "YAVSOS: " + text);
        return text;
    }

    internal static string Localize(string token) => Localization.instance != null && !string.IsNullOrEmpty(token) ? Localization.instance.Localize(token) : token;
    internal static string Name(string prefab)
    {
        var drop = ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(prefab)?.GetComponent<ItemDrop>() : null;
        var token = drop ? drop!.m_itemData?.m_shared?.m_name : null;
        return string.IsNullOrEmpty(token) ? prefab : Localize(token!);
    }
}
