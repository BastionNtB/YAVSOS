using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Transfers;
using YAVSOS.Interface;
using YAVSOS.Transfers;

namespace YAVSOS.Cleanup;

/// <summary>D07: the state of one stash or cleanup run that both runners share.</summary>
internal abstract class MoveRun<TMove> where TMove : class
{
    internal object Session = null!;
    internal Dictionary<string, Container> Chests = null!;
    internal GameInventorySnapshotAdapter Adapter = null!;
    internal CleanupReport Report = new();
    internal float Radius;
    internal int Next;
    internal bool PolicyChanged, MayHaveWritten;
    internal readonly List<MovePending<TMove>> Delegated = new();
    /// <summary>D093: moves set aside because they conflict with a pending MultiUserChest request; they run once it ends.</summary>
    internal readonly List<TMove> Deferred = new();
    internal float NextPoll;
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal abstract IReadOnlyList<TMove> Moves { get; }
}

/// <summary>A move MultiUserChest holds; <see cref="Source"/> is null when the source is the player's inventory.</summary>
internal sealed class MovePending<TMove> where TMove : class
{
    internal TMove Move = null!;
    internal Container? Source;
    internal Container Destination = null!;
    internal DelegatedMoveObserver Observer = null!;
    internal string Key = "";
}

/// <summary>
/// D07: the run lifecycle both runners share: a few moves per frame, MultiUserChest observation, the end of a run and its
/// report. Once a game write or a MultiUserChest request may have happened, a failure is Unconfirmed; before that it
/// moved nothing. Nothing is retried, replayed or refunded (D041). Unity game thread only.
/// </summary>
internal abstract class MoveRunner<TMove, TRun> where TMove : class where TRun : MoveRun<TMove>
{
    private const int MovesPerFrame = 8;
    private const double FrameBudgetMilliseconds = 2;
    protected readonly ManualLogSource log;
    private readonly RunResults results;
    protected TRun? active;
    internal string LastSummary { get; private set; } = "";
    internal bool Running => active != null;
    internal string Progress => active is { } run ? $"{run.Next - run.Deferred.Count}/{run.Moves.Count} started, {run.Delegated.Count} pending, {run.Deferred.Count} set aside" : "idle";

    protected MoveRunner(ManualLogSource log, RunResults results) { this.log = log; this.results = results; }

    protected abstract float PollSeconds { get; }
    protected abstract string Prefab(TMove move);
    protected abstract Guid Stack(TMove move);
    protected abstract CleanupRoute Route(TMove move);
    /// <summary>
    /// D093: this move conflicts with a pending MultiUserChest request, so it is set aside until that request ends; the
    /// moves after it go ahead (no head-of-line wait). Must have no side effects: it is asked again every frame.
    /// </summary>
    protected virtual bool Waits(TRun run, TMove next) => false;
    /// <summary>Lab only: holds the whole run before its next move (the frame's moves stop here).</summary>
    protected virtual bool HoldNext(TRun run) => false;
    protected abstract void ExecuteChecked(TRun run, TMove move);
    /// <summary>Recaptures a pending move's two inventories as <see cref="MoveVerification.Counts"/>; throws if they cannot be read.</summary>
    protected abstract (int Source, int Destination, int OtherDestination) Observe(TRun run, MovePending<TMove> pending);
    protected abstract bool PendingCurrent(TRun run, MovePending<TMove> pending);
    protected abstract string MovedViaMuc(TMove move, DelegatedMoveObserver observer);
    protected abstract void Log(TMove move, string text);
    /// <summary>SF15 (D087): a failure's type and message for the move's log line.</summary>
    protected static string Cause(Exception error) => error.GetType().Name + ": " + error.Message;

    internal void Update()
    {
        var run = active;
        if (run == null) return;
        if (!ReferenceEquals(ZNet.instance, run.Session) || !Player.m_localPlayer) { Abort(TransferRefusal.Disconnected); return; }
        var frame = Stopwatch.StartNew();
        for (var count = 0; count < MovesPerFrame && frame.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds; count++)
        {
            // A set-aside move whose request has ended runs first, in plan order; then the plan continues.
            var deferred = run.Deferred.FindIndex(x => !Waits(run, x));
            if (deferred < 0)
            {
                if (run.Next >= run.Moves.Count) break;
                if (Waits(run, run.Moves[run.Next])) { run.Deferred.Add(run.Moves[run.Next++]); continue; }
            }
            if (HoldNext(run)) break;
            TMove next;
            if (deferred >= 0) { next = run.Deferred[deferred]; run.Deferred.RemoveAt(deferred); }
            else next = run.Moves[run.Next++];
            Execute(run, next);
        }
        if (run.Delegated.Count != 0 && Time.unscaledTime >= run.NextPoll) { run.NextPoll = Time.unscaledTime + PollSeconds; Poll(run); }
        if (run.Next >= run.Moves.Count && run.Delegated.Count == 0 && run.Deferred.Count == 0) Finish();
    }

