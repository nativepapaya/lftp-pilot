using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonAppPreferencesStore : IAppPreferencesStore
{
    private const long MaximumSerializedBytes = 64 * 1024;
    private readonly string _path;
    private readonly AtomicJsonStore<AppPreferences> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAppPreferencesStore(string path)
    {
        _path = Path.GetFullPath(path);
        _store = new(_path);
    }

    public async Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > MaximumSerializedBytes)
                throw new InvalidDataException("The UI preferences store exceeds its size limit.");
            AppPreferences preferences;
            try
            {
                preferences = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? new();
                AppPreferencesPolicy.Validate(preferences);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
            {
                throw new InvalidDataException("The UI preferences store contains invalid data.", exception);
            }
            return preferences;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        AppPreferencesPolicy.Validate(preferences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _store.WriteAsync(preferences, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
