using System.Threading;
using YAVSOS.Core.Policy;

namespace YAVSOS.Networking;

// At most one complete remote snapshot and one dirty-file flag. No game calls from watcher threads.
internal sealed class PolicyMailbox
{
    private readonly object gate = new object();
    private long activeSession;
    private string? remote;
    private int fileDirty;
    public void Reset(long session)
    {
        lock (gate) { activeSession = session; remote = null; }
    }
    public void MarkFileDirty() => Interlocked.Exchange(ref fileDirty, 1);
    public bool TakeFileDirty() => Interlocked.Exchange(ref fileDirty, 0) != 0;
    public void PostRemote(long session, string? value)
    {
        lock (gate)
        {
            if (session != activeSession) return;
            // An over-limit snapshot is still a rejection, never a silent drop retaining readiness.
            remote = value == null || value.Length > CategoryPolicyCodec.MaxPayloadBytes ? "" : value;
        }
    }
    public string? TakeRemote(long session)
    {
        lock (gate)
        {
            if (session != activeSession) return null;
            var result = remote;
            remote = null;
            return result;
        }
    }
}
