using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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
    private readonly JobHistoryRecorder? _history;
    private readonly AgentEventHub _events = new();
    private readonly Func<int, bool>? _clientAuthorizer;
    private readonly Action<JobSnapshot>? _jobObserver;
    private readonly AgentIdleExitPolicy _idleExit;
    private readonly Func<Stream, ProtocolEnvelope, CancellationToken, ValueTask> _writeControlResponse =
        static (stream, response, cancellationToken) => FramedJsonStream.WriteAsync(stream, response, cancellationToken);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _clients = [];
    private readonly object _clientGate = new();
    private readonly TaskCompletionSource _acceptorsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _jobStateOwned;
    private bool _started;
    private bool _disposed;

    public AgentHost(
        string statePath,
        TimeProvider? timeProvider = null,
        IProfileStore? profileStore = null,
        ISecretStore? secretStore = null,
        SftpHostKeyManager? hostKeyManager = null,
        ILftpProcessHost? processHost = null,
        ILftpRuntimeProvider? runtimeProvider = null,
        IMirrorPlanner? mirrorPlanner = null,
        AgentWorkspaceOptions? workspaceOptions = null,
        Func<int, bool>? clientAuthorizer = null,
        IMirrorDefinitionStore? mirrorDefinitionStore = null,
        IHistoryStore? historyStore = null,
        Action<JobSnapshot>? jobObserver = null)
    {
        _idleExit = new(HasBackgroundWork, timeProvider);
        _coordinator = new();
        _store = new(statePath);
        _scheduler = new(_coordinator, _store, timeProvider);
        _coordinator.JobChanged += OnJobChanged;
        _clientAuthorizer = clientAuthorizer;
        _jobObserver = jobObserver;
        var services = new object?[] { profileStore, secretStore, hostKeyManager, processHost, runtimeProvider, mirrorPlanner, workspaceOptions };
        if (services.Any(static service => service is not null) && services.Any(static service => service is null))
            throw new ArgumentException("All workspace services must be supplied together.");
        if (profileStore is not null)
        {
            var effectiveHistoryStore = historyStore ?? new InMemoryHistoryStore();
            _history = new(
                effectiveHistoryStore,
                record => _events.Publish(EngineEventKind.Job, "history.appended", record, record.JobId),
                PublishHistoryPersistenceFailure);
            _workspace = new(profileStore, secretStore!, hostKeyManager!, processHost!, runtimeProvider!, _coordinator, mirrorPlanner!, workspaceOptions!,
                (kind, name, payload, jobId, sessionId) =>
                {
                    _events.Publish(kind, name, payload, jobId, sessionId);
                    if (kind == EngineEventKind.RemoteEdit) _idleExit.BackgroundWorkChanged();
                },
                _scheduler,
                _store,
                mirrorDefinitionStore,
                effectiveHistoryStore);
        }
    }

    internal AgentHost(
        string statePath,
        Func<Stream, ProtocolEnvelope, CancellationToken, ValueTask> controlResponseWriter)
        : this(statePath)
    {
        _writeControlResponse = controlResponseWriter ?? throw new ArgumentNullException(nameof(controlResponseWriter));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("The agent host is already running.");
        _started = true;
        try
        {
            var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var restoredJobs = NormalizeInterruptedJobs(state.Jobs);
            _coordinator.Restore(restoredJobs);
            await _scheduler.RestoreAsync(restoredJobs, cancellationToken).ConfigureAwait(false);
            if (_history is not null)
            {
                foreach (var job in _coordinator.GetJobs()) _history.Observe(job);
                await _history.FlushAsync().ConfigureAwait(false);
            }
            // From this point onward the coordinator and scheduler have both
            // restored and durably committed their view of the job collection.
            // Disposal is now allowed to finalize that state.
            Volatile.Write(ref _jobStateOwned, true);
            if (_workspace is not null)
                await _workspace.RestoreSessionTabsAsync(state.EffectiveSessionTabs, cancellationToken).ConfigureAwait(false);

            _idleExit.AgentReady();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await Task.WhenAll(AcceptControlClientsAsync(linked.Token), AcceptEventClientsAsync(linked.Token)).ConfigureAwait(false);
        }
        finally
        {
            _acceptorsStopped.TrySetResult();
        }
    }

    internal Task WaitForIdleExitAsync(TimeSpan idleDelay, CancellationToken cancellationToken = default) =>
        _idleExit.WaitForIdleExitAsync(idleDelay, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        var failures = new List<Exception>();
        if (_started) await _acceptorsStopped.Task.ConfigureAwait(false);
        Task[] clients;
        lock (_clientGate) clients = _clients.ToArray();
        try { await Task.WhenAll(clients).ConfigureAwait(false); }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            if (exception is not OperationCanceledException) failures.Add(exception);
        }
        if (Volatile.Read(ref _jobStateOwned))
        {
            try { await _scheduler.MarkPendingMissedAsync("The agent was explicitly stopped.").ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                PublishPersistenceFailure(exception);
                failures.Add(exception);
            }
        }
        if (_history is not null)
        {
            try { await _history.FlushAsync().ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatalRuntimeException(exception)) { failures.Add(exception); }
        }
        try { await _scheduler.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (!IsFatalRuntimeException(exception)) { failures.Add(exception); }
        if (_workspace is not null)
        {
            try { await _workspace.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatalRuntimeException(exception)) { failures.Add(exception); }
        }
        if (Volatile.Read(ref _jobStateOwned))
        {
            try { await _store.SaveAsync(_coordinator.GetJobs).ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                PublishPersistenceFailure(exception);
                failures.Add(exception);
            }
        }
        _coordinator.JobChanged -= OnJobChanged;
        _lifetime.Dispose();
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("The Agent encountered multiple shutdown failures after completing cleanup.", failures);
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
                TrackClient(HandleControlClientAsync(pipe, processId, cancellationToken), countsAsAppConnection: true);
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
                // The event stream writes only when an event is published, so a
                // disconnected reader can remain undetected while the Agent is
                // quiet. The control stream continuously waits for its next frame
                // and is the authoritative App-lifetime signal for idle exit.
                TrackClient(HandleEventClientAsync(pipe, cancellationToken), countsAsAppConnection: false);
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
                if (dispatch.StopAfterReply)
                {
                    try
                    {
                        await _writeControlResponse(pipe, dispatch.Response, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Stop is committed by dispatch, not by successful delivery of its reply.
                        // A broken client pipe must not leave the Agent or its LFTP children alive.
                        _lifetime.Cancel();
                    }
                    return;
                }
                await _writeControlResponse(pipe, dispatch.Response, cancellationToken).ConfigureAwait(false);
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
                    await _store.SaveAsync(_coordinator.GetJobs, cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(result, FramedJsonStream.SerializerOptions);
                }
            case "jobs.transition":
                {
                    if (_workspace is not null)
                        throw new NotSupportedException("Direct job mutation is disabled when workspace services are active. Use a typed workspace operation instead.");
                    var transition = request.Arguments.Deserialize<JobTransitionRequest>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException("A transition is required.");
                    ValidateJobTransition(transition);
                    var result = _coordinator.Transition(transition.JobId, transition.State, transition.Status, transition.Error);
                    await _store.SaveAsync(_coordinator.GetJobs, cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(result, FramedJsonStream.SerializerOptions);
                }
            case "jobs.cancel":
                {
                    var cancel = request.Arguments.Deserialize<JobCancelRequest>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException("A cancellation request is required.");
                    JobSnapshotPolicy.ValidateStatus(cancel.Reason);
                    var cancelled = _workspace?.TryCancelOperation(cancel.JobId, cancel.Reason) == true || _coordinator.TryCancel(cancel.JobId, cancel.Reason);
                    await _store.SaveAsync(_coordinator.GetJobs, cancellationToken).ConfigureAwait(false);
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
        _history?.Observe(job);
        if (_jobObserver is not null)
        {
            try { _jobObserver(job); }
            catch (Exception exception) when (!IsFatalRuntimeException(exception)) { }
        }
        if (Volatile.Read(ref _jobStateOwned)) _ = PersistJobsAsync();
        _idleExit.BackgroundWorkChanged();
    }

    private async Task PersistJobsAsync()
    {
        try { await _store.SaveAsync(_coordinator.GetJobs, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            PublishPersistenceFailure(exception);
        }
    }

    private void PublishPersistenceFailure(Exception exception)
    {
        _events.Publish(
            EngineEventKind.Error,
            "job.persistence-failed",
            new EngineError(
                "job-persistence-failed",
                "The durable job state could not be saved.",
                Detail: exception.GetType().Name));
    }

    private void PublishHistoryPersistenceFailure(Exception exception)
    {
        _events.Publish(
            EngineEventKind.Error,
            "history.persistence-failed",
            new EngineError(
                "history-persistence-failed",
                "The durable Activity history could not be saved.",
                Detail: exception.GetType().Name));
    }

    private void TrackClient(Task task, bool countsAsAppConnection)
    {
        if (countsAsAppConnection) _idleExit.ClientConnected();
        task = ObserveTrackedClientAsync(task, countsAsAppConnection);
        lock (_clientGate) _clients.Add(task);
        _ = task.ContinueWith(
            completed => { lock (_clientGate) _clients.Remove(completed); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveTrackedClientAsync(Task task, bool countsAsAppConnection)
    {
        try { await ObserveClientAsync(task).ConfigureAwait(false); }
        finally
        {
            if (countsAsAppConnection) _idleExit.ClientDisconnected();
        }
    }

    private async Task ObserveClientAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            _events.Publish(
                EngineEventKind.Error,
                "client.failed",
                new EngineError(
                    "agent-client-failed",
                    "An Agent client connection ended unexpectedly.",
                    Detail: exception.GetType().Name));
        }
    }

    private bool HasBackgroundWork() =>
        _coordinator.GetJobs().Any(static job =>
            job.State is JobState.Scheduled or JobState.Queued or JobState.Running or JobState.Paused) ||
        _workspace?.HasActiveRemoteEdits == true;

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
        JobSnapshotPolicy.ValidateForEnqueue(job, _scheduler.UtcNow);
        if (job.RetryAvailable) throw new ArgumentException("Direct jobs cannot advertise an Agent-owned retry operation.");
        if (job.State == JobState.Scheduled && (job.RunAt is null || job.RunAt <= _scheduler.UtcNow))
            throw new ArgumentException("A scheduled job requires a future run time.");
        if (job.State == JobState.Queued && job.RunAt is not null)
            throw new ArgumentException("A queued job cannot have a run-once time.");
    }

    private void ValidateJobTransition(JobTransitionRequest transition)
    {
        var current = _coordinator.GetJobs().FirstOrDefault(job => job.Id == transition.JobId)
            ?? throw new KeyNotFoundException($"Job {transition.JobId} was not found.");
        var updatedAt = DateTimeOffset.UtcNow;
        if (updatedAt < current.CreatedAt) updatedAt = current.CreatedAt;
        JobSnapshotPolicy.Validate(current with
        {
            State = transition.State,
            Status = transition.Status ?? current.Status,
            Error = transition.Error,
            UpdatedAt = updatedAt,
        });
    }

    private static IReadOnlyList<JobSnapshot> NormalizeInterruptedJobs(IEnumerable<JobSnapshot> jobs)
    {
        var now = DateTimeOffset.UtcNow;
        return jobs.Select(job =>
        {
            var normalized = job with { RetryAvailable = false };
            return normalized.State is JobState.Queued or JobState.Running or JobState.Paused
            ? normalized with
            {
                State = JobState.Failed,
                Status = "Interrupted because the previous Agent process stopped.",
                Error = new("agent-interrupted", "The operation did not complete before the Agent stopped.", IsTransient: true),
                UpdatedAt = now >= normalized.CreatedAt ? now : normalized.CreatedAt,
            }
            : normalized;
        }).ToArray();
    }

    private sealed record DispatchResult(ProtocolEnvelope Response, bool StopAfterReply);

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
    }
}
