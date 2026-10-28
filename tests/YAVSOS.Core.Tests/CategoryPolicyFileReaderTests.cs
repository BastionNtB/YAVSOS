using System;
using System.IO;
using System.Text;
using Xunit;
using YAVSOS.Core.Policy;
using YAVSOS.Networking;

namespace YAVSOS.Core.Tests;

public sealed class CategoryPolicyFileReaderTests
{
    [Fact]
    public void PooledRead_EmptyAndSequentialFailures_DoNotLeakPriorPayload()
    {
        using var first = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 262144)));
        Assert.True(CategoryPolicyFileReader.TryRead(first, out var full, out var diagnostic), diagnostic);
        Assert.Equal(new string('x', 262144), full);

        using var invalid = new MemoryStream(new byte[] { 0xC3, 0x28 });
        Assert.False(CategoryPolicyFileReader.TryRead(invalid, out var rejected, out diagnostic));
        Assert.Equal("", rejected);
        Assert.NotEmpty(diagnostic);

        using var empty = new MemoryStream();
        Assert.True(CategoryPolicyFileReader.TryRead(empty, out var emptyPayload, out diagnostic), diagnostic);
        Assert.Equal("", emptyPayload);
        Assert.Equal("", diagnostic);

        using var last = new MemoryStream(Encoding.UTF8.GetBytes("Træ 🪵"));
        Assert.True(CategoryPolicyFileReader.TryRead(last, out var shortPayload, out diagnostic), diagnostic);
        Assert.Equal("Træ 🪵", shortPayload);
        Assert.Equal("", diagnostic);
        Assert.True(first.CanRead && invalid.CanRead && empty.CanRead && last.CanRead);
    }

    [Fact]
    public void DeclaredLengthOverLimit_ReturnsDiagnosticWithoutReading()
    {
        using var stream = new ObservedStream(new byte[] { 1, 2, 3 }, reportedLength: 262145);

        Assert.False(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal("", payload);
        Assert.Equal("Category file exceeds 256 KiB.", diagnostic);
        Assert.Equal(0, stream.ReadCalls);
        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void GrowingStream_RejectsAfterReadingOnlyLimitPlusOneBytes()
    {
        using var stream = new ObservedStream(new byte[262208], reportedLength: 1, maxChunk: 4096);

        Assert.False(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal("", payload);
        Assert.Equal("Category file exceeds 256 KiB.", diagnostic);
        Assert.Equal(262145, stream.BytesRead);
        Assert.Equal(262145, stream.Position);
        Assert.Equal(65, stream.ReadCalls);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ExactByteLimit_ReadsCompletePayloadWithoutTruncation()
    {
        var xml = "<policy xmlns=\"urn:com.bastionntb.yavsos:categories\" schema=\"1\" revision=\"8\">"
            + "<categories><category id=\"wood\" displayName=\"Træ\" /></categories><assignments /></policy>";
        var expected = xml + new string(' ', 262144 - Encoding.UTF8.GetByteCount(xml));
        using var stream = new ObservedStream(Encoding.UTF8.GetBytes(expected), maxChunk: 4096);

        Assert.True(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal(expected, payload);
        Assert.Equal("", diagnostic);
        Assert.Equal(262144, stream.BytesRead);
        Assert.Equal(65, stream.ReadCalls); // 64 complete chunks, then EOF.
        Assert.True(CategoryPolicyCodec.TryParse(payload, out var policy, out var parseDiagnostic), parseDiagnostic);
        Assert.Equal(8, policy!.Revision);
        Assert.Equal("Træ", Assert.Single(policy.Categories).DisplayName);
    }

    [Fact]
    public void PartialReadsAndUtf8Bom_PreserveUnicodePolicyContent()
    {
        var expected = "<policy xmlns=\"urn:com.bastionntb.yavsos:categories\" schema=\"1\" revision=\"2\">"
            + "<categories><category id=\"wood\" displayName=\"Træ 🪵\" /></categories><assignments /></policy>";
        var bytes = Encoding.UTF8.GetBytes("\uFEFF" + expected);
        using var stream = new ObservedStream(bytes, maxChunk: 1);

        Assert.True(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal(expected, payload);
        Assert.Equal("", diagnostic);
        Assert.Equal(bytes.Length, stream.BytesRead);
        Assert.Equal(bytes.Length + 1, stream.ReadCalls);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void InvalidUtf8_RejectsWithoutReturningReplacementCharacters()
    {
        using var stream = new ObservedStream(new byte[] { 0x3C, 0xC3, 0x28, 0x3E });

        Assert.False(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal("", payload);
        Assert.NotEmpty(diagnostic);
        Assert.Equal(4, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ReadIoFailure_ReturnsOriginalDiagnosticWithoutDisposingStream()
    {
        using var stream = new ObservedStream(new byte[] { 1 }, readFailure: new IOException("Controlled read failure"));

        Assert.False(CategoryPolicyFileReader.TryRead(stream, out var payload, out var diagnostic));

        Assert.Equal("", payload);
        Assert.Equal("Controlled read failure", diagnostic);
        Assert.Equal(1, stream.ReadCalls);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    private sealed class ObservedStream : MemoryStream
    {
        private readonly long? reportedLength;
        private readonly int maxChunk;
        private readonly IOException? readFailure;
        public int ReadCalls { get; private set; }
        public int BytesRead { get; private set; }

        public ObservedStream(byte[] bytes, long? reportedLength = null, int maxChunk = int.MaxValue,
            IOException? readFailure = null) : base(bytes, writable: false)
            => (this.reportedLength, this.maxChunk, this.readFailure) = (reportedLength, maxChunk, readFailure);

        public override long Length => reportedLength ?? base.Length;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            if (readFailure != null) throw readFailure;
            var read = base.Read(buffer, offset, Math.Min(count, maxChunk));
            BytesRead += read;
            return read;
        }
    }
}
