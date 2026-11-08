using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using YAVSOS.Core.Messages;
using YAVSOS.Integrations;
using YAVSOS.Core.Transfers;
using YAVSOS.Transfers;

namespace YAVSOS.Networking;

/// <summary>
/// X32 (D154, design X32-REMOTE-RULE-EDITS.md): edits on a chest another game owns. The requester's game builds the
/// new payload and sends one checked write request, on a global routed message addressed by the chest's ID, to the
/// game that owns the chest; that game checks ownership, the chest's privacy, the rate, the compare-and-set and the
/// envelope (<see cref="RuleWriteOwner"/>), writes, reads back and answers. The requester waits 5 s for the answer,
/// then watches the chest's synced rule until 40 s in all (<see cref="RuleWriteTracker{TKey}"/>). YAVSOS's first
/// network messages of its own (internals 05 §9). No items move. Unity game thread only.
/// </summary>
internal sealed class RuleWriteChannel
{
    internal const string RequestName = "com.bastionntb.yavsos.rule-write";
    internal const string ReplyName = "com.bastionntb.yavsos.rule-write-reply";
    private readonly ManualLogSource log;
    private readonly RuleWriteTracker<ZDOID> tracker = new RuleWriteTracker<ZDOID>();
    private readonly RuleWriteRate rate = new RuleWriteRate();
    private ZRoutedRpc? registered;
    private bool failed, warnedMalformed;
#if YAVSOS_LAB
    private readonly List<(double At, long Target, byte[] Reply)> delayed = new();
    /// <summary>Lab: the last outcomes, newest last, for <c>RuleLab.Remote</c>.</summary>
    internal readonly Queue<string> LabOutcomes = new();
    internal int LabWaiting => tracker.Count;
    internal int LabAnswered { get; private set; }
#endif

    /// <summary>Raised on the game thread when a write ends, or stops blocking its chest (<see cref="RuleWriteOutcome.NoAnswer"/>).</summary>
    internal event Action<RuleWriteSettled<ZDOID>>? Settled;

    internal RuleWriteChannel(ManualLogSource log) => this.log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>The messages are registered for this world: remote edits can be sent and answered.</summary>
    internal bool Available => !failed && registered != null && ReferenceEquals(registered, ZRoutedRpc.instance);

    /// <summary>At world start (after <c>ZNet.Awake</c> made the routed RPC): register both messages, once per world.</summary>
    internal void StartWorld()
    {
        tracker.Clear(); rate.Clear();
#if YAVSOS_LAB
        delayed.Clear();
#endif
        if (failed || ZRoutedRpc.instance == null || ReferenceEquals(registered, ZRoutedRpc.instance)) return;
        try
        {
            ZRoutedRpc.instance.Register<ZDOID, ZPackage>(RequestName, OnRequest);
            ZRoutedRpc.instance.Register<ZPackage>(ReplyName, OnReply);
            registered = ZRoutedRpc.instance;
        }
        // D058: without the messages, another player's chest stays read-only, as before 1.3.
        catch (Exception error) when (error is MissingMethodException || error is MemberAccessException || error is ArgumentException || error is InvalidOperationException)
        {
            failed = true;
            log.LogWarning($"Edits on other players' chests turned off: the game's routed messages couldn't be registered ({error.GetType().Name}: {error.Message}). Those chests stay read-only; everything else keeps working.");
        }
    }

    internal void EndWorld()
    {
        tracker.Clear(); rate.Clear(); registered = null;
#if YAVSOS_LAB
        delayed.Clear();
#endif
    }

    /// <summary>This game wrote the chest itself: an unanswered earlier remote write to it stops being followed.</summary>
    internal void Forget(ZDOID chest) => tracker.Forget(chest);

    /// <summary>The chest's controls take no clicks: a write to it waits for its answer.</summary>
    internal bool Blocking(ZDOID chest) => tracker.Blocking(chest);
    internal RuleWritePending<ZDOID>? Waiting(ZDOID chest) => tracker.For(chest);

    /// <summary>
    /// True on a client when the chest's owner is a dedicated server (for the strip's wording). A client can't ask ZNet
    /// whether its server is dedicated; no player in the player list having the server's ID says so (a host has one).
    /// </summary>
    internal static bool ServerOwns(ZDO zdo)
    {
        var net = ZNet.instance;
        if (!net || net.IsServer() || net.GetServerPeer() is not { } server || zdo.GetOwner() != server.m_uid) return false;
        foreach (var player in net.GetPlayerList())
            if (player.m_characterID.UserID == server.m_uid) return false;
        return true;
    }

