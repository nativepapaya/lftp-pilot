using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task FramedJsonRoundTripsAcrossFragmentedReads()
    {
        var payload = JsonSerializer.SerializeToElement(new { message = "曲.txt", value = 42 });
        var expected = new ProtocolEnvelope(AgentProtocol.CurrentVersion, "request", Guid.NewGuid(), payload);
        await using var backing = new MemoryStream();
        await FramedJsonStream.WriteAsync(backing, expected, TestContext.Current.CancellationToken);
        backing.Position = 0;
        await using var fragmented = new FragmentedReadStream(backing, 3);
        var actual = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(fragmented, TestContext.Current.CancellationToken);
        Assert.NotNull(actual);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal("曲.txt", actual.Payload.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1048577)]
    public async Task InvalidFrameLengthsAreRejected(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TruncatedHeaderAndInvalidJsonAreRejected()
    {
        await using var truncated = new MemoryStream([0, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await FramedJsonStream.ReadAsync<ProtocolEnvelope>(truncated, TestContext.Current.CancellationToken));

        var json = Encoding.UTF8.GetBytes("{not-json}");
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await using var invalid = new MemoryStream(frame);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FramedJsonStream.ReadAsync<ProtocolEnvelope>(invalid, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriterRejectsOversizedPayload()
    {
        await using var stream = new MemoryStream();
        var value = new { data = new string('x', AgentProtocol.MaximumFrameBytes + 1) };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await FramedJsonStream.WriteAsync(stream, value, TestContext.Current.CancellationToken));
    }

    private sealed class FragmentedReadStream(Stream inner, int maximumRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maximumRead));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