    /// <summary>The world ended mid-run: the rest of the moves never start.</summary>
    internal void EndWorld() { if (active != null) Abort(TransferRefusal.Disconnected); }

    private void Poll(TRun run)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        foreach (var pending in run.Delegated.ToArray())
        {
            var current = ReferenceEquals(ZNet.instance, run.Session) && PendingCurrent(run, pending);
            (int Source, int Destination, int OtherDestination) counts = default;
            if (current)
            {
                try { counts = Observe(run, pending); }
                catch (Exception error) when (error is not OutOfMemoryException) { current = false; Log(pending.Move, "observation failed: " + Cause(error)); }
            }
            if (pending.Observer.Observe(now, counts.Source, counts.Destination, counts.OtherDestination, current) == DelegatedMoveState.Pending) continue;
            run.Delegated.Remove(pending);
            Resolve(run, pending);
        }
    }

    private void Resolve(TRun run, MovePending<TMove> pending)
    {
        var observer = pending.Observer; var move = pending.Move;
        switch (observer.State)
        {
            case DelegatedMoveState.Moved:
                run.Report.Moved(observer.MovedUnits, viaMuc: true, Prefab(move), Stack(move));
                if (observer.MovedUnits < observer.Units) run.Report.Remain(CleanupReason.NoCapacity, Prefab(move));
                log.LogInfo(MovedViaMuc(move, observer));
                break;
            case DelegatedMoveState.Refused:
                run.Report.Refuse(TransferRefusal.Busy, Prefab(move)); Log(move, "MultiUserChest: " + observer.Detail);
                break;
            default:
                run.Report.Unconfirm(viaMuc: true, Prefab(move)); Log(move, "unconfirmed via MultiUserChest: " + observer.Detail + ". Not corrected (D041).");
                break;
        }
    }

    private void Abort(TransferRefusal reason)
    {
        var run = active!;
        while (run.Next < run.Moves.Count) run.Report.Refuse(reason, Prefab(run.Moves[run.Next++]));
        foreach (var move in run.Deferred) run.Report.Refuse(reason, Prefab(move));
        run.Deferred.Clear();
        // A request MultiUserChest already holds is MultiUserChest's to finish; YAVSOS cannot tell how it ends.
        foreach (var pending in run.Delegated) { run.Report.Unconfirm(viaMuc: true, Prefab(pending.Move)); Log(pending.Move, "the run ended before MultiUserChest answered"); }
        run.Delegated.Clear();
        Finish();
    }

    protected void Finish()
    {
        var run = active!; active = null;
        try { run.Adapter.Dispose(); } catch { /* adapter only holds handles */ }
        LastSummary = run.Report.Summary();
        log.LogInfo(LastSummary + $" ({run.Clock.Elapsed.TotalMilliseconds:0} ms)");
        CleanupRunner.Say(LastSummary);
        results.Publish(run.Report);
    }

    // Nothing may escape into Plugin.Update.
    private void Execute(TRun run, TMove move)
    {
        run.MayHaveWritten = false;
        try { ExecuteChecked(run, move); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (run.MayHaveWritten) { run.Report.Unconfirm(Route(move) != CleanupRoute.Direct, Prefab(move)); Log(move, "failed after the write (" + Cause(error) + "); result unconfirmed. Not corrected (D041)."); }
            else { run.Report.Refuse(TransferRefusal.UnsupportedCapability, Prefab(move)); Log(move, "failed before any write: " + Cause(error)); }
        }
    }

    /// <summary>
    /// D07: one direct move, written and verified the same way for both runners. The caller has rechecked the move and
    /// captured both inventories; this creates the plan from those captures, writes one vanilla <c>MoveItemToThis</c> as
    /// the owner of both, recaptures and verifies conservation, identity, cell, owners and session.
    /// </summary>
    /// <param name="owners">Owner and owner revision of both inventories, read before and after the write.</param>
    /// <param name="ownersLocal">Both inventories are still owned by this peer.</param>
    /// <param name="labels">How the log names the two sides ("source"/"destination", "inventory"/"chest").</param>
    protected DirectMoveResult WriteDirect(TRun run, TMove move, Func<GameInventoryCapture> captureSource, Func<GameInventoryCapture> captureDestination,
        GameInventoryCapture before, GameInventoryCapture beforeDestination, Inventory source, Inventory destination, ItemDrop.ItemData item,
        Guid handle, TransferItemIdentity identity, int units, int x, int y, Guid? merge, string ruleStamp,
        Func<((long, ushort), (long, ushort))> owners, Func<bool> ownersLocal, (string Source, string Destination) labels)
    {
        var now = Time.realtimeSinceStartupAsDouble;
        // What the write can move at most: the plan's units, or less if the stack itself shrank (used, taken) since.
        var planned = Math.Min(units, item.m_stack);
        var created = DepositPlan.TryCreateAt(Guid.NewGuid(), Guid.NewGuid(), PolicyStamp(run), ruleStamp, before.Snapshot,
            beforeDestination.Snapshot, handle, units, x, y, merge, now, now + 5, out var plan);
        if (created != TransferRefusal.None) return DirectMoveResult.Refused(created, null);
        var operation = new TransferOperation(plan!);
        operation.BeginApplying(TransferRefusal.None);
        var ownersBefore = owners();
        run.MayHaveWritten = true;
        try { destination.MoveItemToThis(source, item, plan!.PlannedUnits, plan.DestinationX, plan.DestinationY); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            operation.Refuse(TransferRefusal.InvariantFailed);
            return DirectMoveResult.Unconfirmed("the game's move threw " + Cause(error) + "; result unconfirmed");
        }
        GameInventoryCapture after, afterDestination;
        var side = labels.Source; // SF15: which recapture failed, for the log only.
        try { after = captureSource(); side = labels.Destination; afterDestination = captureDestination(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { operation.Refuse(TransferRefusal.InvariantFailed); return DirectMoveResult.Unconfirmed("recapture of the " + side + " failed: " + Cause(error) + "; result unconfirmed"); }
        var key = MoveVerification.MergeKey(before.Snapshot, handle, identity);
        var seen = MoveVerification.Observe(before.Snapshot, beforeDestination.Snapshot, after.Snapshot, afterDestination.Snapshot, key, plan.DestinationX, plan.DestinationY);
        var ownersCurrent = ownersBefore == owners() && ReferenceEquals(ZNet.instance, run.Session) && ownersLocal();
        if (operation.Verify(seen.Decrease, seen.Increase, seen.OthersUnchanged, ownersCurrent, seen.CellHoldsKey) == TransferRefusal.None)
            return DirectMoveResult.Moved(seen.Decrease, planned);
        // Nothing observable changed: the game refused the move, so nothing moved.
        if (seen.NothingChanged) return DirectMoveResult.Refused(TransferRefusal.NoCapacity, "the game refused the move; nothing changed");
        return DirectMoveResult.Unconfirmed($"unconfirmed: {labels.Source} -{seen.Decrease}, {labels.Destination} +{seen.Increase}, others unchanged {seen.OthersUnchanged}, " +
            $"owners current {ownersCurrent}, cell {seen.CellHoldsKey}. Not corrected (D041).");
    }

    protected abstract string PolicyStamp(TRun run);

    /// <summary>Reports a direct move's result; true when it moved. A refusal without detail is not logged.</summary>
    protected bool Report(TRun run, TMove move, DirectMoveResult result)
    {
        switch (result.Outcome)
        {
            case DirectMoveOutcome.Moved:
                run.Report.Moved(result.Units, item: Prefab(move), stack: Stack(move));
                // D086 M4 (B2.6): the plan clamps to the room left at the write; what didn't fit is reported, as on the MultiUserChest path.
                if (result.Units < result.Planned) run.Report.Remain(CleanupReason.NoCapacity, Prefab(move));
                return true;
            case DirectMoveOutcome.Refused: run.Report.Refuse(result.Refusal, Prefab(move)); break;
            default: run.Report.Unconfirm(item: Prefab(move)); break;
        }
        if (result.Detail != null) Log(move, result.Detail);
        return false;
    }
}

internal enum DirectMoveOutcome { Moved, Refused, Unconfirmed }

internal readonly struct DirectMoveResult
{
    internal DirectMoveOutcome Outcome { get; }
    internal int Units { get; }
    internal TransferRefusal Refusal { get; }
    internal string? Detail { get; }
    /// <summary>The units the move was planned with; more than <see cref="Units"/> when the write had less room.</summary>
    internal int Planned { get; }
    private DirectMoveResult(DirectMoveOutcome outcome, int units, TransferRefusal refusal, string? detail, int planned = 0)
    { Outcome = outcome; Units = units; Refusal = refusal; Detail = detail; Planned = planned; }
    internal static DirectMoveResult Moved(int units, int planned) => new(DirectMoveOutcome.Moved, units, TransferRefusal.None, null, planned);
    internal static DirectMoveResult Refused(TransferRefusal refusal, string? detail) => new(DirectMoveOutcome.Refused, 0, refusal, detail);
    internal static DirectMoveResult Unconfirmed(string detail) => new(DirectMoveOutcome.Unconfirmed, 0, TransferRefusal.Unconfirmed, detail);
}
