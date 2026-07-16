using System.Collections.Concurrent;
using System.Diagnostics;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

internal sealed class NativeTransferQueue : IAsyncDisposable
{
    private static readonly TimeSpan ProcessHealthInterval = TimeSpan.FromMilliseconds(250);
    private readonly ILftpSession _session;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _transferTimeout;
    private readonly bool _isolatedSettings;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private readonly CancellationTokenSource _availability = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, PendingTransfer> _pending = [];
    private readonly ConcurrentDictionary<string, CompletionMarker> _markers = new(StringComparer.Ordinal);
    private int _retired;
    private int _disposed;
    private int _activeExecutions;

    private NativeTransferQueue(
        ILftpSession session,
        int parallelism,
        TimeSpan commandTimeout,
        TimeSpan transferTimeout,
        bool isolatedSettings)
    {
        _session = session;
        _commandTimeout = commandTimeout;
        _transferTimeout = transferTimeout;
        _isolatedSettings = isolatedSettings;
        _slots = new(parallelism, parallelism);
        _session.OutputReceived += OnOutputReceived;
    }

    public bool IsAvailable => Volatile.Read(ref _retired) == 0 && Volatile.Read(ref _disposed) == 0 && _session.IsRunning;

    public static async Task<NativeTransferQueue> CreateAsync(
        ILftpSession session,
        int parallelism,
        TimeSpan commandTimeout,
        TimeSpan transferTimeout,
        bool isolatedSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (parallelism is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(parallelism));
        if (commandTimeout <= TimeSpan.Zero || transferTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), "Queue timeouts must be positive.");

        var queue = new NativeTransferQueue(session, parallelism, commandTimeout, transferTimeout, isolatedSettings);
        try
        {
            var configured = await session.ExecuteAsync(
                $"set cmd:queue-parallel {parallelism}; queue; queue start",
                commandTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(configured, "LFTP transfer queue initialization");
            return queue;
        }
        catch
        {
            await queue.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ExecuteAsync(
        TransferPlan plan,
        Func<ILftpSession, CancellationToken, Task> preSubmit,
        CancellationToken cancellationToken)
    {
        EnterExecution();
        try
        {
            if (!IsAvailable) throw new NativeTransferQueueRetiredException("The per-profile LFTP transfer queue is not available.");
            if (!_isolatedSettings && (plan.RateLimitBytesPerSecond is not null ||
                plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip))
                throw new InvalidOperationException("Setting-sensitive transfers require an isolated LFTP queue process.");

            using var available = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _availability.Token);
            await WaitForGateAsync(_slots, available.Token, cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForGateAsync(_submissionGate, available.Token, cancellationToken).ConfigureAwait(false);
                PendingTransfer pending;
                try
                {
                    if (!IsAvailable)
                        throw new NativeTransferQueueRetiredException("The per-profile LFTP transfer queue is not available.");
                    await preSubmit(_session, cancellationToken).ConfigureAwait(false);
                    if (!IsAvailable)
                        throw new NativeTransferQueueRetiredException("The per-profile LFTP transfer queue was retired during validation.");

                    var id = Guid.NewGuid();
                    var token = id.ToString("N");
                    pending = new(
                        id,
                        $"__LFTPPILOT_QUEUE_ALIAS_{token}",
                        $"__LFTPPILOT_QUEUE_{token}_OK",
                        $"__LFTPPILOT_QUEUE_{token}_FAILED",
                        $"__LFTPPILOT_QUEUE_{token}_SUBMIT_OK",
                        $"__LFTPPILOT_QUEUE_{token}_SUBMIT_FAILED");
                    Register(pending);

                    try
                    {
                        var command = LftpCommandBuilder.BuildQueuedTransfer(
                            plan,
                            pending.AliasName,
                            pending.SuccessMarker,
                            pending.FailureMarker,
                            pending.SubmissionSuccessMarker,
                            pending.SubmissionFailureMarker);
                        var queued = await _session.ExecuteAsync(command, _commandTimeout, cancellationToken).ConfigureAwait(false);
                        ValidateSubmission(queued, pending);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await RetireAsync(new NativeTransferQueueRetiredException(
                            "A queued transfer was cancelled, so its shared per-profile LFTP queue was retired safely.")).ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception exception) when (!IsFatalRuntimeException(exception))
                    {
                        Cleanup(pending);
                        await RetireAsync(new NativeTransferQueueRetiredException(
                            "The LFTP queue could not accept a transfer and was retired to avoid uncertain state.", exception)).ConfigureAwait(false);
                        throw;
                    }
                }
                finally
                {
                    _submissionGate.Release();
                }

                try
                {
                    var succeeded = await WaitForCompletionAsync(pending.Completion.Task, cancellationToken).ConfigureAwait(false);
                    if (!succeeded) throw new InvalidOperationException("LFTP reported that the queued transfer failed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await RetireAsync(new NativeTransferQueueRetiredException(
                        "A queued transfer was cancelled, so its shared per-profile LFTP queue was retired safely.")).ConfigureAwait(false);
                    throw;
                }
                catch (TimeoutException)
                {
                    await RetireAsync(new NativeTransferQueueRetiredException(
                        "A queued transfer timed out, so its shared per-profile LFTP queue was retired safely.")).ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    Cleanup(pending);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            ExitExecution();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await RetireAsync(new NativeTransferQueueRetiredException("The per-profile LFTP transfer queue was closed.")).ConfigureAwait(false);
        if (Volatile.Read(ref _activeExecutions) == 0) _drained.TrySetResult();
        await _drained.Task.ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
        _availability.Dispose();
        _submissionGate.Dispose();
        _slots.Dispose();
    }

    private void EnterExecution()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Increment(ref _activeExecutions);
        if (Volatile.Read(ref _disposed) == 0) return;
        ExitExecution();
        throw new ObjectDisposedException(nameof(NativeTransferQueue));
    }

    private void ExitExecution()
    {
        if (Interlocked.Decrement(ref _activeExecutions) == 0 && Volatile.Read(ref _disposed) != 0)
            _drained.TrySetResult();
    }

    private async Task<bool> WaitForCompletionAsync(Task<bool> completion, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = _transferTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("The queued transfer did not finish before its timeout.");
            var delay = Task.Delay(remaining < ProcessHealthInterval ? remaining : ProcessHealthInterval, cancellationToken);
            if (await Task.WhenAny(completion, delay).ConfigureAwait(false) == completion)
                return await completion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_session.IsRunning)
                throw new NativeTransferQueueRetiredException("The LFTP process exited before the queued transfer reported completion.");
        }
    }

