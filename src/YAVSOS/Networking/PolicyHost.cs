using System;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using ServerSync;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

internal sealed class PolicyHost : IDisposable
{
    private readonly ConfigSync sync;
    private readonly CustomSyncedValue<string> wirePolicy;
    private readonly ManualLogSource log;
    private readonly string path;
    private readonly PolicyMailbox mailbox = new PolicyMailbox();
    private readonly ServerSyncSessionCache sessionCache;
    private FileSystemWatcher? watcher;
    private int watcherFailed;
    private bool reloadPending;
    private float reloadAt;
    private float pollAt;
    private DateTime lastWrite;
    private long lastLength = -1;
    private bool disposed;
    public PolicyLifecycle State { get; } = new PolicyLifecycle();
    public ZRpc? IncomingRpc => sessionCache.IncomingRpc;

    public PolicyHost(ConfigSync sync, string configDirectory, ManualLogSource log)
    {
        this.sync = sync;
        this.log = log;
        sessionCache = new ServerSyncSessionCache(sync);
        path = Path.Combine(configDirectory, CategoryPolicyCodec.FileName);
        // Never publish unvalidated disk contents, including the initial value.
        wirePolicy = new CustomSyncedValue<string>(sync, CategoryPolicyCodec.SyncIdentifier, "");
        sync.SourceOfTruthChanged += OnSourceChanged;
        wirePolicy.ValueChanged += OnRemoteChanged;
        try
        {
            Directory.CreateDirectory(configDirectory);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = Encoding.UTF8.GetBytes(CategoryPolicyCodec.EmptyDocument);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (IOException) when (File.Exists(path)) { /* Preserve the user's existing file. */ }
        // B1.4: a read-only config folder warns and continues with the built-in categories, as the rest of this class does.
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            log.LogWarning($"Could not create the categories file {path} ({error.GetType().Name}: {error.Message}). Check the folder's permissions.");
        }
        ReloadLocal();
        try
        {
            watcher = new FileSystemWatcher(configDirectory, CategoryPolicyCodec.FileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false
            };
            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }
        // ArgumentException: the folder doesn't exist because it could not be created (B1.4).
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is PlatformNotSupportedException || error is ArgumentException)
        {
            log.LogWarning($"Category watcher unavailable; using five-second file-stat polling: {error.Message}");
        }
    }

    public void BeginSession(PolicyMode mode)
    {
        State.BeginSession(mode);
        sessionCache.Clear();
        mailbox.Reset(State.Stamp.Session);
        // Local policy may have changed between worlds even when the watcher was unavailable.
        ReloadLocal();
        log.LogInfo($"Policy mode: {mode}. {State.Diagnostic}");
    }

    public void Update(float now)
    {
        if (disposed) return;
        if (Interlocked.Exchange(ref watcherFailed, 0) != 0)
        {
            log.LogWarning("Category watcher lost events; rescanning and retaining five-second polling.");
            mailbox.MarkFileDirty();
        }
        if (now >= pollAt)
        {
            pollAt = now + 5f;
            try
            {
                var info = new FileInfo(path);
                var write = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
                var length = info.Exists ? info.Length : -1;
                if (write != lastWrite || length != lastLength)
                {
                    lastWrite = write;
                    lastLength = length;
                    mailbox.MarkFileDirty();
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                log.LogWarning($"Cannot inspect category file: {error.Message}");
            }
        }
        if (mailbox.TakeFileDirty()) { reloadPending = true; reloadAt = now + 0.35f; }
        if (reloadPending && now >= reloadAt) { reloadPending = false; ReloadLocal(); }
        if (!sync.IsSourceOfTruth && sync.InitialSyncDone && !ConfigSync.ProcessingServerUpdate)
        {
            var payload = mailbox.TakeRemote(State.Stamp.Session);
            if (payload != null)
            {
                var result = State.ApplyRemote(State.Stamp.Session, payload);
                Report(result, "Server");
            }
        }
    }

    private void OnSourceChanged(bool local)
    {
        if (disposed) return;
        // Shutdown restores library values after our ZNet prefix has invalidated the old session.
        if (!local)
        {
            State.AwaitServer();
            mailbox.Reset(State.Stamp.Session);
        }
    }
    private void OnRemoteChanged()
    {
        if (!disposed && !sync.IsSourceOfTruth && State.Mode != PolicyMode.Offline)
        {
            if (State.Mode == PolicyMode.ManagedClient && State.PolicyReady && State.Effective?.CanonicalXml == wirePolicy.Value) return;
            State.AwaitServer(); // Close the gate immediately, before main-thread parsing/rebuild.
            mailbox.PostRemote(State.Stamp.Session, wirePolicy.Value);
        }
    }

    private void ReloadLocal()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic))
            {
                log.LogWarning($"Cannot reload {path}; last accepted policy retained: {diagnostic}");
                return;
            }
            ApplyLocal(payload);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is DecoderFallbackException)
        {
            // B1.4: with no file and nothing accepted yet (a read-only config folder), the built-in categories apply.
            if (State.Local == null && !File.Exists(path))
            {
                log.LogWarning($"Cannot read {path} ({error.Message}); using the built-in categories.");
                ApplyLocal(CategoryPolicyCodec.EmptyDocument);
            }
            else log.LogWarning($"Cannot reload {path}; last accepted policy retained: {error.Message}");
        }
    }

    private void ApplyLocal(string payload)
    {
        var result = State.ApplyLocal(payload);
        if (result == PolicyUpdate.Accepted && sync.IsSourceOfTruth && !ConfigSync.ProcessingServerUpdate)
            wirePolicy.Value = State.Local!.CanonicalXml;
        else if (result == PolicyUpdate.Accepted && !sync.IsSourceOfTruth)
            wirePolicy.AssignLocalValue(State.Local!.CanonicalXml); // Restoration value only; no echo/broadcast.
        Report(result, "Local");
    }

    private void Report(PolicyUpdate result, string source)
    {
        if (result == PolicyUpdate.Rejected) log.LogWarning(State.Diagnostic);
        else if (result == PolicyUpdate.Accepted)
        {
            var policy = source == "Local" ? State.Local : State.Effective;
            log.LogInfo($"{source} category policy accepted: revision {policy!.Revision}, SHA-256 {policy.Fingerprint}. Classification remains pending.");
        }
    }
    private void OnFileChanged(object sender, FileSystemEventArgs args) => mailbox.MarkFileDirty();
    private void OnWatcherError(object sender, ErrorEventArgs args) => Interlocked.Exchange(ref watcherFailed, 1);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        State.BeginSession(PolicyMode.Offline);
        sessionCache.Clear();
        mailbox.Reset(State.Stamp.Session);
        sync.SourceOfTruthChanged -= OnSourceChanged;
        wirePolicy.ValueChanged -= OnRemoteChanged;
        watcher?.Dispose();
    }
}
