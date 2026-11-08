using System;
using System.Linq;
using Xunit;
using YAVSOS.Core.Classification;
using YAVSOS.Core.Interface;
using YAVSOS.Core.Messages;

namespace YAVSOS.Core.Tests;

/// <summary>
/// X62 (D145): the server's stage state value (encoding, bounds, malformed and unknown versions), the old/new mix and
/// session isolation on the client, the server's report, and which mark and message a client shows for each state.
/// </summary>
public sealed class ServerStageStateTests
{
    private const string Session = "0123456789abcdef0123456789abcdef", Other = "fedcba9876543210fedcba9876543210";

    private static ServerStageState Failed(long sequence = 1, string session = Session, string failure = "Classification rejected: InvalidOperationException: boom. Trying again in 60 s (failure 1).") =>
        new(session, sequence, ServerStageCondition.Failed, ServerStage.Classification, failure, 1_800_000_000);
    private static ServerStageState Loading(long sequence = 1, string session = Session) =>
        new(session, sequence, ServerStageCondition.Loading, ServerStage.Catalog, "", 0);
    private static ServerStageState Ready(long sequence = 1, string session = Session) =>
        new(session, sequence, ServerStageCondition.Ready, ServerStage.None, "", 0);

    private static ServerStageCodec.DecodeResult Decode(string? value, out ServerStageState? state) => ServerStageCodec.TryDecode(value, out state, out _);

    [Fact]
    public void Encode_RoundTrips_EveryConditionAndStage()
    {
        var failure = ServerStageCodec.Encode(Failed(7));
        Assert.Equal("yss1:" + Session + ":7:failed:classification:1800000000:Classification rejected: InvalidOperationException: boom. Trying again in 60 s (failure 1).", failure);
        Assert.Equal("yss1:" + Session + ":1:loading:catalog:-:", ServerStageCodec.Encode(Loading()));
        Assert.Equal("yss1:" + Session + ":2:ready:none:-:", ServerStageCodec.Encode(Ready(2)));
        foreach (var state in new[] { Failed(7), Loading(3), Ready(9),
                     new ServerStageState(Session, 4, ServerStageCondition.Loading, ServerStage.Snapshot, "", 0),
                     new ServerStageState(Session, 5, ServerStageCondition.Failed, ServerStage.Sources, "a:b:c ✓ 𝄞", ServerStageCodec.MaximumRetryAtUnixSeconds),
                     new ServerStageState(Session, long.MaxValue, ServerStageCondition.Failed, ServerStage.Catalog, "x", 0) })
        {
            Assert.Equal(ServerStageCodec.DecodeResult.Decoded, Decode(ServerStageCodec.Encode(state), out var back));
            Assert.Equal((state.Session, state.Sequence, state.Condition, state.Stage, state.Failure, state.RetryAtUnixSeconds),
                (back!.Session, back.Sequence, back.Condition, back.Stage, back.Failure, back.RetryAtUnixSeconds));
        }
    }

    [Fact]
    public void Bounds_LongestValueFits_AndOversizedValuesAreRefused()
    {
        var longest = new ServerStageState(Session, long.MaxValue, ServerStageCondition.Failed, ServerStage.Classification,
            new string('x', ServerStageCodec.MaximumFailureCharacters), ServerStageCodec.MaximumRetryAtUnixSeconds);
        var encoded = ServerStageCodec.Encode(longest);
        Assert.True(encoded.Length <= ServerStageCodec.MaximumCharacters);
        Assert.Equal(ServerStageCodec.DecodeResult.Decoded, Decode(encoded, out _));

        Assert.Throws<ArgumentException>(() => new ServerStageState(Session, 1, ServerStageCondition.Failed, ServerStage.Catalog,
            new string('x', ServerStageCodec.MaximumFailureCharacters + 1), 0));
        Assert.Equal(ServerStageCodec.DecodeResult.Malformed, Decode(encoded.Replace(":x", ":xx"), out _)); // 513 characters of failure.
        Assert.Equal(ServerStageCodec.DecodeResult.Malformed, Decode(new string('y', ServerStageCodec.MaximumCharacters + 1), out _));
        Assert.Equal(ServerStageCodec.DecodeResult.Malformed, Decode("yss1:" + Session + ":1:failed:catalog:253402300800:x", out _)); // Past year 9999.
    }

