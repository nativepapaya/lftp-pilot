using LFTPPilot.Core;

namespace LFTPPilot.App.Models;

public sealed record WorkspaceSessionSeed(
    SessionSnapshot Snapshot,
    IReadOnlyList<FileEntry> LocalEntries,
    IReadOnlyList<FileEntry> RemoteEntries);

public sealed record UiWorkspaceBootstrap(
    IReadOnlyList<ConnectionProfile> Profiles,
    IReadOnlyList<WorkspaceSessionSeed> Sessions,
    IReadOnlyList<JobSnapshot> Jobs,
    IReadOnlyList<RemoteEditSession> RemoteEdits,
    IReadOnlyList<HistoryRecord> History,
    IReadOnlyList<ActivityLogEntry> Log,
    bool IsDemoMode,
    string AgentStatus);

public sealed record ActivityLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message)
{
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed record MirrorUiPreview(
    MirrorDefinition Definition,
    MirrorPreview Preview);

public sealed record TransferUiOptions(
    TransferMode Mode,
    int DownloadSegments,
    long? RateLimitBytesPerSecond,
    DateTimeOffset? RunAt)
{
    public static TransferUiOptions Defaults(TransferDirection direction) => new(
        TransferMode.Auto,
        direction == TransferDirection.Download ? 4 : 1,
        null,
        null);
}

public sealed record ConsoleLine(DateTimeOffset Timestamp, string Stream, string Text)
{
    public string Prefix => Stream switch
    {
        "input" => ">",
        "error" => "!",
        _ => "·",
    };
}

public enum FilePaneSortColumn
{
    Name,
    Size,
    Modified,
    Type,
}

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}
