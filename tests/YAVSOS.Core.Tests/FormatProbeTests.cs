using System;
using System.IO;
using System.Linq;
using Xunit;
using YAVSOS.Vendor.MultiUserChest;

namespace YAVSOS.Core.Tests;

/// <summary>
/// D116 / X19 research §4: the echo probe's bytes, the answer classifier and the per-peer schedule. Layouts follow
/// RequestChestRemove, RequestChestRemoveResponse and InventoryHelper.WriteItemToPackage/LoadItemFromPackage; format 2
/// writes m_cheated after m_pickedUp, format 1 (AzuAutoStore 3.1.7's copy) doesn't.
/// </summary>
public sealed class FormatProbeTests
{
    private const int Id = 123456;

    [Fact]
    public void Request_ReadByFormat2_IsAPlainWoodSwitchItemForNoCell()
    {
        var request = ReadRequest(FormatProbe.Request(Id), withCheated: true, out var leftOver);

        Assert.Equal((Id, -1, -1, -1, -1, 0), (request.Id, request.FromX, request.FromY, request.ToX, request.ToY, request.Amount));
        Assert.Equal(("Wood", 1, false, ""), (request.Item.Prefab, request.Item.Stack, request.Item.Cheated, request.Item.CustomData));
        Assert.Equal(0, leftOver);
    }

