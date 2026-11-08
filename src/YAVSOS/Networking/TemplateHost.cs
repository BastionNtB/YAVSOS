using System;
using System.IO;
using System.Text;
using BepInEx.Logging;
using ServerSync;
using UnityEngine;
using YAVSOS.Core.Templates;
using YAVSOS.Integrations;

namespace YAVSOS.Networking;

/// <summary>
/// X23 (D157): the server's rule templates. The server keeps them in a file beside the categories file
/// (<see cref="RuleTemplateCodec.FileName"/>, BepInEx/config) and sends the list to every player through a ServerSync
/// custom value (<see cref="RuleTemplateCodec.SyncIdentifier"/>), as it sends the categories file; the server's copy is
/// the truth. ServerSync would let an admin's game push a custom value, but YAVSOS's settings guard refuses every pushed
/// custom value (ServerSettingsAcceptedPatch), and a pushed value would replace the whole list with no revision check and
/// no file save. So an admin's change is one small request on the server connection itself (not a routed message, whose
/// sender a game could misstate): the server reads the sender from that connection, checks it against the game's admin
/// list (<c>ZNet.IsAdmin</c>), the rate and the revision, saves the file, publishes the list and answers. On a hosted world
/// the host's game is the server and decides at once. A list this version can't read (newer or damaged) is kept and never
/// written over (D153). Unity game thread only.
/// </summary>
internal sealed class TemplateHost : IDisposable
{
    internal const string RequestName = "com.bastionntb.yavsos.template-change";
    internal const string ReplyName = "com.bastionntb.yavsos.template-change-reply";
    /// <summary>
    /// "Am I a server admin?" and its answer, on the player's own server connection. Valheim sends its admin list only to
    /// peers already ready, so a joining player rarely has it, and the game's client check (and one that reads the list)
    /// said no to real admins (session B, 2026-11-07). The server answers with the check it uses for changes.
    /// </summary>
    internal const string AdminQueryName = "com.bastionntb.yavsos.template-admin-query";
    internal const string AdminReplyName = "com.bastionntb.yavsos.template-admin";
    private const float AdminAnswerSeconds = 60, AdminAskSeconds = 10;
    private const float PollSeconds = 5;
    private readonly ConfigSync sync;
    private readonly CustomSyncedValue<string> wire;
    private readonly ManualLogSource log;
    private readonly string path;
    private readonly TemplateChangeRate rate = new TemplateChangeRate();
    private readonly System.Random random = new System.Random();
    private TemplateRequestTracker tracker = new TemplateRequestTracker();
    private bool serving, remoteDirty, disposed, adminCheckFailed, warnedMalformed;
    private TemplateListRead? file;   // The server's list, as read from its file (or empty when there's no file yet).
    private TemplateListRead? received; // A client's copy of the server's list; null until the server sends one.
    private float pollAt;
    private DateTime fileWrite;
    private long fileLength = -1;
    private string fileWarned = "";
    private bool? serverAdmin;            // A client's last answer from the server; null until one arrives.
    private float adminAnsweredAt = float.NegativeInfinity, adminAskedAt = float.NegativeInfinity;

    /// <summary>The server answered a change, or the wait for its answer ran out (<see cref="TemplateOutcome.NoAnswer"/>).</summary>
    internal event Action<TemplateChangeRequest, TemplateChangeReply>? Answered;

    internal TemplateHost(ConfigSync sync, string configDirectory, ManualLogSource log)
    {
        this.sync = sync; this.log = log;
        path = Path.Combine(configDirectory, RuleTemplateCodec.FileName);
        // Never publish an unread file: the value starts empty, which a client reads as "nothing sent yet".
        wire = new CustomSyncedValue<string>(sync, RuleTemplateCodec.SyncIdentifier, "");
        wire.ValueChanged += OnRemoteChanged;
    }

    /// <summary>This game is the server: it reads the file, and answers changes.</summary>
    internal bool Serving => serving;

    /// <summary>
    /// The server's list as this game knows it: the server's own, or a client's received copy; null when there is none
    /// (a server without YAVSOS sends none, so the Server templates section is absent there).
    /// </summary>
    internal TemplateListRead? List => serving ? file : received;

    /// <summary>A change waits for the server's answer.</summary>
    internal bool Waiting => tracker.Busy;
    internal TemplateChangeRequest? Pending => tracker.Waiting;

