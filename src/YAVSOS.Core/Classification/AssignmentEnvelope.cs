using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace YAVSOS.Core.Classification;

/// <summary>Connection-scoped ordering of complete snapshots and withdrawals, independent of fragment delivery.</summary>
public sealed class AssignmentEnvelopeReceiver
{
    private string? session;
    private long sequence;
    public string Payload { get; private set; } = "";
    public string Diagnostic { get; private set; } = "Authoritative assignment delivery pending.";
    public void Reset() { session = null; sequence = 0; Payload = ""; Diagnostic = "Authoritative assignment delivery pending."; }
    public bool Receive(string envelope)
    {
        try
        {
            AssignmentEnvelope.Decode(envelope, out var nextSession, out var nextSequence, out var payload);
            if (session != null && (session != nextSession || nextSequence <= sequence)) return false;
            session = nextSession; sequence = nextSequence; Payload = payload;
            Diagnostic = payload.Length == 0 ? "Server withdrew authoritative assignment evidence." : "Complete authoritative assignment delivery pending validation.";
            return true;
        }
        catch (InvalidDataException error)
        { Payload = ""; Diagnostic = "Authoritative assignment envelope rejected: " + error.Message; return true; }
    }
}

public static class AssignmentEnvelope
{
    public const string Version = "yae1";
    public const int MaximumCharacters = AuthoritativeAssignmentsCodec.MaximumWireCharacters + 58;
    public static string Encode(string session, long sequence, string payload)
    {
        RequireSession(session);
        if (sequence <= 0 || payload == null || payload.Length > AuthoritativeAssignmentsCodec.MaximumWireCharacters)
            throw new InvalidDataException("Invalid assignment envelope sequence/payload bound.");
        return Version + ":" + session + ":" + sequence.ToString(CultureInfo.InvariantCulture) + ":" + payload;
    }
    public static void Decode(string envelope, out string session, out long sequence, out string payload)
    {
        if (envelope == null || envelope.Length > MaximumCharacters || !envelope.StartsWith(Version + ":", StringComparison.Ordinal))
            throw new InvalidDataException("Missing, oversized or unsupported assignment envelope.");
        if (envelope.Length < 40 || envelope[37] != ':') throw new InvalidDataException("Malformed assignment envelope.");
        var separator = envelope.IndexOf(':', 38);
        if (separator < 39) throw new InvalidDataException("Malformed assignment envelope.");
        session = envelope.Substring(5, 32); RequireSession(session);
        var number = envelope.Substring(38, separator - 38);
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence <= 0 ||
            number != sequence.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("Invalid assignment envelope sequence.");
        payload = envelope.Substring(separator + 1);
        if (payload.Length > AuthoritativeAssignmentsCodec.MaximumWireCharacters) throw new InvalidDataException("Assignment envelope payload bound exceeded.");
    }
    private static void RequireSession(string session)
    {
        if (session == null || session.Length != 32 || session.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid assignment session identity.");
    }
}
