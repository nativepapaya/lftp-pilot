using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

public sealed record AgentState(int Version, DateTimeOffset SavedAt, IReadOnlyList<JobSnapshot> Jobs)
{
    public const int CurrentVersion = 1;
    public static AgentState Empty => new(CurrentVersion, DateTimeOffset.UtcNow, []);
}

public sealed class DurableJobStore
{
    private const long MaximumStateBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DurableJobStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The durable state path must be fully qualified.", nameof(path));
        _path = path;
    }

    public async Task<AgentState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return AgentState.Empty;
            var info = new FileInfo(_path);
            if (info.Length > MaximumStateBytes) throw new InvalidDataException("The durable agent state exceeds its size limit.");
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<AgentState>(stream, FramedJsonStream.SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The durable agent state is empty.");
            if (state.Version != AgentState.CurrentVersion) throw new InvalidDataException($"Unsupported durable agent state version {state.Version}.");
            if (state.Jobs.Count > 100_000) throw new InvalidDataException("The durable agent state contains too many jobs.");
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The durable agent state contains invalid JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<JobSnapshot> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var state = new AgentState(AgentState.CurrentVersion, DateTimeOffset.UtcNow, jobs.ToArray());
                    await JsonSerializer.SerializeAsync(stream, state, FramedJsonStream.SerializerOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
