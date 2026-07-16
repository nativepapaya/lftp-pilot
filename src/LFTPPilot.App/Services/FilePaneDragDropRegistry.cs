using System.Collections.Immutable;
using LFTPPilot.Core;

namespace LFTPPilot.App.Services;

public sealed record FilePaneTransferSource(string Path, TransferSourceKind Kind);

public enum FilePaneDropRejectionKind
{
    Empty,
    TooManyItems,
    UnsupportedItem,
    InvalidLocalPath,
    DataUnavailable,
}

public sealed record FilePaneDropRejection(FilePaneDropRejectionKind Kind, string Message)
{
    internal static FilePaneDropRejection Create(FilePaneDropRejectionKind kind) => kind switch
    {
        FilePaneDropRejectionKind.Empty => new(kind, "Explorer did not provide any files or folders. Select between 1 and 100 local items and try again."),
        FilePaneDropRejectionKind.TooManyItems => new(kind, "Explorer provided more than 100 items. Select 100 or fewer local files or folders and try again."),
        FilePaneDropRejectionKind.UnsupportedItem => new(kind, "Explorer included an unsupported item. Drop only regular local files and folders."),
        FilePaneDropRejectionKind.InvalidLocalPath => new(kind, "One or more Explorer items do not expose a valid local path. Copy them to a local folder before uploading."),
        FilePaneDropRejectionKind.DataUnavailable => new(kind, "Windows could not read the dropped Explorer items. Select the local files or folders in Explorer and try again."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed record FilePaneDragPayload(
    Guid SessionId,
    PaneKind SourcePane,
    ImmutableArray<FilePaneTransferSource> Sources);

internal sealed class FilePaneDragDropRegistry
{
    internal const string DataFormat = "application/vnd.lftp-pilot.file-pane-token";
    internal const int MaximumSources = 100;

    private const int DefaultCapacity = 128;
    private const int MaximumCapacity = 1_024;
    private const int MaximumPathLength = 32_767;
    private const int MaximumCombinedPathLength = 1_048_576;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tokenLifetime;
    private readonly Dictionary<string, StoredPayload> _payloads = new(StringComparer.Ordinal);

    internal FilePaneDragDropRegistry(
        TimeProvider? timeProvider = null,
        TimeSpan? tokenLifetime = null,
        int capacity = DefaultCapacity)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokenLifetime = tokenLifetime ?? DefaultLifetime;
        if (_tokenLifetime <= TimeSpan.Zero || _tokenLifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(tokenLifetime));
        if (capacity is < 1 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal static FilePaneDragDropRegistry Shared { get; } = new();

    internal bool TryIssue(
        Guid sessionId,
        PaneKind sourcePane,
        IEnumerable<FilePaneTransferSource> sources,
        out string token)
    {
        ArgumentNullException.ThrowIfNull(sources);
        token = string.Empty;
        if (sessionId == Guid.Empty || !Enum.IsDefined(sourcePane)) return false;

        if (!TryCopyValidSources(sources, out var immutableSources)) return false;

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            while (_payloads.Count >= _capacity)
            {
                var oldest = _payloads.MinBy(static pair => pair.Value.IssuedAt).Key;
                _payloads.Remove(oldest);
            }

            do
            {
                token = Guid.NewGuid().ToString("N");
            }
            while (_payloads.ContainsKey(token));

            var payload = new FilePaneDragPayload(sessionId, sourcePane, immutableSources);
            _payloads[token] = new(payload, now, now + _tokenLifetime);
            return true;
        }
    }

    internal bool CanAccept(string? token, Guid targetSessionId, PaneKind targetPane)
    {
        if (!IsTokenShapeValid(token) || targetSessionId == Guid.Empty || !Enum.IsDefined(targetPane)) return false;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            return _payloads.TryGetValue(token!, out var stored) && IsOppositePaneInSameSession(stored.Payload, targetSessionId, targetPane);
        }
    }

    internal bool TryConsume(
        string? token,
        Guid targetSessionId,
        PaneKind targetPane,
        out FilePaneDragPayload? payload)
    {
        payload = null;
        if (!IsTokenShapeValid(token) || targetSessionId == Guid.Empty || !Enum.IsDefined(targetPane)) return false;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneExpired(now);
            if (!_payloads.TryGetValue(token!, out var stored) ||
                !IsOppositePaneInSameSession(stored.Payload, targetSessionId, targetPane))
            {
                return false;
            }

            _payloads.Remove(token!);
            payload = stored.Payload;
            return true;
        }
    }

    internal static bool CanAcceptExplorerStorageItems(PaneKind targetPane) => targetPane == PaneKind.Remote;

    internal static bool AreValidExplorerSources(ImmutableArray<FilePaneTransferSource> sources) =>
        AreValidSources(sources) && sources.All(static source =>
            Path.IsPathFullyQualified(source.Path) && !IsLocalFileSystemRoot(source.Path));

    internal static bool TryCopyValidSources(
        IEnumerable<FilePaneTransferSource> sources,
        out ImmutableArray<FilePaneTransferSource> immutableSources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var builder = ImmutableArray.CreateBuilder<FilePaneTransferSource>();
        foreach (var source in sources)
        {
            if (builder.Count >= MaximumSources)
            {
                immutableSources = [];
                return false;
            }

            builder.Add(source);
        }

        immutableSources = builder.ToImmutable();
        return AreValidSources(immutableSources);
    }

    internal static bool AreValidSources(ImmutableArray<FilePaneTransferSource> sources)
    {
        if (sources.Length is < 1 or > MaximumSources) return false;
        var combinedLength = 0;
        foreach (var source in sources)
        {
            if (source is null || source.Kind is not TransferSourceKind.File and not TransferSourceKind.Directory ||
                string.IsNullOrWhiteSpace(source.Path) || source.Path.Length > MaximumPathLength ||
                source.Path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
                source.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(static segment => segment is "." or ".."))
            {
                return false;
            }

            combinedLength += source.Path.Length;
            if (combinedLength > MaximumCombinedPathLength) return false;
        }

        return true;
    }

    private static bool IsTokenShapeValid(string? token) =>
        token is { Length: 32 } && Guid.TryParseExact(token, "N", out _);

    private static bool IsOppositePaneInSameSession(FilePaneDragPayload payload, Guid targetSessionId, PaneKind targetPane) =>
        payload.SessionId == targetSessionId && payload.SourcePane != targetPane;

    private static bool IsLocalFileSystemRoot(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrEmpty(root) && string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var expired in _payloads.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
        {
            _payloads.Remove(expired);
        }
    }

    private sealed record StoredPayload(FilePaneDragPayload Payload, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
}
