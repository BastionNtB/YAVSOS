using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace YAVSOS.Core.Transfers;

/// <summary>Current registered prefab limits supplied by the game adapter, never inferred from names.</summary>
public sealed class NativeTransferPrefabLimits
{
    public int MaximumStack { get; }
    public int MaximumQuality { get; }
    public int Variants { get; }
    public NativeTransferPrefabLimits(int maximumStack, int maximumQuality, int variants)
    { MaximumStack = maximumStack; MaximumQuality = maximumQuality; Variants = variants; }
}

/// <summary>One validated native item cell. Identity excludes only cell and stack count.</summary>
public sealed class NativeTransferRecord
{
    public int X { get; }
    public int Y { get; }
    public int Stack { get; }
    private readonly byte[] identity;
    internal NativeTransferRecord(int x, int y, int stack, byte[] identity) { X = x; Y = y; Stack = stack; this.identity = identity; }
    public byte[] CopyIdentity() => (byte[])identity.Clone();
    public bool SameIdentity(NativeTransferRecord other) => other != null && System.Linq.Enumerable.SequenceEqual(identity, other.identity);
}

/// <summary>
/// Bounded preflight of native inventory/item format 110 (inspected on game 1.0.20; the format number, not the game
/// version, is checked). This reads detached
/// bytes only; it never invokes native Inventory.Load, creates items, or changes inventories.
/// Unknown, truncated, noncanonical or lossy records refuse before the caller refreshes live state.
/// </summary>
public static class NativeTransferInventoryCodec
{
    public const int MaximumBytes = TransferLimits.MaximumInventoryPayloadBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Validate(byte[] payload, int width, int height, Func<int, NativeTransferPrefabLimits?> currentPrefab)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length > MaximumBytes) throw new InvalidDataException("Unsupported bounded native inventory.");
        ValidateCore(payload, 0, width, height, currentPrefab, true, null);
    }

    /// <summary>
    /// Validates then returns owned per-cell records. Identity bytes are the exact native record with
    /// its cell and stack count removed, so equal identity means every other serialized field matches.
    /// </summary>
    public static IReadOnlyList<NativeTransferRecord> Decode(byte[] payload, int width, int height, Func<int, NativeTransferPrefabLimits?> currentPrefab)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length > MaximumBytes) throw new InvalidDataException("Unsupported bounded native inventory.");
        var records = new List<NativeTransferRecord>();
        ValidateCore(payload, 0, width, height, currentPrefab, true, records);
        return records.AsReadOnly();
    }

    /// <summary>Validates an embedded inventory and returns its byte length; subsequent player fields are not inventory bytes.</summary>
    public static int ValidatePrefix(byte[] payload, int offset, int width, int height, Func<int, NativeTransferPrefabLimits?> currentPrefab) =>
        ValidateCore(payload, offset, width, height, currentPrefab, false, null);

    private static int ValidateCore(byte[] payload, int start, int width, int height, Func<int, NativeTransferPrefabLimits?> currentPrefab, bool requireEnd,
        List<NativeTransferRecord>? decoded)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (currentPrefab == null) throw new ArgumentNullException(nameof(currentPrefab));
        if (start < 0 || start > payload.Length || width < 1 || height < 1 || width > 256 || height > 256)
            throw new InvalidDataException("Unsupported bounded native inventory.");
        var reader = new Cursor(payload, start, (int)Math.Min(payload.Length, (long)start + MaximumBytes));
        if (reader.Int() != 110) throw new InvalidDataException("Unknown native inventory format.");
        var count = reader.UShort();
        if (count > width * height) throw new InvalidDataException("Unsupported native item count.");
        var occupied = new HashSet<int>();
        var records = new List<(int Hash, int Stack, int Quality, int Variant)>(count);
        var spans = new List<(int Start, int End, int X, int Y, int Stack, int StackField)>(count);
        for (var at = 0; at < count; at++)
        {
            var recordStart = reader.Offset;
            var durability = reader.Int();
            var serializedAgain = (float)durability * 0.01f * 100f;
            if (durability < 0 || (double)serializedAgain > int.MaxValue || (int)serializedAgain != durability)
                throw new InvalidDataException("Native durability would normalize.");
            var x = reader.Byte(); var y = reader.Byte(); reader.Byte(); // world level
            if (x >= width || y >= height || !occupied.Add(y * width + x))
                throw new InvalidDataException("Invalid or duplicate native item cell.");
            var flags = reader.Byte();
            var quality = (flags & 4) == 0 ? 1 : reader.UShort();
            var stackField = (flags & 8) == 0 ? -1 : reader.Offset;
            var stack = (flags & 8) == 0 ? 1 : reader.UShort();
            var variant = (flags & 16) == 0 ? 0 : reader.Int();
            if (quality < 1 || stack < 1 || variant < 0) throw new InvalidDataException("Invalid native item scalar.");
            if (((flags & 4) != 0 && quality == 1) || ((flags & 8) != 0 && stack == 1) || ((flags & 16) != 0 && variant == 0))
                throw new InvalidDataException("Noncanonical native scalar flags.");
            if ((flags & 32) != 0)
            {
                if (reader.Long() == 0) throw new InvalidDataException("Noncanonical native crafter flag.");
                reader.Text(16384, false);
            }
            if ((flags & 64) == 0) throw new InvalidDataException("Native item lacks prefab identity.");
            var hash = reader.Int();
            if (hash == 0) throw new InvalidDataException("Native item has no registered identity.");
            if ((flags & 128) != 0)
            {
                var fields = reader.NumItems();
                if (fields < 1 || fields > 2048) throw new InvalidDataException("Unsupported native metadata count.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (var field = 0; field < fields; field++)
                {
                    if (!keys.Add(reader.Text(16384, true)!)) throw new InvalidDataException("Duplicate native metadata key.");
                    reader.Text(65536, false);
                }
            }
            if ((reader.Byte() & ~1) != 0) throw new InvalidDataException("Unknown native cheat flags.");
            records.Add((hash, stack, quality, variant));
            spans.Add((recordStart, reader.Offset, x, y, stack, stackField));
        }
        if (requireEnd && reader.Offset != payload.Length) throw new InvalidDataException("Trailing native inventory data.");
        // Malformed bytes never reach even the caller's registration lookup.
        foreach (var record in records)
        {
            var limits = currentPrefab(record.Hash);
            if (limits == null || limits.MaximumStack < 1 || limits.MaximumStack > ushort.MaxValue ||
                limits.MaximumQuality < 1 || limits.MaximumQuality > ushort.MaxValue || limits.Variants < 0 ||
                record.Stack > limits.MaximumStack || record.Quality > limits.MaximumQuality ||
                (record.Variant != 0 && record.Variant >= limits.Variants))
                throw new InvalidDataException("Native item is not supported by its current registered prefab.");
        }
        if (decoded != null)
            foreach (var span in spans)
            {
                // Remove cell bytes (offsets 4/5), the stack field and its presence flag (bit 8).
                var identity = new List<byte>(span.End - span.Start);
                for (var at = span.Start; at < span.End; at++)
                {
                    var relative = at - span.Start;
                    if (relative == 4 || relative == 5 || span.StackField >= 0 && (at == span.StackField || at == span.StackField + 1)) continue;
                    identity.Add(relative == 7 ? (byte)(payload[at] & ~8) : payload[at]);
                }
                decoded.Add(new NativeTransferRecord(span.X, span.Y, span.Stack, identity.ToArray()));
            }
        return reader.Offset - start;
    }

    private sealed class Cursor
    {
        private readonly byte[] bytes;
        private int offset;
        private readonly int end;
        internal Cursor(byte[] bytes, int start, int end) { this.bytes = bytes; offset = start; this.end = end; }
        internal int Offset => offset;
        private void Require(int count)
        { if (count < 0 || count > end - offset) throw new InvalidDataException("Truncated native inventory."); }
        internal byte Byte() { Require(1); return bytes[offset++]; }
        internal int UShort() => Byte() | Byte() << 8;
        internal int Int() => Byte() | Byte() << 8 | Byte() << 16 | Byte() << 24;
        internal long Long() => (uint)Int() | (long)Int() << 32;
        internal int NumItems()
        {
            var first = Byte();
            if (first < 128) return first;
            var count = ((first & 127) << 8) | Byte();
            if (count < 128) throw new InvalidDataException("Noncanonical native metadata count.");
            return count;
        }
        internal string? Text(int maximum, bool retain)
        {
            uint length = 0; var shift = 0;
            while (true)
            {
                var next = Byte();
                if (shift == 28 && (next & 0xF8) != 0) throw new InvalidDataException("Native text length overflow.");
                length |= (uint)(next & 127) << shift;
                if ((next & 128) == 0)
                {
                    if (shift != 0 && next == 0) throw new InvalidDataException("Noncanonical native text length.");
                    break;
                }
                shift += 7;
                if (shift > 28) throw new InvalidDataException("Native text length overflow.");
            }
            if (length > maximum) throw new InvalidDataException("Native text exceeds bound.");
            Require((int)length);
            try
            {
                // Validate strict UTF-8 without allocating discarded values, after byte bounds.
                Utf8.GetCharCount(bytes, offset, (int)length);
                var value = retain ? Utf8.GetString(bytes, offset, (int)length) : null;
                offset += (int)length; return value;
            }
            catch (DecoderFallbackException) { throw new InvalidDataException("Malformed native UTF-8."); }
        }
    }
}
