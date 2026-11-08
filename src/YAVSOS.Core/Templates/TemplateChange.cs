using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace YAVSOS.Core.Templates;

/// <summary>What an admin asks the server to do to its list.</summary>
public enum TemplateOperation { Share, Update, Rename, Delete }

/// <summary>
/// X23 (D157): one change an admin's game asks of the server: Share (a copy of a personal template), Update (from the
/// open chest), Rename or Delete. Every change but Share names the template's ID and the revision the admin saw, so a
/// change made against an older revision is refused (two admins can't overwrite each other). Immutable.
/// </summary>
public sealed class TemplateChangeRequest
{
    public ulong RequestId { get; }
    public TemplateOperation Operation { get; }
    /// <summary>The server template changed (Update, Rename, Delete); "" for Share.</summary>
    public string Id { get; }
    /// <summary>The revision the admin saw (Update, Rename, Delete); 0 for Share.</summary>
    public ulong Seen { get; }
    /// <summary>The new name (Rename); "" otherwise.</summary>
    public string Name { get; }
    /// <summary>The template to share or whose rule to take (Share, Update); null otherwise. Its ID and revision are ignored.</summary>
    public RuleTemplate? Template { get; }

    private TemplateChangeRequest(ulong request, TemplateOperation operation, string id, ulong seen, string name, RuleTemplate? template)
    {
        RequestId = request; Operation = operation; Id = id; Seen = seen; Name = name; Template = template;
    }

    public static TemplateChangeRequest Share(ulong request, RuleTemplate template) =>
        new TemplateChangeRequest(request, TemplateOperation.Share, "", 0, "", template ?? throw new ArgumentNullException(nameof(template)));

    public static TemplateChangeRequest Update(ulong request, string id, ulong seen, RuleTemplate content) =>
        new TemplateChangeRequest(request, TemplateOperation.Update, CheckedId(id), CheckedSeen(seen), "", content ?? throw new ArgumentNullException(nameof(content)));

    public static TemplateChangeRequest Rename(ulong request, string id, ulong seen, string name) =>
        new TemplateChangeRequest(request, TemplateOperation.Rename, CheckedId(id), CheckedSeen(seen), TemplateNames.Clean(name) ?? throw new ArgumentException("Invalid template name.", nameof(name)), null);

    public static TemplateChangeRequest Delete(ulong request, string id, ulong seen) =>
        new TemplateChangeRequest(request, TemplateOperation.Delete, CheckedId(id), CheckedSeen(seen), "", null);

    private static string CheckedId(string id) => TemplateIds.Valid(id) ? id : throw new ArgumentException("Invalid template ID.", nameof(id));
    private static ulong CheckedSeen(ulong seen) => seen != 0 ? seen : throw new ArgumentOutOfRangeException(nameof(seen));

    internal static TemplateChangeRequest Of(ulong request, TemplateOperation operation, string id, ulong seen, string name, RuleTemplate? template) =>
        operation switch
        {
            TemplateOperation.Share => Share(request, template ?? throw new InvalidDataException("a share holds no template")),
            TemplateOperation.Update => Update(request, id, seen, template ?? throw new InvalidDataException("an update holds no template")),
            TemplateOperation.Rename => Rename(request, id, seen, name),
            _ => Delete(request, id, seen)
        };
}

/// <summary>
/// The wire form of a change request (XML, the template in the list's own form) and of its reply
/// (<c>ytc1:{request}:{outcome}:{id}:{revision}</c>). Both are bounded and read strictly; anything else is malformed.
/// </summary>
public static class TemplateChangeCodec
{
    /// <summary>A request: one template and a little more.</summary>
    public const int MaximumRequestBytes = TemplateLimits.TemplateBytes + 4096;
    public const int MaximumReplyBytes = 128;
    private const string Root = "yavsos-template-change", ReplyPrefix = "ytc1:";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

