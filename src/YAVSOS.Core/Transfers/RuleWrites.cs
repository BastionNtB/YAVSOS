using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YAVSOS.Core.Transfers;

/// <summary>X32 (D154): what a remote rule write asks the chest's owner to do.</summary>
public enum RuleWriteOperation : byte { WriteRule = 1, SetCleanupOptOut = 2 }

/// <summary>
/// X32 (D154): the owner's answer to a remote rule write, and the requester's own endings (no answer, gave up). The
/// wire values are fixed: a reply carries the byte.
/// </summary>
public enum RuleWriteOutcome : byte
{
    /// <summary>The value is on the chest now (written, or already there).</summary>
    Done = 1,
    /// <summary>The receiving game doesn't own the chest (ownership moved while the request travelled).</summary>
    NotOwner = 2,
    /// <summary>The receiving game hasn't loaded the chest (D154: a dedicated server far from it): try again in a moment.</summary>
    NotLoaded = 3,
    NotEligible = 4,
    AccessDenied = 5,
    /// <summary>Too many requests from one player (more than <see cref="RuleWriteRate.PerSecond"/> a second).</summary>
    Busy = 6,
    /// <summary>The chest's rule isn't the one the requester saw.</summary>
    RuleChanged = 7,
    /// <summary>The value isn't a payload the owner may write (unreadable envelope, too large, not a newer revision).</summary>
    Invalid = 8,
    /// <summary>A protocol or operation the receiving game doesn't know.</summary>
    Unsupported = 9,
    /// <summary>Written, but the read-back differed or ownership left during the write.</summary>
    Unconfirmed = 10,
    /// <summary>Requester only: no answer and no sight of the value within <see cref="RuleWriteTracker{TKey}.AnswerSeconds"/>.</summary>
    NoAnswer = 100,
    /// <summary>Requester only: still not seen after <see cref="RuleWriteTracker{TKey}.WatchSeconds"/> in all.</summary>
    GaveUp = 101,
}

/// <summary>X32 (D154): one remote rule write, as built by the requester's game. Immutable.</summary>
public sealed class RuleWriteRequest
{
    public const int Protocol = 1;
    public const int DigestBytes = 32;
    public int Version { get; }
    public long RequestId { get; }
    /// <summary>The requester's profile player ID, which the owner checks against the chest's privacy (as vanilla's open request).</summary>
    public long PlayerId { get; }
    public RuleWriteOperation Operation { get; }
    /// <summary>SHA-256 of the payload the requester saw (<see cref="RuleWriteOperation.WriteRule"/>); empty for the opt-out.</summary>
    public byte[] Expected { get; }
    /// <summary>The new payload: "" removes the rule (<see cref="RuleWriteOperation.WriteRule"/>).</summary>
    public string Value { get; }
    /// <summary>The new opt-out (<see cref="RuleWriteOperation.SetCleanupOptOut"/>).</summary>
    public bool OptOut { get; }

    private RuleWriteRequest(int version, long requestId, long playerId, RuleWriteOperation operation, byte[] expected, string value, bool optOut)
    { Version = version; RequestId = requestId; PlayerId = playerId; Operation = operation; Expected = expected; Value = value; OptOut = optOut; }

    public static RuleWriteRequest WriteRule(long requestId, long playerId, string expectedPayload, string value)
    {
        if (expectedPayload == null) throw new ArgumentNullException(nameof(expectedPayload));
        if (value == null) throw new ArgumentNullException(nameof(value));
        return new RuleWriteRequest(Protocol, requestId, playerId, RuleWriteOperation.WriteRule, RuleWriteCodec.Digest(expectedPayload), value, false);
    }

    public static RuleWriteRequest SetCleanupOptOut(long requestId, long playerId, bool optOut) =>
        new RuleWriteRequest(Protocol, requestId, playerId, RuleWriteOperation.SetCleanupOptOut, Array.Empty<byte>(), "", optOut);

    internal static RuleWriteRequest Decoded(int version, long requestId, long playerId, RuleWriteOperation operation, byte[] expected, string value, bool optOut) =>
        new RuleWriteRequest(version, requestId, playerId, operation, expected, value, optOut);
}

