using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

/// <summary>
/// Owns transient recursive remote searches. Search work never occupies a control-pipe
/// request: start is idempotent and immediate, results are polled in bounded pages, and
/// cancellation retires only the dedicated ephemeral LFTP process.
/// </summary>
internal sealed class RemoteSearchCoordinator : IAsyncDisposable
{
    private const int MaximumConcurrentSearches = 4;
    private const int MaximumRetainedSearches = 8;
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(5);
    private readonly SessionRegistry _sessions;
    private readonly TimeSpan _timeout;
    private readonly Action<EngineEventKind, string, object?, Guid?, Guid?>? _publish;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Registration> _searches = [];
    private bool _disposed;

    public RemoteSearchCoordinator(
        SessionRegistry sessions,
        TimeSpan timeout,
        Action<EngineEventKind, string, object?, Guid?, Guid?>? publish = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _publish = publish;
    }

    public RemoteSearchPage Start(RemoteSearchStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RemoteSearchPolicy.Validate(request.Search);
        var session = _sessions.Get(request.Search.SessionId);
        Registration registration;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpiredCore(DateTimeOffset.UtcNow);
            if (_searches.TryGetValue(request.Search.SearchId, out registration!))
            {
                if (registration.Spec != request.Search)
                    throw new InvalidOperationException("A remote-search identifier is already bound to different search inputs.");
                return CreatePageCore(registration, continuationToken: null, RemoteSearchPolicy.DefaultPageSize);
            }

            if (_searches.Values.Any(candidate =>
                candidate.Spec.SessionId == request.Search.SessionId && IsActive(candidate.State)))
            {
                throw new InvalidOperationException("This session already has an active remote search. Cancel it before starting another.");
            }
            if (_searches.Values.Count(candidate => IsActive(candidate.State)) >= MaximumConcurrentSearches)
                throw new InvalidOperationException("The Agent is already running the maximum number of concurrent remote searches.");

            MakeRoomCore();
            registration = new(
                request.Search,
                session.Profile.Id,
                DateTimeOffset.UtcNow,
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _searches.Add(request.Search.SearchId, registration);
        }

        // RunCoreAsync enters WithEphemeralSessionAsync synchronously through its first
        // await, acquiring the WorkspaceSession operation lease before Start returns.
        var operation = RunCoreAsync(registration, session);
        lock (_gate) registration.Operation = operation;
        Observe(operation);
        return Get(new(request.Search.SearchId, request.Search.SessionId));
    }

    public RemoteSearchPage Get(RemoteSearchGetRequest request)
    {
        RemoteSearchPolicy.ValidatePageRequest(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpiredCore(DateTimeOffset.UtcNow);
            var registration = FindCore(request.SearchId, request.SessionId);
            return CreatePageCore(registration, request.ContinuationToken, request.PageSize);
        }
    }

    public async Task<bool> CancelAsync(
        RemoteSearchCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        RemoteSearchPolicy.ValidateCancelRequest(request);
        Registration registration;
        Task? operation;
        var requested = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PurgeExpiredCore(DateTimeOffset.UtcNow);
            registration = FindCore(request.SearchId, request.SessionId);
            if (IsActive(registration.State))
            {
                requested = true;
                registration.Cancellation.Cancel();
            }
            operation = registration.Operation;
        }

