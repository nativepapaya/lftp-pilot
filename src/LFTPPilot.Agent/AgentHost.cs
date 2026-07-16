using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

public sealed record JobTransitionRequest(Guid JobId, JobState State, string? Status = null, EngineError? Error = null);
public sealed record JobCancelRequest(Guid JobId, string? Reason = null);

public sealed partial class AgentHost : IAsyncDisposable
{
    private readonly JobCoordinator _coordinator;
    private readonly DurableJobStore _store;
    private readonly RunOnceScheduler _scheduler;
    private readonly AgentWorkspaceService? _workspace;
    private readonly AgentEventHub _events = new();
    private readonly Func<int, bool>? _clientAuthorizer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _clients = [];
    private readonly object _clientGate = new();
    private bool _started;
    private bool _disposed;

    public AgentHost(
        string statePath,
        TimeProvider? timeProvider = null,
        IProfileStore? profileStore = null,
        ISecretStore? secretStore = null,
        ILftpProcessHost? processHost = null,
        ILftpRuntimeProvider? runtimeProvider = null,
        IMirrorPlanner? mirrorPlanner = null,
        AgentWorkspaceOptions? workspaceOptions = null,
        Func<int, bool>? clientAuthorizer = null)
    {
        _coordinator = new();
        _store = new(statePath);
        _scheduler = new(_coordinator, _store, timeProvider);
        _coordinator.JobChanged += OnJobChanged;
        _clientAuthorizer = clientAuthorizer;
        var services = new object?[] { profileStore, secretStore, processHost, runtimeProvider, mirrorPlanner, workspaceOptions };
        if (services.Any(static service => service is not null) && services.Any(static service => service is null))
            throw new ArgumentException("All workspace services must be supplied together.");
        if (profileStore is not null)
        {
            _workspace = new(profileStore, secretStore!, processHost!, runtimeProvider!, _coordinator, mirrorPlanner!, workspaceOptions!,
                (kind, name, payload, jobId, sessionId) => _events.Publish(kind, name, payload, jobId, sessionId),
                _scheduler);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The agent host is already running.");
        _started = true;
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var restoredJobs = NormalizeInterruptedJobs(state.Jobs);
        _coordinator.Restore(restoredJobs);
        await _scheduler.RestoreAsync(restoredJobs, cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await Task.WhenAll(AcceptControlClientsAsync(linked.Token), AcceptEventClientsAsync(linked.Token)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_started)
        {
            try { await _scheduler.MarkPendingMissedAsync("The agent was explicitly stopped.").ConfigureAwait(false); }
            catch (IOException) { }
        }
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        if (_workspace is not null) await _workspace.DisposeAsync().ConfigureAwait(false);
        await _store.SaveAsync(_coordinator.GetJobs()).ConfigureAwait(false);
        Task[] clients;
        lock (_clientGate) clients = _clients.ToArray();
        try { await Task.WhenAll(clients).ConfigureAwait(false); } catch (IOException) { } catch (OperationCanceledException) { }
        _coordinator.JobChanged -= OnJobChanged;
        _lifetime.Dispose();
    }

    private async Task AcceptControlClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = CreatePipe(AgentProtocol.ControlPipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!TryAuthenticateClient(pipe, out var processId)) { pipe.Dispose(); continue; }
                TrackClient(HandleControlClientAsync(pipe, processId, cancellationToken));
            }
            catch
            {
                pipe.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
                throw;
            }
        }
    }

    private async Task AcceptEventClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = CreatePipe(AgentProtocol.EventPipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (!TryAuthenticateClient(pipe, out _)) { pipe.Dispose(); continue; }
                TrackClient(HandleEventClientAsync(pipe, cancellationToken));
            }
            catch
            {
                pipe.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
                throw;
            }
        }
    }

    private async Task HandleControlClientAsync(NamedPipeServerStream pipe, int clientProcessId, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var envelope = await FramedJsonStream.ReadAsync<ProtocolEnvelope>(pipe, cancellationToken).ConfigureAwait(false);
                if (envelope is null) return;
                var dispatch = await DispatchAsync(envelope, clientProcessId, cancellationToken).ConfigureAwait(false);
                await FramedJsonStream.WriteAsync(pipe, dispatch.Response, cancellationToken).ConfigureAwait(false);
                if (dispatch.StopAfterReply)
                {
                    _lifetime.Cancel();
                    return;
                }
            }
        }
    }

    private async Task<DispatchResult> DispatchAsync(ProtocolEnvelope envelope, int clientProcessId, CancellationToken cancellationToken)
    {
        AgentResponse response;
        var stopAfterReply = false;
        try
        {
            if (envelope.Version != AgentProtocol.CurrentVersion) throw new InvalidDataException($"Unsupported protocol version {envelope.Version}.");
            if (!string.Equals(envelope.Kind, "request", StringComparison.Ordinal)) throw new InvalidDataException("Expected a request envelope.");
            var request = envelope.Payload.Deserialize<AgentRequest>(FramedJsonStream.SerializerOptions)
                ?? throw new InvalidDataException("The agent request was empty.");
            var result = await HandleRequestAsync(request, clientProcessId, cancellationToken).ConfigureAwait(false);
            response = new(true, result);
            stopAfterReply = string.Equals(request.Method, AgentProtocol.StopMethod, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException or JsonException or IOException or UnauthorizedAccessException or TimeoutException or NotSupportedException or CryptographicException)
        {
            response = new(false, Error: new(exception.GetType().Name, exception.Message));
        }
        return new(
            new(AgentProtocol.CurrentVersion, "response", envelope.CorrelationId, JsonSerializer.SerializeToElement(response, FramedJsonStream.SerializerOptions)),
            stopAfterReply);
    }

    private async Task<JsonElement> HandleRequestAsync(AgentRequest request, int clientProcessId, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "ping":
                return JsonSerializer.SerializeToElement(new { protocolVersion = AgentProtocol.CurrentVersion, processId = Environment.ProcessId, clientProcessId }, FramedJsonStream.SerializerOptions);
            case AgentProtocol.StopMethod:
                return JsonSerializer.SerializeToElement(new { stopping = true }, FramedJsonStream.SerializerOptions);
            case "jobs.list":
                return JsonSerializer.SerializeToElement(_coordinator.GetJobs(), FramedJsonStream.SerializerOptions);
            case "jobs.enqueue":
                {
                    if (_workspace is not null)
                        throw new NotSupportedException("Direct job creation is disabled when workspace services are active. Use a typed workspace operation instead.");
                    var job = request.Arguments.Deserialize<JobSnapshot>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException("A job is required.");
                    if (job.State == JobState.Scheduled)
                        throw new NotSupportedException("Run-once work requires an executable transfer payload through transfers.enqueue.");
                    ValidateNewJob(job);
                    var result = _coordinator.Enqueue(job);
                    await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(result, FramedJsonStream.SerializerOptions);
                }
            case "jobs.transition":
                {
                    if (_workspace is not null)
                        throw new NotSupportedException("Direct job mutation is disabled when workspace services are active. Use a typed workspace operation instead.");
                    var transition = request.Arguments.Deserialize<JobTransitionRequest>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException("A transition is required.");
                    var result = _coordinator.Transition(transition.JobId, transition.State, transition.Status, transition.Error);
                    await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(result, FramedJsonStream.SerializerOptions);
                }
            case "jobs.cancel":
                {
                    var cancel = request.Arguments.Deserialize<JobCancelRequest>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException("A cancellation request is required.");
                    var cancelled = _workspace?.TryCancelOperation(cancel.JobId, cancel.Reason) == true || _coordinator.TryCancel(cancel.JobId, cancel.Reason);
                    await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(new { cancelled }, FramedJsonStream.SerializerOptions);
                }
            default:
                if (_workspace is null) throw new InvalidOperationException("Workspace services are not configured for this Agent host.");
                return await _workspace.HandleAsync(request.Method, request.Arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleEventClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        await using (var subscription = _events.Subscribe())
        {
            await foreach (var engineEvent in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var envelope = new ProtocolEnvelope(AgentProtocol.CurrentVersion, "event", Guid.Empty, JsonSerializer.SerializeToElement(engineEvent, FramedJsonStream.SerializerOptions));
                await FramedJsonStream.WriteAsync(pipe, envelope, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void OnJobChanged(object? sender, JobSnapshot job)
    {
        _events.Publish(EngineEventKind.Job, "job.changed", job, job.Id);
        _ = PersistJobsAsync();
    }

    private async Task PersistJobsAsync()
    {
        try { await _store.SaveAsync(_coordinator.GetJobs(), _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private void TrackClient(Task task)
    {
        task = ObserveClientAsync(task);
        lock (_clientGate) _clients.Add(task);
        _ = task.ContinueWith(
            completed => { lock (_clientGate) _clients.Remove(completed); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveClientAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
        catch (InvalidDataException) { }
    }

    private static NamedPipeServerStream CreatePipe(string name) => new(
        name,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 4096,
        outBufferSize: 4096);

    private bool TryAuthenticateClient(NamedPipeServerStream pipe, out int processId)
    {
        processId = 0;
        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var rawProcessId) || rawProcessId is 0 or > int.MaxValue) return false;
        // PipeOptions.CurrentUserOnly applies a SID-restricted ACL and rejects
        // clients from every other Windows identity before this point. The
        // kernel-reported PID then gives us a live, attributable peer rather
        // than trusting a process identifier supplied in JSON.
        try
        {
            using var process = Process.GetProcessById((int)rawProcessId);
            _ = process.Handle;
        }
        catch (ArgumentException) { return false; }
        processId = (int)rawProcessId;
        if (_clientAuthorizer is not null)
        {
            try { if (!_clientAuthorizer(processId)) return false; }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return false; }
        }
        return true;
    }

    private void ValidateNewJob(JobSnapshot job)
    {
        if (string.IsNullOrWhiteSpace(job.DisplayName) || job.DisplayName.Length > 256) throw new ArgumentException("The job display name must contain between 1 and 256 characters.");
        if (!Enum.IsDefined(job.Kind) || !Enum.IsDefined(job.State)) throw new ArgumentException("The job kind or state is unsupported.");
        if (job.State == JobState.Scheduled && (job.RunAt is null || job.RunAt <= _scheduler.UtcNow))
            throw new ArgumentException("A scheduled job requires a future run time.");
        if (job.State == JobState.Queued && job.RunAt is not null)
            throw new ArgumentException("A queued job cannot have a run-once time.");
    }

    private static IReadOnlyList<JobSnapshot> NormalizeInterruptedJobs(IEnumerable<JobSnapshot> jobs)
    {
        var now = DateTimeOffset.UtcNow;
        return jobs.Select(job => job.State is JobState.Queued or JobState.Running or JobState.Paused
            ? job with
            {
                State = JobState.Failed,
                Status = "Interrupted because the previous Agent process stopped.",
                Error = new("agent-interrupted", "The operation did not complete before the Agent stopped.", IsTransient: true),
                UpdatedAt = now,
            }
            : job).ToArray();
    }

    private sealed record DispatchResult(ProtocolEnvelope Response, bool StopAfterReply);

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }
}