/// <summary>
/// X32 (D154): the bytes of a request and a reply. Bounded and strict: a package that doesn't decode is dropped
/// (request) or ignored (reply), never thrown into the game's network code.
/// </summary>
public static class RuleWriteCodec
{
    /// <summary>A request's largest encoding: the header, the digest and a 64 KiB value.</summary>
    public const int MaximumRequestBytes = 128 + RuleWriteRequest.DigestBytes + TransferLimits.MaximumPolicyBytes;
    public const int ReplyBytes = 4 + 8 + 1;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>SHA-256 of a payload's UTF-8 bytes: the compare-and-set's key, so the request needn't carry the old rule.</summary>
    public static byte[] Digest(string payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        using var sha = SHA256.Create();
        return sha.ComputeHash(Utf8.GetBytes(payload));
    }

    public static bool SameDigest(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    public static byte[] Encode(RuleWriteRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var value = Utf8.GetBytes(request.Value);
        if (value.Length > TransferLimits.MaximumPolicyBytes) throw new InvalidDataException("The rule is larger than a chest can hold.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, true))
        {
            writer.Write(request.Version);
            writer.Write(request.RequestId);
            writer.Write(request.PlayerId);
            writer.Write((byte)request.Operation);
            writer.Write((byte)request.Expected.Length);
            writer.Write(request.Expected);
            writer.Write(value.Length);
            writer.Write(value);
            writer.Write(request.OptOut);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// False for anything malformed or over the bounds. A request with another protocol number decodes far enough to
    /// be answered <see cref="RuleWriteOutcome.Unsupported"/> (its <see cref="RuleWriteRequest.Version"/> says so).
    /// </summary>
    public static bool TryDecode(byte[]? bytes, out RuleWriteRequest? request)
    {
        request = null;
        if (bytes == null || bytes.Length < 4 + 8 + 8 + 1 + 1 || bytes.Length > MaximumRequestBytes) return false;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, false), Utf8);
            var version = reader.ReadInt32();
            var id = reader.ReadInt64();
            var player = reader.ReadInt64();
            if (version != RuleWriteRequest.Protocol) { request = RuleWriteRequest.Decoded(version, id, player, 0, Array.Empty<byte>(), "", false); return true; }
            var operation = (RuleWriteOperation)reader.ReadByte();
            int digestLength = reader.ReadByte();
            if (digestLength != 0 && digestLength != RuleWriteRequest.DigestBytes) return false;
            var expected = reader.ReadBytes(digestLength);
            if (expected.Length != digestLength) return false;
            var valueLength = reader.ReadInt32();
            if (valueLength < 0 || valueLength > TransferLimits.MaximumPolicyBytes) return false;
            var raw = reader.ReadBytes(valueLength);
            if (raw.Length != valueLength) return false;
            var value = Utf8.GetString(raw);
            var optOut = reader.ReadBoolean();
            if (reader.BaseStream.Position != bytes.Length) return false;
            request = RuleWriteRequest.Decoded(version, id, player, operation, expected, value, optOut);
            return true;
        }
        catch (Exception error) when (error is EndOfStreamException || error is DecoderFallbackException || error is IOException) { return false; }
    }

    public static byte[] EncodeReply(long requestId, RuleWriteOutcome outcome)
    {
        var bytes = new byte[ReplyBytes];
        BitConverter.GetBytes(RuleWriteRequest.Protocol).CopyTo(bytes, 0);
        BitConverter.GetBytes(requestId).CopyTo(bytes, 4);
        bytes[12] = (byte)outcome;
        if (!BitConverter.IsLittleEndian) { Array.Reverse(bytes, 0, 4); Array.Reverse(bytes, 4, 8); }
        return bytes;
    }

    public static bool TryDecodeReply(byte[]? bytes, out long requestId, out RuleWriteOutcome outcome)
    {
        requestId = 0; outcome = 0;
        if (bytes == null || bytes.Length != ReplyBytes) return false;
        var copy = (byte[])bytes.Clone();
        if (!BitConverter.IsLittleEndian) { Array.Reverse(copy, 0, 4); Array.Reverse(copy, 4, 8); }
        if (BitConverter.ToInt32(copy, 0) != RuleWriteRequest.Protocol) return false;
        requestId = BitConverter.ToInt64(copy, 4);
        outcome = (RuleWriteOutcome)copy[12];
        return outcome >= RuleWriteOutcome.Done && outcome <= RuleWriteOutcome.Unconfirmed;
    }
}

/// <summary>What the owner's game knows about the chest when a request arrives (X32 §3.2, D154).</summary>
public readonly struct RuleWriteOwnerView
{
    public bool Loaded { get; }
    public bool Owner { get; }
    public bool Eligible { get; }
    /// <summary><c>Container.CheckAccess(playerID)</c> for the request's player (the chest's privacy; wards are the requester's check, Q17).</summary>
    public bool Access { get; }
    public bool WithinRate { get; }
    public string CurrentPayload { get; }
    public bool CurrentOptOut { get; }

    public RuleWriteOwnerView(bool loaded, bool owner, bool eligible, bool access, bool withinRate, string currentPayload, bool currentOptOut)
    {
        Loaded = loaded; Owner = owner; Eligible = eligible; Access = access; WithinRate = withinRate;
        CurrentPayload = currentPayload ?? ""; CurrentOptOut = currentOptOut;
    }
}

/// <summary>The owner's decision: the outcome and, when it says to write, what.</summary>
public readonly struct RuleWriteDecision
{
    public RuleWriteOutcome Outcome { get; }
    /// <summary>True: write <see cref="Payload"/> (or the opt-out), read it back, then answer Done or Unconfirmed.</summary>
    public bool Write { get; }
    public string Payload { get; }
    public bool OptOut { get; }
    internal RuleWriteDecision(RuleWriteOutcome outcome, bool write, string payload, bool optOut)
    { Outcome = outcome; Write = write; Payload = payload; OptOut = optOut; }
    internal static RuleWriteDecision Answer(RuleWriteOutcome outcome) => new RuleWriteDecision(outcome, false, "", false);
}

/// <summary>
/// X32 §3.2 (D154): the owner's checks, in order, as a pure function. No step needs the owner's categories or a
/// local player, so a hosted world's host and a dedicated server answer like any client (Q19). The rules text inside
/// the envelope is not parsed (as for an undo), so a rule from another YAVSOS version passes through unread.
/// </summary>
public static class RuleWriteOwner
{
    public static RuleWriteDecision Decide(RuleWriteRequest request, RuleWriteOwnerView chest)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Version != RuleWriteRequest.Protocol ||
            request.Operation != RuleWriteOperation.WriteRule && request.Operation != RuleWriteOperation.SetCleanupOptOut)
            return RuleWriteDecision.Answer(RuleWriteOutcome.Unsupported);
        if (!chest.Loaded) return RuleWriteDecision.Answer(RuleWriteOutcome.NotLoaded);
        if (!chest.Owner) return RuleWriteDecision.Answer(RuleWriteOutcome.NotOwner);
        if (!chest.Eligible) return RuleWriteDecision.Answer(RuleWriteOutcome.NotEligible);
        if (!chest.Access) return RuleWriteDecision.Answer(RuleWriteOutcome.AccessDenied);
        if (!chest.WithinRate) return RuleWriteDecision.Answer(RuleWriteOutcome.Busy);
        if (request.Operation == RuleWriteOperation.SetCleanupOptOut)
            return chest.CurrentOptOut == request.OptOut ? RuleWriteDecision.Answer(RuleWriteOutcome.Done) : new RuleWriteDecision(RuleWriteOutcome.Done, true, "", request.OptOut);
        var current = chest.CurrentPayload;
        if (!RuleWriteCodec.SameDigest(RuleWriteCodec.Digest(current), request.Expected))
            return RuleWriteDecision.Answer(StringComparer.Ordinal.Equals(current, request.Value) ? RuleWriteOutcome.Done : RuleWriteOutcome.RuleChanged);
        if (StringComparer.Ordinal.Equals(current, request.Value)) return RuleWriteDecision.Answer(RuleWriteOutcome.Done);
        if (request.Value.Length != 0)
        {
            if (!ContainerPolicyEnvelope.TryRevision(request.Value, out var revision)) return RuleWriteDecision.Answer(RuleWriteOutcome.Invalid);
            // Above the current revision, so a value can't put back an older payload by chance (and the requester can
            // tell its own write from anyone else's).
            var now = current.Length != 0 && ContainerPolicyEnvelope.TryRevision(current, out var stored) ? stored : 0;
            if (revision <= now) return RuleWriteDecision.Answer(RuleWriteOutcome.Invalid);
        }
        return new RuleWriteDecision(RuleWriteOutcome.Done, true, request.Value, false);
    }
}

