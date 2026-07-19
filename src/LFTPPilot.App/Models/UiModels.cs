using System.Collections.Immutable;
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
    string AgentStatus)
{
    public IReadOnlyList<MirrorDefinition> MirrorDefinitions { get; init; } = [];
    public IReadOnlyList<FolderTransferPreset> FolderTransferPresets { get; init; } = [];
}

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

public sealed record RemoteSearchResultItem(RemoteSearchMatch Match)
{
    public string Name => Match.Name;
    public string FullPath => Match.FullPath;
    public string ContainingPath
    {
        get
        {
            var separator = FullPath.LastIndexOf('/');
            return separator <= 0 ? "/" : FullPath[..separator];
        }
    }
    public string KindDisplay => Match.IsDirectory ? "Folder" : "Remote item";
    public string Glyph => Match.IsDirectory ? "\uE8B7" : "\uE7C3";
}

public sealed record TransferUiOptions(
    TransferMode Mode,
    int DownloadSegments,
    long? RateLimitBytesPerSecond,
    DateTimeOffset? RunAt,
    ImmutableArray<string> Includes = default,
    ImmutableArray<string> Excludes = default,
    int ParallelFiles = 1)
{
    public ImmutableArray<string> Includes { get; init; } = Includes.IsDefault ? [] : Includes;
    public ImmutableArray<string> Excludes { get; init; } = Excludes.IsDefault ? [] : Excludes;
    public static TransferUiOptions Defaults(TransferDirection direction) => new(
        TransferMode.Auto,
        direction == TransferDirection.Download ? 4 : 1,
        null,
        null,
        ParallelFiles: 2);
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
