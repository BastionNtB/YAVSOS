using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using YAVSOS.Networking;

namespace YAVSOS.Core.Tests;

public sealed class ServerSyncSettingsBridgeTests
{
    private enum Sources { ConfiguredContainersOnly }
    private static Type? SettingType(string section, string key) => (section, key) switch
    {
        ("Storage", "StashRange") => typeof(float),
        ("Storage", "DefaultPriority") => typeof(int),
        ("Storage", "EligibleContainerPrefabs") => typeof(string),
        ("Server", "LockSharedPolicy") => typeof(bool),
        ("Cleanup", "Sources") => typeof(Sources),
        _ => null
    };

    [Fact]
    public void AcceptedRequest_PublishesCurrentServerValuesOnceAfterProcessing()
    {
        var bridge = new ServerSyncSettingsBridge();
        bridge.BeginSession();
        var captured = bridge.CaptureUpdate(true, true, true);
        var serverRange = 12f;
        var received = new List<float>();
        bridge.CompleteUpdate(captured, true);
        Assert.False(bridge.Publish(true, true, () => received.Add(serverRange)));
        Assert.Empty(received);
        // Publication reads current canonical state at the main-thread boundary,
        // not an earlier captured/request payload.
        serverRange = 14f;
        Assert.True(bridge.Publish(true, false, () => received.Add(serverRange)));
        Assert.Equal(14f, Assert.Single(received));
        Assert.False(bridge.Publish(true, false, () => received.Add(serverRange)));
        Assert.Single(received);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void RefusedIncompleteOrNoncurrentUpdate_DoesNotPublish(bool server, bool clientUpdate, bool peer, bool accepted)
    {
        var bridge = new ServerSyncSettingsBridge();
        bridge.BeginSession();
        bridge.CompleteUpdate(bridge.CaptureUpdate(server, clientUpdate, peer), accepted);
        var publications = new List<string>();
        Assert.False(bridge.Publish(true, false, () => publications.Add("settings")));
        Assert.Empty(publications);
    }

    [Fact]
    public void SessionTransition_DiscardsPendingAndLateAcceptedUpdates()
    {
        var bridge = new ServerSyncSettingsBridge();
        bridge.BeginSession();
        var old = bridge.CaptureUpdate(true, true, true);
        bridge.CompleteUpdate(old, true);
        bridge.BeginSession();
        bridge.CompleteUpdate(old, true);
        var publications = new List<string>();
        Assert.False(bridge.Publish(true, false, () => publications.Add("stale")));
        Assert.Empty(publications);
        bridge.CompleteUpdate(bridge.CaptureUpdate(true, true, true), true);
        Assert.True(bridge.Publish(true, false, () => publications.Add("current")));
        Assert.Equal("current", Assert.Single(publications));
    }

    [Fact]
    public void ReentrantAcceptedUpdate_IsPublishedOnTheNextBoundary()
    {
        var bridge = new ServerSyncSettingsBridge();
        bridge.BeginSession();
        var captured = bridge.CaptureUpdate(true, true, true);
        bridge.CompleteUpdate(captured, true);
        bridge.CompleteUpdate(captured, true); // Burst coalesces into one snapshot.
        var publications = new List<int>();
        Assert.True(bridge.Publish(true, false, () =>
        {
            publications.Add(12);
            bridge.CompleteUpdate(captured, true);
        }));
        Assert.Equal(12, Assert.Single(publications));
        Assert.True(bridge.Publish(true, false, () => publications.Add(14)));
        Assert.Equal(new[] { 12, 14 }, publications);
    }

    [Fact]
    public void LosingServerAuthority_DiscardsQueuedPublication()
    {
        var bridge = new ServerSyncSettingsBridge();
        bridge.CompleteUpdate(bridge.CaptureUpdate(true, true, true), true);
        var publications = new List<int>();
        Assert.False(bridge.Publish(false, false, () => publications.Add(12)));
        Assert.False(bridge.Publish(true, false, () => publications.Add(12)));
        Assert.Empty(publications);
    }

    [Fact]
    public void ManagedSharedSave_PassesCachedLocalValuesAndTypesToSerializer()
    {
        var local = new[] { 5f, 16.83819f };
        var saved = new List<string>();
        for (var i = 0; i < local.Length; i++)
        {
            Assert.True(ServerSyncSettingsBridge.TrySerializeLocal(true, true, local[i], typeof(float),
                (value, type) =>
                {
                    Assert.Equal(typeof(float), type);
                    return ((float)value).ToString(CultureInfo.InvariantCulture);
                }, out var text));
            saved.Add(text);
        }
        Assert.Equal(new[] { "5", "16.83819" }, saved);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void SourcePersonalOrUncachedSave_LeavesSerializationToBepInEx(bool overlay, bool shared, bool cached)
    {
        var calls = new List<object>();
        Assert.False(ServerSyncSettingsBridge.TrySerializeLocal(overlay, shared, cached ? 5f : null, typeof(float),
            (value, _) => { calls.Add(value); return "unexpected"; }, out var text));
        Assert.Empty(calls);
        Assert.Equal("", text);
    }

    [Fact]
    public void TypedPartialSettingsRequest_ValidatesWithoutModifyingThePayload()
    {
        var bytes = Request(("Storage", "StashRange", typeof(float), 12f),
            ("Server", "LockSharedPolicy", typeof(bool), false),
            ("Storage", "DefaultPriority", typeof(int), -3),
            ("Storage", "EligibleContainerPrefabs", typeof(string), "piece_棚"),
            ("Cleanup", "Sources", typeof(int), 0));
        var original = (byte[])bytes.Clone();
        Assert.True(ServerSyncSettingsBridge.ValidateRequest(bytes, 13, SettingType, out var error), error);
        Assert.Equal("", error);
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData("Internal", "lockexempt")]
    [InlineData("Internal", "category-policy-v1")]
    [InlineData("Internal", "serverversion")]
    [InlineData("Local", "UiScale")]
    [InlineData("Storage", "Missing")]
    public void ServerOwnedPersonalAndUnknownEntries_AreRefusedBeforeLibraryApplication(string section, string key)
    {
        var bytes = Request((section, key, typeof(bool), true));
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(bytes, 13, SettingType, out var error));
        Assert.Equal("Unknown or server-owned settings entry.", error);
    }

    [Fact]
    public void TypeMismatchAndUnknownEnumeration_AreRefused()
    {
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(Request(("Storage", "StashRange", typeof(int), 12)), 13, SettingType, out var typeError));
        Assert.Equal("Settings entry type mismatch.", typeError);
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(Request(("Cleanup", "Sources", typeof(int), 1)), 13, SettingType, out var enumError));
        Assert.Equal("Unknown settings enumeration value.", enumError);
    }

    [Fact]
    public void MalformedEnvelopeAndValues_AreRefused()
    {
        var valid = Request(("Storage", "StashRange", typeof(float), 12f));
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(Array.Empty<byte>(), 13, SettingType, out _));
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(valid[..^1], 13, SettingType, out _));
        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(trailing, 13, SettingType, out var trailingError));
        Assert.Equal("Trailing settings request data.", trailingError);
        foreach (var flag in new byte[] { 0, 2, 8 })
        {
            var flagged = (byte[])valid.Clone(); flagged[0] = flag;
            Assert.False(ServerSyncSettingsBridge.ValidateRequest(flagged, 13, SettingType, out var flagError));
            Assert.Equal("Only complete partial-settings requests are supported.", flagError);
        }
        var empty = new byte[] { 1, 0, 0, 0, 0 };
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(empty, 13, SettingType, out var countError));
        Assert.Equal("Invalid settings entry count.", countError);
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(Request(("Storage", "StashRange", typeof(float), float.NaN)), 13, SettingType, out var valueError));
        Assert.Equal("Nonfinite settings value.", valueError);
    }

    [Fact]
    public void CompressedSettings_AreValidatedAndExpansionIsBounded()
    {
        var valid = Compress(Request(("Storage", "StashRange", typeof(float), 12f)));
        Assert.True(ServerSyncSettingsBridge.ValidateRequest(valid, 13, SettingType, out var error), error);
        var excessive = Compress(Request(("Storage", "EligibleContainerPrefabs", typeof(string), new string('x', ServerSyncSettingsBridge.MaxRequestBytes))));
        Assert.True(excessive.Length < 10000);
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(excessive, 13, SettingType, out var sizeError));
        Assert.Equal("Expanded settings request exceeds 256 KiB.", sizeError);
    }

    [Fact]
    public void InvalidBooleanUtf8StringLengthAndEntryCount_AreRefused()
    {
        var boolean = Request(("Server", "LockSharedPolicy", typeof(bool), true));
        boolean[^1] = 2;
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(boolean, 13, SettingType, out var booleanError));
        Assert.Equal("Invalid Boolean settings value.", booleanError);

        var utf8 = Request(("Storage", "EligibleContainerPrefabs", typeof(string), "x"));
        utf8[^1] = 255;
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(utf8, 13, SettingType, out var utf8Error));
        Assert.NotEmpty(utf8Error);

        var length = new byte[] { 1, 1, 0, 0, 0, 255, 255, 255, 255, 255 };
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(length, 13, SettingType, out var lengthError));
        Assert.Equal("Invalid settings string length.", lengthError);

        var count = Request(("Storage", "StashRange", typeof(float), 12f));
        count[1] = 14;
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(count, 13, SettingType, out var countError));
        Assert.Equal("Invalid settings entry count.", countError);
    }

    [Fact]
    public void RequestByteLimit_AcceptsExactBoundaryAndRefusesTheNextByte()
    {
        var overhead = Request(("Storage", "EligibleContainerPrefabs", typeof(string), "")).Length + 2;
        var exact = Request(("Storage", "EligibleContainerPrefabs", typeof(string), new string('x', ServerSyncSettingsBridge.MaxRequestBytes - overhead)));
        Assert.Equal(ServerSyncSettingsBridge.MaxRequestBytes, exact.Length);
        Assert.True(ServerSyncSettingsBridge.ValidateRequest(exact, 13, SettingType, out var error), error);
        var over = new byte[exact.Length + 1]; exact.CopyTo(over, 0);
        Assert.False(ServerSyncSettingsBridge.ValidateRequest(over, 13, SettingType, out var sizeError));
        Assert.Equal("Settings request exceeds 256 KiB.", sizeError);
    }

    private static byte[] Request(params (string Section, string Key, Type Type, object Value)[] entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)1); writer.Write(entries.Length);
        foreach (var entry in entries)
        {
            writer.Write(entry.Section); writer.Write(entry.Key); writer.Write(entry.Type.AssemblyQualifiedName!);
            switch (entry.Value)
            {
                case bool b: writer.Write(b); break;
                case int i: writer.Write(i); break;
                case float f: writer.Write(f); break;
                case string s: writer.Write(s); break;
                default: throw new InvalidOperationException("Unsupported fixture value");
            }
        }
        return stream.ToArray();
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var data = new MemoryStream();
        using (var deflater = new DeflateStream(data, CompressionLevel.Optimal, true)) deflater.Write(bytes);
        using var envelope = new MemoryStream();
        using var writer = new BinaryWriter(envelope);
        writer.Write((byte)4); writer.Write((int)data.Length); writer.Write(data.ToArray());
        return envelope.ToArray();
    }
}
