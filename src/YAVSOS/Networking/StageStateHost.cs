using System;
using System.Globalization;
using BepInEx.Logging;
using ServerSync;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

/// <summary>
/// X62 (D145): a managed server publishes its category stage state in its own synced value, <c>server-stage-state-v1</c>,
/// so a client can say the server couldn't build the categories instead of "still loading". A 1.2 client doesn't
/// register the value, and a 1.2 server never sends it (the client keeps today's text). The value carries no authority:
/// category answers still come only from <see cref="AssignmentHost"/>. Session isolation follows the assignment value:
/// the state shares the server's assignment session, and a client uses it only while that is the session it is bound to.
/// </summary>
internal sealed class StageStateHost : IDisposable
{
    // Coalesces bursts (a catalog that restarts its capture flips loading and ready); the latest state always goes out.
    private const double MinimumPublishSeconds = 1;
    private readonly ConfigSync sync;
    private readonly CustomSyncedValue<string> value;
    private readonly PolicyLifecycle policy;
    private readonly ManualLogSource log;
    private readonly Func<string> serverSession;
    private readonly Func<string?> receivedSession;
    private readonly Func<ServerStageReport> report;
    private readonly ServerStageReceiver receiver = new ServerStageReceiver();
    private ServerStageReport? reported;
    private bool dirty, disposed;
    private long sequence, changedAt;
    private double nextPublishAt;
    private string? rejectedLogged, failureLogged;

    internal StageStateHost(ConfigSync sync, PolicyLifecycle policy, ManualLogSource log, Func<string> serverSession, Func<string?> receivedSession,
        Func<ServerStageReport> report)
    {
        this.sync = sync; this.policy = policy; this.log = log; this.serverSession = serverSession; this.receivedSession = receivedSession; this.report = report;
        value = new CustomSyncedValue<string>(sync, ServerStageCodec.SyncIdentifier, ""); value.ValueChanged += OnRemoteChanged;
    }

    /// <summary>On a client: the server's state for the current assignment session, or null (none sent, unreadable or older).</summary>
    internal ServerStageState? Current => disposed || policy.Mode != PolicyMode.ManagedClient ? null : receiver.For(receivedSession());

    internal void BeginSession()
    {
        receiver.Reset(); reported = null; dirty = false; sequence = 0; changedAt = 0; nextPublishAt = 0;
        rejectedLogged = failureLogged = null;
        value.AssignLocalValue("");
    }

    private void OnRemoteChanged()
    {
        if (disposed || sync.IsSourceOfTruth || policy.Mode is PolicyMode.Offline or PolicyMode.Standalone or PolicyMode.ManagedServer) return;
        receiver.Receive(value.Value, receivedSession());
        if (receiver.Rejected && receiver.Diagnostic != rejectedLogged)
        {
            rejectedLogged = receiver.Diagnostic;
            log.LogWarning(receiver.Diagnostic + ". The panel shows categories as loading until the server sends a valid value; a server and client on different YAVSOS versions can cause this.");
        }
        else if (!receiver.Rejected) rejectedLogged = null;
    }

    /// <param name="now">Seconds on a steady clock, for the publication pause.</param>
    internal void Update(double now)
    {
        if (disposed) return;
        if (policy.Mode == PolicyMode.ManagedServer) Publish(now);
        else LogServerState();
    }

    private void Publish(double now)
    {
        if (!sync.IsSourceOfTruth || ConfigSync.ProcessingServerUpdate) return;
        var next = report();
        // The retry time is fixed when the state changes (each failure has its own diagnostic, D144), so its countdown
        // doesn't republish.
        if (!next.Equals(reported)) { reported = next; changedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); dirty = true; }
        if (!dirty || now < nextPublishAt) return;
        dirty = false; nextPublishAt = now + MinimumPublishSeconds;
        value.Value = ServerStageCodec.Encode(reported!.ToState(serverSession(), checked(++sequence), changedAt));
    }

    // One warning per distinct server failure, so the client's log says what the server's log says.
    private void LogServerState()
    {
        var current = Current;
        var key = current?.Condition == ServerStageCondition.Failed ? current.Stage + "|" + current.Failure : null;
        if (key == failureLogged) return;
        if (key != null)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(current!.RetryAtUnixSeconds).UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            log.LogWarning($"The server couldn't build the categories: its {ServerStageCodec.Stage(current.Stage)} stage failed: {current.Failure} " +
                $"It tries again at {at} UTC by the server's clock; the server's log says more.");
        }
        else if (failureLogged != null)
            log.LogInfo("The server's categories are " + (current?.Condition == ServerStageCondition.Ready ? "ready" : "loading") + " again.");
        failureLogged = key;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; value.ValueChanged -= OnRemoteChanged; receiver.Reset();
    }
}