    [Fact]
    public void Bound_CleansAndCutsADiagnosticSoItAlwaysEncodes()
    {
        Assert.Equal("a b", ServerStageCodec.Bound("a\nb"));
        Assert.Equal("No reason given.", ServerStageCodec.Bound(" \t "));
        Assert.Equal("No reason given.", ServerStageCodec.Bound(null));
        Assert.Equal("? lone", ServerStageCodec.Bound("\uD800 lone"));
        var cut = ServerStageCodec.Bound(new string('x', 510) + "𝄞𝄞𝄞");
        Assert.True(cut.Length <= ServerStageCodec.MaximumFailureCharacters);
        Assert.EndsWith("…", cut);
        Assert.False(char.IsHighSurrogate(cut[cut.Length - 2])); // Never half a character before the ellipsis.
        _ = ServerStageCodec.Encode(new ServerStageState(Session, 1, ServerStageCondition.Failed, ServerStage.Catalog, cut, 0));
    }

    [Theory]
    [InlineData("yss1:" + Session + ":1:failed:catalog:5:")]              // Failed with no failure text.
    [InlineData("yss1:" + Session + ":1:failed:catalog:5:   ")]           // Whitespace only.
    [InlineData("yss1:" + Session + ":1:failed:catalog:5:a\nb")]          // Control character.
    [InlineData("yss1:" + Session + ":1:failed:catalog:-:x")]             // Failed with no retry time.
    [InlineData("yss1:" + Session + ":1:failed:catalog:05:x")]            // Leading zero.
    [InlineData("yss1:" + Session + ":1:failed:catalog:+5:x")]            // Sign.
    [InlineData("yss1:" + Session + ":1:failed:none:5:x")]                // Failed names no stage.
    [InlineData("yss1:" + Session + ":1:loading:catalog:5:")]             // A retry time while loading.
    [InlineData("yss1:" + Session + ":1:loading:catalog:-:x")]            // A failure while loading.
    [InlineData("yss1:" + Session + ":1:loading:none:-:")]                // Loading names no stage.
    [InlineData("yss1:" + Session + ":1:ready:catalog:-:")]               // Ready names a stage.
    [InlineData("yss1:" + Session + ":0:ready:none:-:")]                  // Sequence 0.
    [InlineData("yss1:" + Session + ":01:ready:none:-:")]                 // Leading zero.
    [InlineData("yss1:" + Session + ":9223372036854775808:ready:none:-:")] // Past Int64.
    [InlineData("yss1:" + Session + ":1:Ready:none:-:")]                  // Case matters.
    [InlineData("yss1:" + Session + ":1:stuck:none:-:")]                  // Unknown condition.
    [InlineData("yss1:" + Session + ":1:loading:biomes:-:")]              // Unknown stage.
    [InlineData("yss1:" + Session + ":1:ready:none:-")]                   // A field short.
    [InlineData("yss1:0123456789ABCDEF0123456789ABCDEF:1:ready:none:-:")] // Upper-case session.
    [InlineData("yss1:0123:1:ready:none:-:")]                             // Short session.
    [InlineData("yae1:" + Session + ":1:")]                               // The assignment value's format.
    [InlineData("ready")]
    public void Malformed_IsRefused(string value) => Assert.Equal(ServerStageCodec.DecodeResult.Malformed, Decode(value, out _));

    [Theory]
    [InlineData("yss2:" + Session + ":1:failed:catalog:5:x")]
    [InlineData("yss10:anything a later version sends")]
    public void AnotherFormatVersion_IsUnknown_NotMalformed(string value) =>
        Assert.Equal(ServerStageCodec.DecodeResult.UnknownVersion, Decode(value, out _));

    [Fact]
    public void Empty_IsNoState()
    {
        Assert.Equal(ServerStageCodec.DecodeResult.Empty, Decode(null, out var a));
        Assert.Equal(ServerStageCodec.DecodeResult.Empty, Decode("", out var b));
        Assert.Null(a); Assert.Null(b);
    }

    [Fact]
    public void Receiver_FromAServerWithoutTheValue_HoldsNothing_SoTodaysTextStays()
    {
        // A 1.2 server never registers the value: the client's copy stays at its "" default.
        var receiver = new ServerStageReceiver();
        Assert.False(receiver.Receive("", Session));
        Assert.Null(receiver.For(Session));
        Assert.False(CategoryWait.ServerFailed(true, false, receiver.For(Session)));
        Assert.Equal("yavsos_mark_categories_loading", PanelMarks.Categories(false, 0, false, serverFailed: false).Lines.Single().Text.Key);
    }

