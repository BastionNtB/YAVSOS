using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Vendor.MultiUserChest;

namespace YAVSOS.Core.Tests;

public sealed class UnknownFormatGuardTests
{
    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 3, false)]
    public void ReadsAlike_OnlyPlainItems(bool cheated, int customData, bool expected) =>
        Assert.Equal(expected, UnknownFormatGuard.ReadsAlike(cheated, customData));

    [Theory]
    [InlineData(false, true, 2, false)]
    [InlineData(false, false, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, false, 1, true)]
    public void Refuses_OnlyNonPlainItemsForAnUnknownPeer(bool peerUnknown, bool cheated, int customData, bool expected) =>
        Assert.Equal(expected, UnknownFormatGuard.Refuses(peerUnknown, cheated, customData));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ReturnsSwitchItem_OnlyWhatTheSenderGaveUpForASwap(bool hasSwitchItem, bool stacks, bool expected) =>
        Assert.Equal(expected, UnknownFormatGuard.ReturnsSwitchItem(hasSwitchItem, stacks));

    // The byte layout of research §2.1, on the primitives ZPackage wraps (BinaryWriter/BinaryReader). The item fields follow
    // InventoryHelper.WriteItemToPackage and LoadItemFromPackage; format 2 writes m_cheated after m_pickedUp, format 1 doesn't.

    [Fact]
    public void PlainItemInFormat2_ReadByFormat1_IsTheSameItem()
    {
        var item = Item(cheated: false);
        var bytes = Write(w => WriteItem(w, item, withCheated: true));

        var read = Read(bytes, r => ReadItem(r, withCheated: false), out var leftOver);

        Assert.Equal(item, read);
        Assert.Equal(1, leftOver);
    }

    [Fact]
    public void PlainItemInFormat2_ConsumeAnswer_Format1ReadsTheItem()
    {
        // RequestConsumeResponse: item, Success, Amount. The item reads alike; the fields after it don't, and the handler ignores them.
        var item = Item(cheated: false);
        var bytes = Write(w => { WriteItem(w, item, withCheated: true); w.Write(true); w.Write(1); });

        Assert.Equal(item, Read(bytes, r => ReadItem(r, withCheated: false), out _));
    }

    [Fact]
    public void CheatedItemInFormat2_FollowedByMoreFields_ReadByFormat1_GainsAnEmptyEntryAndLosesTheFlag()
    {
        // As in a consume answer: the format-1 count is 1 and its key and value come from the next two zero bytes.
        var bytes = Write(w => { WriteItem(w, Item(cheated: true), withCheated: true); w.Write(false); w.Write(0); });

        var read = Read(bytes, r => ReadItem(r, withCheated: false), out _)!;

        Assert.False(read.Cheated);
        Assert.Equal("=", read.CustomData);
    }

    [Fact]
    public void CheatedItemInFormat2_AsTheLastField_ReadByFormat1_FailsToParse()
    {
        // As in a remove answer, which ends with its item: only one zero byte follows the misread count.
        var bytes = Write(w => WriteItem(w, Item(cheated: true), withCheated: true));

        Assert.ThrowsAny<IOException>(() => Read(bytes, r => ReadItem(r, withCheated: false), out _));
    }

    [Fact]
    public void CustomDataInFormat2_ReadByFormat1_FailsToParse()
    {
        var bytes = Write(w => WriteItem(w, Item(cheated: false, ("randyx.EpicLoot#MagicItem", "{\"Rarity\":2}")), withCheated: true));

        Assert.ThrowsAny<IOException>(() => Read(bytes, r => ReadItem(r, withCheated: false), out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format1RemoveWithSwitchItem_ReadByFormat2_FailsToParse(bool customData)
    {
        // RequestChestRemove ends with its switch item: today's EOS for a swap from AzuAutoStore's copy (research §5.1).
        var item = customData ? Item(cheated: false, ("k", "v")) : Item(cheated: false);
        var bytes = Write(w => { w.Write(7); WriteCell(w, 0, 0); WriteCell(w, 1, 1); w.Write(5); WriteItem(w, item, withCheated: false); });

        Assert.ThrowsAny<IOException>(() => Read(bytes, r => { r.ReadInt32(); ReadCell(r); ReadCell(r); r.ReadInt32(); return ReadItem(r, withCheated: true); }, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format1AddOfAPlainItem_ReadByFormat2_FailsToParse(bool allowSwitch)
    {
        // RequestChestAdd: item, then allowSwitch. Every add from AzuAutoStore's copy fails here (research §2.1).
        var bytes = Write(w => { w.Write(7); WriteCell(w, -1, -1); WriteItem(w, Item(cheated: false), withCheated: false); w.Write(allowSwitch); });

        Assert.ThrowsAny<IOException>(() => Read(bytes, r => { r.ReadInt32(); ReadCell(r); ReadItem(r, withCheated: true); return r.ReadBoolean(); }, out _));
    }

    [Fact]
    public void RefusalsCarryNoItem_SameBytesInBothFormats()
    {
        // The guard's refusals write a null item: one false byte, whatever the format.
        Assert.Equal(Write(w => WriteItem(w, null, withCheated: true)), Write(w => WriteItem(w, null, withCheated: false)));
    }

    private sealed record ItemFields(string Prefab, int Stack, float Durability, int X, int Y, int Quality, int Variant,
        long CrafterId, string CrafterName, int WorldLevel, bool PickedUp, bool Cheated, string CustomData);

    private static ItemFields Item(bool cheated, params (string Key, string Value)[] customData) =>
        new("Wood", 30, 100f, 2, 1, 1, 0, 123456789L, "Crafter", 3, true, cheated, string.Join(";", customData.Select(x => x.Key + "=" + x.Value)));

    private static void WriteItem(BinaryWriter w, ItemFields? item, bool withCheated)
    {
        w.Write(item != null);
        if (item == null) return;
        w.Write(item.Prefab);
        w.Write(item.Stack);
        w.Write(item.Durability);
        WriteCell(w, item.X, item.Y);
        w.Write(item.Quality);
        w.Write(item.Variant);
        w.Write(item.CrafterId);
        w.Write(item.CrafterName);
        w.Write(item.WorldLevel);
        w.Write(item.PickedUp);
        if (withCheated) w.Write(item.Cheated);
        var pairs = item.CustomData.Length == 0 ? new string[0] : item.CustomData.Split(';');
        w.Write(pairs.Length);
        foreach (var pair in pairs.Select(x => x.Split('=')))
        {
            w.Write(pair[0]);
            w.Write(pair[1]);
        }
    }

    private static ItemFields? ReadItem(BinaryReader r, bool withCheated)
    {
        if (!r.ReadBoolean()) return null;
        var prefab = r.ReadString();
        var stack = r.ReadInt32();
        var durability = r.ReadSingle();
        var (x, y) = ReadCell(r);
        var quality = r.ReadInt32();
        var variant = r.ReadInt32();
        var crafterId = r.ReadInt64();
        var crafterName = r.ReadString();
        var worldLevel = r.ReadInt32();
        var pickedUp = r.ReadBoolean();
        var cheated = withCheated && r.ReadBoolean();
        var customData = new List<string>();
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++) customData.Add(r.ReadString() + "=" + r.ReadString());
        return new ItemFields(prefab, stack, durability, x, y, quality, variant, crafterId, crafterName, worldLevel, pickedUp, cheated, string.Join(";", customData));
    }

    private static void WriteCell(BinaryWriter w, int x, int y) { w.Write(x); w.Write(y); }
    private static (int, int) ReadCell(BinaryReader r) => (r.ReadInt32(), r.ReadInt32());

    private static byte[] Write(System.Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) write(writer);
        return stream.ToArray();
    }

    private static T Read<T>(byte[] bytes, System.Func<BinaryReader, T> read, out long leftOver)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        var result = read(reader);
        leftOver = stream.Length - stream.Position;
        return result;
    }
}
