using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Bootstrap;
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
/// P5-02 (D061): YAVSOS's own stash, used only when AzuAutoStore is not installed. Planning is
/// <see cref="StashPlanner"/>; each move then goes through the same write boundary as cleanup, with the player's
/// inventory as the source: a fresh recheck, one vanilla <c>MoveItemToThis</c> as the owner of both inventories, and
/// verification by recapture. Another player's chest goes through MultiUserChest and counts only once the item shows
/// up there. While a request is in flight, later moves into that chest, and later moves of the same stack key (every
/// stash move takes from the player's inventory, whose count the observer compares), wait for it, so the observation
/// stays unambiguous (A02). Nothing is retried, replayed or refunded (D041). Unity game thread only.
/// </summary>
internal sealed class StashRunner : MoveRunner<StashMove, StashRunner.Run>
{
    private const string PlayerId = "player";
    private readonly PluginSettings settings;

    internal sealed class Run : MoveRun<StashMove>
    {
        internal StashPlan Plan = null!;
        /// <summary>Stack key of each planned stack, from the inventory the plan was made from (A02).</summary>
        internal Dictionary<Guid, string> Keys = new();
        internal Run() { Report = CleanupReport.ForStash(); }
        internal override IReadOnlyList<StashMove> Moves => Plan.Moves;
    }

    internal StashRunner(PluginSettings settings, ManualLogSource log, RunResults results) : base(log, results) { this.settings = settings; }

    protected override float PollSeconds => 0.25f;
    protected override string Prefab(StashMove move) => move.Identity.Prefab;
    protected override CleanupRoute Route(StashMove move) => move.Route;
    protected override string PolicyStamp(Run run) => run.Plan.PolicyStamp;
    // D093: one MultiUserChest request per chest in flight, and none of the same stack key (A02): a request that would share
    // either is set aside until the pending one ends, and the moves after it go ahead. Direct moves never wait; a same-key
    // one adjusts the pending request's observer instead (ExecuteChecked).
    protected override bool Waits(Run run, StashMove next) =>
        next.Route != CleanupRoute.Direct &&
        run.Delegated.Any(x => x.Move.DestinationId == next.DestinationId || run.Keys.TryGetValue(next.Handle, out var key) && x.Key == key);
#if YAVSOS_LAB
    // Lab fixture (P8-09 M3): the cleanup hold also holds the stash, so a test can act between planning and a write.
    protected override bool HoldNext(Run run)
    {
        if (!CleanupRunner.DiagnosticHold) return false;
        if (CleanupRunner.DiagnosticSteps <= 0) return true;
        CleanupRunner.DiagnosticSteps--; return false;
    }
#endif
    protected override bool PendingCurrent(Run run, MovePending<StashMove> pending) => pending.Destination && Player.m_localPlayer;
    protected override (int Source, int Destination, int OtherDestination) Observe(Run run, MovePending<StashMove> pending) =>
        MoveVerification.Counts(CapturePlayer(run, Player.m_localPlayer!).Snapshot,
            CleanupRunner.Capture(run.Adapter, pending.Destination, pending.Move.DestinationId).Snapshot, pending.Key);
    protected override string MovedViaMuc(StashMove move, DelegatedMoveObserver observer) =>
        $"Stash stored {observer.MovedUnits} {CleanupRunner.Name(move.Identity.Prefab)} via MultiUserChest ({observer.Detail}).";

    // The category answers a stash plans and rechecks with. The lab's "categories still loading" hold (RuleLab) applies to
    // YAVSOS's own stash too (D088), so D086 D4 can be checked at runtime; the release build reads the answers as they are.
    private static ICategoryAnswers? Answers(Plugin plugin)
    {
#if YAVSOS_LAB
        if (AzuAutoStoreIntegration.DiagnosticCategoriesPending) return LoadingAnswers.Instance;
#endif
        return plugin.CategoryAnswers;
    }
#if YAVSOS_LAB
    private sealed class LoadingAnswers : ICategoryAnswers
    {
        internal static readonly LoadingAnswers Instance = new();
        public long Generation => long.MinValue;
        public bool CategoriesReady => false;
        public ClassificationResult? Resolve(string prefab) => null;
    }
#endif