    [Fact]
    public void Receiver_DropsOlderSessionsAndSequences()
    {
        var receiver = new ServerStageReceiver();
        Assert.True(receiver.Receive(ServerStageCodec.Encode(Failed(3)), Session));
        Assert.False(receiver.Receive(ServerStageCodec.Encode(Ready(2)), Session));   // Older sequence.
        Assert.False(receiver.Receive(ServerStageCodec.Encode(Ready(3)), Session));   // Same sequence.
        Assert.False(receiver.Receive(ServerStageCodec.Encode(Ready(9, Other)), Session)); // Another session than the bound one.
        Assert.Equal(ServerStageCondition.Failed, receiver.For(Session)!.Condition);
        Assert.True(receiver.Receive(ServerStageCodec.Encode(Ready(4)), Session));
        Assert.Equal(ServerStageCondition.Ready, receiver.For(Session)!.Condition);
    }

    [Fact]
    public void Receiver_BeforeTheAssignmentSessionIsKnown_HoldsTheValue_ButShowsItOnlyForItsOwnSession()
    {
        var receiver = new ServerStageReceiver();
        Assert.True(receiver.Receive(ServerStageCodec.Encode(Failed(1, Other)), null)); // Arrived before the assignment value.
        Assert.Null(receiver.For(null));
        Assert.Null(receiver.For(Session)); // A value from an older session: ignored.
        Assert.NotNull(receiver.For(Other));
        Assert.True(receiver.Receive(ServerStageCodec.Encode(Loading(1)), Session)); // The current session's value replaces it.
        Assert.Equal(ServerStageCondition.Loading, receiver.For(Session)!.Condition);
        Assert.False(receiver.Receive(ServerStageCodec.Encode(Failed(5, Other)), Session)); // A late one from the older session.
        Assert.Equal(ServerStageCondition.Loading, receiver.For(Session)!.Condition);
    }

    [Fact]
    public void Receiver_MalformedOrUnknownVersion_ClearsTheState_NeverAFailure()
    {
        var receiver = new ServerStageReceiver();
        receiver.Receive(ServerStageCodec.Encode(Failed(1)), Session);
        Assert.True(receiver.Receive("yss1:garbage", Session));
        Assert.True(receiver.Rejected);
        Assert.NotEmpty(receiver.Diagnostic);
        Assert.Null(receiver.For(Session));
        Assert.False(CategoryWait.ServerFailed(true, false, receiver.For(Session)));

        receiver.Receive(ServerStageCodec.Encode(Failed(2)), Session);
        Assert.False(receiver.Rejected);
        Assert.True(receiver.Receive("yss2:" + Session + ":3:failed:catalog:5:x", Session));
        Assert.False(receiver.Rejected); // Not a fault worth a warning: a later YAVSOS on the server.
        Assert.Null(receiver.For(Session));

        receiver.Receive(ServerStageCodec.Encode(Failed(4)), Session);
        receiver.Reset();
        Assert.Null(receiver.State);
    }

    [Fact]
    public void Report_NamesTheFirstFailedStage_ThenReady_ThenTheFirstStageStillLoading()
    {
        var done = new StageFacts(true);
        var loading = new StageFacts(false);
        var failed = new StageFacts(false, "boom", 42.5);

        var r = ServerStageReport.Of(false, done, done, failed, new StageFacts(false, "later"));
        Assert.Equal((ServerStageCondition.Failed, ServerStage.Classification, "boom", 42.5), (r.Condition, r.Stage, r.Failure, r.RetryInSeconds));
        // A failed stage shows even while answers from before are kept (the client keeps its own, ready answers then).
        Assert.Equal(ServerStageCondition.Failed, ServerStageReport.Of(true, failed, loading, loading, loading).Condition);
        Assert.Equal(ServerStageCondition.Ready, ServerStageReport.Of(true, done, done, done, done).Condition);
        Assert.Equal(ServerStage.Sources, ServerStageReport.Of(false, done, loading, loading, loading).Stage);
        Assert.Equal(ServerStage.Snapshot, ServerStageReport.Of(false, done, done, done, done).Stage); // Done, but answers not ready yet.
        Assert.Equal(0, ServerStageReport.Of(false, new StageFacts(false, "x", double.NaN), done, done, done).RetryInSeconds);
        Assert.Equal(3600, ServerStageReport.Of(false, new StageFacts(false, "x", 1e9), done, done, done).RetryInSeconds);

        // Equality leaves out the retry countdown, so the server republishes only when the state changes.
        Assert.Equal(ServerStageReport.Of(false, new StageFacts(false, "boom", 59), done, done, done), ServerStageReport.Of(false, new StageFacts(false, "boom", 12), done, done, done));
        Assert.NotEqual(ServerStageReport.Of(false, new StageFacts(false, "boom (failure 1)", 5), done, done, done), ServerStageReport.Of(false, new StageFacts(false, "boom (failure 2)", 5), done, done, done));

        var state = r.ToState(Session, 3, 1_000);
        Assert.Equal((ServerStageCondition.Failed, 1_043L), (state.Condition, state.RetryAtUnixSeconds));
        Assert.Equal(0, ServerStageReport.Of(true, done, done, done, done).ToState(Session, 1, 1_000).RetryAtUnixSeconds);
    }

