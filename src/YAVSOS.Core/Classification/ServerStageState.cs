using System;
using System.Globalization;
using System.Text;

namespace YAVSOS.Core.Classification;

/// <summary>X62 (D145): the loading stage a server reports, in pipeline order. <see cref="None"/> only when categories are ready.</summary>
public enum ServerStage { None, Catalog, Sources, Classification, Snapshot }

/// <summary>X62: whether the server's categories are loading, failed and waiting for a slow retry (D144), or ready.</summary>
public enum ServerStageCondition { Loading, Failed, Ready }

/// <summary>
/// X62 (D145): what a server says about its own category stages. Information only: it grants no authority and never
/// changes a client's answers, which still come only from the assignment value (D042). <see cref="Failure"/> is an
/// English diagnostic for the client's log; no player reads it.
/// </summary>
public sealed class ServerStageState
{
    /// <summary>The server's assignment session (32 lowercase hex), so a value from an older session can be told apart.</summary>
    public string Session { get; }
    public long Sequence { get; }
    public ServerStageCondition Condition { get; }
    public ServerStage Stage { get; }
    /// <summary>The failed stage's diagnostic; "" unless <see cref="Condition"/> is <see cref="ServerStageCondition.Failed"/>.</summary>
    public string Failure { get; }
    /// <summary>When the failed stage tries again, as UTC Unix seconds by the server's clock; 0 unless failed.</summary>
    public long RetryAtUnixSeconds { get; }

    public ServerStageState(string session, long sequence, ServerStageCondition condition, ServerStage stage, string failure, long retryAtUnixSeconds)
    {
        if (!ServerStageCodec.ValidSession(session)) throw new ArgumentException("Session must be 32 lowercase hex characters.", nameof(session));
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (condition is not (ServerStageCondition.Loading or ServerStageCondition.Failed or ServerStageCondition.Ready))
            throw new ArgumentOutOfRangeException(nameof(condition));
        if (stage is < ServerStage.None or > ServerStage.Snapshot) throw new ArgumentOutOfRangeException(nameof(stage));
        if (condition == ServerStageCondition.Ready != (stage == ServerStage.None))
            throw new ArgumentException("Only a ready state has no stage, and a ready state names none.", nameof(stage));
        failure ??= "";
        if (condition == ServerStageCondition.Failed)
        {
            if (!ServerStageCodec.ValidFailure(failure)) throw new ArgumentException("Failure text is empty, too long or has control characters.", nameof(failure));
            if (retryAtUnixSeconds < 0 || retryAtUnixSeconds > ServerStageCodec.MaximumRetryAtUnixSeconds) throw new ArgumentOutOfRangeException(nameof(retryAtUnixSeconds));
        }
        else if (failure.Length != 0 || retryAtUnixSeconds != 0) throw new ArgumentException("Only a failed state carries a failure and a retry time.");
        Session = session; Sequence = sequence; Condition = condition; Stage = stage; Failure = failure; RetryAtUnixSeconds = retryAtUnixSeconds;
    }
}

/// <summary>
/// X62 (D145): the <c>server-stage-state-v1</c> synced value. Layout, strictly parsed:
/// <c>yss1:{session}:{sequence}:{loading|failed|ready}:{none|catalog|sources|classification|snapshot}:{retryAt|-}:{failure}</c>.
/// The failure is the rest of the string (it may hold ':'), present only when failed. A value from another format
/// version (<c>yss2:</c> …) is reported as unknown, never as a failure, so a client keeps today's text.
/// </summary>
public static class ServerStageCodec
{
    public const string SyncIdentifier = "server-stage-state-v1";
    public const string Prefix = "yss1:";
    public const int MaximumFailureCharacters = 512;
    /// <summary>9999-12-31T23:59:59Z, the last second <see cref="DateTimeOffset"/> can show.</summary>
    public const long MaximumRetryAtUnixSeconds = 253402300799;
    /// <summary>Checked before anything else is read, so a hostile value costs at most this much work.</summary>
    public const int MaximumCharacters = 640;