    /// <summary>
    /// Sends a write for <paramref name="container"/> to its owner. Null: sent, the chest now waits. Otherwise the refusal
    /// to show (no owner right now, the channel is off, too many writes waiting). The requester's own checks (eligible,
    /// settings loaded, a living player, chest and ward access) are the caller's (<see cref="ChestRuleStore"/>).
    /// </summary>
    internal Message? Send(Container container, RuleWriteKind kind, string before, string value, bool optOut, int control)
    {
        if (!Available) return Message.Of("yavsos_edit_chest_in_use");
        var zdo = ChestRuleStore.View(container)?.GetZDO();
        var player = Player.m_localPlayer;
        if (zdo == null || !player) return Message.Of("yavsos_edit_no_player");
        var owner = zdo.GetOwner();
        // Target 0 would reach every game (ZRoutedRpc.Everybody): never send while the chest has no owner.
        if (owner == 0) return Message.Of("yavsos_edit_try_again");
        if (!tracker.CanStart(zdo.m_uid)) return Message.Of(tracker.For(zdo.m_uid) != null ? "yavsos_edit_saving" : "yavsos_edit_busy");
        var write = tracker.Start(zdo.m_uid, owner, kind, before, value, optOut, control, Time.realtimeSinceStartupAsDouble);
        var request = kind == RuleWriteKind.OptOut
            ? RuleWriteRequest.SetCleanupOptOut(write.RequestId, player.GetPlayerID(), optOut)
            : RuleWriteRequest.WriteRule(write.RequestId, player.GetPlayerID(), before, value);
        Invoke(owner, zdo.m_uid, request);
        return null;
    }

    private static void Invoke(long target, ZDOID chest, RuleWriteRequest request) =>
        ZRoutedRpc.instance.InvokeRoutedRPC(target, RequestName, chest, new ZPackage(RuleWriteCodec.Encode(request)));

    /// <summary>Once a frame: a write whose value shows up on the chest counts as saved (a lost reply); then the 5 s and 40 s windows.</summary>
    internal void Update()
    {
#if YAVSOS_LAB
        SendDelayed();
#endif
        if (tracker.Count == 0) return;
        foreach (var settled in tracker.Step(Time.realtimeSinceStartupAsDouble, Seen)) Raise(settled);
    }

    private static bool? Seen(RuleWritePending<ZDOID> write)
    {
        var zdo = ZDOMan.instance?.GetZDO(write.Chest);
        if (zdo == null) return null;
        return write.Kind == RuleWriteKind.OptOut
            ? zdo.GetBool(ChestRuleStore.CleanupOptOutKey, false) == write.OptOut
            : StringComparer.Ordinal.Equals(zdo.GetString(ContainerPolicyEnvelope.ZdoKey, ""), write.Value);
    }

    private void Raise(RuleWriteSettled<ZDOID> settled)
    {
#if YAVSOS_LAB
        LabOutcomes.Enqueue($"{settled.Write.Kind} {settled.Write.Chest} -> {settled.Outcome} (final={settled.Final}, target={settled.Write.Target}, after={Time.realtimeSinceStartupAsDouble - settled.Write.SentAt:0.00}s)");
        while (LabOutcomes.Count > 16) LabOutcomes.Dequeue();
        log.LogInfo($"X32 settled {settled.Write.Kind} {settled.Write.Chest} {settled.Outcome} final={settled.Final} after={Time.realtimeSinceStartupAsDouble - settled.Write.SentAt:0.00}s");
#endif
        try { Settled?.Invoke(settled); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { log.LogWarning($"A remote rule write's result couldn't be shown ({error.GetType().Name}: {error.Message})."); }
    }

    private void OnReply(long sender, ZPackage package)
    {
        try { Replied(sender, package); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Failed("reply", error); }
    }

    private void Replied(long sender, ZPackage package)
    {
        if (!RuleWriteCodec.TryDecodeReply(Bytes(package), out var id, out var outcome)) return;
        var chest = default(ZDOID); var owner = 0L;
        foreach (var write in tracker.Waiting)
            if (write.RequestId == id) { chest = write.Chest; owner = ZDOMan.instance?.GetZDO(write.Chest)?.GetOwner() ?? 0; break; }
        var settled = tracker.Reply(sender, id, outcome, owner, Time.realtimeSinceStartupAsDouble, out var resend);
        if (resend != null)
        {
            var player = Player.m_localPlayer;
            // B4.43: without a player (dead or leaving) there is no ID to send; the write stays until it is seen or its
            // window ends, and leaving the world clears it.
            if (!player) return;
            var request = resend.Kind == RuleWriteKind.OptOut
                ? RuleWriteRequest.SetCleanupOptOut(resend.RequestId, player.GetPlayerID(), resend.OptOut)
                : RuleWriteRequest.WriteRule(resend.RequestId, player.GetPlayerID(), resend.Before, resend.Value);
            Invoke(resend.Target, chest, request);
            return;
        }
        if (settled is { } done) Raise(done);
        else log.LogDebug($"Dropped a rule-write reply {id} ({outcome}) from {sender}: nothing waits for it.");
    }

    // The owner's side (X32 §3.2): every game answers, the dedicated server included (D154 Q19); none needs a player.
    private void OnRequest(long sender, ZDOID chest, ZPackage package)
    {
        // Sender 0 comes only from a modified game; a reply to it would reach every game.
        if (sender == 0) return;
        try { Requested(sender, chest, package); }
        catch (Exception error) when (OptionalFailure.Recoverable(error)) { Failed("request", error); }
    }

    // A failure here must not reach the game's network code; the requester's wait covers the missing answer.
    private bool warnedFailure;
    private void Failed(string what, Exception error)
    {
        if (warnedFailure) return;
        warnedFailure = true;
        log.LogWarning($"A remote rule-write {what} failed ({error.GetType().Name}: {error.Message}); logged once. The other player sees no answer.");
    }

    private void Requested(long sender, ZDOID chest, ZPackage package)
    {
        if (!RuleWriteCodec.TryDecode(Bytes(package), out var request))
        {
            if (!warnedMalformed) { warnedMalformed = true; log.LogWarning($"Ignored a malformed rule-write request from {sender} (logged once)."); }
            return;
        }
        var outcome = Apply(sender, chest, request!);
#if YAVSOS_LAB
        LabAnswered++;
        log.LogInfo($"X32 answered {request!.Operation} {chest} from {sender}: {outcome} (delay={FaultLab.RuleWriteReplyDelay})");
        if (FaultLab.RuleWriteReplyDelay != 0)
        {
            // Lab fault: the owner answers late, or (below 0) never, to show the requester's windows.
            if (FaultLab.RuleWriteReplyDelay > 0)
                delayed.Add((Time.realtimeSinceStartupAsDouble + FaultLab.RuleWriteReplyDelay, sender, RuleWriteCodec.EncodeReply(request!.RequestId, outcome)));
            return;
        }
#endif
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, ReplyName, new ZPackage(RuleWriteCodec.EncodeReply(request!.RequestId, outcome)));
    }

#if YAVSOS_LAB
    private void SendDelayed()
    {
        if (delayed.Count == 0 || ZRoutedRpc.instance == null) return;
        var now = Time.realtimeSinceStartupAsDouble;
        for (var i = delayed.Count - 1; i >= 0; i--)
        {
            if (delayed[i].At > now) continue;
            ZRoutedRpc.instance.InvokeRoutedRPC(delayed[i].Target, ReplyName, new ZPackage(delayed[i].Reply));
            delayed.RemoveAt(i);
        }
    }
#endif