    [Fact]
    public void StageRetry_SecondsUntil_CountsDownInSeconds()
    {
        var retry = new StageRetry(0, 0, 1000);
        Assert.Equal(-1, retry.SecondsUntil(0));
        retry.Schedule(10_000);
        Assert.Equal(60, retry.SecondsUntil(10_000));
        Assert.Equal(15, retry.SecondsUntil(55_000));
        Assert.Equal(0, retry.SecondsUntil(999_999));
    }

    [Theory]
    // managed client, categories ready, server state → server failed?
    [InlineData(true, false, "failed", true)]
    [InlineData(true, false, "loading", false)]
    [InlineData(true, false, "ready", false)]
    [InlineData(true, false, null, false)]      // Nothing from the server (1.2 server, or none yet).
    [InlineData(true, true, "failed", false)]   // Answers from before still work: no "loading" text to replace.
    [InlineData(false, false, "failed", false)] // Host, single player, or still waiting for the server's settings.
    public void ServerFailed_OnlyForAManagedClientWithoutCategories(bool managed, bool ready, string? condition, bool expected)
    {
        var state = condition switch { "failed" => Failed(), "loading" => Loading(), "ready" => Ready(), _ => null };
        Assert.Equal(expected, CategoryWait.ServerFailed(managed, ready, state));
    }

    // X71 (D159): which story every place tells, in the Categories mark's order: this game's own failure, then the server's.
    [Theory]
    // managed client, categories ready, this game gave up, server state → wait state
    [InlineData(true, false, false, null, CategoryWaitState.Loading)]
    [InlineData(true, false, false, "loading", CategoryWaitState.Loading)]
    [InlineData(true, false, false, "failed", CategoryWaitState.ServerFailed)]
    [InlineData(true, false, true, "failed", CategoryWaitState.Failed)]      // The client's own catalog failed: the mark says so first.
    [InlineData(false, false, true, null, CategoryWaitState.Failed)]        // Hosted world or single player that gave up.
    [InlineData(false, false, false, "failed", CategoryWaitState.Loading)]  // Not a managed client: the server's state isn't read.
    [InlineData(true, true, false, "failed", CategoryWaitState.Loading)]    // Ready: nothing waits.
    [InlineData(false, true, true, null, CategoryWaitState.Loading)]
    public void State_SaysWhyCategoriesAreNotReady(bool managed, bool ready, bool gaveUp, string? condition, CategoryWaitState expected)
    {
        var state = condition switch { "failed" => Failed(), "loading" => Loading(), "ready" => Ready(), _ => null };
        Assert.Equal(expected, CategoryWait.State(managed, ready, gaveUp, state));
    }

    [Theory]
    [InlineData(true, true, "failed", true)]
    [InlineData(true, true, "loading", false)]  // A rebuild in progress keeps the old categories quietly.
    [InlineData(true, true, "ready", false)]
    [InlineData(true, true, null, false)]       // A 1.2 server sends nothing.
    [InlineData(true, false, "failed", false)]  // Not ready: the mark says the server couldn't build them instead.
    [InlineData(false, true, "failed", false)]  // A host shows its own failed rebuild (gaveUp).
    public void ServerRebuildFailed_OnlyForAManagedClientWithWorkingCategories(bool managed, bool ready, string? condition, bool expected)
    {
        var state = condition switch { "failed" => Failed(), "loading" => Loading(), "ready" => Ready(), _ => null };
        Assert.Equal(expected, CategoryWait.ServerRebuildFailed(managed, ready, state));
    }

