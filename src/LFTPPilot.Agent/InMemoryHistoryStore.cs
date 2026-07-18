using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class InMemoryHistoryStore : IHistoryStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, HistoryRecord> _records = [];

    public Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > HistoryRecordPolicy.RetentionLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HistoryRecord>>(
                _records.Values.OrderByDescending(static record => record.FinishedAt).Take(maximumCount).ToArray());
        }
    }

    public Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HistoryRecordPolicy.Validate(record);
        lock (_gate)
        {
            _records[record.Id] = record;
            if (_records.Count > HistoryRecordPolicy.RetentionLimit)
            {
                foreach (var id in _records.Values
                    .OrderByDescending(static item => item.FinishedAt)
                    .Skip(HistoryRecordPolicy.RetentionLimit)
                    .Select(static item => item.Id)
                    .ToArray())
                {
                    _records.Remove(id);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) _records.Clear();
        return Task.CompletedTask;
    }
}
