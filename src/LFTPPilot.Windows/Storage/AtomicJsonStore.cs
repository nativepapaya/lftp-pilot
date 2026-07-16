using System.Text.Json;

namespace LFTPPilot.Windows.Storage;

public sealed class AtomicJsonStore<T>
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AtomicJsonStore(string path, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    }

    public async Task<T?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return default;
            await using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            await AtomicFile.WriteBytesAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