    private static async Task WaitForGateAsync(
        SemaphoreSlim gate,
        CancellationToken availabilityToken,
        CancellationToken callerToken)
    {
        try
        {
            await gate.WaitAsync(availabilityToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new NativeTransferQueueRetiredException("The per-profile LFTP transfer queue was retired while this transfer was waiting for a slot.");
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private void Register(PendingTransfer pending)
    {
        if (!_pending.TryAdd(pending.Id, pending) ||
            !_markers.TryAdd(pending.SuccessMarker, new(pending, true)) ||
            !_markers.TryAdd(pending.FailureMarker, new(pending, false)))
        {
            Cleanup(pending);
            throw new InvalidOperationException("Could not allocate a unique LFTP queue completion marker.");
        }
    }

    private static void ValidateSubmission(LftpCommandResult result, PendingTransfer pending)
    {
        if (result.TimedOut) throw new TimeoutException("LFTP transfer queue submission timed out.");
        if (result.Failure is not null) throw new InvalidOperationException($"LFTP transfer queue submission failed: {result.Failure}");
        if (result.Truncated) throw new InvalidDataException("LFTP transfer queue submission produced more output than can be processed safely.");

        var succeeded = result.Lines.Any(line =>
            string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(line.Line, pending.SubmissionSuccessMarker, StringComparison.Ordinal));
        var failed = result.Lines.Any(line =>
            string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(line.Line, pending.SubmissionFailureMarker, StringComparison.Ordinal));
        if (succeeded == failed)
            throw new InvalidDataException("LFTP did not report one unambiguous queue-submission result.");
        if (failed) throw new InvalidOperationException("LFTP rejected the transfer queue submission.");
    }

    private void OnOutputReceived(object? sender, LftpOutputLine output)
    {
        if (!string.Equals(output.Stream, "stdout", StringComparison.OrdinalIgnoreCase) ||
            !_markers.TryGetValue(output.Line, out var marker)) return;
        if (marker.Pending.Completion.TrySetResult(marker.Succeeded)) Cleanup(marker.Pending);
    }

    private void Cleanup(PendingTransfer pending)
    {
        _pending.TryRemove(pending.Id, out _);
        _markers.TryRemove(pending.SuccessMarker, out _);
        _markers.TryRemove(pending.FailureMarker, out _);
    }

    private async Task RetireAsync(Exception reason)
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        _availability.Cancel();
        _session.OutputReceived -= OnOutputReceived;
        foreach (var pending in _pending.Values) pending.Completion.TrySetException(reason);
        _pending.Clear();
        _markers.Clear();
        try { await _session.StopAsync(force: true).ConfigureAwait(false); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private sealed record CompletionMarker(PendingTransfer Pending, bool Succeeded);

    private sealed record PendingTransfer(
        Guid Id,
        string AliasName,
        string SuccessMarker,
        string FailureMarker,
        string SubmissionSuccessMarker,
        string SubmissionFailureMarker)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class NativeTransferQueueRetiredException : IOException
{
    public NativeTransferQueueRetiredException(string message) : base(message) { }
    public NativeTransferQueueRetiredException(string message, Exception innerException) : base(message, innerException) { }
}