    public static byte[] Encode(TemplateChangeRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var text = RuleTemplateCodec.Write(writer =>
        {
            writer.WriteStartElement(Root);
            writer.WriteAttributeString("schema", "1");
            writer.WriteAttributeString("request", request.RequestId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("operation", request.Operation.ToString().ToLowerInvariant());
            if (request.Id.Length != 0) writer.WriteAttributeString("id", request.Id);
            if (request.Seen != 0) writer.WriteAttributeString("seen", request.Seen.ToString(CultureInfo.InvariantCulture));
            if (request.Name.Length != 0) writer.WriteAttributeString("name", request.Name);
            if (request.Template != null) RuleTemplateCodec.WriteTemplate(writer, request.Template);
            writer.WriteEndElement();
        });
        return Utf8.GetBytes(text);
    }

    public static bool TryDecode(byte[]? bytes, out TemplateChangeRequest? request)
    {
        request = null;
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumRequestBytes) return false;
        try
        {
            var root = RuleTemplateCodec.Parse(Utf8.GetString(bytes), MaximumRequestBytes);
            if (root.Name != Root) return false;
            RuleTemplateCodec.Only(root, "schema", "request", "operation", "id", "seen", "name");
            if ((string?)root.Attribute("schema") != "1") return false;
            var id = RuleTemplateCodec.Unsigned(root, "request");
            var operation = (string?)root.Attribute("operation") switch
            {
                "share" => TemplateOperation.Share, "update" => TemplateOperation.Update, "rename" => TemplateOperation.Rename,
                "delete" => TemplateOperation.Delete, _ => throw new InvalidDataException("unknown operation")
            };
            var seen = root.Attribute("seen") is { } s ? ulong.Parse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;
            var elements = root.Elements().ToArray();
            if (elements.Length > 1 || root.Nodes().Any(x => x is not XElement && !(x is XText { Value: var t } && string.IsNullOrWhiteSpace(t)))) return false;
            var template = elements.Length == 1 ? elements[0].Name == "template" ? RuleTemplateCodec.ReadTemplate(elements[0]) : throw new InvalidDataException("unknown element") : null;
            if ((operation is TemplateOperation.Share or TemplateOperation.Update) != (template != null)) return false;
            request = TemplateChangeRequest.Of(id, operation, (string?)root.Attribute("id") ?? "", seen, (string?)root.Attribute("name") ?? "", template);
            return true;
        }
        catch (Exception error) when (error is XmlException || error is InvalidDataException || error is ArgumentException || error is FormatException ||
                                      error is OverflowException || error is DecoderFallbackException || error is InvalidOperationException)
        { request = null; return false; }
    }

    public static byte[] EncodeReply(TemplateChangeReply reply) => Utf8.GetBytes(string.Join(":",
        ReplyPrefix.TrimEnd(':'), reply.RequestId.ToString(CultureInfo.InvariantCulture), ((int)reply.Outcome).ToString(CultureInfo.InvariantCulture),
        reply.Id.Length == 0 ? "-" : reply.Id, reply.Revision.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecodeReply(byte[]? bytes, out TemplateChangeReply reply)
    {
        reply = default;
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumReplyBytes) return false;
        string text;
        try { text = Utf8.GetString(bytes); } catch (DecoderFallbackException) { return false; }
        var parts = text.Split(':');
        if (parts.Length != 5 || parts[0] + ":" != ReplyPrefix) return false;
        if (!ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var request)) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var outcome) || !Enum.IsDefined(typeof(TemplateOutcome), outcome)) return false;
        var id = parts[3] == "-" ? "" : parts[3];
        if (id.Length != 0 && !TemplateIds.Valid(id)) return false;
        if (!ulong.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) return false;
        reply = new TemplateChangeReply(request, (TemplateOutcome)outcome, id, revision);
        return true;
    }
}

/// <summary>The server's answer: the outcome and, when done, the template's ID and its new revision (0 after a delete).</summary>
public readonly struct TemplateChangeReply
{
    public ulong RequestId { get; }
    public TemplateOutcome Outcome { get; }
    public string Id { get; }
    public ulong Revision { get; }
    public TemplateChangeReply(ulong request, TemplateOutcome outcome, string id, ulong revision)
    {
        RequestId = request; Outcome = outcome; Id = id ?? ""; Revision = revision;
    }
}

