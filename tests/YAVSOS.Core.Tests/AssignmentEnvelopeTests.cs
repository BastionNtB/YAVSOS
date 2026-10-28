using System;
using System.IO;
using Xunit;
using YAVSOS.Core.Classification;

namespace YAVSOS.Core.Tests;

public sealed class AssignmentEnvelopeTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";
    [Fact]
    public void Envelope_NewerWithdrawalRejectsLateSnapshotAndDuplicateUntilNewerRecovery()
    {
        var receiver = new AssignmentEnvelopeReceiver();
        Assert.True(receiver.Receive(AssignmentEnvelope.Encode(Session, 1, "yas1:snapshot")));
        Assert.Equal("yas1:snapshot", receiver.Payload);
        Assert.True(receiver.Receive(AssignmentEnvelope.Encode(Session, 3, ""))); Assert.Empty(receiver.Payload);
        Assert.False(receiver.Receive(AssignmentEnvelope.Encode(Session, 2, "yas1:late"))); Assert.Empty(receiver.Payload);
        Assert.False(receiver.Receive(AssignmentEnvelope.Encode(Session, 3, "yas1:conflicting"))); Assert.Empty(receiver.Payload);
        Assert.True(receiver.Receive(AssignmentEnvelope.Encode(Session, 4, "yas1:recovery"))); Assert.Equal("yas1:recovery", receiver.Payload);
    }
    [Fact]
    public void Envelope_UnexpectedServerSessionCannotReplaceCurrentConnectionAndResetAllowsNextServer()
    {
        var receiver = new AssignmentEnvelopeReceiver(); var other = new string('a', 32);
        receiver.Receive(AssignmentEnvelope.Encode(Session, 1, "first"));
        Assert.False(receiver.Receive(AssignmentEnvelope.Encode(other, 99, "foreign"))); Assert.Equal("first", receiver.Payload);
        receiver.Reset(); Assert.Empty(receiver.Payload);
        Assert.True(receiver.Receive(AssignmentEnvelope.Encode(other, 1, "second"))); Assert.Equal("second", receiver.Payload);
    }
    [Theory]
    [InlineData("")]
    [InlineData("yae2:0123456789abcdef0123456789abcdef:1:bad")]
    [InlineData("yae1:0123456789abcdef0123456789abcdef:01:bad")]
    [InlineData("yae1:0123456789abcdef0123456789abcdef:0:bad")]
    [InlineData("yae1:0123456789abcdef0123456789abcdef:9223372036854775808:bad")]
    [InlineData("yae1:0123456789abcdef0123456789abcdeF:1:bad")]
    [InlineData("yae1:short:1:bad")]
    public void Envelope_MalformedDeliveryWithdrawsReadinessAndRequiresNewerValidRecovery(string malformed)
    {
        var receiver = new AssignmentEnvelopeReceiver(); receiver.Receive(AssignmentEnvelope.Encode(Session, 5, "accepted"));
        Assert.True(receiver.Receive(malformed)); Assert.Empty(receiver.Payload); Assert.Contains("rejected", receiver.Diagnostic);
        Assert.False(receiver.Receive(AssignmentEnvelope.Encode(Session, 5, "replayed"))); Assert.Empty(receiver.Payload);
        Assert.True(receiver.Receive(AssignmentEnvelope.Encode(Session, 6, "recovered"))); Assert.Equal("recovered", receiver.Payload);
    }
    [Fact]
    public void Envelope_PayloadAndSequenceBoundsAcceptMaximumAndRejectAdjacentOverrun()
    {
        var payload = new string('x', AuthoritativeAssignmentsCodec.MaximumWireCharacters);
        var envelope = AssignmentEnvelope.Encode(Session, long.MaxValue, payload);
        AssignmentEnvelope.Decode(envelope, out var session, out var sequence, out var decoded);
        Assert.Equal(Session, session); Assert.Equal(long.MaxValue, sequence); Assert.Equal(payload.Length, decoded.Length);
        Assert.Throws<InvalidDataException>(() => AssignmentEnvelope.Encode(Session, 1, payload + "x"));
        Assert.Throws<InvalidDataException>(() => AssignmentEnvelope.Decode(envelope + "x", out _, out _, out _));
        Assert.Throws<InvalidDataException>(() => AssignmentEnvelope.Encode(Session, -1, ""));
    }
}
