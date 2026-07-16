using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonHistoryStore : IHistoryStore
{
    private const int RetentionLimit = 2_000;
    private readonly AtomicJsonStore<List<HistoryRecord>> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHistoryStore(string path) => _store = new(path);

    public async Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > RetentionLimit) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<HistoryRecord> records = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
            return records.OrderByDescending(static record => record.FinishedAt).Take(maximumCount).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == Guid.Empty || record.JobId == Guid.Empty || string.IsNullOrWhiteSpace(record.DisplayName) ||
            record.DisplayName.Length > 256 || record.FinishedAt < record.StartedAt)
            throw new ArgumentException("The history record is invalid.", nameof(record));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<HistoryRecord> records = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
            records.RemoveAll(item => item.Id == record.Id);
            records.Add(record);
            records = records.OrderByDescending(static item => item.FinishedAt).Take(RetentionLimit).ToList();
            await _store.WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _store.WriteAsync(new List<HistoryRecord>(), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