/// <summary>
/// X23 (D157): the server's checks on one change, in order: it can write its list (it read the file, D153), the sender is
/// an admin, then the change itself: the template it names still has the revision the sender saw ("this template
/// changed, look again"), the name is free, and the list stays within its limits. A share gets a new ID and revision 1,
/// with the sender's name as its author. Pure: the caller saves the file and publishes the list when it is done.
/// </summary>
public static class TemplateServerRules
{
    public static TemplateEdit Decide(TemplateList? current, TemplateChangeRequest request, bool admin, string by, Func<int, int> random)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (current == null) return new TemplateEdit(TemplateOutcome.Unreadable, TemplateList.Empty(TemplateListKind.Server), null);
        if (current.Kind != TemplateListKind.Server) throw new ArgumentException("Not the server's list.", nameof(current));
        if (!admin) return new TemplateEdit(TemplateOutcome.NotAdmin, current, null);
        switch (request.Operation)
        {
            case TemplateOperation.Share:
                var author = new string((by ?? "").Where(c => !char.IsControl(c)).Take(TemplateLimits.ByLength).ToArray());
                return current.Add(request.Template!.With(id: current.NewId(random), revision: 1, by: author));
            case TemplateOperation.Update: return current.Replace(request.Id, request.Template!, request.Seen);
            case TemplateOperation.Rename: return current.Rename(request.Id, request.Name, request.Seen);
            default: return current.Delete(request.Id, request.Seen);
        }
    }

    /// <summary>The reply for a decision.</summary>
    public static TemplateChangeReply Reply(TemplateChangeRequest request, TemplateEdit edit) => new TemplateChangeReply(request.RequestId, edit.Outcome,
        edit.Done ? edit.Template?.Id ?? request.Id : request.Id, edit.Done ? edit.Template?.Revision ?? 0 : 0);
}

/// <summary>Changes a server accepts from one sender: a few at once, then one a second (admins change templates by hand).</summary>
public sealed class TemplateChangeRate
{
    public const int Burst = 4;
    public const double PerSecond = 1;
    public const int MaximumSenders = 256;
    private readonly Dictionary<string, (double Tokens, double At)> senders = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

    public bool Allow(string sender, double now)
    {
        if (senders.TryGetValue(sender, out var state)) state = (Math.Min(Burst, state.Tokens + Math.Max(0, now - state.At) * PerSecond), now);
        else
        {
            if (senders.Count >= MaximumSenders) senders.Clear(); // Idle buckets refill; forgetting them only forgives.
            state = (Burst, now);
        }
        var allowed = state.Tokens >= 1;
        senders[sender] = allowed ? (state.Tokens - 1, now) : state;
        return allowed;
    }

    public void Clear() => senders.Clear();
}

/// <summary>
/// The admin's side: one change waits at a time, for <see cref="AnswerSeconds"/>. A reply counts only for the request
/// waiting now; a late reply (after the wait, a disconnect or a world change, which clear it) is dropped.
/// </summary>
public sealed class TemplateRequestTracker
{
    public const double AnswerSeconds = 10;
    private ulong next;
    public TemplateChangeRequest? Waiting { get; private set; }
    public double SentAt { get; private set; }

    /// <param name="seed">Where request numbers start this session (from the clock), so a reply from an earlier session never matches.</param>
    public TemplateRequestTracker(ulong seed = 1) => next = Math.Max(1, seed);

    public bool Busy => Waiting != null;
    public ulong NextId() => next++;

    /// <summary>False while another change waits.</summary>
    public bool Start(TemplateChangeRequest request, double now)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (Waiting != null) return false;
        Waiting = request; SentAt = now;
        return true;
    }

    /// <summary>The request a reply answers, which then stops waiting; null for a reply nothing waits for.</summary>
    public TemplateChangeRequest? Reply(TemplateChangeReply reply)
    {
        if (Waiting == null || Waiting.RequestId != reply.RequestId) return null;
        var answered = Waiting; Waiting = null;
        return answered;
    }

    /// <summary>The request that waited too long, which then stops waiting; null while there's time.</summary>
    public TemplateChangeRequest? Expired(double now)
    {
        if (Waiting == null || now - SentAt < AnswerSeconds) return null;
        var lost = Waiting; Waiting = null;
        return lost;
    }

    public void Clear() => Waiting = null;
}
