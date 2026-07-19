using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace LFTPPilot.Core;

public enum ConnectionProtocol
{
    Sftp,
    Ftp,
    FtpOpportunisticTls,
    FtpsExplicit,
    FtpsImplicit,
}

public enum AuthenticationKind
{
    Password,
    AskOnConnect,
    SshKey,
    Anonymous,
}

public enum SftpHostKeyState { Trusted, EnrollmentRequired, Changed }
public enum PaneKind { Local, Remote }
public enum EntryKind { File, Directory, SymbolicLink, Other }
public enum TransferDirection { Download, Upload }
public enum TransferSourceKind { File, Directory }
public enum TransferMode { Auto, Resume, Overwrite, Skip }
public enum MirrorDirection { Download, Upload }
public enum MirrorActionKind { Download, Upload, CreateDirectory, DeleteFile, DeleteDirectory, UpdateMetadata, Other }
public enum RemoteTransferMode { Fxp, ClientRelay }
public enum RemoteSearchState { Queued, Running, Completed, Cancelled, Failed }
public enum RemoteSearchEntryKind { Directory, Other }
public enum RemoteEditReviewState { ReadyToUpload, Conflict }
public enum RemoteEditConflictKind { None, RemoteChanged, RemoteMissingOrRenamed, RemoteIdentityUnavailable, LocalMissingOrRenamed, LocalChanged, LocalTooLarge, ManagedCacheLimitExceeded }
public enum RemoteEditResolution { Upload, RefreshLocal, Overwrite }
public enum RemoteEditActionOutcome { Uploaded, Refreshed, ReviewRequired }
public enum RemoteEditLocalChangeKind { Saved, MissingOrRenamed, TooLarge, WatcherError }
public enum JobKind { Transfer, Mirror, RemoteTransfer, RemoteEdit }
public enum JobState { Scheduled, Queued, Running, Paused, Completed, Failed, Cancelled, Missed }
public enum EngineEventKind { Session, Directory, Job, RemoteEdit, Log, Error }
public enum UpdateAvailability { Unknown, Current, Available, Required, Unassociated, Error }

public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    ConnectionProtocol Protocol,
    string Host,
    int Port,
    string UserName,
    AuthenticationKind Authentication,
    string? SshKeyPath = null,
    string? InitialRemotePath = null,
    string? InitialLocalPath = null,
    ImmutableArray<string> Bookmarks = default)
{
    public ImmutableArray<string> Bookmarks { get; init; } = Bookmarks.IsDefault ? [] : Bookmarks;
    public ImmutableArray<string> EffectiveBookmarks => Bookmarks;
}

/// <summary>
/// The security-significant identity the App captured before asking the Agent to
/// inspect or connect a profile. Cosmetic profile fields are intentionally not
/// included, while every field that can change the transport or credential target is.
/// </summary>
public sealed record ConnectionIdentity(
    Guid ProfileId,
    ConnectionProtocol Protocol,
    string Host,
    int Port,
    string UserName,
    AuthenticationKind Authentication,
    string? SshKeyPath = null)
{
    [JsonIgnore]
    public string CanonicalEndpoint =>
        $"{Protocol.ToString().ToLowerInvariant()}://{FormatHost(Host)}:{Port}";

    [JsonIgnore]
    public string SftpHostKeyEndpoint => Protocol == ConnectionProtocol.Sftp
        ? $"sftp://{FormatHost(Host)}:{Port}"
        : throw new InvalidOperationException("Only an SFTP connection identity has a host-key endpoint.");

    public static ConnectionIdentity FromProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileValidator.ThrowIfInvalid(profile);

        var host = profile.Host.Trim();
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
        host = host.ToLowerInvariant();

        var sshKeyPath = profile.Authentication == AuthenticationKind.SshKey
            ? Path.GetFullPath(profile.SshKeyPath!.Trim())
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            : null;
        return new(
            profile.Id,
            profile.Protocol,
            host,
            profile.Port,
            profile.UserName,
            profile.Authentication,
            sshKeyPath);
    }

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
}

public sealed record HostKeyBinding(Guid ProfileId, string Endpoint);

public sealed record TrustedSftpHostKey(
    HostKeyBinding Binding,
    string Algorithm,
    string PublicKeyBase64,
    string FingerprintSha256);

public sealed record SftpHostKeyReview(
    Guid ReviewId,
    Guid ProfileId,
    string Endpoint,
    SftpHostKeyState State,
    string PresentedAlgorithm,
    string PresentedFingerprintSha256,
    string? TrustedAlgorithm,
    string? TrustedFingerprintSha256,
    DateTimeOffset ExpiresAt,
    string ApprovalToken);

public sealed record SftpHostKeyInspection(
    SftpHostKeyState State,
    SftpHostKeyReview? Review = null);