    /// <summary>
    /// For the panel only (which buttons show): whether this player is a server admin, as the game's own client check says.
    /// The server decides each change against its admin list.
    /// </summary>
    internal bool LooksAdmin()
    {
        var net = ZNet.instance;
        if (!net) return false;
        if (net.IsServer()) return true;
        var now = Time.realtimeSinceStartup;
        // Ask the server, at most every 10 s, and again a minute after its last answer (the admin list can change).
        if (now - adminAnsweredAt >= AdminAnswerSeconds && now - adminAskedAt >= AdminAskSeconds) AskAdmin(net, now);
        if (serverAdmin is { } answered) return answered;
        try
        {
            if (net.LocalPlayerIsAdminOrHost()) return true;
            // The game's client check misses a list that holds the bare Steam number; the server accepts it (AdminListMatch).
            // The ID type lives in the game's Splatform assembly, which YAVSOS doesn't reference: read it by reflection.
            object user = UserInfo.GetLocalUser();
            var id = user.GetType().GetProperty("UserId")?.GetValue(user) ?? user.GetType().GetField("UserId")?.GetValue(user);
            if (id == null) return false;
            var platform = id.ToString() ?? "";
            var bare = id.GetType().GetField("m_userID")?.GetValue(id)?.ToString() ?? "";
            return AdminListMatch.Contains(net.GetAdminList(), platform, bare, platform.StartsWith("Steam_", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is MissingMethodException || error is MissingFieldException || error is NullReferenceException || error is System.Reflection.TargetException || error is InvalidCastException) { return false; }
    }

    /// <summary>At world start (after ZNet.Awake): a server reads its file and publishes it.</summary>
    internal void StartWorld(ZNet net)
    {
        tracker = new TemplateRequestTracker((ulong)DateTime.UtcNow.Ticks);
        rate.Clear(); received = null; remoteDirty = false;
        serving = net.IsServer();
        if (!serving) { file = null; return; }
        fileWrite = DateTime.MinValue; fileLength = -1;
        LoadFile();
    }

    internal void EndWorld()
    {
        tracker.Clear(); rate.Clear(); received = null; remoteDirty = false; serving = false;
        serverAdmin = null; adminAnsweredAt = adminAskedAt = float.NegativeInfinity;
    }

    private void AskAdmin(ZNet net, float now)
    {
        adminAskedAt = now;
        try { net.GetServerPeer()?.m_rpc?.Invoke(AdminQueryName); }
        catch (Exception error) when (error is NullReferenceException || error is InvalidOperationException) { }
    }

    /// <summary>
    /// A new connection (ZNet.OnNewConnection): the server takes change requests on it; a client takes the server's
    /// answers on its server connection. Each connection is a new ZRpc, so each registers once.
    /// </summary>
    internal void Connected(ZNet net, ZNetPeer peer)
    {
        try
        {
            if (net.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RequestName, OnRequest);
                peer.m_rpc.Register(AdminQueryName, OnAdminQuery);
            }
            else
            {
                peer.m_rpc.Register<ZPackage>(ReplyName, OnReply);
                peer.m_rpc.Register<bool>(AdminReplyName, OnAdminReply);
            }
        }
        // D058: without the messages only server template changes are off; applying and keeping copies still work.
        catch (Exception error) when (error is MissingMethodException || error is MemberAccessException || error is ArgumentException || error is InvalidOperationException)
        {
            log.LogWarning($"Server template changes turned off on this connection: its messages couldn't be registered ({error.GetType().Name}: {error.Message}).");
        }
    }

    /// <summary>Once a frame: a client reads a new list; a server notices a hand edit of its file; a wait runs out.</summary>
    internal void Update(float now)
    {
        if (disposed) return;
        if (remoteDirty && !serving && !ConfigSync.ProcessingServerUpdate)
        {
            remoteDirty = false;
            received = RuleTemplateCodec.Decode(wire.Value, TemplateListKind.Server);
            if (!received.Writable) log.LogWarning($"The server's rule templates can't be read by this YAVSOS version ({received.State}: {received.Diagnostic}).");
        }
        if (serving && now >= pollAt) { pollAt = now + PollSeconds; Poll(); }
        if (tracker.Expired(Time.realtimeSinceStartupAsDouble) is { } lost)
            Raise(lost, new TemplateChangeReply(lost.RequestId, TemplateOutcome.NoAnswer, lost.Id, 0));
    }

    /// <summary>
    /// Sends a change to the server, or decides it at once on the server's own game. Null: sent, the answer comes through
    /// <see cref="Answered"/> (already raised when this game is the server). Otherwise the refusal to show.
    /// </summary>
    internal TemplateOutcome? Send(Func<ulong, TemplateChangeRequest> make)
    {
        var net = ZNet.instance;
        if (!net || ZRoutedRpc.instance == null) return TemplateOutcome.Unavailable;
        if (tracker.Busy) return TemplateOutcome.Busy;
        var request = make(tracker.NextId());
        if (serving)
        {
            var player = Player.m_localPlayer;
            var reply = Decide(request, admin: true, by: player ? player.GetPlayerName() : "");
            Raise(request, reply);
            return null;
        }
        var server = net.GetServerRPC();
        if (server == null) return TemplateOutcome.Unavailable;
        var bytes = TemplateChangeCodec.Encode(request);
        if (bytes.Length > TemplateChangeCodec.MaximumRequestBytes) return TemplateOutcome.TooLarge;
        tracker.Start(request, Time.realtimeSinceStartupAsDouble);
        server.Invoke(RequestName, new ZPackage(bytes));
        return null;
    }

    // ---- The server's side.
    private void OnRequest(ZRpc rpc, ZPackage package)
    {
        try { Answer(rpc, package); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { log.LogWarning($"A rule template change couldn't be handled ({error.GetType().Name}: {error.Message}); nothing changed."); }
    }

    private void Answer(ZRpc rpc, ZPackage package)
    {
        if (!serving) return;
        var bytes = Bytes(package);
        if (!TemplateChangeCodec.TryDecode(bytes, out var request))
        {
            if (!warnedMalformed) { warnedMalformed = true; log.LogWarning("Ignored a malformed rule template change (logged once)."); }
            return;
        }
        var host = Host(rpc);
        TemplateChangeReply reply;
        if (host == null) reply = new TemplateChangeReply(request!.RequestId, TemplateOutcome.Unavailable, request.Id, 0);
        else if (!rate.Allow(host, Time.realtimeSinceStartupAsDouble)) reply = new TemplateChangeReply(request!.RequestId, TemplateOutcome.Busy, request.Id, 0);
        else if (Admin(host) is not { } admin) reply = new TemplateChangeReply(request!.RequestId, TemplateOutcome.Unavailable, request.Id, 0);
        else reply = Decide(request!, admin, PeerName(rpc));
        log.LogInfo($"Rule template {request!.Operation} from {host ?? "?"}: {reply.Outcome} (template {(reply.Id.Length == 0 ? "-" : reply.Id)}, revision {reply.Revision}).");
        rpc.Invoke(ReplyName, new ZPackage(TemplateChangeCodec.EncodeReply(reply)));
    }

    // The server: whether this connection's player is on its admin list, by the check changes use. Rate-limited with them.
    private void OnAdminQuery(ZRpc rpc)
    {
        try
        {
            if (!serving || Host(rpc) is not { } host || !rate.Allow(host, Time.realtimeSinceStartupAsDouble)) return;
            rpc.Invoke(AdminReplyName, Admin(host) == true);
        }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { log.LogWarning($"A template admin question couldn't be answered ({error.GetType().Name}: {error.Message})."); }
    }

    private void OnAdminReply(ZRpc rpc, bool admin)
    {
        serverAdmin = admin; adminAnsweredAt = Time.realtimeSinceStartup;
    }

    private TemplateChangeReply Decide(TemplateChangeRequest request, bool admin, string by)
    {
        var current = file != null && file.Writable ? file.List : null;
        var edit = TemplateServerRules.Decide(current, request, admin, by, n => random.Next(n));
        if (edit.Done && !Save(edit.List)) return new TemplateChangeReply(request.RequestId, TemplateOutcome.Unavailable, request.Id, 0);
        return TemplateServerRules.Reply(request, edit);
    }

    // The game's own check, as its admin commands use: the connection's platform ID against adminlist.txt (D058: a game
    // without it refuses every change, once logged, and nothing else turns off).
    private bool? Admin(string host)
    {
        if (adminCheckFailed) return null;
        try { return ZNet.instance.IsAdmin(host); }
        catch (Exception error) when (error is MissingMethodException || error is NullReferenceException || error is MemberAccessException)
        {
            adminCheckFailed = true;
            log.LogWarning($"Server template changes turned off: the game's admin check couldn't be used ({error.GetType().Name}: {error.Message}). Players can still apply and copy the server's templates.");
            return null;
        }
    }

    private static string? Host(ZRpc rpc)
    {
        try { var name = rpc?.GetSocket()?.GetHostName(); return string.IsNullOrEmpty(name) ? null : name; }
        catch (Exception error) when (error is NullReferenceException || error is InvalidOperationException) { return null; }
    }

    // The name the game knows this connection's player by, for "Shared by"; never what the request says.
    private static string PeerName(ZRpc rpc)
    {
        foreach (var peer in ZNet.instance.GetPeers())
            if (ReferenceEquals(peer.m_rpc, rpc)) return peer.m_playerName ?? "";
        return "";
    }

    private void LoadFile()
    {
        try
        {
            var info = new FileInfo(path);
            fileWrite = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue; fileLength = info.Exists ? info.Length : -1;
            if (!info.Exists) { Accept(RuleTemplateCodec.Decode("", TemplateListKind.Server)); return; }
            // Far over the limit: not read at all, kept as it is and never written over.
            if (info.Length > TemplateLimits.ServerListBytes * 4L) { Accept(TemplateListRead.Unreadable($"the file is over {TemplateLimits.ServerListBytes / 1024} KiB")); return; }
            Accept(RuleTemplateCodec.Decode(File.ReadAllText(path, new UTF8Encoding(false, true)), TemplateListKind.Server));
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
        {
            log.LogWarning($"Couldn't read the rule templates file {path} ({error.GetType().Name}: {error.Message}); the last list read stays.");
            file ??= TemplateListRead.Unreadable(error.Message);
            Publish();
        }
    }

    private void Accept(TemplateListRead read)
    {
        if (read.Writable) { fileWarned = ""; file = read; }
        else
        {
            if (fileWarned != read.Diagnostic)
            {
                fileWarned = read.Diagnostic;
                log.LogWarning($"The rule templates file {path} can't be read by this YAVSOS version ({read.State}: {read.Diagnostic}). It stays as it is; server template changes are off until it can be read.");
            }
            // The list players had stays, but nothing is written over the file (D153).
            file = file?.List is { } kept ? TemplateListRead.Kept(read, kept) : read;
        }
        Publish();
    }

    // A hand edit of the file shows up within PollSeconds.
    private void Poll()
    {
        try
        {
            var info = new FileInfo(path);
            var write = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            var length = info.Exists ? info.Length : -1;
            if (write == fileWrite && length == fileLength) return;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return; }
        LoadFile();
    }

    private bool Save(TemplateList list)
    {
        var text = RuleTemplateCodec.Encode(list);
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            var info = new FileInfo(path);
            fileWrite = info.LastWriteTimeUtc; fileLength = info.Length;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException)
        {
            log.LogWarning($"Couldn't save the rule templates file {path} ({error.GetType().Name}: {error.Message}); the change wasn't made.");
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception again) when (again is IOException || again is UnauthorizedAccessException) { }
            return false;
        }
        file = RuleTemplateCodec.Decode(text, TemplateListKind.Server);
        Publish();
        return true;
    }

    private void Publish()
    {
        if (!serving || file?.List is not { } list || !sync.IsSourceOfTruth || ConfigSync.ProcessingServerUpdate) return;
        var text = RuleTemplateCodec.Encode(list);
        if (!StringComparer.Ordinal.Equals(wire.Value, text)) wire.Value = text;
    }

    // ---- The admin's side.
    private void OnReply(ZRpc rpc, ZPackage package)
    {
        // Only the current server's connection answers.
        if (!ReferenceEquals(rpc, ZNet.instance ? ZNet.instance.GetServerRPC() : null)) return;
        if (!TemplateChangeCodec.TryDecodeReply(Bytes(package), out var reply)) return;
        if (tracker.Reply(reply) is { } request) Raise(request, reply);
        else log.LogDebug($"Dropped a rule template answer {reply.RequestId} ({reply.Outcome}): nothing waits for it.");
    }

    private void OnRemoteChanged()
    {
        if (!disposed && !sync.IsSourceOfTruth) remoteDirty = true; // Parsed on the next Update, outside ServerSync's handler.
    }

    private void Raise(TemplateChangeRequest request, TemplateChangeReply reply)
    {
        try { Answered?.Invoke(request, reply); }
        catch (Exception error) when (OptionalFailure.Recoverable(error))
        { log.LogWarning($"A rule template answer couldn't be shown ({error.GetType().Name}: {error.Message})."); }
    }

    private static byte[]? Bytes(ZPackage? package)
    {
        try { return package?.GetArray(); }
        catch (Exception error) when (error is EndOfStreamException || error is ArgumentException) { return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        wire.ValueChanged -= OnRemoteChanged;
    }

}