/// <summary>X32 §3.2 step 5: at most <see cref="PerSecond"/> requests a second from one player's game (a token bucket). Not thread-safe.</summary>
public sealed class RuleWriteRate
{
    public const int PerSecond = 20;
    public const int MaximumPeers = 512;
    private readonly Dictionary<long, (double Tokens, double At)> peers = new Dictionary<long, (double, double)>();

    public bool Allow(long peer, double now)
    {
        if (peers.TryGetValue(peer, out var state))
            state = (Math.Min(PerSecond, state.Tokens + Math.Max(0, now - state.At) * PerSecond), now);
        else
        {
            if (peers.Count >= MaximumPeers) Prune(now);
            state = (PerSecond, now);
        }
        var allowed = state.Tokens >= 1;
        peers[peer] = allowed ? (state.Tokens - 1, now) : state;
        return allowed;
    }

    public void Clear() => peers.Clear();
    public int Peers => peers.Count;

    // Peers idle for a second have a full bucket again, so forgetting them changes nothing.
    private void Prune(double now)
    {
        var idle = new List<long>();
        foreach (var pair in peers) if (now - pair.Value.At >= 1) idle.Add(pair.Key);
        foreach (var peer in idle) peers.Remove(peer);
        if (peers.Count >= MaximumPeers) peers.Clear();
    }
}