    /// <summary>YAVSOS stashes only when AzuAutoStore is not installed (D042, D061), even if YAVSOS's Azu adapter is off.</summary>
    internal static bool Enabled => !Chainloader.PluginInfos.ContainsKey(AzuAutoStoreIntegration.AzuGuid);

    /// <summary>Plans a stash around the local player; <paramref name="single"/> stashes only that stack. Returns the message shown.</summary>
    internal string Start(float? radius = null, ItemDrop.ItemData? single = null)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        if (!Enabled) return CleanupRunner.Say("Stashing is AzuAutoStore's in this game.");
        if (plugin == null || !player || player.IsDead() || !ZNet.instance) return CleanupRunner.Say("Stashing needs a player in the world.");
        if (active != null || plugin.Cleanup.Running) return CleanupRunner.Say("A stash or cleanup is already running.");
        if (!settings.AllowStashing.Value) return CleanupRunner.Say("Stashing is turned off on this server.");
        if (!NativeInventoryProbe.Supported) return CleanupRunner.Say("Stashing is unavailable: " + NativeInventoryProbe.Status + ".");
        var answers = Answers(plugin);
        if (answers == null) return CleanupRunner.Say("YAVSOS categories are still loading. Try again shortly.");
        var range = Math.Min(radius ?? settings.EffectiveStashRange, settings.MaximumRange.Value);
        var run = new Run { Session = ZNet.instance, Radius = range, Adapter = new GameInventorySnapshotAdapter(), Chests = new Dictionary<string, Container>(StringComparer.Ordinal) };
        var input = Gather(plugin, answers, player!, run, single, out var unreadable, out var mucSource, out var failure);
        if (input == null) { run.Adapter.Dispose(); return CleanupRunner.Say(failure); }
        var plan = StashPlanner.Plan(input, plugin.RuleEvaluator);
        // D086 D4 (M12): while categories load, Always rules and chests already holding the item still take it.
        if (plan.CategoriesPending && plan.Moves.Count == 0)
        { run.Adapter.Dispose(); return CleanupRunner.Say("YAVSOS categories are still loading; only Always items and chests already holding an item take items now. Try again shortly."); }
        run.Plan = plan;
        foreach (var stack in input.Player.Stacks) run.Keys[stack.Handle] = stack.MergeKey;
        foreach (var rest in plan.Remainders) run.Report.Remain(rest.Reason, rest.Identity.Prefab);
        for (var i = 0; i < plan.SkippedChests; i++) run.Report.Remain(CleanupReason.ChestLimit);
        log.LogInfo($"Stash planned {plan.Moves.Count} moves into {input.Chests.Count} containers within {range:0.#} m in {run.Clock.Elapsed.TotalMilliseconds:0.0} ms" +
            (unreadable == 0 ? "" : $" ({unreadable} unreadable skipped)") +
            (input.MucAvailable ? $"; other players' chests through {mucSource}." : "; no MultiUserChest, other players' chests are skipped."));
        active = run;
        if (plan.Moves.Count == 0) { Finish(); return LastSummary; }
        return "Stash started.";
    }

    // The planner's live inputs, shared by Start and Explain (P6-05), so an explanation sees what a run would see.
    // Null with the message to show when the player's inventory cannot be read exactly. The caller disposes the adapter.
    private StashInput? Gather(Plugin plugin, ICategoryAnswers answers, Player player, Run run, ItemDrop.ItemData? single,
        out int unreadable, out string mucSource, out string failure)
    {
        unreadable = 0; mucSource = ""; failure = "";
        GameInventoryCapture own;
        try { own = CapturePlayer(run, player); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogWarning($"Stash could not read your inventory: {error.GetType().Name}: {error.Message}");
            failure = "Stashing is unavailable: your inventory could not be read exactly."; return null;
        }
        // D086 D16, D087: a stack that couldn't be captured stays where it is (reported as not supported); the log names it.
        foreach (var skipped in own.Uncapturable) log.LogWarning("Stash leaves " + skipped + " in your inventory: YAVSOS can't move it without changing it.");
        var held = new List<Guid>(); var only = new List<Guid>();
        var drag = CleanupRunner.DragItem != null && InventoryGui.instance ? CleanupRunner.DragItem(InventoryGui.instance) : null;
        if (drag != null && run.Adapter.TryHandle(player.GetInventory(), drag, out var dragHandle)) held.Add(dragHandle);
        if (single != null)
        {
            if (!run.Adapter.TryHandle(player.GetInventory(), single, out var picked)) { failure = "That item cannot be stashed."; return null; }
            only.Add(picked);
        }
        var chests = new List<StashChest>();
        // D086 D9: the nearest chests (Nearby is nearest first); the report says how many were in range.
        var near = CleanupRunner.Nearby(player, run.Radius).ToList();
        var limit = settings.EffectiveMaximumChests; // D095
#if YAVSOS_LAB
        if (FaultLab.ChestLimit > 0) limit = Math.Min(limit, FaultLab.ChestLimit); // D088: lab-only lower limit.
#endif
        run.Report.Range(near.Count, Math.Min(near.Count, limit));
        foreach (var container in near.Take(limit))
            if (!GatherChest(run, container, chests)) unreadable++;
        return Input(plugin, answers, player, run, own, chests, held, single == null ? null : only, out mucSource);
    }

    // One chest of Gather: false when it could not be read (logged); a chest already gathered is skipped.
    private bool GatherChest(Run run, Container container, List<StashChest> chests)
    {
        var view = ChestRuleStore.View(container)!; var zdo = view.GetZDO();
        var id = zdo.m_uid.ToString();
        if (run.Chests.ContainsKey(id)) return true;
        try
        {
            var capture = CleanupRunner.Capture(run.Adapter, container, id);
            chests.Add(new StashChest(id, ChestRuleStore.Payload(container), capture.Snapshot, view.IsOwner(), MucRoute.OwnerFormatKnown(zdo.GetOwner())));
            run.Chests.Add(id, container);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogInfo($"Stash skipped container {container.m_name} at {container.transform.position}: {error.GetType().Name}: {error.Message}");
            return false;
        }
    }

    private StashInput Input(Plugin plugin, ICategoryAnswers answers, Player player, Run run, GameInventoryCapture own, List<StashChest> chests,
        List<Guid> held, List<Guid>? only, out string mucSource)
    {
        var muc = MucRoute.Available(out mucSource);
        var inventory = player.GetInventory(); var adapter = run.Adapter;
        // P6-07b: each stack's own rarity and condition, read once per stack for the planner's instance filters.
        var facets = new Dictionary<Guid, IReadOnlyList<FacetEvidence>>();
        IReadOnlyList<FacetEvidence> Facets(TransferStack stack)
        {
            if (facets.TryGetValue(stack.Handle, out var known)) return known;
            known = adapter.TryItem(inventory, stack.Handle, out var item) ? plugin.InstanceFacets(item, answers) : Plugin.UnreadableFacets(stack.Identity.Prefab, answers);
            facets.Add(stack.Handle, known);
            return known;
        }
        // With AzuAutoStore, the ruled tier is what its store key does in YAVSOS's order (D070), with Azu's exclusions.
        var azu = !Enabled;
        return new StashInput(plugin.RuleTaxonomy, answers, own.Snapshot, chests, plugin.CategoryMemberCount, muc, held, only, Facets,
            azuStores: azu, azuAllows: azu ? (id, identity) => !run.Chests.TryGetValue(id, out var chest) || AzuExclusions.Allows(chest, identity.Prefab) != false : null);
    }

    /// <summary>
    /// P6-05: what the stash key would do with the player's stack <paramref name="item"/> now, from the inputs a run
    /// gathers (<see cref="PlacementExplainer.Stash"/>). With AzuAutoStore it is Azu's store key in YAVSOS's chest order
    /// (D070): Azu's nearby chests, skips and exclusions, and Azu's own settings for what no ruled chest takes. Read only: it captures and plans nothing it acts on. Advance one
    /// step per frame; <paramref name="done"/> gets the lines, or null with a note saying why it cannot be explained (the
    /// note may also qualify an answer). Chests are read a few per frame, so the answer may be a few frames older than a run's.
    /// </summary>
    internal IEnumerator<bool> Explain(ItemDrop.ItemData item, Container? open, Action<string?, string> done)
    {
        var plugin = Plugin.Instance; var player = Player.m_localPlayer;
        var note = "";
        var azu = !Enabled;
        if (azu && !AzuStoreOrder.Active)
        { done(null, "The store key (AzuAutoStore) is not following chest priority in this game: YAVSOS could not line it up with this AzuAutoStore version."); yield break; }
        if (plugin == null || !player || player.IsDead() || !ZNet.instance) { done(null, note); yield break; }
        if (!NativeInventoryProbe.Supported) { done(null, "Stashing is unavailable: " + NativeInventoryProbe.Status + "."); yield break; }
        var answers = Answers(plugin);
        if (answers == null) { done(null, "YAVSOS categories are still loading."); yield break; }
        if (!azu && !settings.AllowStashing.Value) note = "Stashing is turned off on this server; this is what it would do.";
        var range = Math.Min(settings.EffectiveStashRange, settings.MaximumRange.Value);
        var run = new Run { Session = ZNet.instance, Radius = range, Adapter = new GameInventorySnapshotAdapter(), Chests = new Dictionary<string, Container>(StringComparer.Ordinal) };
        try
        {
            // Spread over frames (P6-11 cost check): one scene search, then a few chests per step, then the plan.
            var timed = UiCost.Start();
            var near = (azu ? AzuStoreOrder.Nearby(player!).Where(x => ChestRuleStore.Payload(x).Length != 0 && ChestRuleStore.HasAccess(x, player!))
                : CleanupRunner.Nearby(player, range, CleanupRunner.Scene())).Take(azu ? StashPlanner.MaximumChests : settings.EffectiveMaximumChests).ToList();
            UiCost.Stop("stash explain scan", timed);
            var chests = new List<StashChest>();
            for (var at = 0; at < near.Count; at++)
            {
                if (at != 0 && at % CleanupRunner.ExplainBatch == 0) yield return true;
                if (!player) { done(null, ""); yield break; }
                if (near[at]) GatherChest(run, near[at], chests); // One removed since the search is left out.
            }
            yield return true;
            if (!player || player.IsDead()) { done(null, ""); yield break; }
            GameInventoryCapture own;
            timed = UiCost.Start();
            try { own = CapturePlayer(run, player!, azu); }
            catch (Exception error) when (error is not OutOfMemoryException) { done(null, "Stashing is unavailable: your inventory could not be read exactly."); yield break; }
            var held = new List<Guid>();
            var drag = CleanupRunner.DragItem != null && InventoryGui.instance ? CleanupRunner.DragItem(InventoryGui.instance) : null;
            if (drag != null && run.Adapter.TryHandle(player!.GetInventory(), drag, out var dragHandle)) held.Add(dragHandle);
            UiCost.Stop("stash explain player", timed);
            timed = UiCost.Start();
            var input = Input(plugin, answers, player!, run, own, chests, held, null, out _);
            UiCost.Stop("stash explain input", timed);
            yield return true;
            if (!player || player.IsDead()) { done(null, ""); yield break; }
            if (!run.Adapter.TryHandle(player!.GetInventory(), item, out var handle) || input.Player.Stacks.All(x => x.Handle != handle))
            { done(null, "That item cannot be stashed."); yield break; }
            var names = CleanupRunner.ChestNamer(player!, open, run.Chests);
            timed = UiCost.Start();
            var answer = PlacementExplainer.Stash(input, handle, plugin.RuleEvaluator, names, CleanupRunner.Name, openName: RuleControls.OpenValueName, detail: false);
            UiCost.Stop("stash explain plan", timed);
            done(ExplanationView.Tooltip(answer, false, names, azuStores: azu, stackKey: CleanupTriggers.StackKey(azu, settings.StashItemShortcut)), note);
        }
        finally { run.Adapter.Dispose(); }
    }

    protected override void ExecuteChecked(Run run, StashMove move)
    {
        if (run.PolicyChanged) { run.Report.Refuse(TransferRefusal.PolicyChanged, move.Identity.Prefab); return; }
        var refusal = Recheck(run, move, out var player, out var destination);
        if (refusal == TransferRefusal.PolicyChanged) { run.PolicyChanged = true; run.Report.Refuse(refusal, move.Identity.Prefab); return; }
        if (refusal != TransferRefusal.None) { run.Report.Refuse(refusal, move.Identity.Prefab); return; }
        GameInventoryCapture before, beforeDestination;
        try { before = CapturePlayer(run, player!); beforeDestination = CleanupRunner.Capture(run.Adapter, destination!, move.DestinationId); }
        catch (Exception error) when (error is not OutOfMemoryException) { run.Report.Refuse(TransferRefusal.UnsupportedCapability, move.Identity.Prefab); Log(move, "capture failed: " + error.GetType().Name); return; }
        var source = player!.GetInventory(); var target = destination!.GetInventory();
        if (!run.Adapter.TryResolve(source, before.Snapshot.Stamp.InstanceGeneration, move.Handle, out var item) || item == null ||
            !before.Snapshot.Stacks.Any(x => x.Handle == move.Handle && x.Identity.Equals(move.Identity) && !x.Protected))
        { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        var drag = CleanupRunner.DragItem != null && InventoryGui.instance ? CleanupRunner.DragItem(InventoryGui.instance) : null;
        if (ReferenceEquals(drag, item)) { run.Report.Remain(CleanupReason.Held, move.Identity.Prefab); return; }
        // D086 M3 (B2.4): a stack moved into a protected cell (the hotbar, a quick slot) since planning stays, on every route;
        // the direct write checks this again in DepositPlan.
        var stack = before.Snapshot.Stacks.First(x => x.Handle == move.Handle);
        if (before.Snapshot.ProtectedCells.Contains(stack.Y * before.Snapshot.Width + stack.X))
        { run.Report.Refuse(TransferRefusal.Protected, move.Identity.Prefab); return; }
        // A02 backstop: the stack key changed since planning and now matches a request MultiUserChest holds.
        var key = MoveVerification.MergeKey(before.Snapshot, move.Handle, move.Identity);
        if (move.Route != CleanupRoute.Direct && run.Delegated.Any(x => x.Key == key)) { run.Report.Remain(CleanupReason.MucBusy, move.Identity.Prefab); return; }
        if (move.Route != CleanupRoute.Direct) { Delegate(run, move, destination, before, beforeDestination, item, key); return; }
        if (!move.TryMergeTarget(before.Snapshot, beforeDestination.Snapshot, out var merge)) { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        var result = WriteDirect(run, move, () => CapturePlayer(run, player), () => CleanupRunner.Capture(run.Adapter, destination, move.DestinationId),
            before, beforeDestination, source, target, item, move.Handle, move.Identity, move.Units, move.DestinationX, move.DestinationY,
            merge, "Stash", () => (PlayerOwner(player), CleanupRunner.Owner(destination)), () => ChestRuleStore.View(destination)!.IsOwner(),
            ("inventory", "chest"));
        // D093: a pending request of the same key counts its source by this inventory; keep its baseline in step.
        foreach (var pending in run.Delegated.Where(x => x.Key == key))
        {
            if (result.Outcome == DirectMoveOutcome.Moved) pending.Observer.SourceTaken(result.Units);
            else if (result.Outcome == DirectMoveOutcome.Unconfirmed) pending.Observer.SourceUnknown();
        }
        if (Report(run, move, result))
            log.LogInfo($"Stash stored {result.Units} {CleanupRunner.Name(move.Identity.Prefab)} in {CleanupRunner.Localize(destination.m_name)} ({move.Tier}).");
    }

    // MultiUserChest takes the request from here (D043); its windows (refund after 30 s) are inherited.
    private void Delegate(Run run, StashMove move, Container destination, GameInventoryCapture before, GameInventoryCapture beforeDestination,
        ItemDrop.ItemData item, string key)
    {
        var player = Player.m_localPlayer!;
        if (!MucRoute.Available(out _)) { run.Report.Remain(CleanupReason.OtherOwnerNoMuc, move.Identity.Prefab); return; }
        var zdo = ChestRuleStore.View(destination)!.GetZDO();
        if (!zdo.HasOwner()) { run.Report.Refuse(TransferRefusal.OwnerChanged, move.Identity.Prefab); return; }
        if (!MucRoute.OwnerFormatKnown(zdo.GetOwner())) { run.Report.Remain(CleanupReason.OtherOwnerFormatUnknown, move.Identity.Prefab); return; }
        var units = Math.Min(move.Units, item.m_stack);
        if (units < 1) { run.Report.Refuse(TransferRefusal.InventoryChanged, move.Identity.Prefab); return; }
        var baseline = MoveVerification.Counts(before.Snapshot, beforeDestination.Snapshot, key);
        run.MayHaveWritten = true;
        // Cell (−1, −1): the owner picks a free slot; if MultiUserChest does not intercept, the game refuses the move.
        try { destination.GetInventory().MoveItemToThis(player.GetInventory(), item, units, -1, -1); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { run.Report.Unconfirm(viaMuc: true, move.Identity.Prefab); Log(move, "the MultiUserChest request threw " + error.GetType().Name + "; result unconfirmed"); return; }
        // MultiUserChest takes the item out of the local inventory at once; if it did not, it never sent a request.
        if (StringComparer.Ordinal.Equals(CapturePlayer(run, player).Snapshot.ContentFingerprint, before.Snapshot.ContentFingerprint))
        { run.Report.Refuse(TransferRefusal.Busy, move.Identity.Prefab); Log(move, "MultiUserChest did not take the request"); return; }
        run.Delegated.Add(new MovePending<StashMove>
        {
            Move = move, Destination = destination, Key = key,
            Observer = new DelegatedMoveObserver(CleanupRoute.MucAdd, units, baseline.Source, baseline.Destination, baseline.OtherDestination, Time.realtimeSinceStartupAsDouble)
        });
    }

    private TransferRefusal Recheck(Run run, StashMove move, out Player? player, out Container? destination)
    {
        destination = null; player = Player.m_localPlayer;
        var plugin = Plugin.Instance;
        if (plugin == null || !player || player!.IsDead() || !ReferenceEquals(ZNet.instance, run.Session)) return TransferRefusal.Disconnected;
        if (!settings.AllowStashing.Value) return TransferRefusal.TurnedOff;
        var answers = Answers(plugin);
        if (answers == null || !StringComparer.Ordinal.Equals(plugin.RuleTaxonomy.Fingerprint + ":" + answers.Generation, run.Plan.PolicyStamp))
            return TransferRefusal.PolicyChanged;
        if (!run.Chests.TryGetValue(move.DestinationId, out destination) || !destination || ChestRuleStore.Eligibility(destination) != RuleEligibility.Eligible)
            return TransferRefusal.InventoryChanged;
        if (ChestRuleStore.View(destination)!.IsOwner() != (move.Route == CleanupRoute.Direct)) return TransferRefusal.OwnerChanged;
        if (!ChestRuleStore.HasAccess(destination, player)) return TransferRefusal.AccessDenied;
        var reach = run.Radius + 2f;
        if ((destination.transform.position - player.transform.position).sqrMagnitude > reach * reach) return TransferRefusal.Expired;
        var prefab = move.Identity.Prefab; var payload = ChestRuleStore.Payload(destination);
        if (move.Tier == StashTier.Ruled)
        {
            // The stack's current rarity and condition (P6-07b), as planned; a stack gone since is refused at the write.
            var facets = plugin.InstanceFacetsFor(payload, prefab, run.Adapter.TryItem(player!.GetInventory(), move.Handle, out var item) ? item : null, answers);
            return plugin.RuleEvaluator.Evaluate(payload, prefab, plugin.RuleTaxonomy, answers, facets) == ChestRuleDecision.Accept ? TransferRefusal.None : TransferRefusal.RuleMismatch;
        }
        // An unruled chest still has no rule and still holds the item.
        return plugin.RuleEvaluator.Unruled(payload, plugin.RuleTaxonomy) && destination.GetInventory().GetAllItems().Any(x => x?.m_dropPrefab && x.m_dropPrefab.name == prefab)
            ? TransferRefusal.None : TransferRefusal.RuleMismatch;
    }

    // The hotbar (first row) and AzuEPI's quick slots are protected unless the player's settings include them, for the
    // single-item key too; AzuAutoStore protects them the same way when it does the stashing (D061 Q3, D065).
    // Equipped and quest items are protected by the adapter itself.
    // With AzuAutoStore (explanations only), the stacks Azu's store key skips are the protected ones.
    private GameInventoryCapture CapturePlayer(Run run, Player player, bool azu = false)
    {
        var inventory = player.GetInventory(); var zdo = player.GetComponent<ZNetView>().GetZDO();
        if (azu)
            return run.Adapter.Capture(inventory, PlayerId, zdo.GetOwner(), zdo.OwnerRevision, CleanupRunner.Registration,
                AzuStoreOrder.Skips, Array.Empty<int>(), () => NativeInventoryProbe.Supported, keepUncapturable: true);
        var quick = QuickSlots.Items(inventory);
        var keepQuick = !settings.StashQuickSlots.Value;
        var cells = (settings.StashHotbar.Value ? Enumerable.Empty<int>() : Enumerable.Range(0, inventory.GetWidth()))
            .Concat(keepQuick ? quick.Cells : Enumerable.Empty<int>());
        return run.Adapter.Capture(inventory, PlayerId, zdo.GetOwner(), zdo.OwnerRevision, CleanupRunner.Registration,
            item => keepQuick && quick.Items.Contains(item), cells.Distinct().ToArray(), () => NativeInventoryProbe.Supported, keepUncapturable: true);
    }

    private static (long, ushort) PlayerOwner(Player player)
    { var zdo = player.GetComponent<ZNetView>().GetZDO(); return (zdo.GetOwner(), zdo.OwnerRevision); }

    protected override void Log(StashMove move, string text) => log.LogWarning($"Stash {CleanupRunner.Name(move.Identity.Prefab)} x{move.Units} (-> {move.DestinationId}): {text}");
}

/// <summary>
/// AzuExtendedPlayerInventory's quick slots, read through its public API by shape (D058). If AzuEPI is loaded but
/// the API cannot be read, every row past the game's four is protected instead.
/// </summary>
internal static class QuickSlots
{
    private const string Guid = "Azumatt.AzuExtendedPlayerInventory";
    private static Func<List<ItemDrop.ItemData>>? read;
    private static bool bound;

    internal static (HashSet<ItemDrop.ItemData> Items, IEnumerable<int> Cells) Items(Inventory inventory)
    {
        var none = (new HashSet<ItemDrop.ItemData>(), Enumerable.Empty<int>());
        if (!Chainloader.PluginInfos.ContainsKey(Guid)) return none;
        if (!bound)
        {
            bound = true;
            var method = new[] { "AzuEPI.API", "AzuExtendedPlayerInventory.API" }.Select(AccessTools.TypeByName).Where(x => x != null)
                .Select(x => AccessTools.DeclaredMethod(x, "GetQuickSlotsItems", Type.EmptyTypes))
                .FirstOrDefault(x => x != null && x.IsStatic && x.ReturnType == typeof(List<ItemDrop.ItemData>));
            if (method != null) read = (Func<List<ItemDrop.ItemData>>)Delegate.CreateDelegate(typeof(Func<List<ItemDrop.ItemData>>), method);
        }
        // D086 M3 (B2.7): a null list is no answer, so this read falls back as a missing API does.
        try { if (read?.Invoke() is { } list) return (new HashSet<ItemDrop.ItemData>(list), Enumerable.Empty<int>()); }
        catch (Exception error) when (error is not OutOfMemoryException) { read = null; }
        var width = inventory.GetWidth();
        return (new HashSet<ItemDrop.ItemData>(), Enumerable.Range(4 * width, Math.Max(0, inventory.GetHeight() - 4) * width));
    }
}

/// <summary>
/// D061 Q1/Q5: YAVSOS's store key (default <c>.</c>) and single-item key (default middle mouse on an inventory stack),
/// only when AzuAutoStore is not installed. Ctrl+<c>.</c> stays cleanup. Unity game thread only.
/// </summary>
internal static class StashTriggers
{
    internal static void Poll(PluginSettings settings, StashRunner runner)
    {
        if (!StashRunner.Enabled || !Player.m_localPlayer || Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible() || Minimap.IsOpen() ||
            Chat.instance && Chat.instance.HasFocus() || Interface.TypingBlock.Active) return; // Typing in the panel's search.
        if (InventoryGui.IsVisible())
        {
            if (settings.StashItemShortcut.Value.IsDown() && Pointed() is { } item) runner.Start(single: item);
            return;
        }
        if (settings.StashShortcut.Value.IsDown()) runner.Start();
    }

    // The player's own stack under the mouse; null when anything differs. Through the public screen-point lookup: the
    // grid element's former private m_pos field is gone in the current game, so a by-name read found nothing.
    private static ItemDrop.ItemData? Pointed()
    {
        var gui = InventoryGui.instance; var player = Player.m_localPlayer;
        var grid = gui ? gui.m_playerGrid : null;
        return grid && player && ReferenceEquals(grid!.GetInventory(), player.GetInventory()) ? GridHover.Item(grid) : null;
    }
}
