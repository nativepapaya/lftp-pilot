using LFTPPilot.Core;

namespace LFTPPilot.Agent;

public sealed class RunOnceScheduler : IAsyncDisposable
{
    private readonly JobCoordinator _coordinator;
    private readonly DurableJobStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, ScheduledRegistration> _scheduled = [];
    private readonly object _gate = new();

    public RunOnceScheduler(JobCoordinator coordinator, DurableJobStore store, TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public async Task RestoreAsync(IEnumerable<JobSnapshot> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (var job in jobs.Where(static job => job.State == JobState.Scheduled))
            _coordinator.Transition(job.Id, JobState.Missed, "The run-once schedule was missed because the agent restarted or was not running continuously.");
        await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
    }

    public void Schedule(JobSnapshot job, Func<CancellationToken, Task> onDue)
    {
        ArgumentNullException.ThrowIfNull(onDue);
        if (job.State != JobState.Scheduled || job.RunAt is null || job.RunAt <= _timeProvider.GetUtcNow())
            throw new ArgumentException("A future scheduled job is required.", nameof(job));
        lock (_gate)
        {
            if (_scheduled.ContainsKey(job.Id)) return;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var registration = new ScheduledRegistration(cancellation);
            _scheduled[job.Id] = registration;
            registration.Task = WaitAndQueueAsync(job, onDue, registration, cancellation.Token);
        }
    }

    public async Task ScheduleAsync(
        JobSnapshot job,
        Func<CancellationToken, Task> onDue,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
            Schedule(job, onDue);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            if (TryMarkUnscheduledJobMissed(job.Id))
            {
                try { await _store.SaveAsync(_coordinator.GetJobs(), CancellationToken.None).ConfigureAwait(false); }
                catch (IOException) { }
            }
            throw;
        }
    }

    public bool TryCancel(Guid jobId, string? reason = null)
    {
        lock (_gate)
        {
            if (!_scheduled.TryGetValue(jobId, out var registration) ||
                !_coordinator.TryCancel(jobId, reason)) return false;
            registration.Cancellation.Cancel();
            return true;
        }
    }

    public bool IsRegistered(Guid jobId)
    {
        lock (_gate) return _scheduled.ContainsKey(jobId);
    }

    public async Task MarkPendingMissedAsync(string reason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var job in _coordinator.GetJobs().Where(static job => job.State == JobState.Scheduled))
            {
                if (_scheduled.TryGetValue(job.Id, out var registration)) registration.Cancellation.Cancel();
                _coordinator.Transition(job.Id, JobState.Missed, reason);
            }
        }
        await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task[] tasks;
        lock (_gate)
        {
            foreach (var registration in _scheduled.Values) registration.Cancellation.Cancel();
            tasks = _scheduled.Values.Select(static registration => registration.Task).ToArray();
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private async Task WaitAndQueueAsync(
        JobSnapshot job,
        Func<CancellationToken, Task> onDue,
        ScheduledRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = job.RunAt!.Value - _timeProvider.GetUtcNow();
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _timeProvider, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _coordinator.Transition(job.Id, JobState.Queued, "Scheduled time reached.");
            }
            await _store.SaveAsync(_coordinator.GetJobs(), cancellationToken).ConfigureAwait(false);
            await onDue(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var state = _coordinator.GetJobs().FirstOrDefault(current => current.Id == job.Id)?.State;
            if (state is JobState.Queued or JobState.Running)
            {
                try
                {
                    _coordinator.Transition(job.Id, JobState.Failed, "Scheduled execution failed.",
                        new("scheduled-execution-failed", exception.Message));
                    await _store.SaveAsync(_coordinator.GetJobs(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException) { }
                catch (IOException) { }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_scheduled.TryGetValue(job.Id, out var current) && ReferenceEquals(current, registration))
                    _scheduled.Remove(job.Id);
            }
            registration.Cancellation.Dispose();
        }
    }

    private bool TryMarkUnscheduledJobMissed(Guid jobId)
    {
        var state = _coordinator.GetJobs().FirstOrDefault(job => job.Id == jobId)?.State;
        if (state != JobState.Scheduled) return false;
        _coordinator.Transition(jobId, JobState.Missed, "The run-once schedule could not be committed before its selected time.");
        return true;
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private sealed class ScheduledRegistration(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