/// <summary>What a remote write was for, so the panel knows what to record once it is saved.</summary>
public enum RuleWriteKind { Edit, Undo, Redo, OptOut }

/// <summary>One waiting remote write, as the requester's game tracks it.</summary>
public sealed class RuleWritePending<TKey>
{
    public TKey Chest { get; }
    public long RequestId { get; internal set; }
    public long Target { get; internal set; }
    public RuleWriteKind Kind { get; }
    /// <summary>The payload the requester saw (the undo record's Before for an edit).</summary>
    public string Before { get; }
    /// <summary>The payload to write (or "" for the opt-out).</summary>
    public string Value { get; }
    public bool OptOut { get; }
    /// <summary>The panel control the write came from, as the panel numbers them; carried back unchanged.</summary>
    public int Control { get; }
    public double SentAt { get; }
    /// <summary>True until the value is seen, or <see cref="RuleWriteTracker{TKey}.AnswerSeconds"/> pass without an answer: the chest's controls take no clicks.</summary>
    public bool Blocking { get; internal set; } = true;
    /// <summary>
    /// When the owner answered Done. The answer usually arrives before the chest's synced rule does (the reply is routed
    /// at once, the rule goes out with the next sync), so the write keeps waiting for the value, up to
    /// <see cref="RuleWriteTracker{TKey}.ConfirmedSeconds"/> after the answer.
    /// </summary>
    public double? AnsweredAt { get; internal set; }
    /// <summary>The 5 s passed without an answer, so the panel said "No answer yet"; a later save clears that.</summary>
    public bool WasUnanswered { get; internal set; }
    internal int Sends { get; set; } = 1;

    internal RuleWritePending(TKey chest, long requestId, long target, RuleWriteKind kind, string before, string value, bool optOut, int control, double sentAt)
    { Chest = chest; RequestId = requestId; Target = target; Kind = kind; Before = before; Value = value; OptOut = optOut; Control = control; SentAt = sentAt; }
}

/// <summary>A remote write that has ended, for the panel: the outcome and the pending entry it ended.</summary>
public readonly struct RuleWriteSettled<TKey>
{
    public RuleWritePending<TKey> Write { get; }
    public RuleWriteOutcome Outcome { get; }
    /// <summary>True: the write is over. False (<see cref="RuleWriteOutcome.NoAnswer"/>): the chest takes clicks again, and the value is still watched for.</summary>
    public bool Final { get; }
    internal RuleWriteSettled(RuleWritePending<TKey> write, RuleWriteOutcome outcome, bool final) { Write = write; Outcome = outcome; Final = final; }
}

