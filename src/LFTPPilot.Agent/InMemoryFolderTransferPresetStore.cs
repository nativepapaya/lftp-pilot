using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class InMemoryFolderTransferPresetStore : IFolderTransferPresetStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, FolderTransferPreset> _presets = [];

    public Task<IReadOnlyList<FolderTransferPreset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FolderTransferPreset>>(
                _presets.Values.OrderBy(static preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
        }
    }

    public Task SaveAsync(FolderTransferPreset preset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlanValidator.Validate(preset);
        lock (_gate)
        {
            if (_presets.Values.Any(item => item.Id != preset.Id &&
                string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A folder-transfer preset with this name already exists.");
            if (!_presets.ContainsKey(preset.Id) && _presets.Count >= FolderTransferPolicy.MaximumPresets)
                throw new InvalidDataException("The folder-transfer preset limit has been reached.");
            _presets[preset.Id] = preset;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (presetId == Guid.Empty)
            throw new ArgumentException("The folder-transfer preset identifier cannot be empty.", nameof(presetId));
        lock (_gate) _presets.Remove(presetId);
        return Task.CompletedTask;
    }
}
