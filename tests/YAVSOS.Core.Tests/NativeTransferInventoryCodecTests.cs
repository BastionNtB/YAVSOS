using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using YAVSOS.Core.Transfers;

namespace YAVSOS.Core.Tests;

public sealed class NativeTransferInventoryCodecTests
{
    private const int Prefab = 123456;
    private static NativeTransferPrefabLimits? Registered(int hash) => hash == Prefab ? new(50, 4, 3) : null;
    private static byte[] Payload(Action<BinaryWriter> records, ushort count = 1)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true));
        writer.Write(110); writer.Write(count); records(writer); return stream.ToArray();
    }
    private static void Record(BinaryWriter writer, byte x = 0, byte y = 0, ushort quality = 2, ushort stack = 3,
        int variant = 1, int hash = Prefab, int durability = 0, byte cheats = 1, Action<BinaryWriter>? metadata = null)
    {
        writer.Write(durability); writer.Write(x); writer.Write(y); writer.Write((byte)255); writer.Write((byte)255);
        writer.Write(quality); writer.Write(stack); writer.Write(variant); writer.Write(7L); writer.Write("Crafter"); writer.Write(hash);
        if (metadata == null) { writer.Write((byte)2); writer.Write("alpha"); writer.Write("value"); writer.Write("beta"); writer.Write("\u00e9\0"); }
        else metadata(writer);
        writer.Write(cheats);
    }
    private static void Validate(byte[] bytes) => NativeTransferInventoryCodec.Validate(bytes, 5, 2, Registered);

    [Fact]
    public void ValidatePrefix_EmbeddedInventory_ReturnsExactLengthAndPreservesSurroundings()
    {
        var inventory = Payload(writer => Record(writer));
        var bytes = new byte[] { 34, 0, 0, 0, 99 }.Concat(inventory).Concat(new byte[] { 255, 13, 37 }).ToArray();
        var original = (byte[])bytes.Clone();
        Assert.Equal(inventory.Length, NativeTransferInventoryCodec.ValidatePrefix(bytes, 5, 5, 2, Registered));
        Assert.Equal(original, bytes);
        var lookups = 0;
        Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.Validate(inventory.Concat(new byte[] { 1 }).ToArray(),
            5, 2, hash => { lookups++; return Registered(hash); }));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void ValidatePrefix_PlayerLayoutAcceptsMoreThanTenItems_ChestLayoutRefuses()
    {
        var bytes = Payload(writer => { for (byte y = 0; y < 4; y++) for (byte x = 0; x < 8; x++) Record(writer, x, y); }, 32);
        Assert.Equal(bytes.Length, NativeTransferInventoryCodec.ValidatePrefix(bytes, 0, 8, 4, Registered));
        Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.ValidatePrefix(bytes, 0, 5, 2, Registered));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void ValidatePrefix_InvalidOrExhaustedOffset_RefusesBeforeRegistration(int offset)
    {
        var lookups = 0;
        Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.ValidatePrefix(new byte[7], offset, 8, 4,
            hash => { lookups++; return Registered(hash); }));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void ValidatePrefix_InventoryExceedsByteBound_RefusesBeforeRegistration()
    {
        var inventory = Payload(writer => Record(writer, metadata: metadata =>
        {
            metadata.Write((byte)17);
            for (var at = 0; at < 17; at++) { metadata.Write("key" + at); metadata.Write(new string('v', 65536)); }
        }));
        Assert.True(inventory.Length > NativeTransferInventoryCodec.MaximumBytes);
        var bytes = new byte[] { 99, 98 }.Concat(inventory).ToArray(); var lookups = 0;
        Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.ValidatePrefix(bytes, 2, 8, 4,
            hash => { lookups++; return Registered(hash); }));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void Validate_SixByteTruncatedCandidate_RefusesBeforeRegistrationAndPreservesBytes()
    {
        var bytes = Payload(_ => { }); var original = (byte[])bytes.Clone(); var lookups = 0;
        Assert.Equal(6, bytes.Length);
        Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.Validate(bytes, 5, 2, hash => { lookups++; return Registered(hash); }));
        Assert.Equal(0, lookups); Assert.Equal(original, bytes);
    }
    [Fact]
    public void Validate_EveryTruncationOfAllOptionalRecordSections_RefusesBeforeRegistration()
    {
        var complete = Payload(writer => Record(writer));
        for (var length = 0; length < complete.Length; length++)
        {
            var bytes = complete.Take(length).ToArray(); var lookups = 0;
            Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.Validate(bytes, 5, 2, hash => { lookups++; return Registered(hash); }));
            Assert.Equal(0, lookups);
        }
        Validate(complete);
    }
    [Fact]
    public void Validate_EmptyMinimalAndCompleteTenCellPayloads_AcceptsWithoutChangingBytes()
    {
        Validate(Payload(_ => { }, 0));
        Validate(Payload(writer => { writer.Write(0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)64); writer.Write(Prefab); writer.Write((byte)0); }));
        var bytes = Payload(writer => { for (byte y = 0; y < 2; y++) for (byte x = 0; x < 5; x++) Record(writer, x, y); }, 10);
        var original = (byte[])bytes.Clone(); Validate(bytes); Assert.Equal(original, bytes);
    }
    [Theory]
    [InlineData("version")]
    [InlineData("excess-count")]
    [InlineData("trailing")]
    [InlineData("duplicate-cell")]
    [InlineData("x-outside")]
    [InlineData("y-outside")]
    [InlineData("negative-durability")]
    [InlineData("lossy-durability")]
    [InlineData("zero-quality")]
    [InlineData("excess-quality")]
    [InlineData("zero-stack")]
    [InlineData("excess-stack")]
    [InlineData("negative-variant")]
    [InlineData("excess-variant")]
    [InlineData("unknown-prefab")]
    [InlineData("missing-prefab")]
    [InlineData("zero-prefab")]
    [InlineData("noncanonical-quality")]
    [InlineData("noncanonical-stack")]
    [InlineData("noncanonical-variant")]
    [InlineData("unknown-cheat-flags")]
    [InlineData("duplicate-key")]
    public void Validate_MalformedOrUnsupportedRecord_RefusesWithoutChangingPayload(string scenario)
    {
        var bytes = scenario switch
        {
            "version" => Payload(writer => Record(writer)),
            "excess-count" => Payload(_ => { }, 11),
            "trailing" => Payload(writer => { Record(writer); writer.Write((byte)0); }),
            "duplicate-cell" => Payload(writer => { Record(writer); Record(writer); }, 2),
            "x-outside" => Payload(writer => Record(writer, x: 5)),
            "y-outside" => Payload(writer => Record(writer, y: 2)),
            "negative-durability" => Payload(writer => Record(writer, durability: -1)),
            "lossy-durability" => Payload(writer => Record(writer, durability: int.MaxValue)),
            "zero-quality" => Payload(writer => Record(writer, quality: 0)),
            "excess-quality" => Payload(writer => Record(writer, quality: 5)),
            "zero-stack" => Payload(writer => Record(writer, stack: 0)),
            "excess-stack" => Payload(writer => Record(writer, stack: 51)),
            "negative-variant" => Payload(writer => Record(writer, variant: -1)),
            "excess-variant" => Payload(writer => Record(writer, variant: 3)),
            "unknown-prefab" => Payload(writer => Record(writer, hash: 998)),
            "missing-prefab" => Payload(writer => Record(writer)),
            "zero-prefab" => Payload(writer => Record(writer, hash: 0)),
            "noncanonical-quality" => Payload(writer => Record(writer, quality: 1)),
            "noncanonical-stack" => Payload(writer => Record(writer, stack: 1)),
            "noncanonical-variant" => Payload(writer => Record(writer, variant: 0)),
            "unknown-cheat-flags" => Payload(writer => Record(writer, cheats: 2)),
            "duplicate-key" => Payload(writer => Record(writer, metadata: metadata => { metadata.Write((byte)2); metadata.Write("same"); metadata.Write("a"); metadata.Write("same"); metadata.Write("b"); })),
            _ => throw new ArgumentException(nameof(scenario))
        };
        if (scenario == "version") bytes[0] = 109;
        if (scenario == "missing-prefab") bytes[13] &= 191;
        var original = (byte[])bytes.Clone(); Assert.Throws<InvalidDataException>(() => Validate(bytes)); Assert.Equal(original, bytes);
    }
    [Theory]
    [InlineData("empty-count")]
    [InlineData("oversized-count")]
    [InlineData("truncated-count")]
    [InlineData("noncanonical-count")]
    [InlineData("oversized-key")]
    [InlineData("overflow-length")]
    [InlineData("noncanonical-length")]
    [InlineData("invalid-utf8")]
    public void Validate_MetadataPrefixesAndEncoding_RefusesBeforeUnboundedAllocation(string scenario)
    {
        var bytes = Payload(writer => Record(writer, metadata: metadata =>
        {
            switch (scenario)
            {
                case "empty-count": metadata.Write((byte)0); break;
                case "oversized-count": metadata.Write(new byte[] { 0x88, 1 }); break; // 2049
                case "truncated-count": metadata.Write((byte)0x80); break;
                case "noncanonical-count": metadata.Write(new byte[] { 0x80, 1 }); break;
                case "oversized-key": metadata.Write((byte)1); metadata.Write(new byte[] { 0x81, 0x80, 1 }); break; // 16385 bytes
                case "overflow-length": metadata.Write((byte)1); metadata.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f }); break;
                case "noncanonical-length": metadata.Write((byte)1); metadata.Write(new byte[] { 0x80, 0 }); break;
                case "invalid-utf8": metadata.Write((byte)1); metadata.Write("key"); metadata.Write((byte)1); metadata.Write((byte)0xff); break;
                default: throw new ArgumentException(nameof(scenario));
            }
        }));
        if (scenario == "truncated-count") bytes = bytes.Take(bytes.Length - 1).ToArray();
        Assert.Throws<InvalidDataException>(() => Validate(bytes));
    }
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(2048)]
    public void Validate_NativeOneAndTwoByteMetadataCountBoundaries_Accepts(int count)
    {
        var bytes = Payload(writer => Record(writer, metadata: metadata =>
        {
            if (count < 128) metadata.Write((byte)count);
            else { metadata.Write((byte)((count >> 8) | 128)); metadata.Write((byte)count); }
            for (var at = 0; at < count; at++) { metadata.Write("key" + at); metadata.Write(""); }
        }));
        Validate(bytes);
    }
    [Fact]
    public void Validate_ByteBoundAndInvalidRegistrationLimits_Refuses()
    {
        Assert.Throws<InvalidDataException>(() => Validate(new byte[NativeTransferInventoryCodec.MaximumBytes + 1]));
        var bytes = Payload(writer => Record(writer));
        foreach (var limits in new[] { new NativeTransferPrefabLimits(0, 4, 3), new(65536, 4, 3), new(50, 0, 3), new(50, 65536, 3), new(50, 4, -1) })
            Assert.Throws<InvalidDataException>(() => NativeTransferInventoryCodec.Validate(bytes, 5, 2, _ => limits));
    }
    [Theory]
    [InlineData("key-at-bound")]
    [InlineData("value-at-bound")]
    [InlineData("value-over-bound")]
    public void Validate_MetadataStringByteBoundaries_AcceptsOnlySupportedLengths(string scenario)
    {
        var bytes = Payload(writer => Record(writer, metadata: metadata =>
        {
            metadata.Write((byte)1);
            metadata.Write(scenario == "key-at-bound" ? new string('k', 16384) : "key");
            metadata.Write(scenario == "value-at-bound" ? new string('v', 65536) :
                scenario == "value-over-bound" ? new string('v', 65537) : "value");
        }));
        if (scenario == "value-over-bound") Assert.Throws<InvalidDataException>(() => Validate(bytes));
        else Validate(bytes);
    }
}