/// <summary>
/// X32 §3.3 (D154): the requester's waiting writes, one per chest, with a fake-able clock. A write ends on the first of
/// its reply, the sight of its value on the chest (a lost reply), or the watch window: <see cref="AnswerSeconds"/> for an
/// answer, after which the chest takes clicks again and the value is still watched for, until
/// <see cref="WatchSeconds"/> in all (as YAVSOS's moves through MultiUserChest). A <see cref="RuleWriteOutcome.NotOwner"/>
/// answer is sent once more to the chest's new owner. A reply for no waiting request, or from a game it wasn't sent to,
/// is dropped. Not thread-safe.
/// </summary>
public sealed class RuleWriteTracker<TKey> where TKey : notnull
{
    public const double AnswerSeconds = 5;
    /// <summary>After a Done answer, how long the write waits to see its value before settling Done anyway (the owner confirmed it).</summary>
    public const double ConfirmedSeconds = 5;
    public const double WatchSeconds = DelegatedMoveObserver.DefaultWindowSeconds;
    /// <summary>At most this many writes wait at once (X32 §3.6), so a burst can't flood the owners.</summary>
    public const int MaximumWaiting = 4;
    public const int MaximumSends = 2;

    private readonly Dictionary<TKey, RuleWritePending<TKey>> waiting = new Dictionary<TKey, RuleWritePending<TKey>>();
    private long nextId;

    public int Count => waiting.Count;
    public IEnumerable<RuleWritePending<TKey>> Waiting => waiting.Values;

    public RuleWritePending<TKey>? For(TKey chest) => waiting.TryGetValue(chest, out var write) ? write : null;
    public bool Blocking(TKey chest) => waiting.TryGetValue(chest, out var write) && write.Blocking;
    /// <summary>
    /// A new write for this chest may start: none blocks it, and fewer than <see cref="MaximumWaiting"/> wait in all. A
    /// write that stopped blocking ("No answer yet") is replaced by the new one: a late old write can't overwrite the newer
    /// rule, because the owner's compare-and-set refuses it.
    /// </summary>
    public bool CanStart(TKey chest) =>
        waiting.TryGetValue(chest, out var old) ? !old.Blocking : waiting.Count < MaximumWaiting;

    /// <summary>Records a write about to be sent and returns it; its <see cref="RuleWritePending{TKey}.RequestId"/> goes in the request.</summary>
    public RuleWritePending<TKey> Start(TKey chest, long target, RuleWriteKind kind, string before, string value, bool optOut, int control, double now)
    {
        if (!CanStart(chest)) throw new InvalidOperationException("A write is already waiting for this chest, or too many are waiting.");
        if (target == 0) throw new ArgumentOutOfRangeException(nameof(target), "Target 0 sends to every game.");
        waiting.Remove(chest); // An unanswered earlier write stops being followed, silently.
        var write = new RuleWritePending<TKey>(chest, ++nextId, target, kind, before ?? "", value ?? "", optOut, control, now);
        waiting[chest] = write;
        return write;
    }

    /// <summary>
    /// A reply arrived. Returns the settled write, or null when the reply matches nothing waiting. For
    /// <see cref="RuleWriteOutcome.NotOwner"/> with a new owner and a send left, <paramref name="resend"/> is the write
    /// to send again (with a new request ID, to <paramref name="currentOwner"/>) and nothing settles.
    /// </summary>
    /// <remarks>A Done answer settles nothing yet: the write waits to see its value (<see cref="RuleWritePending{TKey}.AnsweredAt"/>, <see cref="Step"/>).</remarks>
    public RuleWriteSettled<TKey>? Reply(long sender, long requestId, RuleWriteOutcome outcome, long currentOwner, double now, out RuleWritePending<TKey>? resend)
    {
        resend = null;
        RuleWritePending<TKey>? write = null;
        foreach (var candidate in waiting.Values)
            if (candidate.RequestId == requestId) { write = candidate; break; }
        if (write == null || write.Target != sender) return null;
        if (outcome == RuleWriteOutcome.NotOwner && currentOwner != 0 && currentOwner != write.Target && write.Sends < MaximumSends)
        {
            write.Sends++; write.Target = currentOwner; write.RequestId = ++nextId;
            resend = write;
            return null;
        }
        if (outcome == RuleWriteOutcome.Done) { write.AnsweredAt ??= now; return null; }
        waiting.Remove(write.Chest);
        return new RuleWriteSettled<TKey>(write, outcome, true);
    }

