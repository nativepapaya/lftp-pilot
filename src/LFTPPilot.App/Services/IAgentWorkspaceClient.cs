using LFTPPilot.App.Models;
using LFTPPilot.Core;

namespace LFTPPilot.App.Services;

/// <summary>
/// UI-facing boundary for the background Agent. The named-pipe adapter can replace the
/// demo implementation without exposing transport details to view models.
/// </summary>
public interface IAgentWorkspaceClient : IAsyncDisposable
{
    event EventHandler<EngineEvent>? EventReceived;
    event EventHandler? StateInvalidated;
    bool IsConnected { get; }
    Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default);
    Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default);
    Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default);
    Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default);
    Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default);
    Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default);
    Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default);
    Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default);
    Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default);
    Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default);
    Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default);
    Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default);
    Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default);
    Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default);
    Task StopAgentAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default);
}
