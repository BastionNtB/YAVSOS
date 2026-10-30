using System;

namespace YAVSOS.Core.Policy;

public enum PolicyMode { Offline, Standalone, UnmanagedLocal, AwaitingServer, ManagedServer, ManagedClient }
public enum PolicyUpdate { Accepted, Unchanged, Rejected, StaleSession }
public readonly struct PolicyStamp : IEquatable<PolicyStamp>
{
    public long Session { get; }
    public long Generation { get; }
    public PolicyStamp(long session, long generation) => (Session, Generation) = (session, generation);
    public bool Equals(PolicyStamp other) => Session == other.Session && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is PolicyStamp other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Session, Generation);
}

// Main-thread owned. Network/file adapters supply complete snapshots; no inventory authority is implied.
public sealed class PolicyLifecycle
{
    private CategoryPolicy? local;
    private CategoryPolicy? remote;
    private string? localInput;
    private string? remoteInput;
    private bool remoteAccepted;
    private bool catalogReady;
    private string? assignments;
    private long session;
    private long generation;
    public PolicyMode Mode { get; private set; }
    public string Diagnostic { get; private set; } = "No active world.";
    public PolicyStamp Stamp => new PolicyStamp(session, generation);
    public CategoryPolicy? Local => local;
    public CategoryPolicy? Effective => Mode is PolicyMode.ManagedClient or PolicyMode.AwaitingServer ? remote : local;
    public bool PolicyReady => Mode != PolicyMode.Offline && Mode != PolicyMode.AwaitingServer && Effective != null
        && (Mode != PolicyMode.ManagedClient || remoteAccepted);
    public bool CanAutomate => PolicyReady && catalogReady;
    /// <summary>B4.9: the server's policy arrived but was rejected, so no server policy applies (the mode is already managed).</summary>
    public bool RemoteRejected => Mode == PolicyMode.ManagedClient && !remoteAccepted;

    public void BeginSession(PolicyMode mode)
    {
        if (mode == PolicyMode.ManagedClient) throw new ArgumentException("Begin as AwaitingServer until a server policy is accepted.", nameof(mode));
        session++;
        Mode = mode;
        remote = null;
        remoteInput = null;
        remoteAccepted = false;
        assignments = null;
        Invalidate();
        Diagnostic = mode == PolicyMode.Offline ? "No active world." :
            mode == PolicyMode.AwaitingServer ? "Waiting for valid managed policy." : "Catalog/classifier readiness is pending.";
    }

    public PolicyUpdate ApplyLocal(string? payload)
    {
        // Only a previously validated, byte-identical canonical snapshot can
        // bypass parsing. Changed/invalid input still takes the complete codec path.
        if (local != null && MatchesValidatedInput(payload, localInput, local.CanonicalXml)) return PolicyUpdate.Unchanged;
        if (!CategoryPolicyCodec.TryParse(payload, out var candidate, out var error))
        {
            Diagnostic = "Local reload rejected; last accepted policy retained: " + error;
            return PolicyUpdate.Rejected;
        }
        var outcome = Compare(local, candidate!, out error);
        if (outcome == PolicyUpdate.Rejected) Diagnostic = "Local reload rejected: " + error;
        if (outcome != PolicyUpdate.Rejected) localInput = payload;
        if (outcome != PolicyUpdate.Accepted) return outcome;
        local = candidate;
        if (Mode is not (PolicyMode.ManagedClient or PolicyMode.AwaitingServer))
        {
            Invalidate();
            Diagnostic = Mode == PolicyMode.Offline ? "No active world." : "Local policy accepted; catalog/classifier readiness is pending.";
        }
        return outcome;
    }

    public PolicyUpdate ApplyRemote(long expectedSession, string? payload)
    {
        if (expectedSession != session || Mode is PolicyMode.Offline or PolicyMode.Standalone or PolicyMode.ManagedServer)
            return PolicyUpdate.StaleSession;
        Mode = PolicyMode.ManagedClient;
        if (remote != null && MatchesValidatedInput(payload, remoteInput, remote.CanonicalXml))
        {
            if (!remoteAccepted)
            {
                remoteAccepted = true;
                Invalidate();
                Diagnostic = "Managed policy accepted; catalog/classifier readiness is pending.";
            }
            return PolicyUpdate.Unchanged;
        }
        if (!CategoryPolicyCodec.TryParse(payload, out var candidate, out var error))
        {
            remoteAccepted = false;
            Invalidate();
            Diagnostic = "Managed policy rejected; automation blocked: " + error;
            return PolicyUpdate.Rejected;
        }
        var outcome = Compare(remote, candidate!, out error);
        if (outcome == PolicyUpdate.Rejected)
        {
            remoteAccepted = false;
            Invalidate();
            Diagnostic = "Managed policy rejected; automation blocked: " + error;
            return outcome;
        }
        remoteInput = payload;
        if (outcome == PolicyUpdate.Accepted || !remoteAccepted)
        {
            remote = candidate;
            remoteAccepted = true;
            Invalidate();
            Diagnostic = "Managed policy accepted; catalog/classifier readiness is pending.";
        }
        return outcome;
    }

    public void AwaitServer()
    {
        if (Mode is PolicyMode.ManagedServer or PolicyMode.Standalone or PolicyMode.Offline) return;
        Mode = PolicyMode.AwaitingServer;
        remoteAccepted = false;
        Invalidate();
    }

    // D044: the handshake completed without YAVSOS on the server. Only a waiting client of the
    // current session moves to local authority; a later managed policy still wins (ApplyRemote).
    public bool ServerLacksYavsos(long expectedSession)
    {
        if (expectedSession != session || Mode != PolicyMode.AwaitingServer) return false;
        Mode = PolicyMode.UnmanagedLocal;
        remote = null;
        remoteInput = null;
        remoteAccepted = false;
        Invalidate();
        Diagnostic = "Server without YAVSOS; local policy applies. Catalog/classifier readiness is pending.";
        return true;
    }

    public void SharedSettingsChanged() => Invalidate();

    /// <summary>
    /// The accepted assignment snapshot (its fingerprint) and whether its categories are ready. On a managed client it is the
    /// server's answer, which wins per item (D042), so there is no separate local catalog to match. A change invalidates plans.
    /// </summary>
    public void SetAssignmentEvidence(string? fingerprint, bool categoriesReady)
    {
        if (assignments == fingerprint && catalogReady == categoriesReady) return;
        generation++;
        assignments = fingerprint;
        catalogReady = categoriesReady && !string.IsNullOrEmpty(fingerprint);
        Diagnostic = catalogReady ? "Policy and catalog are ready." : "Catalog/classifier readiness is pending.";
    }

    public bool IsCurrent(PolicyStamp stamp) => Stamp.Equals(stamp);
    private static bool MatchesValidatedInput(string? payload, string? input, string canonical) =>
        payload != null && (payload == input || payload == canonical && payload.Length <= CategoryPolicyCodec.MaxPayloadBytes
        && System.Text.Encoding.UTF8.GetByteCount(payload) <= CategoryPolicyCodec.MaxPayloadBytes);
    private void Invalidate() { generation++; catalogReady = false; }
    private static PolicyUpdate Compare(CategoryPolicy? current, CategoryPolicy candidate, out string error)
    {
        error = "";
        if (current == null || candidate.Revision > current.Revision) return PolicyUpdate.Accepted;
        if (current.Fingerprint == candidate.Fingerprint) return PolicyUpdate.Unchanged;
        error = "Increase revision for every changed policy; revision rollback/conflicting reuse is refused.";
        return PolicyUpdate.Rejected;
    }
}