    /// <summary>
    /// Once a frame: <paramref name="seen"/> says whether a chest now holds its write's value (null: the chest can't be
    /// read here, which counts as not seen). Returns what ended or stopped blocking.
    /// </summary>
    public List<RuleWriteSettled<TKey>> Step(double now, Func<RuleWritePending<TKey>, bool?> seen)
    {
        if (seen == null) throw new ArgumentNullException(nameof(seen));
        var settled = new List<RuleWriteSettled<TKey>>();
        if (waiting.Count == 0) return settled;
        foreach (var write in new List<RuleWritePending<TKey>>(waiting.Values))
        {
            if (seen(write) == true) { waiting.Remove(write.Chest); settled.Add(new RuleWriteSettled<TKey>(write, RuleWriteOutcome.Done, true)); continue; }
            if (write.AnsweredAt is { } answered && now - answered >= ConfirmedSeconds)
            { waiting.Remove(write.Chest); settled.Add(new RuleWriteSettled<TKey>(write, RuleWriteOutcome.Done, true)); continue; }
            var age = now - write.SentAt;
            if (age >= WatchSeconds && write.AnsweredAt == null) { waiting.Remove(write.Chest); settled.Add(new RuleWriteSettled<TKey>(write, RuleWriteOutcome.GaveUp, true)); continue; }
            if (write.Blocking && write.AnsweredAt == null && age >= AnswerSeconds) { write.Blocking = false; write.WasUnanswered = true; settled.Add(new RuleWriteSettled<TKey>(write, RuleWriteOutcome.NoAnswer, false)); }
        }
        return settled;
    }

    /// <summary>Leaving the world: every waiting write is dropped without an outcome.</summary>
    public void Clear() => waiting.Clear();

    /// <summary>This game wrote the chest itself (it owns it now): an unanswered earlier remote write stops being followed, silently.</summary>
    public void Forget(TKey chest)
    {
        if (waiting.TryGetValue(chest, out var write) && !write.Blocking) waiting.Remove(chest);
    }
}

/// <summary>What the panel does with a remote write's outcome (X32): the undo record, and the strip's message.</summary>
public readonly struct RuleWriteEffect
{
    /// <summary>Record the change (Edit: before to value) or the restore (Undo, Redo: value).</summary>
    public bool Record { get; }
    /// <summary>Drop the chest's undo record: the rule changed since it.</summary>
    public bool DropRecord { get; }
    /// <summary>The strip message key for the chest, if it is open; null says nothing.</summary>
    public string? MessageKey { get; }
    /// <summary>Clear an earlier "No answer yet" (the change turned out to be saved).</summary>
    public bool ClearMessage { get; }
    internal RuleWriteEffect(bool record, bool drop, string? key, bool clear) { Record = record; DropRecord = drop; MessageKey = key; ClearMessage = clear; }

    public static RuleWriteEffect Of<TKey>(RuleWriteSettled<TKey> settled)
    {
        var write = settled.Write;
        var restore = write.Kind == RuleWriteKind.Undo || write.Kind == RuleWriteKind.Redo;
        if (settled.Outcome == RuleWriteOutcome.Done)
            return new RuleWriteEffect(write.Kind != RuleWriteKind.OptOut, false, null, write.WasUnanswered);
        var key = settled.Outcome switch
        {
            RuleWriteOutcome.RuleChanged => write.Kind switch
            {
                RuleWriteKind.Undo => "yavsos_edit_undo_changed", RuleWriteKind.Redo => "yavsos_edit_redo_changed", _ => "yavsos_edit_rule_changed"
            },
            RuleWriteOutcome.NoAnswer => "yavsos_edit_no_answer",
            RuleWriteOutcome.GaveUp or RuleWriteOutcome.Unconfirmed => "yavsos_edit_unconfirmed",
            RuleWriteOutcome.NotOwner or RuleWriteOutcome.NotLoaded => "yavsos_edit_try_again",
            RuleWriteOutcome.NotEligible => "yavsos_edit_not_eligible",
            RuleWriteOutcome.AccessDenied => "yavsos_edit_no_access",
            RuleWriteOutcome.Busy => "yavsos_edit_busy",
            _ => "yavsos_edit_remote_refused"
        };
        return new RuleWriteEffect(false, restore && settled.Outcome == RuleWriteOutcome.RuleChanged, key, false);
    }
}
