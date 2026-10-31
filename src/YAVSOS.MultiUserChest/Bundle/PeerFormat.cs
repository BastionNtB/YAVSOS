using System;
using System.Collections.Generic;
using System.IO;

namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition (D061 Q8). MultiUserChest copies differ in one field of every item they send: 0.6.2 writes
    /// <c>m_cheated</c>, AzuAutoStore 3.1.7's copy does not. This copy reads and writes each request in the other
    /// peer's format, which YAVSOS publishes for every peer. When it is unknown, the copy refuses to send, and answers
    /// a request only with items both formats read alike (<see cref="UnknownFormatGuard"/>, D116).
    /// Unity game thread only.
    /// </summary>
    public static class PeerFormat {
        /// <summary>Set by YAVSOS: whether a peer's copy writes <c>m_cheated</c>; null when unknown.</summary>
        public static Func<long, bool?> Resolve;

        private static bool? current;
        // The peer of the request being handled when its format is unknown; 0 otherwise (our own peer and "no owner" are known).
        private static long unknownPeer;

        /// <summary>The format of the request being read or written; this copy's own format outside a request.</summary>
        internal static bool WithCheated => current ?? true;

        /// <summary>The request being handled comes from a peer whose format is unknown.</summary>
        internal static bool PeerUnknown => unknownPeer != 0;

        /// <summary>Requests to a peer whose format is unknown are refused. Our own peer and "no owner" always pass.</summary>
        public static bool Known(long peer) => Of(peer).HasValue;

        private static bool? Of(long peer) {
            if (peer == 0 || ZDOMan.instance == null || peer == ZDOMan.GetSessionID()) {
                return true;
            }

            return Resolve?.Invoke(peer);
        }

        internal static Scope For(long peer) {
            Scope scope = new Scope(current, unknownPeer);
            bool? format = Of(peer);
            // An unknown sender is read in this copy's own format, as upstream does; sends to it were refused.
            current = format ?? true;
            unknownPeer = format.HasValue ? 0 : peer;
            return scope;
        }

        /// <summary>
        /// Reads a request. One from a peer whose format is unknown may be in the other format and fail to parse (research
        /// §2.1): it is dropped unanswered, as upstream's exception did, with nothing added or removed, and logged once per
        /// peer. The sender's AzuAutoStore copy gives its item back after about 30 s. Known peers read as upstream does.
        /// </summary>
        internal static bool TryRead<T>(ZPackage package, Func<ZPackage, T> read, out T request) {
            if (!PeerUnknown) {
                request = read(package);
                return true;
            }

            try {
                request = read(package);
                return true;
            } catch (Exception error) when (error is IOException || error is FormatException) {
                request = default;
                WarnOnce("unreadable", $"a chest request from {Describe(unknownPeer)} could not be read ({error.GetType().Name}): " +
                                       "their MultiUserChest copy publishes no item format, so it may write the other one. Nothing was added or removed; " +
                                       "AzuAutoStore's copy gives their item back after about 30 seconds.");
                return false;
            }
        }

        /// <summary>Logs a guard refusal (<see cref="UnknownFormatGuard.Refuses"/>) once per peer.</summary>
        internal static void WarnRefused() {
            WarnOnce("refused", $"refused to hand an item with custom data or a cheated flag to {Describe(unknownPeer)}: " +
                                "their MultiUserChest copy publishes no item format and might misread it. Nothing was removed from the chest.");
        }

        // D112 style: one line per peer and cause for the game run, saying further repeats are not logged. Bounded.
        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnOnce(string cause, string text) {
            if (warned.Count >= 1024 || !warned.Add(cause + ":" + unknownPeer)) {
                return;
            }

            Log.LogWarning("YAVSOS's MultiUserChest: " + text + " Further cases from this player are not logged until the game restarts.");
        }

        private static string Describe(long peer) {
            foreach (Player player in Player.GetAllPlayers()) {
                ZDO zdo = player ? player.GetComponent<ZNetView>()?.GetZDO() : null;

                if (zdo != null && zdo.GetOwner() == peer) {
                    return $"player {player.GetPlayerName()} (peer {peer})";
                }
            }

            return $"peer {peer}";
        }

        internal readonly struct Scope : IDisposable {
            private readonly bool? previous;
            private readonly long previousUnknownPeer;

            internal Scope(bool? previous, long previousUnknownPeer) {
                this.previous = previous;
                this.previousUnknownPeer = previousUnknownPeer;
            }

            public void Dispose() {
                current = previous;
                unknownPeer = previousUnknownPeer;
            }
        }
    }
}