    [Fact]
    public void Request_ReadByFormat1_IsTheSameItemWithOneByteLeftOver()
    {
        // Research §4 step 1: the format-1 reader takes the cheated byte and three count bytes as a count of 0.
        var format2 = ReadRequest(FormatProbe.Request(Id), withCheated: true, out _);
        var format1 = ReadRequest(FormatProbe.Request(Id), withCheated: false, out var leftOver);

        Assert.Equal(format2.Item with { Cheated = false }, format1.Item);
        Assert.Equal(1, leftOver);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void Echo_FromEachCopy_GivesItsFormat(bool ownerWritesCheated, int expected)
    {
        // The owner reads the request in its own format and FallbackResponse sends the switch item back in it.
        var item = ReadRequest(FormatProbe.Request(Id), ownerWritesCheated, out _).Item;
        var answer = Answer(Id, item, ownerWritesCheated);

        Assert.Equal(expected, FormatProbe.Classify(answer, out var id));
        Assert.Equal(Id, id);
    }

    [Fact]
    public void Tail_FourZeroBytesIsFormat1_FiveIsFormat2()
    {
        Assert.Equal(1, FormatProbe.Tail(new byte[] { 9, 0, 0, 0, 0 }, 1));
        Assert.Equal(2, FormatProbe.Tail(new byte[] { 9, 0, 0, 0, 0, 0 }, 1));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0 })] // a copy with another field after m_cheated (research §3 row 7b)
    [InlineData(new byte[] { 1, 0, 0, 0, 0 })] // a cheated flag in format 2
    [InlineData(new byte[] { 1, 0, 0, 0 })] // a custom-data count of 1 in format 1
    [InlineData(new byte[] { 0, 0, 0, 1 })]
    public void Tail_AnythingElseIsUnknown(byte[] tail) => Assert.Equal(0, FormatProbe.Tail(tail, 0));

    [Fact]
    public void Answer_WithCustomData_IsUnknown()
    {
        var item = Wood() with { CustomData = "k=v" };

        Assert.Equal(0, FormatProbe.Classify(Answer(Id, item, withCheated: true), out _));
        Assert.Equal(0, FormatProbe.Classify(Answer(Id, item, withCheated: false), out _));
    }

    [Fact]
    public void Answer_WithAnotherFieldAfterTheItem_IsUnknown()
    {
        var bytes = Answer(Id, Wood(), withCheated: true).Concat(new byte[] { 0 }).ToArray();

        Assert.Equal(0, FormatProbe.Classify(bytes, out _));
    }

    [Theory]
    [InlineData("Stone", 1)]
    [InlineData("wood", 1)]
    [InlineData("Wood", 2)]
    [InlineData("Wood", 0)]
    public void Answer_WithAnotherPrefabOrStack_IsUnknown(string prefab, int stack)
    {
        var item = Wood() with { Prefab = prefab, Stack = stack };

        Assert.Equal(0, FormatProbe.Classify(Answer(Id, item, withCheated: false), out var id));
        Assert.Equal(Id, id);
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(false, 0, true)]
    public void Answer_ThatIsNotTheUntouchedFallback_IsUnknown(bool success, int amount, bool switched)
    {
        var bytes = Answer(Id, Wood(), withCheated: true, success, amount, switched);

        Assert.Equal(0, FormatProbe.Classify(bytes, out _));
    }

    [Fact]
    public void Answer_WithoutItem_IsUnknown()
    {
        var bytes = Write(w => { w.Write(Id); w.Write(false); w.Write(0); w.Write(false); w.Write(false); });

        Assert.Equal(0, FormatProbe.Classify(bytes, out var id));
        Assert.Equal(Id, id);
    }

    [Fact]
    public void Answer_Truncated_IsUnknownWithoutThrowing()
    {
        var full = Answer(Id, Wood(), withCheated: true);

        for (var length = 0; length < full.Length - 5; length++)
            Assert.Equal(0, FormatProbe.Classify(full.Take(length).ToArray(), out _));
        Assert.Equal(0, FormatProbe.Classify(null!, out var id));
        Assert.Equal(0, id);
    }

    // The schedule.

    [Fact]
    public void Book_ProbesANewPeerThenRetriesTwiceAtMost()
    {
        var book = new FormatProbeBook();

        Assert.True(book.Due(7, 0));
        book.Sent(7, 1, 0);
        Assert.False(book.Due(7, 29.9));
        Assert.True(book.Due(7, 30));
        book.Sent(7, 2, 30);
        Assert.False(book.Due(7, 119.9));
        Assert.True(book.Due(7, 120));
        book.Sent(7, 3, 120);
        Assert.False(book.Due(7, 100000));
        Assert.Equal("probing (3 sent)", book.Describe(7));
    }

    [Fact]
    public void Book_GivesUpOnceAfterTheLastProbeTimesOut()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0); book.Sent(7, 2, 30); book.Sent(7, 3, 120);

        Assert.Empty(book.Expire(129.9));
        Assert.Equal(new[] { 7L }, book.Expire(130));
        Assert.Empty(book.Expire(500));
        Assert.Null(book.WithCheated(7));
        Assert.Equal("no answer", book.Describe(7));
    }

    [Fact]
    public void Book_AnEarlierUnansweredProbeDoesNotGiveUp()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0);

        Assert.Empty(book.Expire(25));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Book_AnAnswerSettlesTheFormatForTheSession(int format, bool withCheated)
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0);

        Assert.Equal(ProbeAnswer.Settled, book.Answer(7, 1, format));
        Assert.Equal(withCheated, book.WithCheated(7));
        Assert.False(book.Due(7, 1000));
        Assert.Equal(ProbeAnswer.AlreadySettled, book.Answer(7, 1, format == 1 ? 2 : 1));
        Assert.Equal(withCheated, book.WithCheated(7));
        Assert.Empty(book.Expire(1000));
    }

    [Fact]
    public void Book_TakesALateAnswerToAnyProbeSent()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0); book.Sent(7, 2, 30); book.Sent(7, 3, 120);
        book.Expire(200);

        Assert.Equal(ProbeAnswer.Settled, book.Answer(7, 1, 1));
        Assert.False(book.WithCheated(7) ?? true);
    }

    [Fact]
    public void Book_IgnoresAnswersToOtherIdsOrFromOtherPeers()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0);
        book.Sent(8, 2, 0);

        Assert.Equal(ProbeAnswer.NotOurs, book.Answer(7, 2, 2));
        Assert.Equal(ProbeAnswer.NotOurs, book.Answer(9, 1, 2));
        Assert.Equal(ProbeAnswer.NotOurs, book.Answer(7, 0, 0));
        Assert.Null(book.WithCheated(7));
        Assert.Null(book.WithCheated(9));
    }

    [Fact]
    public void Book_AnUnknownAnswerStaysUnknownAndIsNotProbedAgain()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0);

        Assert.Equal(ProbeAnswer.Settled, book.Answer(7, 1, 0));
        Assert.Null(book.WithCheated(7));
        Assert.False(book.Due(7, 1000));
        Assert.Empty(book.Expire(1000));
        Assert.Equal("unknown answer", book.Describe(7));
    }

    // F3: the sender's result line says which: still checking, answered, no answer, or an answer YAVSOS can't read;
    // A2.11: a peer never probed is told apart, so one out of range isn't reported as still being checked.
    [Fact]
    public void Book_StatusFollowsTheProbe()
    {
        var book = new FormatProbeBook();
        Assert.Equal(ProbeStatus.NotProbed, book.Status(7));
        book.Sent(7, 1, 0);
        Assert.Equal(ProbeStatus.Pending, book.Status(7));
        book.Sent(7, 2, 30); book.Sent(7, 3, 120);
        Assert.Equal(ProbeStatus.Pending, book.Status(7));
        book.Expire(130);
        Assert.Equal(ProbeStatus.NoAnswer, book.Status(7));
        book.Answer(7, 2, 1);
        Assert.Equal(ProbeStatus.Answered, book.Status(7));
        book.Sent(8, 4, 0); book.Answer(8, 4, 0);
        Assert.Equal(ProbeStatus.UnknownAnswer, book.Status(8));
    }

    [Fact]
    public void Book_ForgetsPeersThatLeft()
    {
        var book = new FormatProbeBook();
        book.Sent(7, 1, 0); book.Answer(7, 1, 1);
        book.Sent(8, 2, 0); book.Answer(8, 2, 2);

        book.Prune(peer => peer == 8);

        Assert.Equal(1, book.Count);
        Assert.Null(book.WithCheated(7));
        Assert.Equal("not probed", book.Describe(7));
        Assert.True(book.WithCheated(8) ?? false);
        book.Clear();
        Assert.Equal(0, book.Count);
    }

    [Fact]
    public void Book_IsBounded()
    {
        var book = new FormatProbeBook();
        for (var peer = 1; peer <= FormatProbeBook.MaximumPeers; peer++) book.Sent(peer, peer, 0);

        Assert.False(book.Due(100000, 0));
        book.Sent(100000, 1, 0);
        Assert.Equal(FormatProbeBook.MaximumPeers, book.Count);
    }

    // Wire helpers on the primitives ZPackage wraps.

    private sealed record ItemFields(string Prefab, int Stack, float Durability, int X, int Y, int Quality, int Variant,
        long CrafterId, string CrafterName, int WorldLevel, bool PickedUp, bool Cheated, string CustomData);

    private sealed record RequestFields(int Id, int FromX, int FromY, int ToX, int ToY, int Amount, ItemFields Item);

    private static ItemFields Wood() => new("Wood", 1, 100f, 0, 0, 1, 0, 0L, "", 0, false, false, "");

    private static RequestFields ReadRequest(byte[] bytes, bool withCheated, out long leftOver)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var request = new RequestFields(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), ReadItem(reader, withCheated)!);
        leftOver = reader.BaseStream.Length - reader.BaseStream.Position;
        return request;
    }

    private static byte[] Answer(int id, ItemFields item, bool withCheated, bool success = false, int amount = 0, bool switched = false) =>
        Write(w => { w.Write(id); w.Write(success); w.Write(amount); w.Write(switched); WriteItem(w, item, withCheated); });

    private static void WriteItem(BinaryWriter w, ItemFields item, bool withCheated)
    {
        w.Write(true);
        w.Write(item.Prefab);
        w.Write(item.Stack);
        w.Write(item.Durability);
        w.Write(item.X); w.Write(item.Y);
        w.Write(item.Quality);
        w.Write(item.Variant);
        w.Write(item.CrafterId);
        w.Write(item.CrafterName);
        w.Write(item.WorldLevel);
        w.Write(item.PickedUp);
        if (withCheated) w.Write(item.Cheated);
        var pairs = item.CustomData.Length == 0 ? Array.Empty<string>() : item.CustomData.Split(';');
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
        var prefab = r.ReadString(); var stack = r.ReadInt32(); var durability = r.ReadSingle();
        var x = r.ReadInt32(); var y = r.ReadInt32(); var quality = r.ReadInt32(); var variant = r.ReadInt32();
        var crafterId = r.ReadInt64(); var crafterName = r.ReadString(); var worldLevel = r.ReadInt32(); var pickedUp = r.ReadBoolean();
        var cheated = withCheated && r.ReadBoolean();
        var count = r.ReadInt32();
        var pairs = Enumerable.Range(0, count).Select(_ => r.ReadString() + "=" + r.ReadString()).ToArray();
        return new ItemFields(prefab, stack, durability, x, y, quality, variant, crafterId, crafterName, worldLevel, pickedUp, cheated, string.Join(";", pairs));
    }

    private static byte[] Write(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) write(writer);
        return stream.ToArray();
    }
}
