using System.Collections.Immutable;

namespace LFTPPilot.Core;

public static class ExplorerExportPolicy
{
    public const int MaximumFiles = 100;
    public const int MaximumAggregatePathCharacters = 64 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static void ValidateStart(ExplorerExportStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExportId == Guid.Empty || request.SessionId == Guid.Empty)
            throw new ArgumentException("Explorer export and session identifiers are required.", nameof(request));
        if (request.RemotePaths.IsDefaultOrEmpty || request.RemotePaths.Length > MaximumFiles)
            throw new ArgumentException($"Explorer export requires between 1 and {MaximumFiles} remote files.", nameof(request));
        var aggregate = 0;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in request.RemotePaths)
        {
            ValidateRemotePath(path);
            aggregate = checked(aggregate + path.Length);
            if (aggregate > MaximumAggregatePathCharacters || !unique.Add(path))
                throw new ArgumentException("Explorer export paths must be unique and bounded.", nameof(request));
        }
    }

    public static void ValidateSnapshot(ExplorerExportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ExportId == Guid.Empty || snapshot.SessionId == Guid.Empty || snapshot.Job.Id != snapshot.ExportId ||
            snapshot.Job.Kind != JobKind.Transfer || snapshot.ExpiresAt == default || snapshot.ExpiresAt.Offset != TimeSpan.Zero ||
            snapshot.LocalPaths.IsDefault || snapshot.LocalPaths.Length > MaximumFiles)
            throw new ArgumentException("The Explorer export snapshot is invalid.", nameof(snapshot));
        JobSnapshotPolicy.Validate(snapshot.Job);
        if (snapshot.Job.State == JobState.Completed)
        {
            if (snapshot.LocalPaths.Length == 0) throw new ArgumentException("A completed Explorer export requires local files.", nameof(snapshot));
        }
        else if (snapshot.LocalPaths.Length != 0)
        {
            throw new ArgumentException("Only a completed Explorer export may expose local files.", nameof(snapshot));
        }
        if (snapshot.LocalPaths.Any(static path => string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
            !Path.IsPathFullyQualified(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0) ||
            snapshot.LocalPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.LocalPaths.Length)
            throw new ArgumentException("Explorer export local paths are invalid.", nameof(snapshot));
    }

    private static void ValidateRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) ||
            path == "/" || path.Contains("//", StringComparison.Ordinal) || path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            path.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            throw new ArgumentException("Explorer export paths must be bounded absolute remote file paths.");
    }
}
