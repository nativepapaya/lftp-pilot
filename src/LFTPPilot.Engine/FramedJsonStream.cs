using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public static class FramedJsonStream
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
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
    private NamedPipeClientStream? _control;
    private bool _disposed;

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
            _control ??= await ConnectAsync(_controlPipeName, _expectedServerProcessId, cancellationToken).ConfigureAwait(false);
            var correlationId = Guid.NewGuid();
            var request = new AgentRequest(method, JsonSerializer.SerializeToElement(payload, FramedJsonStream.SerializerOptions));
            var envelope = new ProtocolEnvelope(AgentProtocol.CurrentVersion, "request", correlationId, JsonSerializer.SerializeToElement(request, FramedJsonStream.SerializerOptions));
            await FramedJsonStream.WriteAsync(_control, envelope, cancellationToken).ConfigureAwait(false);
            var responseEnvelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(_control, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("The agent closed the control pipe.");
            ValidateEnvelope(responseEnvelope, "response");
            if (responseEnvelope.CorrelationId != correlationId) throw new InvalidDataException("The agent returned a mismatched correlation identifier.");
            var response = responseEnvelope.Payload.Deserialize<AgentResponse>(FramedJsonStream.SerializerOptions)
                ?? throw new InvalidDataException("The agent response was empty.");
            if (!response.Success) throw new InvalidOperationException(response.Error?.Message ?? "The agent request failed.");
            return response.Result ?? JsonSerializer.SerializeToElement<object?>(null, FramedJsonStream.SerializerOptions);
        }
        catch (IOException)
        {
            _control?.Dispose();
            _control = null;
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async IAsyncEnumerable<EngineEvent> Events([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var pipe = await ConnectAsync(_eventPipeName, _expectedServerProcessId, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(pipe, cancellationToken).ConfigureAwait(false);
            if (envelope is null) yield break;
            ValidateEnvelope(envelope, "event");
            yield return envelope.Payload.Deserialize<EngineEvent>(FramedJsonStream.SerializerOptions)
                ?? throw new InvalidDataException("The agent event was empty.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _control?.Dispose();
        _requestGate.Dispose();
        return ValueTask.CompletedTask;
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
