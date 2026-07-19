using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class JobHistoryRecorder
{
    private readonly IHistoryStore _store;
    private readonly Action<HistoryRecord>? _recorded;
    private readonly Action<Exception>? _failed;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _startedAt = [];
    private readonly Dictionary<Guid, List<HistoryLogEntry>> _logs = [];
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
            AppendLogEntry(job);
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Missed)
            {
                var startedAt = _startedAt.Remove(job.Id, out var observedStart) ? observedStart : job.CreatedAt;
                var log = _logs.Remove(job.Id, out var observedLog) ? observedLog : [];
                for (var index = 0; index < log.Count; index++)
                {
                    if (log[index].Timestamp < startedAt)
                        log[index] = log[index] with { Timestamp = startedAt };
                }
                record = new(
                    job.Id,
                    job.Id,
                    job.Kind,
                    job.DisplayName,
                    job.State,
                    startedAt,
                    job.UpdatedAt,
                    Detail: job.Error?.Message ?? job.Status,
                    Log: [.. log]);
                HistoryRecordPolicy.Validate(record);
                _pending = AppendAfterAsync(_pending, record);
            }
        }
    }

    private void AppendLogEntry(JobSnapshot job)
    {
        var message = job.Error?.Message ?? job.Status ?? job.State.ToString();
        if (message.Length > HistoryRecordPolicy.MaximumLogMessageLength)
            message = message[..HistoryRecordPolicy.MaximumLogMessageLength];
        var level = job.State switch
        {
            JobState.Failed => "Error",
            JobState.Cancelled or JobState.Missed => "Warning",
            _ => "Info",
        };
        var entry = new HistoryLogEntry(job.UpdatedAt, level, $"{job.State}: {message}");
        if (entry.Message.Length > HistoryRecordPolicy.MaximumLogMessageLength)
            entry = entry with { Message = entry.Message[..HistoryRecordPolicy.MaximumLogMessageLength] };

        if (!_logs.TryGetValue(job.Id, out var entries))
            _logs[job.Id] = entries = [];
        if (entries.Count > 0 && entries[^1].Level == entry.Level && entries[^1].Message == entry.Message)
            return;
        entries.Add(entry);
        if (entries.Count > HistoryRecordPolicy.MaximumLogEntries)
            entries.RemoveAt(entries.Count > 1 ? 1 : 0);
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