        if (operation is not null)
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        return requested || registration.State == RemoteSearchState.Cancelled;
    }

    public Task CancelSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        CancelWhereAsync(registration => registration.Spec.SessionId == sessionId, remove: true, cancellationToken);

    public Task CancelProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        CancelWhereAsync(registration => registration.ProfileId == profileId, remove: true, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Registration[] registrations;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _lifetime.Cancel();
            registrations = _searches.Values.ToArray();
        }
        return new ValueTask(DisposeCoreAsync(registrations));
    }

    private async Task RunCoreAsync(Registration registration, WorkspaceSession session)
    {
        SetRunning(registration);
        try
        {
            var result = await session.WithEphemeralSessionAsync(
                $"remote-search-{registration.Spec.SearchId:N}",
                process => process.ExecuteToExitAsync(
                    LftpCommandBuilder.BuildRemoteFind(registration.Spec.Root, registration.Spec.MaxDepth),
                    _timeout,
                    registration.Cancellation.Token),
                registration.Cancellation.Token).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Remote search");
            var parsed = LftpOutputParser.ParseRemoteFindOutput(
                result.Lines
                    .Where(static line => string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase))
                    .Select(static line => line.Line),
                registration.Spec);
            Complete(registration, parsed);
        }
        catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
        {
            Cancelled(registration);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
        {
            Failed(registration, exception);
        }
    }

    private void SetRunning(Registration registration)
    {
        RemoteSearchPage? page = null;
        lock (_gate)
        {
            if (registration.State != RemoteSearchState.Queued) return;
            if (registration.Cancellation.IsCancellationRequested)
            {
                registration.State = RemoteSearchState.Cancelled;
                registration.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                registration.State = RemoteSearchState.Running;
                registration.UpdatedAt = DateTimeOffset.UtcNow;
            }
            page = CreateStatusPageCore(registration);
        }
        Publish(page);
    }

    private void Complete(Registration registration, RemoteSearchParseResult parsed)
    {
        RemoteSearchPage? page = null;
        lock (_gate)
        {
            if (!IsActive(registration.State)) return;
            registration.Matches = parsed.Matches;
            registration.ScannedEntries = parsed.ScannedEntries;
            registration.WasLimited = parsed.WasLimited;
            registration.State = RemoteSearchState.Completed;
            registration.UpdatedAt = DateTimeOffset.UtcNow;
            page = CreateStatusPageCore(registration);
        }
        Publish(page);
    }

    private void Cancelled(Registration registration)
    {
        RemoteSearchPage? page = null;
        lock (_gate)
        {
            if (!IsActive(registration.State)) return;
            registration.State = RemoteSearchState.Cancelled;
            registration.UpdatedAt = DateTimeOffset.UtcNow;
            page = CreateStatusPageCore(registration);
        }
        Publish(page);
    }

    private void Failed(Registration registration, Exception exception)
    {
        RemoteSearchPage? page = null;
        lock (_gate)
        {
            if (!IsActive(registration.State)) return;
            registration.State = RemoteSearchState.Failed;
            registration.Error = new(exception.GetType().Name, exception.Message);
            registration.UpdatedAt = DateTimeOffset.UtcNow;
            page = CreateStatusPageCore(registration);
        }
        Publish(page);
    }

    private async Task CancelWhereAsync(
        Func<Registration, bool> predicate,
        bool remove,
        CancellationToken cancellationToken)
    {
        Registration[] registrations;
        lock (_gate)
        {
            if (_disposed) return;
            registrations = _searches.Values.Where(predicate).ToArray();
            foreach (var registration in registrations)
                if (IsActive(registration.State)) registration.Cancellation.Cancel();
        }

        var operations = registrations.Select(static registration => registration.Operation)
            .Where(static operation => operation is not null)
            .Cast<Task>()
            .ToArray();
        if (operations.Length != 0)
            await Task.WhenAll(operations).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (!remove) return;
        lock (_gate)
        {
            foreach (var registration in registrations)
            {
                if (!_searches.Remove(registration.Spec.SearchId)) continue;
                registration.Cancellation.Dispose();
            }
        }
    }

    private async Task DisposeCoreAsync(Registration[] registrations)
    {
        var operations = registrations.Select(static registration => registration.Operation)
            .Where(static operation => operation is not null)
            .Cast<Task>()
            .ToArray();
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var registration in registrations) registration.Cancellation.Dispose();
            _searches.Clear();
        }
        _lifetime.Dispose();
    }

    private RemoteSearchPage CreatePageCore(Registration registration, string? continuationToken, int pageSize)
    {
        if (registration.State != RemoteSearchState.Completed)
        {
            if (continuationToken is not null)
                throw new InvalidDataException("A running or terminal search without results cannot be continued.");
            return CreateStatusPageCore(registration);
        }

        var offset = ParseContinuationToken(registration, continuationToken);
        if (offset > registration.Matches.Length)
            throw new InvalidDataException("The remote-search continuation token is outside the retained result snapshot.");
        if (offset == registration.Matches.Length)
        {
            if (offset != 0)
                throw new InvalidDataException("The remote-search continuation token points past the final page.");
            return new(
                registration.Spec,
                registration.State,
                [],
                null,
                0,
                registration.ScannedEntries,
                registration.WasLimited,
                registration.StartedAt,
                registration.UpdatedAt,
                registration.Error);
        }

        var matches = ImmutableArray.CreateBuilder<RemoteSearchMatch>();
        var estimatedBytes = 1024;
        for (var index = offset; index < registration.Matches.Length && matches.Count < pageSize; index++)
        {
            var match = registration.Matches[index];
            var bytes = JsonSerializer.SerializeToUtf8Bytes(match).Length + 1;
            if (bytes > RemoteSearchPolicy.MaximumPageBytes)
                throw new InvalidDataException("A remote-search match is too large for the bounded Agent protocol.");
            if (matches.Count != 0 && estimatedBytes + bytes > RemoteSearchPolicy.MaximumPageBytes) break;
            estimatedBytes += bytes;
            matches.Add(match);
        }
        if (matches.Count == 0)
            throw new InvalidDataException("The remote-search page could not fit within the bounded Agent protocol.");
        var nextOffset = offset + matches.Count;
        var next = nextOffset < registration.Matches.Length
            ? $"{registration.Spec.SearchId:N}:{nextOffset.ToString(CultureInfo.InvariantCulture)}"
            : null;
        return new(
            registration.Spec,
            registration.State,
            matches.ToImmutable(),
            next,
            registration.Matches.Length,
            registration.ScannedEntries,
            registration.WasLimited,
            registration.StartedAt,
            registration.UpdatedAt,
            registration.Error);
    }

    private static int ParseContinuationToken(Registration registration, string? continuationToken)
    {
        if (continuationToken is null) return 0;
        var parts = continuationToken.Split(':', 2, StringSplitOptions.None);
        if (parts.Length != 2 ||
            !Guid.TryParseExact(parts[0], "N", out var searchId) ||
            searchId != registration.Spec.SearchId ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset <= 0 ||
            !string.Equals(parts[1], offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The remote-search continuation token is invalid or expired.");
        }
        return offset;
    }

    private static RemoteSearchPage CreateStatusPageCore(Registration registration) => new(
        registration.Spec,
        registration.State,
        [],
        null,
        registration.State == RemoteSearchState.Completed ? registration.Matches.Length : null,
        registration.ScannedEntries,
        registration.WasLimited,
        registration.StartedAt,
        registration.UpdatedAt,
        registration.Error);

    private Registration FindCore(Guid searchId, Guid sessionId)
    {
        if (!_searches.TryGetValue(searchId, out var registration))
            throw new KeyNotFoundException($"Remote search {searchId} was not found or has expired.");
        if (registration.Spec.SessionId != sessionId)
            throw new InvalidOperationException("The remote search belongs to a different session.");
        return registration;
    }

    private void MakeRoomCore()
    {
        while (_searches.Count >= MaximumRetainedSearches)
        {
            var oldest = _searches.Values
                .Where(static registration => !IsActive(registration.State))
                .MinBy(static registration => registration.UpdatedAt)
                ?? throw new InvalidOperationException("The Agent remote-search registry is full of active work.");
            _searches.Remove(oldest.Spec.SearchId);
            oldest.Cancellation.Dispose();
        }
    }

    private void PurgeExpiredCore(DateTimeOffset now)
    {
        foreach (var registration in _searches.Values
            .Where(registration => !IsActive(registration.State) && now - registration.UpdatedAt >= TerminalRetention)
            .ToArray())
        {
            _searches.Remove(registration.Spec.SearchId);
            registration.Cancellation.Dispose();
        }
    }

    private void Observe(Task operation) => _ = operation.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private void Publish(RemoteSearchPage? page)
    {
        if (page is null) return;
        _publish?.Invoke(
            EngineEventKind.Directory,
            "remoteSearch.changed",
            page,
            null,
            page.Search.SessionId);
    }

    private static bool IsActive(RemoteSearchState state) =>
        state is RemoteSearchState.Queued or RemoteSearchState.Running;

    private sealed class Registration(
        RemoteSearchSpec spec,
        Guid profileId,
        DateTimeOffset startedAt,
        CancellationTokenSource cancellation)
    {
        public RemoteSearchSpec Spec { get; } = spec;
        public Guid ProfileId { get; } = profileId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Operation { get; set; }
        public RemoteSearchState State { get; set; } = RemoteSearchState.Queued;
        public ImmutableArray<RemoteSearchMatch> Matches { get; set; } = [];
        public int ScannedEntries { get; set; }
        public bool WasLimited { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = startedAt;
        public EngineError? Error { get; set; }
    }
}
