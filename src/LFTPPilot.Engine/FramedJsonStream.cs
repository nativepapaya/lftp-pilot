using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed class EngineRequestOutcomeUnknownException : IOException
{
    public EngineRequestOutcomeUnknownException(string method, Exception innerException)
        : base($"The outcome of the '{method}' Agent request is unknown because its transport failed after request I/O began.", innerException)
    {
        Method = method;
    }

    public string Method { get; }
}

public sealed class EngineRequestRejectedException : InvalidOperationException
{
    public EngineRequestRejectedException(string method, ProtocolError? error)
        : base(error?.Message ?? "The Agent request failed.")
    {
        Method = method;
        ErrorCode = error?.Code;
    }

    public string Method { get; }
    public string? ErrorCode { get; }
}

public static class FramedJsonStream
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        if (payload.Length is <= 0 or > AgentProtocol.MaximumFrameBytes)
            throw new InvalidDataException($"The protocol frame must contain between 1 and {AgentProtocol.MaximumFrameBytes} bytes.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        var headerRead = await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!headerRead) return default;
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > AgentProtocol.MaximumFrameBytes)
            throw new InvalidDataException($"Invalid protocol frame length: {length}.");
        var payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("The protocol frame contained JSON null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protocol frame contained invalid JSON.", exception);
        }
    }

    private static async ValueTask<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("The protocol frame header was truncated.");
            }
            read += count;
        }
        return true;
    }
}

public sealed partial class NamedPipeEngineClient : IEngineClient
{
    private readonly int _expectedServerProcessId;
    private readonly string _controlPipeName;
    private readonly string _eventPipeName;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeSync = new();
    private NamedPipeClientStream? _control;
    private Task? _disposeTask;
    private volatile bool _disposed;

    public NamedPipeEngineClient(
        int expectedServerProcessId,
        string controlPipeName = AgentProtocol.ControlPipeName,
        string eventPipeName = AgentProtocol.EventPipeName)
    {
        if (expectedServerProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(expectedServerProcessId));
        _expectedServerProcessId = expectedServerProcessId;
        _controlPipeName = controlPipeName;
        _eventPipeName = eventPipeName;
    }

    public async Task<JsonElement> RequestAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(method) || method.Length > 128) throw new ArgumentException("A bounded method name is required.", nameof(method));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var requestToken = requestCancellation.Token;
            var control = _control ??= await ConnectAsync(_controlPipeName, _expectedServerProcessId, requestToken).ConfigureAwait(false);
            var correlationId = Guid.NewGuid();
            var request = new AgentRequest(method, JsonSerializer.SerializeToElement(payload, FramedJsonStream.SerializerOptions));
            var envelope = new ProtocolEnvelope(AgentProtocol.CurrentVersion, "request", correlationId, JsonSerializer.SerializeToElement(request, FramedJsonStream.SerializerOptions));
            AgentResponse response;
            try
            {
                // Once request I/O begins, any interrupted write/read or invalid response leaves
                // the byte stream boundary unknowable. Never reuse that connection: a late reply
                // could otherwise be mistaken for the next request's response.
                await FramedJsonStream.WriteAsync(control, envelope, requestToken).ConfigureAwait(false);
                var responseEnvelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(control, requestToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The agent closed the control pipe.");
                ValidateEnvelope(responseEnvelope, "response");
                if (responseEnvelope.CorrelationId != correlationId) throw new InvalidDataException("The agent returned a mismatched correlation identifier.");
                response = responseEnvelope.Payload.Deserialize<AgentResponse>(FramedJsonStream.SerializerOptions)
                    ?? throw new InvalidDataException("The agent response was empty.");
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
            {
                ResetControlConnection();
                throw new EngineRequestOutcomeUnknownException(method, exception);
            }
            catch
            {
                ResetControlConnection();
                throw;
            }

            // A well-framed, correlated Agent error completed the exchange and does not poison
            // the connection. Give higher layers a type that cannot be confused with a local
            // InvalidOperationException raised before request dispatch.
            if (!response.Success) throw new EngineRequestRejectedException(method, response.Error);
            return response.Result ?? JsonSerializer.SerializeToElement<object?>(null, FramedJsonStream.SerializerOptions);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async IAsyncEnumerable<EngineEvent> Events([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lifetimeToken = _lifetime.Token;
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        var eventToken = eventCancellation.Token;
        NamedPipeClientStream pipe;
        try
        {
            pipe = await ConnectAsync(_eventPipeName, _expectedServerProcessId, eventToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        await using (pipe)
        {
            while (true)
            {
                ProtocolEnvelope? envelope;
                try
                {
                    envelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(pipe, eventToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                if (envelope is null) yield break;
                ValidateEnvelope(envelope, "event");
                yield return envelope.Payload.Deserialize<EngineEvent>(FramedJsonStream.SerializerOptions)
                    ?? throw new InvalidDataException("The agent event was empty.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            ResetControlConnection();
            _disposeTask = DrainRequestsAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DrainRequestsAsync()
    {
        // Do not dispose the semaphore while a request's finally block may still release it.
        // Taking it proves the active request has observed cancellation and completed cleanup.
        await _requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A connect that raced the initial reset may have assigned its stream before it
            // observed lifetime cancellation. Quiescence makes this final reset authoritative.
            ResetControlConnection();
            _lifetime.Dispose();
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void ResetControlConnection()
    {
        var control = _control;
        _control = null;
        control?.Dispose();
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, int expectedServerProcessId, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);
            pipe.ReadMode = PipeTransmissionMode.Byte;
            ValidateServerProcess(pipe, expectedServerProcessId);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static void ValidateServerProcess(NamedPipeClientStream pipe, int expectedServerProcessId)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actualProcessId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to identify the named-pipe server.");
        if (actualProcessId != (uint)expectedServerProcessId)
            throw new UnauthorizedAccessException($"Named-pipe server PID {actualProcessId} does not match expected Agent PID {expectedServerProcessId}.");
        try
        {
            using var process = Process.GetProcessById(expectedServerProcessId);
            if (process.HasExited) throw new UnauthorizedAccessException("The expected Agent process has exited.");
        }
        catch (ArgumentException exception)
        {
            throw new UnauthorizedAccessException("The expected Agent process no longer exists.", exception);
        }
    }

    private static void ValidateEnvelope(ProtocolEnvelope envelope, string expectedKind)
    {
        if (envelope.Version != AgentProtocol.CurrentVersion) throw new InvalidDataException($"Unsupported agent protocol version {envelope.Version}.");
        if (!string.Equals(envelope.Kind, expectedKind, StringComparison.Ordinal)) throw new InvalidDataException($"Expected a {expectedKind} envelope.");
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
    }
}
