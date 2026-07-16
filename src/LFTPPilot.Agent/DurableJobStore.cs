using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

internal sealed record PersistedSessionTab(
    Guid SessionId,
    Guid ProfileId,
    ConnectionIdentity IdentityAtSave,
    string LocalPath,
    string RemotePath,
    int Order,
    bool ReconnectRequested);

internal sealed record AgentState(
    int Version,
    DateTimeOffset SavedAt,
    IReadOnlyList<JobSnapshot> Jobs,
    IReadOnlyList<PersistedSessionTab>? SessionTabs = null)
{
    public const int CurrentVersion = 2;
    public static AgentState Empty => new(CurrentVersion, DateTimeOffset.UtcNow, [], []);
    public IReadOnlyList<PersistedSessionTab> EffectiveSessionTabs => SessionTabs ?? [];
}

public sealed class DurableJobStore
{
    internal const int MaximumSessionTabs = 128;
    private const int MaximumJobs = 100_000;
    private const long MaximumStateBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<JobSnapshot> _jobs = [];
    private IReadOnlyList<PersistedSessionTab> _sessionTabs = [];
    private long _nextJobWriteRevision;
    private long _committedJobWriteRevision;
    private bool _loaded;

    public DurableJobStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The durable state path must be fully qualified.", nameof(path));
        _path = path;
    }

    internal async Task<AgentState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return new(AgentState.CurrentVersion, DateTimeOffset.UtcNow, _jobs.ToArray(), _sessionTabs.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveAsync(IEnumerable<JobSnapshot> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var revision = ReserveJobWriteRevision();
        var candidateJobs = ValidateJobs(jobs, DateTimeOffset.UtcNow);
        return SaveJobsAsync(candidateJobs, revision, cancellationToken);
    }

    internal Task SaveAsync(
        Func<IReadOnlyList<JobSnapshot>> captureJobs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captureJobs);
        // Callers that own a mutable coordinator use this overload so the
        // monotonic revision is reserved before the snapshot is captured.
        // A delayed older capture can then never replace a newer committed one.
        var revision = ReserveJobWriteRevision();
        var candidateJobs = ValidateJobs(
            captureJobs() ?? throw new InvalidDataException("The durable job snapshot provider returned no collection."),
            DateTimeOffset.UtcNow);
        return SaveJobsAsync(candidateJobs, revision, cancellationToken);
    }

    private async Task SaveJobsAsync(
        IReadOnlyList<JobSnapshot> candidateJobs,
        long revision,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (revision <= _committedJobWriteRevision) return;
            var state = new AgentState(AgentState.CurrentVersion, DateTimeOffset.UtcNow, candidateJobs, _sessionTabs);
            await WriteAsync(state, cancellationToken).ConfigureAwait(false);
            _jobs = candidateJobs;
            _committedJobWriteRevision = revision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private long ReserveJobWriteRevision()
    {
        var revision = Interlocked.Increment(ref _nextJobWriteRevision);
        return revision > 0
            ? revision
            : throw new InvalidOperationException("The durable job-write revision was exhausted.");
    }

    internal async Task SaveSessionTabsAsync(
        IEnumerable<PersistedSessionTab> sessionTabs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionTabs);
        var candidateTabs = ValidateSessionTabs(sessionTabs);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var state = new AgentState(AgentState.CurrentVersion, DateTimeOffset.UtcNow, _jobs, candidateTabs);
            await WriteAsync(state, cancellationToken).ConfigureAwait(false);
            _sessionTabs = candidateTabs;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        var state = await ReadAsync(cancellationToken).ConfigureAwait(false);
        _jobs = state.Jobs.ToArray();
        _sessionTabs = state.EffectiveSessionTabs.ToArray();
        _loaded = true;
    }

    private async Task<AgentState> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return AgentState.Empty;
        var info = new FileInfo(_path);
        if (info.Length > MaximumStateBytes) throw new InvalidDataException("The durable agent state exceeds its size limit.");
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<AgentState>(
                stream,
                FramedJsonStream.SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The durable agent state is empty.");
            if (state.Version is not 1 && state.Version != AgentState.CurrentVersion)
                throw new InvalidDataException($"Unsupported durable agent state version {state.Version}.");
            if (state.Jobs is null || state.Jobs.Count > MaximumJobs)
                throw new InvalidDataException("The durable agent state contains an invalid job collection.");
            if (state.Version == AgentState.CurrentVersion && state.SessionTabs is null)
                throw new InvalidDataException("The durable agent state does not contain a session-tab collection.");
            ValidateStateTimestamp(state.SavedAt, "saved-at");
            if (state.SavedAt > DateTimeOffset.UtcNow + JobSnapshotPolicy.MaximumFutureTimestampSkew)
                throw new InvalidDataException("The durable agent state has a future-dated saved-at timestamp.");
            var jobs = ValidateJobs(state.Jobs, state.SavedAt);
            var tabs = state.Version == 1 ? [] : ValidateSessionTabs(state.EffectiveSessionTabs);
            return new(AgentState.CurrentVersion, state.SavedAt, jobs, tabs);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The durable agent state contains invalid JSON.", exception);
        }
    }

    private async Task WriteAsync(AgentState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    FramedJsonStream.SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumStateBytes)
                    throw new InvalidDataException("The durable agent state exceeds its size limit.");
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static IReadOnlyList<PersistedSessionTab> ValidateSessionTabs(IEnumerable<PersistedSessionTab> sessionTabs)
    {
        var tabs = sessionTabs.ToArray();
        if (tabs.Length > MaximumSessionTabs)
            throw new InvalidDataException($"The durable agent state contains more than {MaximumSessionTabs} session tabs.");
        if (tabs.Any(static tab => tab is null))
            throw new InvalidDataException("The durable agent state contains an empty session tab.");
        if (tabs.Any(static tab => tab.SessionId == Guid.Empty || tab.ProfileId == Guid.Empty))
            throw new InvalidDataException("Every durable session tab requires non-empty session and profile identifiers.");
        if (tabs.Select(static tab => tab.SessionId).Distinct().Count() != tabs.Length)
            throw new InvalidDataException("The durable agent state contains duplicate session-tab identifiers.");
        if (tabs.Select(static tab => tab.Order).Distinct().Count() != tabs.Length ||
            tabs.Any(static tab => tab.Order is < 0 or >= MaximumSessionTabs))
        {
            throw new InvalidDataException("Every durable session tab requires a unique, bounded display order.");
        }

        foreach (var tab in tabs)
        {
            ValidateIdentity(tab);
            ValidateLocalPath(tab.LocalPath);
            ValidateRemotePath(tab.RemotePath);
        }
        return tabs.OrderBy(static tab => tab.Order).ToArray();
    }

    private static IReadOnlyList<JobSnapshot> ValidateJobs(
        IEnumerable<JobSnapshot> jobs,
        DateTimeOffset timestampReference)
    {
        var snapshots = jobs.ToArray();
        if (snapshots.Length > MaximumJobs)
            throw new InvalidDataException("The durable agent state contains too many jobs.");
        if (snapshots.Any(static job => job is null))
            throw new InvalidDataException("The durable agent state contains an empty job snapshot.");
        if (snapshots.Select(static job => job.Id).Distinct().Count() != snapshots.Length)
            throw new InvalidDataException("The durable agent state contains duplicate job identifiers.");

        foreach (var job in snapshots)
        {
            try { JobSnapshotPolicy.ValidateForEnqueue(job, timestampReference); }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The durable agent state contains an invalid job snapshot.", exception);
            }
        }

        return snapshots;
    }

    private static void ValidateStateTimestamp(DateTimeOffset value, string field)
    {
        if (value == default || value.Offset != TimeSpan.Zero || value.Year is < 2000 or > 9998)
            throw new InvalidDataException($"A durable job contains an invalid {field} timestamp.");
    }

    private static void ValidateIdentity(PersistedSessionTab tab)
    {
        if (tab.IdentityAtSave is null || tab.IdentityAtSave.ProfileId != tab.ProfileId)
            throw new InvalidDataException("A durable session tab has an invalid connection-identity binding.");
        try
        {
            var identity = tab.IdentityAtSave;
            var profile = new ConnectionProfile(
                identity.ProfileId,
                "Persisted session tab",
                identity.Protocol,
                identity.Host,
                identity.Port,
                identity.UserName,
                identity.Authentication,
                identity.SshKeyPath);
            ProfileValidator.ThrowIfInvalid(profile);
            if (ConnectionIdentity.FromProfile(profile) != identity)
                throw new InvalidDataException("A durable session tab contains a non-canonical connection identity.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A durable session tab contains an invalid connection identity.", exception);
        }
    }

    private static void ValidateLocalPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
                path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path) ||
                IsDevicePath(path) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A durable session tab contains an invalid local path.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("A durable session tab contains an invalid local path.", exception);
        }
    }

    private static void ValidateRemotePath(string path)
    {
        if (!ProfileValidator.IsCanonicalRemotePath(path))
        {
            throw new InvalidDataException("A durable session tab contains an invalid remote path.");
        }
    }

    private static bool IsDevicePath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase);
    }
}
