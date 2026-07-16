using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonProfileStore : IProfileStore
{
    private readonly AtomicJsonStore<List<ConnectionProfile>> _store;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonProfileStore(string path) => _store = new(path);

    public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ConnectionProfile> profiles = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
            foreach (ConnectionProfile profile in profiles) ProfileValidator.ThrowIfInvalid(profile);
            return profiles.OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        ProfileValidator.ThrowIfInvalid(profile);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ConnectionProfile> profiles = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
            int index = profiles.FindIndex(item => item.Id == profile.Id);
            if (index < 0) profiles.Add(profile); else profiles[index] = profile;
            await _store.WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<ConnectionProfile> profiles = await _store.ReadAsync(cancellationToken).ConfigureAwait(false) ?? [];
            if (profiles.RemoveAll(item => item.Id == profileId) > 0)
                await _store.WriteAsync(profiles, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