    public static string Encode(ServerStageState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        var failed = state.Condition == ServerStageCondition.Failed;
        var text = Prefix + state.Session + ":" + state.Sequence.ToString(CultureInfo.InvariantCulture) + ":" + Condition(state.Condition) + ":" +
            Stage(state.Stage) + ":" + (failed ? state.RetryAtUnixSeconds.ToString(CultureInfo.InvariantCulture) : "-") + ":" + state.Failure;
        if (text.Length > MaximumCharacters) throw new InvalidOperationException("Encoded stage state over the limit.");
        return text;
    }

    public enum DecodeResult { Empty, Decoded, UnknownVersion, Malformed }

    /// <summary>
    /// Reads a value. <see cref="DecodeResult.Empty"/> for null or "" (no state sent, as from a server without this value),
    /// <see cref="DecodeResult.UnknownVersion"/> for another format version, <see cref="DecodeResult.Malformed"/> with a
    /// diagnostic for anything else that isn't exactly this layout.
    /// </summary>
    public static DecodeResult TryDecode(string? value, out ServerStageState? state, out string diagnostic)
    {
        state = null; diagnostic = "";
        if (string.IsNullOrEmpty(value)) return DecodeResult.Empty;
        if (value!.Length > MaximumCharacters) { diagnostic = "Server stage state over " + MaximumCharacters + " characters"; return DecodeResult.Malformed; }
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            if (OtherVersion(value)) { diagnostic = "Server stage state in another format version"; return DecodeResult.UnknownVersion; }
            diagnostic = "Server stage state has no known format prefix"; return DecodeResult.Malformed;
        }
        var parts = value.Substring(Prefix.Length).Split(new[] { ':' }, 6);
        if (parts.Length != 6) { diagnostic = "Server stage state has too few fields"; return DecodeResult.Malformed; }
        if (!ValidSession(parts[0])) { diagnostic = "Server stage state has an invalid session"; return DecodeResult.Malformed; }
        if (!TryCanonical(parts[1], out var sequence) || sequence <= 0) { diagnostic = "Server stage state has an invalid sequence"; return DecodeResult.Malformed; }
        ServerStageCondition condition;
        switch (parts[2])
        {
            case "loading": condition = ServerStageCondition.Loading; break;
            case "failed": condition = ServerStageCondition.Failed; break;
            case "ready": condition = ServerStageCondition.Ready; break;
            default: diagnostic = "Server stage state has an unknown condition"; return DecodeResult.Malformed;
        }
        ServerStage stage;
        switch (parts[3])
        {
            case "none": stage = ServerStage.None; break;
            case "catalog": stage = ServerStage.Catalog; break;
            case "sources": stage = ServerStage.Sources; break;
            case "classification": stage = ServerStage.Classification; break;
            case "snapshot": stage = ServerStage.Snapshot; break;
            default: diagnostic = "Server stage state has an unknown stage"; return DecodeResult.Malformed;
        }
        if (condition == ServerStageCondition.Ready != (stage == ServerStage.None)) { diagnostic = "Server stage state's stage doesn't fit its condition"; return DecodeResult.Malformed; }
        long retryAt = 0;
        var failure = parts[5];
        if (condition == ServerStageCondition.Failed)
        {
            if (!TryCanonical(parts[4], out retryAt) || retryAt > MaximumRetryAtUnixSeconds) { diagnostic = "Server stage state has an invalid retry time"; return DecodeResult.Malformed; }
            if (!ValidFailure(failure)) { diagnostic = "Server stage state has an invalid failure text"; return DecodeResult.Malformed; }
        }
        else if (parts[4] != "-" || failure.Length != 0) { diagnostic = "Server stage state carries a failure while not failed"; return DecodeResult.Malformed; }
        state = new ServerStageState(parts[0], sequence, condition, stage, failure, retryAt);
        return DecodeResult.Decoded;
    }

    /// <summary>A diagnostic made safe to send: control characters become spaces, and a long one is cut at a whole character with "…".</summary>
    public static string Bound(string? failure)
    {
        var text = new StringBuilder((failure ?? "").Length);
        foreach (var c in failure ?? "") text.Append(char.IsControl(c) ? ' ' : c);
        var cleaned = Paired(text.ToString()).Trim();
        if (cleaned.Length == 0) return "No reason given.";
        if (cleaned.Length <= MaximumFailureCharacters) return cleaned;
        var cut = MaximumFailureCharacters - 1;
        if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
        return cleaned.Substring(0, cut) + "…";
    }

    internal static bool ValidSession(string? session)
    {
        if (session == null || session.Length != 32) return false;
        foreach (var c in session) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
        return true;
    }

    internal static bool ValidFailure(string failure)
    {
        if (failure.Length == 0 || failure.Length > MaximumFailureCharacters || failure.Trim().Length == 0) return false;
        for (var i = 0; i < failure.Length; i++)
        {
            var c = failure[i];
            if (char.IsControl(c)) return false;
            if (char.IsHighSurrogate(c)) { if (i + 1 >= failure.Length || !char.IsLowSurrogate(failure[i + 1])) return false; i++; }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    private static string Paired(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { result.Append(c).Append(text[++i]); }
            else if (char.IsSurrogate(c)) result.Append('?');
            else result.Append(c);
        }
        return result.ToString();
    }

    // "yss" + a version number other than 1 + ':': a format this version can't read, which is never shown as a failure.
    private static bool OtherVersion(string value)
    {
        if (!value.StartsWith("yss", StringComparison.Ordinal)) return false;
        var colon = value.IndexOf(':');
        if (colon <= 3 || colon > 12) return false;
        for (var i = 3; i < colon; i++) if (value[i] < '0' || value[i] > '9') return false;
        return true;
    }

    // A canonical non-negative Int64: "0", or digits without a leading zero or sign.
    private static bool TryCanonical(string text, out long number)
    {
        number = 0;
        if (text.Length == 0 || text.Length > 19 || text.Length > 1 && text[0] == '0') return false;
        foreach (var c in text) if (c < '0' || c > '9') return false;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static string Condition(ServerStageCondition condition) => condition switch
    {
        ServerStageCondition.Loading => "loading",
        ServerStageCondition.Failed => "failed",
        _ => "ready"
    };

    public static string Stage(ServerStage stage) => stage switch
    {
        ServerStage.Catalog => "catalog",
        ServerStage.Sources => "sources",
        ServerStage.Classification => "classification",
        ServerStage.Snapshot => "snapshot",
        _ => "none"
    };
}

/// <summary>
/// X62: the client's view of the server's stage state. Keeps the latest valid value; one from a session other than the
/// current assignment session, or an older sequence, is dropped. An empty, unknown-version or malformed value clears
/// what it held, so the client falls back to today's "still loading" text and never shows a failure it can't read.
/// Main-thread owned.
/// </summary>
public sealed class ServerStageReceiver
{
    public ServerStageState? State { get; private set; }
    /// <summary>The last value was malformed; <see cref="Diagnostic"/> says why, for one log warning.</summary>
    public bool Rejected { get; private set; }
    public string Diagnostic { get; private set; } = "";

    public void Reset() { State = null; Rejected = false; Diagnostic = ""; }

    /// <summary>
    /// Takes a value from the server. <paramref name="session"/> is the assignment session the client has bound to, or
    /// null before the first assignment value arrives. Returns true when what it holds changed.
    /// </summary>
    public bool Receive(string? value, string? session)
    {
        var result = ServerStageCodec.TryDecode(value, out var next, out var diagnostic);
        if (result != ServerStageCodec.DecodeResult.Decoded)
        {
            var changed = State != null || Rejected != (result == ServerStageCodec.DecodeResult.Malformed);
            State = null; Rejected = result == ServerStageCodec.DecodeResult.Malformed; Diagnostic = diagnostic;
            return changed;
        }
        if (session != null && next!.Session != session) return false;
        if (State != null && State.Session == next!.Session && next.Sequence <= State.Sequence) return false;
        State = next; Rejected = false; Diagnostic = "";
        return true;
    }

    /// <summary>The held state, only when it belongs to <paramref name="session"/> (the current assignment session).</summary>
    public ServerStageState? For(string? session) => session != null && State != null && State.Session == session ? State : null;
}

/// <summary>One loading stage as a server sees it: finished, or failed and waiting for a slow retry (D144).</summary>
public readonly struct StageFacts
{
    public bool Done { get; }
    /// <summary>The diagnostic while the stage waits for a slow retry, else null (loading or quick-retrying).</summary>
    public string? Failure { get; }
    /// <summary>Seconds until the failed stage tries again; negative when nothing is scheduled.</summary>
    public double RetryInSeconds { get; }
    public StageFacts(bool done, string? failure = null, double retryInSeconds = -1) { Done = done; Failure = failure; RetryInSeconds = retryInSeconds; }
}

/// <summary>X62: what a server publishes about its stages, before it gets a session, a sequence and a retry time.</summary>
public sealed class ServerStageReport : IEquatable<ServerStageReport>
{
    public ServerStageCondition Condition { get; }
    public ServerStage Stage { get; }
    public string Failure { get; }
    public double RetryInSeconds { get; }
    private ServerStageReport(ServerStageCondition condition, ServerStage stage, string failure, double retryInSeconds)
    { Condition = condition; Stage = stage; Failure = failure; RetryInSeconds = retryInSeconds; }

    /// <summary>
    /// The first stage in pipeline order that failed wins; else ready when <paramref name="categoriesReady"/>; else the
    /// first stage that isn't done is loading (the snapshot when every earlier one is).
    /// </summary>
    public static ServerStageReport Of(bool categoriesReady, StageFacts catalog, StageFacts sources, StageFacts classification, StageFacts snapshot)
    {
        var stages = new[] { (ServerStage.Catalog, catalog), (ServerStage.Sources, sources), (ServerStage.Classification, classification), (ServerStage.Snapshot, snapshot) };
        foreach (var (stage, facts) in stages)
            if (facts.Failure != null)
                return new ServerStageReport(ServerStageCondition.Failed, stage, ServerStageCodec.Bound(facts.Failure),
                    double.IsNaN(facts.RetryInSeconds) || facts.RetryInSeconds < 0 ? 0 : Math.Min(facts.RetryInSeconds, 3600));
        if (categoriesReady) return new ServerStageReport(ServerStageCondition.Ready, ServerStage.None, "", 0);
        foreach (var (stage, facts) in stages) if (!facts.Done) return new ServerStageReport(ServerStageCondition.Loading, stage, "", 0);
        return new ServerStageReport(ServerStageCondition.Loading, ServerStage.Snapshot, "", 0);
    }

    /// <summary>Same condition, stage and failure; the retry time is left out, as it counts down every frame.</summary>
    public bool Equals(ServerStageReport? other) => other != null && other.Condition == Condition && other.Stage == Stage && other.Failure == Failure;
    public override bool Equals(object? obj) => Equals(obj as ServerStageReport);
    public override int GetHashCode() => ((int)Condition * 8 + (int)Stage) ^ StringComparer.Ordinal.GetHashCode(Failure);

    /// <summary>The value to publish, with the retry time fixed as UTC Unix seconds at <paramref name="nowUnixSeconds"/>.</summary>
    public ServerStageState ToState(string session, long sequence, long nowUnixSeconds) =>
        new(session, sequence, Condition, Stage, Failure,
            Condition == ServerStageCondition.Failed ? Math.Min(Math.Max(0, nowUnixSeconds) + (long)Math.Ceiling(RetryInSeconds), ServerStageCodec.MaximumRetryAtUnixSeconds) : 0);
}