public sealed record SessionSnapshot(
    Guid SessionId,
    Guid ProfileId,
    string DisplayName,
    bool IsConnected,
    PaneLocation LocalLocation,
    PaneLocation RemoteLocation,
    DateTimeOffset UpdatedAt,
    EngineError? Error = null);

public sealed record PaneLocation(PaneKind Kind, string Path, bool IsLoading = false);

public sealed record FileEntry(
    string Name,
    string FullPath,
    EntryKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Permissions = null,
    string? Owner = null,
    string? Group = null,
    string? LinkTarget = null,
    bool IsHidden = false)
{
    public bool IsDirectory => Kind == EntryKind.Directory;
}

public sealed record RemoteSearchSpec(
    Guid SearchId,
    Guid SessionId,
    string Root,
    string Query,
    int MaxDepth = RemoteSearchPolicy.DefaultMaxDepth,
    bool MatchCase = false);

public sealed record RemoteSearchMatch(
    string Name,
    string FullPath,
    RemoteSearchEntryKind Kind)
{
    public bool IsDirectory => Kind == RemoteSearchEntryKind.Directory;
}

public sealed record RemoteSearchPage(
    RemoteSearchSpec Search,
    RemoteSearchState State,
    ImmutableArray<RemoteSearchMatch> Matches,
    string? ContinuationToken,
    int? TotalMatches,
    int ScannedEntries,
    bool WasLimited,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    EngineError? Error = null)
{
    public ImmutableArray<RemoteSearchMatch> Matches { get; init; } = Matches.IsDefault ? [] : Matches;
    public ImmutableArray<RemoteSearchMatch> EffectiveMatches => Matches;
}

public sealed record TransferPlan(
    Guid Id,
    Guid ProfileId,
    TransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    TransferMode Mode = TransferMode.Auto,
    int Segments = 1,
    long? RateLimitBytesPerSecond = null,
    DateTimeOffset? RunAt = null,
    TransferSourceKind SourceKind = TransferSourceKind.File,
    ImmutableArray<string> Includes = default,
    ImmutableArray<string> Excludes = default,
    int ParallelFiles = 1)
{
    public ImmutableArray<string> Includes { get; init; } = Includes.IsDefault ? [] : Includes;
    public ImmutableArray<string> Excludes { get; init; } = Excludes.IsDefault ? [] : Excludes;
    public ImmutableArray<string> EffectiveIncludes => Includes;
    public ImmutableArray<string> EffectiveExcludes => Excludes;
}

public sealed record FolderTransferPreset(
    Guid Id,
    string Name,
    ImmutableArray<string> Includes = default,
    ImmutableArray<string> Excludes = default,
    int ParallelFiles = 2,
    int DownloadSegmentsPerFile = 4)
{
    public ImmutableArray<string> Includes { get; init; } = Includes.IsDefault ? [] : Includes;
    public ImmutableArray<string> Excludes { get; init; } = Excludes.IsDefault ? [] : Excludes;
    public ImmutableArray<string> EffectiveIncludes => Includes;
    public ImmutableArray<string> EffectiveExcludes => Excludes;
}

public sealed record MirrorDefinition(
    Guid Id,
    Guid ProfileId,
    string Name,
    MirrorDirection Direction,
    string LocalRoot,
    string RemoteRoot,
    ImmutableArray<string> Includes = default,
    ImmutableArray<string> Excludes = default,
    bool DeleteExtraneous = false,
    int ParallelFiles = 2,
    int SegmentsPerFile = 1,
    long? RateLimitBytesPerSecond = null)
{
    public ImmutableArray<string> Includes { get; init; } = Includes.IsDefault ? [] : Includes;
    public ImmutableArray<string> Excludes { get; init; } = Excludes.IsDefault ? [] : Excludes;
    public ImmutableArray<string> EffectiveIncludes => Includes;
    public ImmutableArray<string> EffectiveExcludes => Excludes;
}

public sealed record MirrorAction(MirrorActionKind Kind, string Path, string? Detail = null)
{
    public bool IsDeletion => Kind is MirrorActionKind.DeleteFile or MirrorActionKind.DeleteDirectory;
}

public sealed record MirrorPreview(
    Guid Id,
    Guid DefinitionId,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    ImmutableArray<MirrorAction> Actions,
    string DefinitionFingerprint,
    string ApprovalToken)
{
    public bool ContainsDeletions => Actions.Any(static action => action.IsDeletion);
}

public sealed record RemoteTransferPlan(
    Guid Id,
    Guid SourceProfileId,
    Guid DestinationProfileId,
    string SourcePath,
    string DestinationPath,
    RemoteTransferMode Mode,
    bool Overwrite = false);

