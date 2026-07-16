using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class InMemoryMirrorDefinitionStore : IMirrorDefinitionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, MirrorDefinition> _definitions = [];

    public Task<IReadOnlyList<MirrorDefinition>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MirrorDefinition>>(
                _definitions.Values.OrderBy(static definition => definition.Id).ToArray());
        }
    }

    public Task SaveAsync(
        MirrorDefinition definition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlanValidator.Validate(definition);
        lock (_gate)
        {
            if (!_definitions.ContainsKey(definition.Id) &&
                _definitions.Count >= MirrorDefinitionPolicy.MaximumDefinitions)
            {
                throw new InvalidDataException("The in-memory mirror-definition store has reached its record limit.");
            }
            _definitions[definition.Id] = definition;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (definitionId == Guid.Empty)
            throw new ArgumentException("The mirror identifier cannot be empty.", nameof(definitionId));
        lock (_gate) _definitions.Remove(definitionId);
        return Task.CompletedTask;
    }
}
