using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonHistoryStore : IHistoryStore
{
    private const long MaximumSerializedBytes = 16 * 1024 * 1024;
    private readonly string _path;
    private readonly AtomicJsonStore<List<HistoryRecord>> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonHistoryStore(string path)
    {
        _path = Path.GetFullPath(path);
        _store = new(_path);
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > HistoryRecordPolicy.RetentionLimit) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<HistoryRecord> records = await ReadValidatedAsync(cancellationToken).ConfigureAwait(false);
            return records.OrderByDescending(static record => record.FinishedAt).Take(maximumCount).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        HistoryRecordPolicy.Validate(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<HistoryRecord> records = await ReadValidatedAsync(cancellationToken).ConfigureAwait(false);
            records.RemoveAll(item => item.Id == record.Id);
            records.Add(record);
            records = records.OrderByDescending(static item => item.FinishedAt).Take(HistoryRecordPolicy.RetentionLimit).ToList();
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

    private async Task<List<HistoryRecord>> ReadValidatedAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_path) && new FileInfo(_path).Length > MaximumSerializedBytes)
            throw new InvalidDataException("The history store exceeds its size limit.");
        List<HistoryRecord> records = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
        if (records.Count > HistoryRecordPolicy.RetentionLimit ||
            records.Select(static record => record.Id).Distinct().Count() != records.Count)
        {
            throw new InvalidDataException("The history store contains too many or duplicate records.");
        }
        try
        {
            foreach (var record in records) HistoryRecordPolicy.Validate(record);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The history store contains an invalid record.", exception);
        }
        return records;
    }
}