    // X71 (D159): the Categories mark in each state: loading, server failed, local failure, and a failed rebuild with the
    // old categories, on a client and on a host.
    [Fact]
    public void CategoriesMark_InEachState()
    {
        var loading = PanelMarks.Categories(false, 0, true);
        Assert.Equal((MarkState.Limited, "yavsos_mark_categories_loading"), (loading.State, loading.Lines.Single().Text.Key));
        var server = PanelMarks.Categories(false, 0, true, serverFailed: true);
        Assert.Equal((MarkState.Broken, "yavsos_mark_categories_server_failed"), (server.State, server.Lines.Single().Text.Key));
        var local = PanelMarks.Categories(false, 0, false, gaveUp: true);
        Assert.Equal((MarkState.Broken, "yavsos_mark_categories_gave_up"), (local.State, local.Lines.Single().Text.Key));

        var client = PanelMarks.Categories(true, 5, true, serverStale: true);
        Assert.Equal(MarkState.Limited, client.State);
        Assert.Equal(new[] { "yavsos_mark_categories_ready", "yavsos_mark_categories_from_server", "yavsos_mark_categories_server_stale" },
            client.Lines.Select(x => x.Text.Key));
        Assert.EndsWith("The server's last update failed, so these are the categories from before the latest mod or settings change (see the server's log). " +
            "The server tries again every few minutes.", EnglishText.Of(client.Tooltip).Text);
        var host = PanelMarks.Categories(true, 5, false, gaveUp: true);
        Assert.Equal((MarkState.Limited, "yavsos_mark_categories_stale"), (host.State, host.Lines.Last().Text.Key));
        Assert.EndsWith("(see the log). YAVSOS tries again every few minutes.", EnglishText.Of(host.Tooltip).Text);
        // While the categories aren't ready, the stale line never shows: the mark names the failure instead.
        Assert.Single(PanelMarks.Categories(false, 0, true, serverFailed: true, serverStale: true).Lines);
        Assert.Equal(MarkState.Working, PanelMarks.Categories(true, 5, true).State);
    }

    [Fact]
    public void MarkAndMessages_ForEachState()
    {
        Assert.Equal("yavsos_run_categories_loading", CategoryWait.Run(CategoryWaitState.Loading).Key);
        Assert.Equal("yavsos_run_categories_server_failed", CategoryWait.Run(CategoryWaitState.ServerFailed).Key);
        Assert.Equal("yavsos_run_categories_failed", CategoryWait.Run(CategoryWaitState.Failed).Key);
        Assert.Equal("yavsos_stash_categories_partial", CategoryWait.StashNothing(CategoryWaitState.Loading).Key);
        Assert.Equal("yavsos_stash_categories_server_failed", CategoryWait.StashNothing(CategoryWaitState.ServerFailed).Key);
        Assert.Equal("yavsos_stash_categories_failed", CategoryWait.StashNothing(CategoryWaitState.Failed).Key);
        Assert.Equal("yavsos_azu_categories_pending", CategoryWait.AzuPending(CategoryWaitState.Loading).Key);
        Assert.Equal("yavsos_azu_categories_server_failed", CategoryWait.AzuPending(CategoryWaitState.ServerFailed).Key);
        Assert.Equal("yavsos_azu_categories_failed", CategoryWait.AzuPending(CategoryWaitState.Failed).Key);
        Assert.Equal("The server couldn't build the categories (see the server's log). It tries again every few minutes.", EnglishText.Of(CategoryWait.Run(CategoryWaitState.ServerFailed)));
        Assert.Equal("YAVSOS couldn't build the categories (see the log). It tries again every few minutes.", EnglishText.Of(CategoryWait.Run(CategoryWaitState.Failed)));

        var failed = PanelMarks.Categories(false, 0, false, serverFailed: true);
        Assert.Equal((MarkState.Broken, "yavsos_mark_categories_server_failed"), (failed.State, failed.Lines.Single().Text.Key));
        Assert.StartsWith("Not working: The server couldn't build the categories", EnglishText.Of(failed.Tooltip).Text);
        // This game's own failure and a rejected server setting come first; ready categories ignore the server's state.
        Assert.Equal("yavsos_mark_categories_gave_up", PanelMarks.Categories(false, 0, false, gaveUp: true, serverFailed: true).Lines.Single().Text.Key);
        Assert.Equal("yavsos_mark_categories_server_rejected", PanelMarks.Categories(false, 0, false, serverRejected: true, serverFailed: true).Lines.Single().Text.Key);
        Assert.Equal(MarkState.Working, PanelMarks.Categories(true, 5, true, serverFailed: true).State);
        Assert.Equal((MarkState.Limited, "yavsos_mark_categories_loading"),
            (PanelMarks.Categories(false, 0, true).State, PanelMarks.Categories(false, 0, true).Lines.Single().Text.Key));
    }
}
