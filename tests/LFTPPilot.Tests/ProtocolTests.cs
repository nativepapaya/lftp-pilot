using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void RemoteSearchContractsRoundTripOnProtocolVersionSeven()
    {
        var startedAt = new DateTimeOffset(2026, 7, 16, 12, 30, 0, TimeSpan.Zero);
        var search = new RemoteSearchSpec(Guid.NewGuid(), Guid.NewGuid(), "/srv/曲 folder", "[final]*", 12, true);
        var page = new RemoteSearchPage(
            search,
            RemoteSearchState.Completed,
            [new("Report [final]*.txt", "/srv/曲 folder/Report [final]*.txt", RemoteSearchEntryKind.Other)],
            "next-page",
            1,
            42,
            false,
            startedAt,
            startedAt.AddSeconds(1));

        var json = JsonSerializer.Serialize(page, FramedJsonStream.SerializerOptions);
        var actual = JsonSerializer.Deserialize<RemoteSearchPage>(json, FramedJsonStream.SerializerOptions);

        Assert.Equal(8, AgentProtocol.CurrentVersion);
        Assert.Equal("remoteSearch.start", WorkspaceMethods.RemoteSearchStart);
        Assert.Equal("remoteSearch.get", WorkspaceMethods.RemoteSearchGet);
        Assert.Equal("remoteSearch.cancel", WorkspaceMethods.RemoteSearchCancel);
        Assert.NotNull(actual);
        Assert.Equal(search, actual.Search);
        Assert.Equal(RemoteSearchState.Completed, actual.State);
        var match = Assert.Single(actual.EffectiveMatches);
        Assert.Equal("Report [final]*.txt", match.Name);
        Assert.Equal(RemoteSearchEntryKind.Other, match.Kind);
        Assert.Equal(42, actual.ScannedEntries);
        Assert.Equal("next-page", actual.ContinuationToken);
    }

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

    [Fact]
    public void MirrorApprovalWirePayloadRequiresReviewFingerprint()
    {
        var definition = new MirrorDefinition(
            Guid.NewGuid(), Guid.NewGuid(), "reviewed", MirrorDirection.Download, @"C:\mirror", "/mirror");
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionId = Guid.NewGuid(),
            definition,
            previewId = Guid.NewGuid(),
            approvalToken = "approval-token",
            deletionsApproved = true,
        }, FramedJsonStream.SerializerOptions);

        Assert.Throws<JsonException>(() =>
            payload.Deserialize<MirrorApproveRequest>(FramedJsonStream.SerializerOptions));
    }

    [Fact]
    public async Task CancelledRequestAfterServerReceiptIsOutcomeUnknownAndDiscardsLateResponse()
    {
        var pipeName = $"LFTPPilot.Tests.Control.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var firstRequestReceived = NewSignal<ProtocolEnvelope>();
        var requestCancelled = NewSignal<bool>();
        var replacementListening = NewSignal<bool>();
        var server = Task.Run(async () =>
        {
            await using var first = CreatePipeServer(pipeName, maximumInstances: 2);
            await first.WaitForConnectionAsync(serverCancellation.Token);
            var firstRequest = await ReadRequiredEnvelopeAsync(first, serverCancellation.Token);
            firstRequestReceived.TrySetResult(firstRequest);
            await requestCancelled.Task.WaitAsync(serverCancellation.Token);

            try
            {
                await FramedJsonStream.WriteAsync(
                    first,
                    CreateResponse(firstRequest.CorrelationId, connection: 1),
                    serverCancellation.Token);
            }
            catch (IOException)
            {
                // The repaired client closes this connection before the late response is written.
            }

            await using var replacement = CreatePipeServer(pipeName, maximumInstances: 2);
            replacementListening.TrySetResult(true);
            await replacement.WaitForConnectionAsync(serverCancellation.Token);
            var secondRequest = await ReadRequiredEnvelopeAsync(replacement, serverCancellation.Token);
            await FramedJsonStream.WriteAsync(
                replacement,
                CreateResponse(secondRequest.CorrelationId, connection: 2),
                serverCancellation.Token);
        }, CancellationToken.None);

        await using var client = new NamedPipeEngineClient(Environment.ProcessId, pipeName, $"{pipeName}.events");
        try
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pending = client.RequestAsync("wait", cancellationToken: requestCancellation.Token);
            await firstRequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
            requestCancellation.Cancel();
            var exception = await Assert.ThrowsAsync<EngineRequestOutcomeUnknownException>(async () => await pending);
            Assert.Equal("wait", exception.Method);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            requestCancelled.TrySetResult(true);
            await replacementListening.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var recovered = await client.RequestAsync("after-cancel", cancellationToken: recoveryTimeout.Token);

            Assert.Equal(2, recovered.GetProperty("connection").GetInt32());
            await server.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            requestCancelled.TrySetResult(true);
            await StopServerAsync(server, serverCancellation);
        }
    }

    [Fact]
    public async Task CorrelationFailureResetsControlPipeBeforeNextRequest()
    {
        var pipeName = $"LFTPPilot.Tests.Control.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var replacementListening = NewSignal<bool>();
        var server = Task.Run(async () =>
        {
            await using (var first = CreatePipeServer(pipeName))
            {
                await first.WaitForConnectionAsync(serverCancellation.Token);
                _ = await ReadRequiredEnvelopeAsync(first, serverCancellation.Token);
                await FramedJsonStream.WriteAsync(
                    first,
                    CreateResponse(Guid.NewGuid(), connection: 1),
                    serverCancellation.Token);
            }

            await using var replacement = CreatePipeServer(pipeName);
            replacementListening.TrySetResult(true);
            await replacement.WaitForConnectionAsync(serverCancellation.Token);
            var secondRequest = await ReadRequiredEnvelopeAsync(replacement, serverCancellation.Token);
            await FramedJsonStream.WriteAsync(
                replacement,
                CreateResponse(secondRequest.CorrelationId, connection: 2),
                serverCancellation.Token);
        }, CancellationToken.None);

        await using var client = new NamedPipeEngineClient(Environment.ProcessId, pipeName, $"{pipeName}.events");
        try
        {
            var exception = await Assert.ThrowsAsync<EngineRequestOutcomeUnknownException>(() =>
                client.RequestAsync("bad-correlation", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("bad-correlation", exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            await replacementListening.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var recovered = await client.RequestAsync("after-protocol-error", cancellationToken: recoveryTimeout.Token);

            Assert.Equal(2, recovered.GetProperty("connection").GetInt32());
            await server.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await StopServerAsync(server, serverCancellation);
        }
    }

    [Fact]
    public async Task InvalidResponseFrameIsOutcomeUnknownAndResetsControlPipe()
    {
        var pipeName = $"LFTPPilot.Tests.Control.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var replacementListening = NewSignal<bool>();
        var server = Task.Run(async () =>
        {
            await using (var first = CreatePipeServer(pipeName))
            {
                await first.WaitForConnectionAsync(serverCancellation.Token);
                _ = await ReadRequiredEnvelopeAsync(first, serverCancellation.Token);
                var invalidHeader = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32BigEndian(invalidHeader, 0);
                await first.WriteAsync(invalidHeader, serverCancellation.Token);
                await first.FlushAsync(serverCancellation.Token);
            }

            await using var replacement = CreatePipeServer(pipeName);
            replacementListening.TrySetResult(true);
            await replacement.WaitForConnectionAsync(serverCancellation.Token);
            var secondRequest = await ReadRequiredEnvelopeAsync(replacement, serverCancellation.Token);
            await FramedJsonStream.WriteAsync(
                replacement,
                CreateResponse(secondRequest.CorrelationId, connection: 2),
                serverCancellation.Token);
        }, CancellationToken.None);

        await using var client = new NamedPipeEngineClient(Environment.ProcessId, pipeName, $"{pipeName}.events");
        try
        {
            var exception = await Assert.ThrowsAsync<EngineRequestOutcomeUnknownException>(() =>
                client.RequestAsync("invalid-frame", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("invalid-frame", exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            await replacementListening.Task.WaitAsync(TestContext.Current.CancellationToken);

            using var recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var recovered = await client.RequestAsync("after-invalid-frame", cancellationToken: recoveryTimeout.Token);

            Assert.Equal(2, recovered.GetProperty("connection").GetInt32());
            await server.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await StopServerAsync(server, serverCancellation);
        }
    }

    [Fact]
    public async Task AgentErrorResponseKeepsControlPipeReusable()
    {
        var pipeName = $"LFTPPilot.Tests.Control.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = Task.Run(async () =>
        {
            await using var pipe = CreatePipeServer(pipeName);
            await pipe.WaitForConnectionAsync(serverCancellation.Token);
            var rejectedRequest = await ReadRequiredEnvelopeAsync(pipe, serverCancellation.Token);
            await FramedJsonStream.WriteAsync(
                pipe,
                CreateResponse(rejectedRequest.CorrelationId, error: "expected rejection"),
                serverCancellation.Token);

            var acceptedRequest = await ReadRequiredEnvelopeAsync(pipe, serverCancellation.Token);
            await FramedJsonStream.WriteAsync(
                pipe,
                CreateResponse(acceptedRequest.CorrelationId, connection: 1),
                serverCancellation.Token);
        }, CancellationToken.None);

        await using var client = new NamedPipeEngineClient(Environment.ProcessId, pipeName, $"{pipeName}.events");
        try
        {
            var error = await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
                client.RequestAsync("rejected", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("expected rejection", error.Message);
            Assert.Equal("rejected", error.Method);
            Assert.Equal("test-error", error.ErrorCode);

            using var reuseTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            reuseTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var accepted = await client.RequestAsync("accepted", cancellationToken: reuseTimeout.Token);

            Assert.Equal(1, accepted.GetProperty("connection").GetInt32());
            await server.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await StopServerAsync(server, serverCancellation);
        }
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsBlockedRequest()
    {
        var pipeName = $"LFTPPilot.Tests.Control.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var requestReceived = NewSignal<bool>();
        var server = Task.Run(async () =>
        {
            await using var pipe = CreatePipeServer(pipeName);
            await pipe.WaitForConnectionAsync(serverCancellation.Token);
            _ = await ReadRequiredEnvelopeAsync(pipe, serverCancellation.Token);
            requestReceived.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, CancellationToken.None);

        var client = new NamedPipeEngineClient(Environment.ProcessId, pipeName, $"{pipeName}.events");
        try
        {
            var pending = client.RequestAsync("blocked", cancellationToken: TestContext.Current.CancellationToken);
            await requestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

            await client.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<EngineRequestOutcomeUnknownException>(async () => await pending);
            Assert.Equal("blocked", exception.Method);
            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.RequestAsync("after-dispose", cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            await client.DisposeAsync();
            await StopServerAsync(server, serverCancellation);
        }
    }

    [Fact]
    public async Task DisposeStopsBlockedEventEnumeration()
    {
        var eventPipeName = $"LFTPPilot.Tests.Events.{Guid.NewGuid():N}";
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var eventClientConnected = NewSignal<bool>();
        var server = Task.Run(async () =>
        {
            await using var pipe = CreatePipeServer(eventPipeName);
            await pipe.WaitForConnectionAsync(serverCancellation.Token);
            eventClientConnected.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, serverCancellation.Token);
        }, CancellationToken.None);

        var client = new NamedPipeEngineClient(
            Environment.ProcessId,
            $"{eventPipeName}.control",
            eventPipeName);
        var enumerator = client.Events(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        try
        {
            var pendingEvent = enumerator.MoveNextAsync().AsTask();
            await eventClientConnected.Task.WaitAsync(TestContext.Current.CancellationToken);

            await client.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            Assert.False(await pendingEvent.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));
            var afterDispose = client.Events(TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await using (afterDispose)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => afterDispose.MoveNextAsync().AsTask());
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            await client.DisposeAsync();
            await StopServerAsync(server, serverCancellation);
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName, int maximumInstances = 1) =>
        new(pipeName, PipeDirection.InOut, maximumInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static ProtocolEnvelope CreateResponse(Guid correlationId, int connection = 0, string? error = null)
    {
        var response = error is null
            ? new AgentResponse(
                true,
                JsonSerializer.SerializeToElement(new { connection }, FramedJsonStream.SerializerOptions))
            : new AgentResponse(false, Error: new ProtocolError("test-error", error));
        return new ProtocolEnvelope(
            AgentProtocol.CurrentVersion,
            "response",
            correlationId,
            JsonSerializer.SerializeToElement(response, FramedJsonStream.SerializerOptions));
    }

    private static async Task<ProtocolEnvelope> ReadRequiredEnvelopeAsync(Stream stream, CancellationToken cancellationToken) =>
        await FramedJsonStream.ReadAsync<ProtocolEnvelope>(stream, cancellationToken)
            ?? throw new EndOfStreamException("The test client disconnected before sending its request.");

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task StopServerAsync(Task server, CancellationTokenSource cancellation)
    {
        cancellation.Cancel();
        try
        {
            await server;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
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
