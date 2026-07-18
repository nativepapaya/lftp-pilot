using System.Collections.Immutable;

namespace LFTPPilot.Core;

public static class WorkspaceMethods
{
    public const string Bootstrap = "workspace.bootstrap";
    public const string ProfileList = "profiles.list";
    public const string ProfileSave = "profiles.save";
    public const string ProfileDelete = "profiles.delete";
    public const string SftpHostKeyInspect = "sftp.hostKeys.inspect";
    public const string SftpHostKeyApprove = "sftp.hostKeys.approve";
    public const string SessionConnect = "sessions.connect";
    public const string SessionDisconnect = "sessions.disconnect";
    public const string BrowseLocal = "browse.local";
    public const string BrowseRemote = "browse.remote";
    public const string RemoteSearchStart = "remoteSearch.start";
    public const string RemoteSearchGet = "remoteSearch.get";
    public const string RemoteSearchCancel = "remoteSearch.cancel";
    public const string FileCreateDirectory = "files.createDirectory";
    public const string FileMove = "files.move";
    public const string FileDelete = "files.delete";
    public const string TransferEnqueue = "transfers.enqueue";
    public const string FolderTransferPresetList = "transfers.folderPresets.list";
    public const string FolderTransferPresetSave = "transfers.folderPresets.save";
    public const string FolderTransferPresetDelete = "transfers.folderPresets.delete";
    public const string JobRetry = "jobs.retry";
    public const string MirrorPreview = "mirrors.preview";
    public const string MirrorApprove = "mirrors.approve";
    public const string MirrorDefinitionList = "mirrors.definitions.list";
    public const string MirrorDefinitionSave = "mirrors.definitions.save";
    public const string MirrorDefinitionDelete = "mirrors.definitions.delete";
    public const string ConsoleExecute = "console.execute";
    public const string RemoteTransferPlan = "remoteTransfers.plan";
    public const string RemoteTransferEnqueue = "remoteTransfers.enqueue";
    public const string RemoteEditStart = "remoteEdits.start";
    public const string RemoteEditReview = "remoteEdits.review";
    public const string RemoteEditResolve = "remoteEdits.resolve";
    public const string RemoteEditComplete = "remoteEdits.complete";
    public const string ExplorerExportStart = "explorerExports.start";
    public const string ExplorerExportGet = "explorerExports.get";
    public const string ExplorerExportRelease = "explorerExports.release";
}

public sealed record RuntimeStatus(bool Available, bool Authenticated, string Source, string? Error = null);

public sealed record WorkspaceBootstrap(
    int ProtocolVersion,
    RuntimeStatus Runtime,
    ImmutableArray<ConnectionProfile> Profiles,
    ImmutableArray<SessionSnapshot> Sessions,
    ImmutableArray<JobSnapshot> Jobs,
    ImmutableArray<RemoteEditSession> RemoteEdits)
{
    public ImmutableArray<MirrorDefinition> MirrorDefinitions { get; init; } = [];
    public ImmutableArray<FolderTransferPreset> FolderTransferPresets { get; init; } = [];
    public ImmutableArray<HistoryRecord> History { get; init; } = [];
}

public sealed record ProfileSaveRequest(ConnectionProfile Profile, string? Credential = null);
public sealed record ProfileDeleteRequest(Guid ProfileId);
public sealed record SftpHostKeyInspectRequest(ConnectionIdentity ExpectedIdentity);
public sealed record SftpHostKeyApproveRequest(
    Guid ProfileId,
    Guid ReviewId,
    string ApprovalToken,
    bool ReplaceExisting = false);
public sealed record SftpHostKeyApproveResult(
    Guid ProfileId,
    string Endpoint,
    string FingerprintSha256);
public sealed record SessionConnectRequest(
    ConnectionIdentity ExpectedIdentity,
    string? EphemeralCredential = null,
    Guid? ExistingSessionId = null);
public sealed record SessionDisconnectRequest(Guid SessionId);
public sealed record BrowseRequest(
    Guid? SessionId,
    string Path,
    bool Fresh = false,
    string? ContinuationToken = null,
    int PageSize = 512);
