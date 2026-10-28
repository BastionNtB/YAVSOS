using System;
using Xunit;
using YAVSOS.Core.Policy;

namespace YAVSOS.Core.Tests;

public sealed class PolicyLifecycleTests
{
    [Fact]
    public void CanonicalFormOverByteLimit_IsRefused_AndTheLastPolicyStays()
    {
        const string category = "<category id=\"c\" displayName=\"C\"/>";
        var assignments = new System.Text.StringBuilder();
        var envelopeLength = CategoryPolicyCodecTests.Wrap(category, "").Length;
        for (var i = 0; i < CategoryPolicyCodec.MaxAssignments; i++)
        {
            var rule = "<assignment match=\"exact\" selector=\"I" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
                + new string('a', 90) + "\"><member category=\"c\"/></assignment>";
            if (envelopeLength + assignments.Length + rule.Length > CategoryPolicyCodec.MaxPayloadBytes) break;
            assignments.Append(rule);
        }
        var payload = CategoryPolicyCodecTests.Wrap(category, assignments.ToString());
        // D086 M5 (B1.6): the file fits, but its canonical (published) form doesn't; it is refused everywhere, so a server
        // never publishes what every client rejects, and the last accepted policy stays.
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload) <= CategoryPolicyCodec.MaxPayloadBytes);
        Assert.False(CategoryPolicyCodec.TryParse(payload, out _, out var error));
        Assert.Contains("canonical form", error);
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.Standalone);
        var small = CategoryPolicyCodecTests.Wrap(category, "");
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyLocal(small));
        var kept = state.Local!.Fingerprint;
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyLocal(payload));
        Assert.Contains("bytes", state.Diagnostic); Assert.Equal(kept, state.Local!.Fingerprint);
        state.BeginSession(PolicyMode.AwaitingServer);
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyRemote(state.Stamp.Session, payload));
        Assert.False(state.PolicyReady);
    }

    [Fact]
    public void DuplicateCanonicalLocalAndRemote_PreserveSnapshotAndRecoverGate()
    {
        var state = Managed();
        var local = state.Local!;
        var remote = state.Effective!;
        var stamp = state.Stamp;

        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyLocal(local.CanonicalXml));
        Assert.Same(local, state.Local);
        Assert.True(state.IsCurrent(stamp));
        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyRemote(stamp.Session, remote.CanonicalXml));
        Assert.Same(remote, state.Effective);
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);

        Assert.Equal(PolicyUpdate.Rejected, state.ApplyRemote(stamp.Session, "broken"));
        Assert.False(state.PolicyReady);
        var rejectedStamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyRemote(stamp.Session, remote.CanonicalXml));
        Assert.Same(remote, state.Effective);
        Assert.True(state.PolicyReady);
        Assert.False(state.IsCurrent(rejectedStamp));
        Assert.False(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.CanAutomate);
    }

    private const string LocalCategory = "<category id=\"local\" displayName=\"Personal\"/>";
    private const string ServerCategory = "<category id=\"server\" displayName=\"Shared\"/>";
    private static string Local(long revision = 1) => CategoryPolicyCodecTests.Wrap(LocalCategory, "", revision);
    private static string Remote(long revision = 1) => CategoryPolicyCodecTests.Wrap(ServerCategory, "", revision);

    [Theory]
    [InlineData(PolicyMode.Standalone)]
    [InlineData(PolicyMode.UnmanagedLocal)]
    [InlineData(PolicyMode.ManagedServer)]
    public void LocalMode_ValidPolicyAndClassification_EnablesReadiness(PolicyMode mode)
    {
        var state = new PolicyLifecycle();
        state.BeginSession(mode);
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyLocal(Local()));
        Assert.True(state.PolicyReady);
        Assert.False(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.CanAutomate);
        Assert.Equal("local", state.Effective!.Categories[0].Id);
    }

    [Fact]
    public void LocalReload_MalformedOrConflictingRevision_RetainsAcceptedSnapshot()
    {
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.Standalone);
        state.ApplyLocal(Local(3));
        state.SetAssignmentEvidence("catalog-A", true);
        var accepted = state.Effective;
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyLocal("broken"));
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyLocal(Remote(3)));
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyLocal(Local(2)));
        Assert.Same(accepted, state.Effective);
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);
        Assert.Contains("revision", state.Diagnostic);
    }

    [Fact]
    public void ManagedJoin_ConflictingLocalPolicy_WaitsForAcceptedServerAndMatchingCatalog()
    {
        var state = new PolicyLifecycle();
        state.ApplyLocal(Local());
        state.BeginSession(PolicyMode.AwaitingServer);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.Null(state.Effective);
        Assert.False(state.PolicyReady);
        Assert.False(state.CanAutomate);
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyRemote(state.Stamp.Session, Remote()));
        Assert.Equal(PolicyMode.ManagedClient, state.Mode);
        Assert.Equal("server", state.Effective!.Categories[0].Id);
        Assert.Equal("local", state.Local!.Categories[0].Id);
        Assert.False(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.CanAutomate);
    }

    [Fact]
    public void ManagedOverlay_LocalEdits_AreSavedForRestorationWithoutReplacingEffectivePolicy()
    {
        var state = Managed();
        var effective = state.Effective;
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyLocal(Local(2)));
        Assert.Same(effective, state.Effective);
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);
        state.BeginSession(PolicyMode.Offline);
        Assert.Equal(2, state.Local!.Revision);
        Assert.False(state.CanAutomate);
        state.BeginSession(PolicyMode.Standalone);
        Assert.Equal("local", state.Effective!.Categories[0].Id);
        Assert.Equal(2, state.Effective.Revision);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unsupported-schema")]
    [InlineData("rollback")]
    [InlineData("conflicting-revision")]
    public void ManagedReload_InvalidPayload_RetainsOverlayButClosesAutomationGate(string caseId)
    {
        var state = Managed();
        var effective = state.Effective;
        var stamp = state.Stamp;
        var payload = caseId switch
        {
            "malformed" => "<policy>",
            "unsupported-schema" => Remote(3).Replace("schema=\"1\"", "schema=\"2\""),
            "rollback" => Remote(1),
            _ => Local(2)
        };
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyRemote(stamp.Session, payload));
        Assert.Same(effective, state.Effective);
        Assert.False(state.PolicyReady);
        Assert.False(state.CanAutomate);
        Assert.False(state.IsCurrent(stamp));
        Assert.Contains("blocked", state.Diagnostic);
    }

    [Fact]
    public void ManagedReload_NewRevision_InvalidatesPlansAndRequiresClassificationRebuild()
    {
        var state = Managed();
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyRemote(stamp.Session, Remote(3)));
        Assert.Equal(3, state.Effective!.Revision);
        Assert.False(state.IsCurrent(stamp));
        Assert.False(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.CanAutomate);
    }

    [Fact]
    public void DuplicatePolicy_DoesNotInvalidateReadyClassification()
    {
        var state = Managed();
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyRemote(stamp.Session, Remote(2)));
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);
    }

    [Fact]
    public void InvalidThenValidSameRevision_RecoversOnlyAfterRebuild()
    {
        var state = Managed();
        state.ApplyRemote(state.Stamp.Session, "broken");
        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyRemote(state.Stamp.Session, Remote(2)));
        Assert.True(state.PolicyReady);
        Assert.False(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.CanAutomate);
    }

    [Fact]
    public void DisconnectAndServerSwitch_DropOverlayAndRejectDelayedOldSessionReply()
    {
        var state = Managed();
        var old = state.Stamp;
        state.BeginSession(PolicyMode.Offline);
        Assert.Equal(PolicyUpdate.StaleSession, state.ApplyRemote(old.Session, Remote(9)));
        Assert.Equal("local", state.Effective!.Categories[0].Id);
        Assert.False(state.CanAutomate);
        state.BeginSession(PolicyMode.AwaitingServer);
        Assert.Null(state.Effective);
        Assert.False(state.IsCurrent(old));
        Assert.Equal(PolicyUpdate.StaleSession, state.ApplyRemote(old.Session, Remote(10)));
        Assert.Null(state.Effective);
        // Revisions are scoped to each authority/session, not globally monotonic across servers.
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyRemote(state.Stamp.Session, Remote(1)));
        Assert.Equal(1, state.Effective!.Revision);
    }

    [Fact]
    public void SharedSettingsChange_InvalidatesPlanAndReadyCatalog()
    {
        var state = Managed();
        var stamp = state.Stamp;
        state.SharedSettingsChanged();
        Assert.False(state.IsCurrent(stamp));
        Assert.True(state.PolicyReady);
        Assert.False(state.CanAutomate);
    }

    [Fact]
    public void LocalRevisionAdvance_InvalidatesPlanButDuplicateDoesNot()
    {
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.Standalone);
        state.ApplyLocal(Local());
        state.SetAssignmentEvidence("catalog-A", true);
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.Unchanged, state.ApplyLocal(Local()));
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyLocal(Local(2)));
        Assert.False(state.IsCurrent(stamp));
        Assert.False(state.CanAutomate);
    }

    [Theory]
    [InlineData(PolicyMode.Offline)]
    [InlineData(PolicyMode.Standalone)]
    [InlineData(PolicyMode.ManagedServer)]
    public void RemotePayload_InNonclientMode_RefusesEvenCurrentSession(PolicyMode mode)
    {
        var state = new PolicyLifecycle();
        state.BeginSession(mode);
        state.ApplyLocal(Local());
        var stamp = state.Stamp;
        Assert.Equal(PolicyUpdate.StaleSession, state.ApplyRemote(stamp.Session, Remote()));
        Assert.Equal("local", state.Effective!.Categories[0].Id);
        Assert.True(state.IsCurrent(stamp));
        Assert.Equal(mode, state.Mode);
    }

    [Fact]
    public void MissingLocalPolicy_ReadyCatalogCannotEnableAutomation()
    {
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.Standalone);
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.False(state.PolicyReady);
        Assert.False(state.CanAutomate);
    }

    [Fact]
    public void BeginSession_ManagedClientWithoutAcceptedPolicy_RejectsTransition()
    {
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.Standalone);
        state.ApplyLocal(Local());
        var stamp = state.Stamp;
        Assert.Throws<ArgumentException>(() => state.BeginSession(PolicyMode.ManagedClient));
        Assert.Equal(PolicyMode.Standalone, state.Mode);
        Assert.True(state.IsCurrent(stamp));
        Assert.Equal("local", state.Effective!.Categories[0].Id);
    }

    [Theory]
    [InlineData(PolicyMode.Offline)]
    [InlineData(PolicyMode.Standalone)]
    [InlineData(PolicyMode.ManagedServer)]
    public void AwaitServer_NonclientMode_DoesNotInvalidateLocalSession(PolicyMode mode)
    {
        var state = new PolicyLifecycle();
        state.BeginSession(mode);
        state.ApplyLocal(Local());
        var stamp = state.Stamp;
        state.AwaitServer();
        Assert.Equal(mode, state.Mode);
        Assert.True(state.IsCurrent(stamp));
        Assert.Equal("local", state.Effective!.Categories[0].Id);
    }

    [Fact]
    public void ManagedJoin_FirstInvalidPolicy_NeverFallsBackToLocal()
    {
        var state = new PolicyLifecycle();
        state.ApplyLocal(Local());
        state.BeginSession(PolicyMode.AwaitingServer);
        Assert.Equal(PolicyUpdate.Rejected, state.ApplyRemote(state.Stamp.Session,
            Remote().Replace("schema=\"1\"", "schema=\"2\"")));
        Assert.Equal(PolicyMode.ManagedClient, state.Mode);
        Assert.Null(state.Effective);
        Assert.Equal("local", state.Local!.Categories[0].Id);
        Assert.False(state.PolicyReady);
        Assert.False(state.CanAutomate);
        Assert.Contains("Unsupported policy schema", state.Diagnostic);
    }

    [Fact]
    public void CatalogEvidence_DuplicateDoesNotInvalidateButChangesDo()
    {
        var state = Managed();
        var stamp = state.Stamp;
        state.SetAssignmentEvidence("catalog-A", true);
        Assert.True(state.IsCurrent(stamp));
        Assert.True(state.CanAutomate);
        state.SetAssignmentEvidence("catalog-B", false);
        Assert.False(state.IsCurrent(stamp));
        Assert.False(state.CanAutomate);
    }

    [Fact]
    public void AwaitingRemoteUpdate_ImmediatelyClosesGateAndRetainsDiagnosticOverlay()
    {
        var state = Managed();
        var effective = state.Effective;
        var stamp = state.Stamp;
        state.AwaitServer();
        Assert.Same(effective, state.Effective);
        Assert.False(state.PolicyReady);
        Assert.False(state.IsCurrent(stamp));
        Assert.False(state.CanAutomate);
    }

    [Fact]
    public void ExplicitUnmanagedMode_LateManagedPolicy_TakesPrecedenceAndInvalidatesLocalPlan()
    {
        var state = new PolicyLifecycle();
        state.BeginSession(PolicyMode.UnmanagedLocal);
        state.ApplyLocal(Local());
        state.SetAssignmentEvidence("catalog-A", true);
        var stamp = state.Stamp;
        state.AwaitServer();
        Assert.False(state.CanAutomate);
        state.ApplyRemote(stamp.Session, Remote());
        Assert.Equal(PolicyMode.ManagedClient, state.Mode);
        Assert.Equal("server", state.Effective!.Categories[0].Id);
        Assert.False(state.IsCurrent(stamp));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("catalog-A", false)]
    public void CatalogEvidence_MissingOrUnready_RefusesManagedAutomation(string? fingerprint, bool ready)
    {
        var state = Managed();
        var stamp = state.Stamp;
        state.SetAssignmentEvidence(fingerprint, ready);
        Assert.False(state.CanAutomate);
        Assert.False(state.IsCurrent(stamp));
    }

    [Fact]
    public void ServerWithoutYavsos_WaitingClientTurnsLocal_AndManagedPolicyStillWins()
    {
        var state = new PolicyLifecycle();
        state.ApplyLocal(Local());
        state.BeginSession(PolicyMode.AwaitingServer);
        Assert.False(state.PolicyReady); // D008: nothing applies while the answer is unknown.
        var waiting = state.Stamp;

        Assert.False(state.ServerLacksYavsos(waiting.Session - 1)); // An earlier connection's answer is ignored.
        Assert.Equal(PolicyMode.AwaitingServer, state.Mode);
        Assert.True(state.ServerLacksYavsos(waiting.Session));
        Assert.Equal(PolicyMode.UnmanagedLocal, state.Mode);
        Assert.True(state.PolicyReady);
        Assert.Same(state.Local, state.Effective);
        Assert.False(state.IsCurrent(waiting)); // Work planned while waiting is stale.
        Assert.False(state.ServerLacksYavsos(waiting.Session)); // Decided once per connection.

        // A managed policy arriving later (a wrong "absent") takes over, as with the old manual setting.
        Assert.Equal(PolicyUpdate.Accepted, state.ApplyRemote(state.Stamp.Session, Remote(2)));
        Assert.Equal(PolicyMode.ManagedClient, state.Mode);
        Assert.NotSame(state.Local, state.Effective);
        Assert.False(state.ServerLacksYavsos(state.Stamp.Session));

        state.BeginSession(PolicyMode.AwaitingServer); // Reconnect: waits again.
        Assert.False(state.PolicyReady);
    }

    [Theory]
    [InlineData(PolicyMode.Offline)]
    [InlineData(PolicyMode.Standalone)]
    [InlineData(PolicyMode.ManagedServer)]
    public void ServerWithoutYavsos_IgnoredOutsideAWaitingClient(PolicyMode mode)
    {
        var state = new PolicyLifecycle();
        state.ApplyLocal(Local());
        state.BeginSession(mode);
        Assert.False(state.ServerLacksYavsos(state.Stamp.Session));
        Assert.Equal(mode, state.Mode);
    }

    [Theory]
    [InlineData(false, false, false, ServerYavsos.Unknown)]
    [InlineData(false, true, true, ServerYavsos.Unknown)]
    [InlineData(true, false, false, ServerYavsos.Absent)]
    [InlineData(true, true, false, ServerYavsos.Present)]
    [InlineData(true, false, true, ServerYavsos.Present)]
    [InlineData(true, true, true, ServerYavsos.Present)]
    public void Detection_AtHandshake(bool readable, bool version, bool settings, ServerYavsos expected) =>
        Assert.Equal(expected, ServerDetection.AtHandshake(readable, version, settings));

    private static PolicyLifecycle Managed()
    {
        var state = new PolicyLifecycle();
        state.ApplyLocal(Local());
        state.BeginSession(PolicyMode.AwaitingServer);
        state.ApplyRemote(state.Stamp.Session, Remote(2));
        state.SetAssignmentEvidence("catalog-A", true);
        return state;
    }
}
