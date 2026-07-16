using System.Text.Json;

namespace LFTPPilot.Core;

public interface ILftpSession : IAsyncDisposable
{
    int ProcessId { get; }
    bool IsRunning { get; }
    event EventHandler<LftpOutputLine>? OutputReceived;
    event EventHandler<LftpOutputLine>? UnsolicitedOutput;
    Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAsync(bool force = false, CancellationToken cancellationToken = default);
}

public interface ILftpProcessHost
{
    Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default);
}

public interface ILftpRuntimeProvider
{
    Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default);
}

public interface IEngineClient : IAsyncDisposable
{
    IAsyncEnumerable<EngineEvent> Events(CancellationToken cancellationToken = default);
    Task<JsonElement> RequestAsync(string method, object? payload = null, CancellationToken cancellationToken = default);
}

public interface IJobCoordinator
{
    event EventHandler<JobSnapshot>? JobChanged;
    IReadOnlyList<JobSnapshot> GetJobs();
    JobSnapshot Enqueue(JobSnapshot job);
    JobSnapshot Transition(Guid jobId, JobState state, string? status = null, EngineError? error = null);
    JobSnapshot Retry(Guid jobId, string? status = null);
    bool TryCancel(Guid jobId, string? reason = null);
}

public interface IMirrorPlanner
{
    MirrorPreview CreatePreview(MirrorDefinition definition, IEnumerable<string> dryRunOutput, DateTimeOffset? now = null);
    string BuildExecutionCommand(MirrorDefinition definition, MirrorPreview preview, string? approvalToken, DateTimeOffset? now = null);
}

public interface IProfileStore
{
    Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default);
    Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface IHostKeyStore
{
    Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default);
    Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default);
}

public interface ISshHostKeyProbe
{
    Task<TrustedSftpHostKey> ProbeAsync(
        ConnectionProfile profile,
        string hostKeyAlias,
        CancellationToken cancellationToken = default);
}

public interface IHistoryStore
{
    Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default);
    Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IAppUpdateService
{
    Task<AppUpdateStatus> CheckAsync(CancellationToken cancellationToken = default);
    Task OpenInstallerAsync(CancellationToken cancellationToken = default);
}
