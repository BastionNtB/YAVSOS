using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace YAVSOS.Core.Transfers;

/// <summary>Canonical, detached identity. Equality checks the preserved fields, not just a hash.</summary>
public sealed class TransferItemIdentity : IEquatable<TransferItemIdentity>
{
    public string Prefab { get; }
    public string Registration { get; }
    public int Quality { get; }
    public int Variant { get; }
    public int WorldLevel { get; }
    public float Durability { get; }
    public long CrafterId { get; }
    public string CrafterName { get; }
    public bool PickedUp { get; }
    public bool Cheated { get; }
    public string ExtensionAdapter { get; }
    public IReadOnlyDictionary<string, string> CustomData { get; }
    private readonly byte[] extensionData;
    public byte[] CopyExtensionData() => (byte[])extensionData.Clone();
    public string Fingerprint { get; }

    public TransferItemIdentity(string prefab, string registration, int quality, int variant, int worldLevel,
        float durability, long crafterId, string crafterName, bool pickedUp, bool cheated,
        IEnumerable<KeyValuePair<string, string>> customData, string extensionAdapter, byte[] extensionData)
    {
        Prefab = TransferCanonical.Text(prefab, 2048, false);
        Registration = TransferCanonical.Text(registration, 2048, false);
        if (quality < 1 || variant < 0 || worldLevel < 0 || float.IsNaN(durability) || float.IsInfinity(durability) || durability < 0)
            throw new ArgumentException("Unsupported item identity values.");
        Quality = quality; Variant = variant; WorldLevel = worldLevel; Durability = durability;
        CrafterId = crafterId; CrafterName = TransferCanonical.Text(crafterName, 16384, true);
        PickedUp = pickedUp; Cheated = cheated;
        ExtensionAdapter = TransferCanonical.Text(extensionAdapter, 2048, false);
        if (extensionData == null || extensionData.Length > TransferLimits.MaximumInventoryPayloadBytes)
            throw new ArgumentException("Extension payload exceeds supported bounds.", nameof(extensionData));
        this.extensionData = (byte[])extensionData.Clone();
        if (customData == null) throw new ArgumentNullException(nameof(customData));
        var own = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in customData)
        {
            if (own.Count >= 2048) throw new ArgumentException("Too many custom-data entries.");
            own.Add(TransferCanonical.Text(pair.Key, 16384, true), TransferCanonical.Text(pair.Value, 65536, true));
        }
        CustomData = new ReadOnlyDictionary<string, string>(own);
        Fingerprint = TransferCanonical.Hash(WriteCanonical, TransferLimits.MaximumInventoryPayloadBytes);
    }


    internal void WriteCanonical(BinaryWriter writer)
    {
        writer.Write("yavsos-transfer-item-1"); writer.Write(Prefab); writer.Write(Registration);
        writer.Write(Quality); writer.Write(Variant); writer.Write(WorldLevel);
        // Normalize -0 to +0: these represent equal live durability.
        writer.Write(Durability == 0 ? 0f : Durability);
        writer.Write(CrafterId); writer.Write(CrafterName); writer.Write(PickedUp); writer.Write(Cheated);
        writer.Write(CustomData.Count);
        foreach (var pair in CustomData) { writer.Write(pair.Key); writer.Write(pair.Value); }
        writer.Write(ExtensionAdapter); writer.Write(extensionData.Length); writer.Write(extensionData);
    }

    public bool Equals(TransferItemIdentity? other) => other != null &&
        StringComparer.Ordinal.Equals(Prefab, other.Prefab) && StringComparer.Ordinal.Equals(Registration, other.Registration) &&
        Quality == other.Quality && Variant == other.Variant && WorldLevel == other.WorldLevel && Durability.Equals(other.Durability) &&
        CrafterId == other.CrafterId && StringComparer.Ordinal.Equals(CrafterName, other.CrafterName) &&
        PickedUp == other.PickedUp && Cheated == other.Cheated && StringComparer.Ordinal.Equals(ExtensionAdapter, other.ExtensionAdapter) &&
        extensionData.SequenceEqual(other.extensionData) && CustomData.Count == other.CustomData.Count &&
        CustomData.All(p => other.CustomData.TryGetValue(p.Key, out var value) && StringComparer.Ordinal.Equals(p.Value, value));
    public override bool Equals(object? obj) => obj is TransferItemIdentity other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
}

public static class TransferLimits
{
    public const int MaximumPolicyBytes = 64 * 1024;
    public const int MaximumInventoryPayloadBytes = 1024 * 1024;
    /// <summary>
    /// Inventories one stash or cleanup run may capture (A03): the player plus every chest the planners consider. The
    /// stash explanation captures chests before the player, so a smaller limit failed on the player's own inventory.
    /// </summary>
    public const int MaximumRunInventories = 256 + 1;
    public const int MaximumTerminalEntries = 4096;
}

internal static class TransferCanonical
{
    internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    internal static string Text(string value, int maximumBytes, bool empty)
    {
        if (value == null || (!empty && value.Length == 0) || value.Length > maximumBytes || Utf8.GetByteCount(value) > maximumBytes)
            throw new ArgumentException("Invalid or oversized transfer text.");
        return value;
    }
    internal static string Hash(Action<BinaryWriter> write, int maximumBytes)
    {
        using var stream = new BoundedTransferStream(maximumBytes);
        using (var writer = new BinaryWriter(stream, Utf8, true)) write(writer);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}

/// <summary>Bounds writes before growing the backing buffer.</summary>
internal sealed class BoundedTransferStream : MemoryStream
{
    private readonly int maximum;
    internal BoundedTransferStream(int maximum) => this.maximum = maximum;
    private void Check(int count)
    {
        if (count < 0 || Position > maximum - count) throw new InvalidDataException("Transfer data bound exceeded.");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void SetLength(long value)
    { if (value < 0 || value > maximum) throw new InvalidDataException("Transfer data bound exceeded."); base.SetLength(value); }
}