public sealed record BrowseResult(
    PaneLocation Location,
    ImmutableArray<FileEntry> Entries,
    string? ContinuationToken = null,
    int TotalCount = 0);
public sealed record RemoteSearchStartRequest(RemoteSearchSpec Search);
public sealed record RemoteSearchGetRequest(
    Guid SearchId,
    Guid SessionId,
    string? ContinuationToken = null,
    int PageSize = RemoteSearchPolicy.DefaultPageSize);
public sealed record RemoteSearchCancelRequest(Guid SearchId, Guid SessionId);
public sealed record CreateDirectoryRequest(PaneKind Kind, string Path, Guid? SessionId = null);
public sealed record MoveEntryRequest(
    PaneKind Kind,
    string SourcePath,
    string DestinationPath,
    Guid? SessionId = null);
public sealed record DeleteEntriesRequest(
    PaneKind Kind,
    ImmutableArray<string> Paths,
    Guid? SessionId = null,
    bool Recursive = false,
    bool Confirmed = false)
{
    public ImmutableArray<string> Paths { get; init; } = Paths.IsDefault ? [] : Paths;
    public ImmutableArray<string> EffectivePaths => Paths;
}
public sealed record FileMutationResult(PaneKind Kind, ImmutableArray<string> AffectedPaths);
public sealed record TransferEnqueueRequest(Guid SessionId, TransferPlan Plan);
public sealed record TransferEnqueueResult(JobSnapshot Job);
public sealed record FolderTransferPresetSaveRequest(FolderTransferPreset Preset);
public sealed record FolderTransferPresetDeleteRequest(Guid PresetId);
public sealed record JobRetryRequest(Guid JobId);
public sealed record JobRetryResult(JobSnapshot Job);
public sealed record MirrorDefinitionSaveRequest(MirrorDefinition Definition);
public sealed record MirrorDefinitionDeleteRequest(Guid DefinitionId);
public sealed record MirrorPreviewRequest(Guid SessionId, MirrorDefinition Definition);
public sealed record MirrorApproveRequest(
    Guid SessionId,
    MirrorDefinition Definition,
    Guid PreviewId,
    string ApprovalToken,
    string ReviewFingerprint,
    bool DeletionsApproved = false);
public sealed record MirrorApproveResult(JobSnapshot Job);
public sealed record ConsoleExecuteRequest(Guid SessionId, string Command);
public sealed record ConsoleExecuteResult(LftpCommandResult Result);
public sealed record RemoteTransferPlanRequest(
    Guid SourceProfileId,
    Guid DestinationProfileId,
    string SourcePath,
    string DestinationPath,
    bool Overwrite = false);
public sealed record RemoteTransferEnqueueRequest(RemoteTransferPlan Plan);
public sealed record RemoteTransferEnqueueResult(JobSnapshot Job, RemoteTransferMode Mode, string RoutingNote);
public sealed record RemoteEditStartRequest(Guid SessionId, string RemotePath);
public sealed record RemoteEditReviewRequest(string EditId);
public sealed record RemoteEditResolveRequest(string EditId, string ReviewToken, RemoteEditResolution Resolution);
public sealed record RemoteEditCompleteRequest(string EditId);
public sealed record ExplorerExportStartRequest(
    Guid ExportId,
    Guid SessionId,
    ImmutableArray<string> RemotePaths)
{
    public ImmutableArray<string> RemotePaths { get; init; } = RemotePaths.IsDefault ? [] : RemotePaths;
}
public sealed record ExplorerExportGetRequest(Guid ExportId);
public sealed record ExplorerExportReleaseRequest(Guid ExportId);
public sealed record ExplorerExportSnapshot(
    Guid ExportId,
    Guid SessionId,
    JobSnapshot Job,
    ImmutableArray<string> LocalPaths,
    DateTimeOffset ExpiresAt)
{
    public ImmutableArray<string> LocalPaths { get; init; } = LocalPaths.IsDefault ? [] : LocalPaths;
}
