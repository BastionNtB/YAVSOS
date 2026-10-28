using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace YAVSOS.Networking;

// Main-thread adapter for the pinned library. No game types or inventory authority.
internal sealed class ServerSyncSettingsBridge
{
    internal const int MaxRequestBytes = 256 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private long session;
    private bool publishPending;

    public void BeginSession() { session++; publishPending = false; }

    public long? CaptureUpdate(bool managedServer, bool clientUpdate, bool currentPeer) =>
        managedServer && clientUpdate && currentPeer ? session : (long?)null;

    public void CompleteUpdate(long? capturedSession, bool accepted)
    {
        // Refused requests and incomplete fragments never open publication.
        if (accepted && capturedSession == session) publishPending = true;
    }

    public bool Publish(bool managedServer, bool processingUpdate, Action publishCurrentSettings)
    {
        if (!managedServer) { publishPending = false; return false; }
        if (!publishPending || processingUpdate) return false;
        publishPending = false; // Clear before callbacks; reentrant work belongs to the next update.
        publishCurrentSettings();
        return true;
    }

    public static bool TrySerializeLocal(bool managedOverlay, bool synchronized, object? localValue,
        Type valueType, Func<object, Type, string> serialize, out string serialized)
    {
        serialized = "";
        if (!managedOverlay || !synchronized || localValue == null) return false;
        serialized = serialize(localValue, valueType);
        return true;
    }

    // Client edits are ordinary shared settings only. Never let a client replace
    // category policy or spoof the library's Internal.lockexempt control value.
    // Validate a copy without moving the real ZPackage's cursor or applying values.
    public static bool ValidateRequest(byte[] payload, int maxEntries,
        Func<string, string, Type?> settingType, out string diagnostic)
    {
        diagnostic = "";
        try
        {
            if (payload.Length > MaxRequestBytes) throw new InvalidDataException("Settings request exceeds 256 KiB.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream, StrictUtf8);
            var flags = reader.ReadByte();
            if (flags == 4)
            {
                var size = reader.ReadInt32();
                if (size < 0 || size != stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid compressed settings envelope.");
                using var compressed = new MemoryStream(reader.ReadBytes(size), false);
                using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                var buffer = new byte[4096];
                int read;
                while ((read = inflater.Read(buffer, 0, Math.Min(buffer.Length, MaxRequestBytes + 1 - (int)expanded.Length))) != 0)
                {
                    expanded.Write(buffer, 0, read);
                    if (expanded.Length > MaxRequestBytes) throw new InvalidDataException("Expanded settings request exceeds 256 KiB.");
                }
                using var unpacked = new BinaryReader(new MemoryStream(expanded.ToArray(), false), StrictUtf8);
                ValidateEntries(unpacked, maxEntries, settingType);
            }
            else
            {
                stream.Position = 0;
                ValidateEntries(reader, maxEntries, settingType);
            }
            return true;
        }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is DecoderFallbackException)
        {
            diagnostic = error.Message;
            return false;
        }
    }

    private static void ValidateEntries(BinaryReader reader, int maxEntries, Func<string, string, Type?> settingType)
    {
        if (reader.ReadByte() != 1) throw new InvalidDataException("Only complete partial-settings requests are supported.");
        var count = reader.ReadInt32();
        if (count < 1 || count > maxEntries) throw new InvalidDataException("Invalid settings entry count.");
        for (var i = 0; i < count; i++)
        {
            var section = ReadString(reader, 128);
            var key = ReadString(reader, 128);
            var typeName = ReadString(reader, 1024);
            var configuredType = section == "Internal" ? null : settingType(section, key);
            if (configuredType == null) throw new InvalidDataException("Unknown or server-owned settings entry.");
            var wireType = configuredType.IsEnum ? Enum.GetUnderlyingType(configuredType) : configuredType;
            if (typeName != wireType.AssemblyQualifiedName) throw new InvalidDataException("Settings entry type mismatch.");
            if (wireType == typeof(bool))
            {
                if (reader.ReadByte() > 1) throw new InvalidDataException("Invalid Boolean settings value.");
            }
            else if (wireType == typeof(int))
            {
                var value = reader.ReadInt32();
                if (configuredType.IsEnum && !Enum.IsDefined(configuredType, value))
                    throw new InvalidDataException("Unknown settings enumeration value.");
            }
            else if (wireType == typeof(float))
            {
                var value = reader.ReadSingle();
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Nonfinite settings value.");
            }
            else if (wireType == typeof(string)) ReadString(reader, MaxRequestBytes);
            else throw new InvalidDataException("Unsupported settings value type.");
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing settings request data.");
    }

    private static string ReadString(BinaryReader reader, int limit)
    {
        uint length = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var part = reader.ReadByte();
            if (shift == 28 && part > 7) throw new InvalidDataException("Invalid settings string length.");
            length |= (uint)(part & 127) << shift;
            if ((part & 128) != 0) continue;
            if (length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Settings string exceeds its bound or is truncated.");
            var bytes = reader.ReadBytes((int)length);
            return StrictUtf8.GetString(bytes);
        }
        throw new InvalidDataException("Invalid settings string length.");
    }
}