public sealed record RemoteFileIdentity(
    string CanonicalPath,
    long Size,
    DateTimeOffset ModifiedAt,
    string ContentSha256);

public sealed record RemoteEditSession(
    string EditId,
    Guid SessionId,
    string DisplayName,
    string RemotePath,
    string LocalPath,
    RemoteFileIdentity Baseline,
    bool Dirty = false,
    bool WatcherFailed = false,
    DateTimeOffset? LastLocalChangeAt = null);

public sealed record RemoteEditLocalChange(
    string EditId,
    string DisplayName,
    RemoteEditLocalChangeKind Kind,
    DateTimeOffset DetectedAt,
    string Message);

public sealed record RemoteEditCompleted(string EditId, string DisplayName);

public sealed record RemoteEditReview(
    string EditId,
    RemoteEditReviewState State,
    RemoteEditConflictKind Conflict,
    RemoteFileIdentity Baseline,
    RemoteFileIdentity? Current,
    string ReviewToken,
    DateTimeOffset ExpiresAt,
    string Message);

public sealed record RemoteEditActionResult(
    RemoteEditActionOutcome Outcome,
    RemoteEditSession Session,
    string Message,
    RemoteEditReview? Review = null);

public sealed record JobSnapshot(
    Guid Id,
    JobKind Kind,
    Guid? ProfileId,
    string DisplayName,
    JobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RunAt = null,
    double? Progress = null,
    string? Status = null,
    EngineError? Error = null,
    bool RetryAvailable = false)
{
    [JsonIgnore]
    public bool CanRetry => RetryAvailable && State == JobState.Failed;
}

public sealed record HistoryLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message);

public sealed record HistoryRecord(
    Guid Id,
    Guid JobId,
    JobKind Kind,
    string DisplayName,
    JobState Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long? BytesTransferred = null,
    string? Detail = null,
    ImmutableArray<HistoryLogEntry> Log = default)
{
    public ImmutableArray<HistoryLogEntry> Log { get; init; } = Log.IsDefault ? [] : Log;
}

public sealed record TransferProgressSnapshot(
    Guid JobId,
    long BytesTransferred,
    long TotalBytes,
    double? BytesPerSecond,
    DateTimeOffset ObservedAt)
{
    [JsonIgnore]
    public double Progress => TotalBytes == 0 ? 0 : (double)BytesTransferred / TotalBytes;
}

public sealed record EngineEvent(
    long Sequence,
    EngineEventKind Kind,
    string Name,
    DateTimeOffset Timestamp,
    object? Payload = null,
    Guid? SessionId = null,
    Guid? JobId = null);

public sealed record EngineError(string Code, string Message, bool IsTransient = false, string? Detail = null);

public sealed record AppUpdateStatus(
    Version InstalledVersion,
    UpdateAvailability Availability,
    Version? AvailableVersion = null,
    Uri? InstallerUri = null,
    string? ErrorMessage = null,
    DateTimeOffset? CheckedAt = null);

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public enum FileListDensity
{
    Compact,
    Comfortable,
}

public sealed record AppPreferences(
    AppThemePreference Theme = AppThemePreference.System,
    FileListDensity FileListDensity = FileListDensity.Compact,
    bool ShowHiddenLocal = false,
    bool ShowHiddenRemote = true,
    bool ExpandActivityCenter = true,
    int DefaultParallelFiles = 2,
    int DefaultDownloadSegments = 4,
    long DownloadLimitKiB = 0,
    long UploadLimitKiB = 0);

public sealed record SecretBinding(
    Guid ProfileId,
    string Endpoint,
    string UserName,
    string Purpose)
{
    public string CanonicalIdentity => $"{ProfileId:N}|{Endpoint.Trim().ToLowerInvariant()}|{UserName}|{Purpose.Trim().ToLowerInvariant()}";
}

public sealed record SecretValue(SecretBinding Binding, string Value);

public sealed record LftpProcessStartOptions(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyList<string>? Secrets = null,
    string Tag = "lftp");

public sealed record LftpRuntimeDescriptor(
    string RuntimeRoot,
    string ExecutablePath,
    string BinaryDirectory,
    bool IsAuthenticated,
    string Source,
    bool IsTestOverride = false);

public sealed record LftpOutputLine(string Stream, string Line);

public sealed record LftpCommandResult(
    ImmutableArray<LftpOutputLine> Lines,
    bool TimedOut = false,
    bool Truncated = false,
    string? Failure = null)
{
    public IEnumerable<string> StandardOutput => Lines.Where(static line => line.Stream == "stdout").Select(static line => line.Line);
    public IEnumerable<string> StandardError => Lines.Where(static line => line.Stream == "stderr").Select(static line => line.Line);
    public bool Succeeded => !TimedOut && Failure is null;
}
