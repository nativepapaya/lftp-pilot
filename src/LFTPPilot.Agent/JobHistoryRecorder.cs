using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class JobHistoryRecorder
{
    private readonly IHistoryStore _store;
    private readonly Action<HistoryRecord>? _recorded;
    private readonly Action<Exception>? _failed;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _startedAt = [];
    private Task _pending = Task.CompletedTask;

    public JobHistoryRecorder(
        IHistoryStore store,
        Action<HistoryRecord>? recorded = null,
        Action<Exception>? failed = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recorded = recorded;
        _failed = failed;
    }

    public void Observe(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        JobSnapshotPolicy.Validate(job);
        HistoryRecord? record = null;
        lock (_gate)
        {
            if (job.State == JobState.Running)
                _startedAt.TryAdd(job.Id, job.UpdatedAt);
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Missed)
            {
                var startedAt = _startedAt.Remove(job.Id, out var observedStart) ? observedStart : job.CreatedAt;
                record = new(
                    job.Id,
                    job.Id,
                    job.Kind,
                    job.DisplayName,
                    job.State,
                    startedAt,
                    job.UpdatedAt,
                    Detail: job.Error?.Message ?? job.Status);
                HistoryRecordPolicy.Validate(record);
                _pending = AppendAfterAsync(_pending, record);
            }
        }
    }

    public Task FlushAsync()
    {
        lock (_gate) return _pending;
    }

    private async Task AppendAfterAsync(Task previous, HistoryRecord record)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await _store.AppendAsync(record).ConfigureAwait(false);
            _recorded?.Invoke(record);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            _failed?.Invoke(exception);
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;
}
