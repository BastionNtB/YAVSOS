using System;
using YAVSOS.Vendor.MultiUserChest.Patches;

namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition (YAVSOS-LOCAL change 4, D116): sends the echo probe (<see cref="FormatProbe"/>) to peers that publish
    /// no item format, and reads the answers. Every answer for container <c>ZDOID.None</c> is a probe's: this copy never
    /// sends a real request for it, so such an answer is read here and never reaches upstream's response handlers, which
    /// would apply it. Probes go out only while this copy is active, since AzuAutoStore's handler would add the echoed
    /// item to the inventory. Unity game thread only.
    /// </summary>
    public static class PeerProbe {
        private static readonly FormatProbeBook book = new FormatProbeBook();
        private static readonly Random ids = new Random();

        /// <summary>The format a probe settled for this peer: true for 2, false for 1; null when unknown.</summary>
        public static bool? Of(long peer) => book.WithCheated(peer);

        /// <summary>Where this peer's probe stands, for the sender's result line (F3).</summary>
        public static ProbeStatus Status(long peer) => book.Status(peer);

        /// <summary>Diagnostics for the lab: the probe state of one peer.</summary>
        public static string Describe(long peer) => book.Describe(peer);

        /// <summary>Forgets every probe: a new world session.</summary>
        public static void Clear() => book.Clear();

        /// <summary>Probes a peer that publishes no format, when it is due (<see cref="FormatProbeBook.Due"/>).</summary>
        public static void Probe(long peer, double now) {
            if (!BundledCopy.Active || ZRoutedRpc.instance == null || peer == 0 || !book.Due(peer, now)) {
                return;
            }

            int id = ids.Next(int.MinValue, int.MaxValue);
            book.Sent(peer, id, now);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, ContainerPatch.ItemRemoveRPC, ZDOID.None, new ZPackage(FormatProbe.Request(id)));
        }

        /// <summary>Logs peers whose last probe went unanswered, once each, and forgets peers that left.</summary>
        public static void Update(double now, Func<long, bool> connected) {
            foreach (long peer in book.Expire(now)) {
                Log.LogInfo($"YAVSOS's MultiUserChest: no MultiUserChest answered from {PeerFormat.Describe(peer)} after {FormatProbeBook.MaximumAttempts} checks, " +
                            "so YAVSOS skips chests they have open until they reconnect. They may have no MultiUserChest copy, or one that doesn't take requests.");
            }

            book.Prune(connected);
        }

        /// <summary>
        /// Called first by each of upstream's response handlers (YAVSOS-LOCAL). True for an answer about container
        /// <c>ZDOID.None</c>: the handler returns at once, so nothing is applied. A remove answer from a probed peer to one
        /// of its probes settles that peer's format; anything else is dropped.
        /// </summary>
        internal static bool Intercepts(string rpc, long sender, ZDOID containerId, ZPackage package) {
            if (containerId != ZDOID.None) {
                return false;
            }

            if (rpc != ContainerPatch.ItemRemoveResponseRPC || package == null) {
                return true;
            }

            int format = FormatProbe.Classify(package.GetArray(), out int requestId);

            if (book.Answer(sender, requestId, format) != ProbeAnswer.Settled) {
                return true;
            }

            if (format == 0) {
                Log.LogWarning($"YAVSOS's MultiUserChest: {PeerFormat.Describe(sender)} answered the item-format check in a layout YAVSOS doesn't recognise, " +
                               "so YAVSOS skips chests they have open, and refuses to hand them items with custom data or a cheated flag, until they reconnect.");
            } else {
                Log.LogInfo($"YAVSOS's MultiUserChest: {PeerFormat.Describe(sender)} publishes no item format; their MultiUserChest copy answered in the " +
                            (format == 2 ? "0.6.2 format (with cheated flag)" : "older format (without cheated flag), as AzuAutoStore's copy writes it") +
                            ". Moves into and out of their chests use it.");
            }

            return true;
        }
    }
}
