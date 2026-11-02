#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YAVSOS.Vendor.MultiUserChest {
    /// <summary>
    /// YAVSOS addition (YAVSOS-LOCAL change 4, D116, X19 research §4 and §5 option C). The echo probe for a peer that
    /// publishes no item format: a <c>MUC_RequestItemRemove</c> for container <c>ZDOID.None</c>, carrying a plain switch
    /// item written in format 2. Every known copy takes its "no such chest" path, touches no inventory, and sends the
    /// switch item back written in its own format; the bytes after <c>m_pickedUp</c> tell which. Plain values only (the
    /// <c>ZPackage</c> primitives are <see cref="BinaryWriter"/> and <see cref="BinaryReader"/>), so it is tested without the game.
    /// </summary>
    public static class FormatProbe {
        /// <summary>The switch item: a base-game prefab every copy can load, one unit, nothing else set.</summary>
        public const string Prefab = "Wood";
        public const int Stack = 1;

        /// <summary>
        /// The request's bytes, laid out as <c>RequestChestRemove.WriteToPackage</c> with a format-2 item. Both cells are
        /// (−1,−1) and the amount 0, so even a copy that found a chest would find nothing to remove.
        /// </summary>
        public static byte[] Request(int requestId) {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream)) {
                writer.Write(requestId);
                writer.Write(-1); writer.Write(-1); // fromPos
                writer.Write(-1); writer.Write(-1); // toPos
                writer.Write(0); // dragAmount
                // InventoryHelper.WriteItemToPackage, format 2.
                writer.Write(true);
                writer.Write(Prefab);
                writer.Write(Stack);
                writer.Write(100f); // durability
                writer.Write(0); writer.Write(0); // grid position
                writer.Write(1); // quality
                writer.Write(0); // variant
                writer.Write(0L); // crafter id
                writer.Write(""); // crafter name
                writer.Write(0); // world level
                writer.Write(false); // m_pickedUp
                writer.Write(false); // m_cheated
                writer.Write(0); // custom-data count
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Reads an answer's request id and the format it was written in: 1 (no <c>m_cheated</c>, AzuAutoStore 3.1.7's copy),
        /// 2 (0.6.2, YAVSOS's copy) or 0 when it isn't an untouched echo of <see cref="Request"/>'s item. The answer is
        /// <c>RequestChestRemoveResponse</c> as <c>FallbackResponse</c> fills it: not successful, amount 0, not switched,
        /// the switch item. Its tail after <c>m_pickedUp</c> is exactly 4 zero bytes in format 1 and 5 in format 2;
        /// anything else is unknown. <paramref name="requestId"/> is 0 when even the id can't be read.
        /// </summary>
        public static int Classify(byte[] answer, out int requestId) {
            requestId = 0;

            if (answer == null || answer.Length < 4) {
                return 0;
            }

            try {
                using (BinaryReader reader = new BinaryReader(new MemoryStream(answer, false), Encoding.UTF8)) {
                    requestId = reader.ReadInt32();
                    bool success = reader.ReadBoolean();
                    int amount = reader.ReadInt32();
                    bool switched = reader.ReadBoolean();

                    if (success || amount != 0 || switched || !reader.ReadBoolean()) {
                        return 0;
                    }

                    string prefab = reader.ReadString();
                    int stack = reader.ReadInt32();

                    if (!StringComparer.Ordinal.Equals(prefab, Prefab) || stack != Stack) {
                        return 0;
                    }

                    reader.ReadSingle(); // durability
                    reader.ReadInt32(); reader.ReadInt32(); // grid position
                    reader.ReadInt32(); // quality
                    reader.ReadInt32(); // variant
                    reader.ReadInt64(); // crafter id
                    reader.ReadString(); // crafter name
                    reader.ReadInt32(); // world level
                    reader.ReadBoolean(); // m_pickedUp
                    return Tail(answer, (int)reader.BaseStream.Position);
                }
            } catch (Exception error) when (error is IOException || error is FormatException || error is ArgumentException) {
                return 0;
            }
        }

        /// <summary>The format the bytes after <c>m_pickedUp</c> (from <paramref name="start"/> to the end) stand for.</summary>
        public static int Tail(byte[] message, int start) {
            int length = message.Length - start;

            if (length != 4 && length != 5) {
                return 0;
            }

            for (int i = start; i < message.Length; i++) {
                if (message[i] != 0) {
                    return 0;
                }
            }

            return length == 4 ? 1 : 2;
        }
    }

    /// <summary>What an answer did to <see cref="FormatProbeBook"/>.</summary>
    public enum ProbeAnswer { NotOurs, Settled, AlreadySettled }

    /// <summary>
    /// YAVSOS addition (F3): where one peer's probe stands, for the sender's result line. <c>Pending</c>: probes are still
    /// out; <c>NotProbed</c>: none sent yet (a peer is probed once its player object is loaded nearby, A2.11).
    /// </summary>
    public enum ProbeStatus { Pending, Answered, UnknownAnswer, NoAnswer, NotProbed }

    /// <summary>
    /// YAVSOS addition (D116): the probes of one world session, by peer id. Session ids change on reconnect, so a peer
    /// that comes back is probed afresh; one that left is forgotten. Each peer gets at most
    /// <see cref="MaximumAttempts"/> probes: the first when it is seen, then <see cref="RetryAfter"/> apart. An answer to
    /// any of them settles the format for the session, also after its <see cref="Timeout"/>. Plain values, game thread only.
    /// </summary>
    public sealed class FormatProbeBook {
        /// <summary>
        /// When an unanswered probe counts as unanswered. An answer goes through the server and back, normally well under a
        /// second; 10 s covers a stalled frame or a short pause (the delayed-reply check pauses a game about 12 s, and a late
        /// answer is still taken).
        /// </summary>
        public const double Timeout = 10;

        /// <summary>A copy can't appear or vanish without a reconnect, so retries cover only a lost message or a busy join.</summary>
        public const int MaximumAttempts = 3;

        /// <summary>
        /// Seconds from one probe to the next: 30 s, then 90 s, so the last answer is awaited about 2 min 10 s after the
        /// player appears. A peer without a copy costs three small messages per session.
        /// </summary>
        public static readonly double[] RetryAfter = { 30, 90 };

        /// <summary>Bounds the book; Valheim servers hold far fewer players.</summary>
        public const int MaximumPeers = 256;

        private sealed class Entry {
            internal readonly List<int> Ids = new List<int>(MaximumAttempts);
            internal double SentAt;
            internal int Format = -1; // -1 unsettled; 0 answered in an unknown format; 1 or 2
            internal bool GaveUp;
        }

        private readonly Dictionary<long, Entry> peers = new Dictionary<long, Entry>();

        public int Count => peers.Count;

        /// <summary>Whether to probe this peer now.</summary>
        public bool Due(long peer, double now) {
            if (!peers.TryGetValue(peer, out Entry? entry)) {
                return peers.Count < MaximumPeers;
            }

            if (entry.Format >= 0 || entry.Ids.Count >= MaximumAttempts) {
                return false;
            }

            return now - entry.SentAt >= RetryAfter[entry.Ids.Count - 1];
        }

        public void Sent(long peer, int requestId, double now) {
            if (!peers.TryGetValue(peer, out Entry? entry)) {
                if (peers.Count >= MaximumPeers) {
                    return;
                }

                peers.Add(peer, entry = new Entry());
            }

            if (entry.Ids.Count < MaximumAttempts) {
                entry.Ids.Add(requestId);
                entry.SentAt = now;
            }
        }

        /// <summary>Records an answer from <paramref name="peer"/>; only one to a probe sent to that peer counts.</summary>
        public ProbeAnswer Answer(long peer, int requestId, int format) {
            if (!peers.TryGetValue(peer, out Entry? entry) || !entry.Ids.Contains(requestId)) {
                return ProbeAnswer.NotOurs;
            }

            if (entry.Format >= 0) {
                return ProbeAnswer.AlreadySettled;
            }

            entry.Format = format == 1 || format == 2 ? format : 0;
            return ProbeAnswer.Settled;
        }

        /// <summary>Peers whose last probe has just gone unanswered; each is returned once.</summary>
        public List<long> Expire(double now) {
            List<long> expired = new List<long>();

            foreach (KeyValuePair<long, Entry> pair in peers) {
                Entry entry = pair.Value;

                if (entry.Format < 0 && !entry.GaveUp && entry.Ids.Count >= MaximumAttempts && now - entry.SentAt >= Timeout) {
                    entry.GaveUp = true;
                    expired.Add(pair.Key);
                }
            }

            return expired;
        }

        /// <summary>The settled format as <c>PeerFormat</c> uses it: true for 2, false for 1, null otherwise.</summary>
        public bool? WithCheated(long peer) {
            if (!peers.TryGetValue(peer, out Entry? entry)) {
                return null;
            }

            return entry.Format == 2 ? true : entry.Format == 1 ? (bool?)false : null;
        }

        /// <summary>Where this peer's probe stands (F3).</summary>
        public ProbeStatus Status(long peer) {
            if (!peers.TryGetValue(peer, out Entry? entry)) {
                return ProbeStatus.NotProbed;
            }

            return entry.Format > 0 ? ProbeStatus.Answered
                : entry.Format == 0 ? ProbeStatus.UnknownAnswer
                : entry.GaveUp ? ProbeStatus.NoAnswer
                : ProbeStatus.Pending;
        }

        /// <summary>Forgets every peer <paramref name="connected"/> no longer holds (they left, or reconnected under a new id).</summary>
        public void Prune(Func<long, bool> connected) {
            List<long>? gone = null;

            foreach (long peer in peers.Keys) {
                if (!connected(peer)) {
                    (gone ??= new List<long>()).Add(peer);
                }
            }

            gone?.ForEach(x => peers.Remove(x));
        }

        public void Clear() => peers.Clear();

        /// <summary>Diagnostics: "format 1", "probing (2 sent)", "no answer", "unknown answer" or "not probed".</summary>
        public string Describe(long peer) {
            if (!peers.TryGetValue(peer, out Entry? entry)) {
                return "not probed";
            }

            return entry.Format > 0 ? "format " + entry.Format
                : entry.Format == 0 ? "unknown answer"
                : entry.GaveUp ? "no answer"
                : $"probing ({entry.Ids.Count} sent)";
        }
    }
}