    private RuleWriteOutcome Apply(long sender, ZDOID chest, RuleWriteRequest request)
    {
        var zdo = ZDOMan.instance?.GetZDO(chest);
        var view = zdo != null && ZNetScene.instance ? ZNetScene.instance.FindInstance(zdo) : null;
        var container = view ? FindContainer(view!) : null;
        var loaded = container != null && view!.IsValid();
        var owner = loaded && view!.IsOwner();
        var eligible = loaded && ChestRuleStore.Eligibility(container) == RuleEligibility.Eligible;
        var access = eligible && ChestRuleStore.PrivacyAccess(container!, request.PlayerId);
        var withinRate = !access || rate.Allow(sender, Time.realtimeSinceStartupAsDouble);
        var current = loaded ? zdo!.GetString(ContainerPolicyEnvelope.ZdoKey, "") : "";
        var optOut = loaded && zdo!.GetBool(ChestRuleStore.CleanupOptOutKey, false);
        var decision = RuleWriteOwner.Decide(request, new RuleWriteOwnerView(loaded, owner, eligible, access, withinRate, current, optOut));
        if (!decision.Write) return decision.Outcome;
        if (request.Operation == RuleWriteOperation.SetCleanupOptOut)
        {
            zdo!.Set(ChestRuleStore.CleanupOptOutKey, decision.OptOut);
            return view!.IsOwner() && zdo.GetBool(ChestRuleStore.CleanupOptOutKey, false) == decision.OptOut ? RuleWriteOutcome.Done : RuleWriteOutcome.Unconfirmed;
        }
        zdo!.Set(ContainerPolicyEnvelope.ZdoKey, decision.Payload);
        var written = zdo.GetString(ContainerPolicyEnvelope.ZdoKey, "");
        return view!.IsOwner() && StringComparer.Ordinal.Equals(written, decision.Payload) ? RuleWriteOutcome.Done : RuleWriteOutcome.Unconfirmed;
    }

    // A cart's container sits on a child object and uses the root's view, so the container is the one whose view this is.
    private static Container? FindContainer(ZNetView view)
    {
        foreach (var candidate in view.GetComponentsInChildren<Container>(true))
            if (ReferenceEquals(ChestRuleStore.View(candidate), view)) return candidate;
        return null;
    }

    private static byte[]? Bytes(ZPackage? package)
    {
        try { return package?.GetArray(); }
        catch (Exception error) when (error is System.IO.EndOfStreamException || error is ArgumentException) { return null; }
    }
}
