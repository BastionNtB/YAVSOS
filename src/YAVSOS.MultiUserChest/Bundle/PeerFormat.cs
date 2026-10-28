using System;

namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition (D061 Q8). MultiUserChest copies differ in one field of every item they send: 0.6.2 writes
    /// <c>m_cheated</c>, AzuAutoStore 3.1.7's copy does not. This copy reads and writes each request in the other
    /// peer's format, which YAVSOS publishes for every peer. When it is unknown, the copy refuses to send.
    /// Unity game thread only.
    /// </summary>
    public static class PeerFormat {
        /// <summary>Set by YAVSOS: whether a peer's copy writes <c>m_cheated</c>; null when unknown.</summary>
        public static Func<long, bool?> Resolve;

        private static bool? current;

        /// <summary>The format of the request being read or written; this copy's own format outside a request.</summary>
        internal static bool WithCheated => current ?? true;

        /// <summary>Requests to a peer whose format is unknown are refused. Our own peer and "no owner" always pass.</summary>
        public static bool Known(long peer) => Of(peer).HasValue;

        private static bool? Of(long peer) {
            if (peer == 0 || ZDOMan.instance == null || peer == ZDOMan.GetSessionID()) {
                return true;
            }

            return Resolve?.Invoke(peer);
        }

        internal static Scope For(long peer) {
            Scope scope = new Scope(current);
            // An unknown sender is read in this copy's own format, as upstream does; sends to it were refused.
            current = Of(peer) ?? true;
            return scope;
        }

        internal readonly struct Scope : IDisposable {
            private readonly bool? previous;
            internal Scope(bool? previous) => this.previous = previous;
            public void Dispose() => current = previous;
        }
    }
}
