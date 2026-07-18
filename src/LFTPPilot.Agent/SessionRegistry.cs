using System.Collections.Concurrent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

public sealed record AgentWorkspaceOptions(
    string RuntimeHomeRoot,
    string CacheRoot,
    string TemporaryRoot,
    TimeSpan ConnectTimeout,
    TimeSpan BrowseTimeout,
    TimeSpan RemoteSearchTimeout,
    TimeSpan TransferTimeout,
    TimeSpan MirrorPreviewTimeout,
    TimeSpan ConsoleTimeout,
    int TransferQueueParallelism)
{
    public static AgentWorkspaceOptions CreateDefault(string runtimeHomeRoot, string? cacheRoot = null, string? temporaryRoot = null) => new(
        runtimeHomeRoot,
        cacheRoot ?? Path.Combine(runtimeHomeRoot, "cache"),
        temporaryRoot ?? Path.Combine(runtimeHomeRoot, "temporary"),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(90),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromHours(24),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(5),
        2);
}

public sealed class SessionRegistry : IAsyncDisposable
{
    private const string CommonSettings = "set cmd:fail-exit no; set file:charset UTF-8; set cmd:cls-exact-time no; set cache:expire 2m; set net:idle 10m; set xfer:make-backup no; set xfer:use-temp-file yes; set sftp:auto-confirm false";
    private readonly ILftpProcessHost _processHost;
    private readonly ILftpRuntimeProvider _runtimeProvider;
    private readonly SftpHostKeyManager _hostKeys;
    private readonly AgentWorkspaceOptions _options;
    private readonly ConcurrentDictionary<Guid, WorkspaceSession> _sessions = [];
    private readonly ConcurrentDictionary<Guid, PersistedSessionTab> _disconnectedTabs = [];
    private readonly ConcurrentDictionary<Guid, string> _disconnectedDisplayNames = [];
    private readonly ConcurrentDictionary<Guid, WorkspaceSession> _closingSessions = [];
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<Guid, TaskCompletionSource<SessionSnapshot>> _reconnectsInFlight = [];
    private TaskCompletionSource? _connectsDrained;
    private TaskCompletionSource? _disposeCompletion;
    private int _connectsInFlight;
    private int _nextTabOrder;
    private volatile bool _closing;

    public SessionRegistry(
        ILftpProcessHost processHost,
        ILftpRuntimeProvider runtimeProvider,
        SftpHostKeyManager hostKeys,
        AgentWorkspaceOptions options)
    {
        _processHost = processHost;
        _runtimeProvider = runtimeProvider;
        _hostKeys = hostKeys;
        _options = options;
        if (!Path.IsPathFullyQualified(options.RuntimeHomeRoot) || !Path.IsPathFullyQualified(options.CacheRoot) || !Path.IsPathFullyQualified(options.TemporaryRoot))
            throw new ArgumentException("The Agent runtime, cache, and temporary roots must be fully qualified.", nameof(options));
        if (options.ConnectTimeout <= TimeSpan.Zero || options.BrowseTimeout <= TimeSpan.Zero || options.RemoteSearchTimeout <= TimeSpan.Zero || options.TransferTimeout <= TimeSpan.Zero ||
            options.MirrorPreviewTimeout <= TimeSpan.Zero || options.ConsoleTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Agent operation timeouts must be positive.", nameof(options));
        if (options.TransferQueueParallelism is < 1 or > 8)
            throw new ArgumentException("The per-profile LFTP transfer queue parallelism must be between 1 and 8.", nameof(options));
    }

    public IReadOnlyList<SessionSnapshot> GetSnapshots() => _sessions.Values
        .Select(static session => (session.Snapshot, session.TabOrder))
        .Concat(_disconnectedTabs.Values.Select(tab => (CreateDisconnectedSnapshot(tab), tab.Order)))
        .OrderBy(static item => item.Item2)
        .Select(static item => item.Item1)
        .ToArray();

    internal IReadOnlyList<PersistedSessionTab> GetPersistedTabs() => _sessions.Values
        .Select(static session => session.ToPersistedTab())
        .Concat(_disconnectedTabs.Values)
        .OrderBy(static tab => tab.Order)
        .ToArray();

    internal void RestoreTabs(
        IEnumerable<PersistedSessionTab> tabs,
        IReadOnlyDictionary<Guid, ConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_sessions.Count != 0 || _disconnectedTabs.Count != 0 || _reconnectsInFlight.Count != 0)
                throw new InvalidOperationException("Session tabs can only be restored into an empty registry.");
            var maximumOrder = -1;
            foreach (var tab in tabs.OrderBy(static tab => tab.Order))
            {
                if (!profiles.TryGetValue(tab.ProfileId, out var profile) ||
                    ConnectionIdentity.FromProfile(profile) != tab.IdentityAtSave)
                {
                    continue;
                }
                if (!_disconnectedTabs.TryAdd(tab.SessionId, tab))
                    throw new InvalidDataException("The durable session-tab state contains a duplicate identifier.");
                _disconnectedDisplayNames[tab.SessionId] = profile.Name;
                maximumOrder = Math.Max(maximumOrder, tab.Order);
            }
            _nextTabOrder = maximumOrder + 1;
        }
    }

    internal WorkspaceSession Get(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        if (_sessions.TryGetValue(sessionId, out var session)) return session;
        if (_closingSessions.ContainsKey(sessionId))
            throw new InvalidOperationException($"Session {sessionId} is closing and cannot accept new operations.");
        if (_disconnectedTabs.ContainsKey(sessionId))
            throw new InvalidOperationException($"Session tab {sessionId} is disconnected and must be reconnected before remote operations can run.");
        throw new KeyNotFoundException($"Session {sessionId} was not found.");
    }

    internal WorkspaceSession GetActive(Guid sessionId)
    {
        var session = Get(sessionId);
        if (!session.IsActive)
            throw new InvalidOperationException($"Session {sessionId} is no longer actively connected.");
        return session;
    }

    internal WorkspaceSession GetActiveProfile(Guid profileId) => _sessions.Values
        .Where(session => session.Profile.Id == profileId && session.IsActive)
        .OrderByDescending(session => session.Snapshot.UpdatedAt)
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"Profile {profileId} must be actively connected before starting a remote-to-remote transfer.");

    internal bool HasClosingProfile(Guid profileId) =>
        _closingSessions.Values.Any(session => session.Profile.Id == profileId);

    internal WorkspaceSession? GetClosing(Guid sessionId) =>
        _closingSessions.TryGetValue(sessionId, out var session) ? session : null;

    internal SessionSnapshot? GetActiveSnapshot(Guid sessionId, ConnectionIdentity expectedIdentity)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_closingSessions.ContainsKey(sessionId))
                throw new InvalidOperationException("The session is still closing and cannot be reconnected yet.");
            if (!_sessions.TryGetValue(sessionId, out var active)) return null;
            if (active.IdentityAtSave != expectedIdentity)
                throw new InvalidOperationException("The active session does not match the requested profile identity.");
            return active.Snapshot;
        }
    }

    public async Task<SessionSnapshot> ConnectAsync(ConnectionProfile profile, string? credential, CancellationToken cancellationToken = default)
    {
        var result = await ConnectAsync(profile, credential, existingSessionId: null, cancellationToken).ConfigureAwait(false);
        return result.Snapshot;
    }

    internal async Task<SessionConnectionResult> ConnectAsync(
        ConnectionProfile profile,
        string? credential,
        Guid? existingSessionId,
        CancellationToken cancellationToken = default)
    {
        ProfileValidator.ThrowIfInvalid(profile);
        using var reservation = AcquireConnectReservation();
        var identity = ConnectionIdentity.FromProfile(profile);
        var id = existingSessionId ?? Guid.NewGuid();
        PersistedSessionTab? replacedTab = null;
        Task<SessionSnapshot>? concurrentReconnect = null;
        TaskCompletionSource<SessionSnapshot>? reconnectCompletion = null;
        var tabOrder = -1;
        var localPath = string.Empty;
        var remotePath = string.Empty;

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (existingSessionId is { } requestedId)
            {
                if (_closingSessions.ContainsKey(requestedId))
                    throw new InvalidOperationException("The session is still closing and cannot be reconnected yet.");
                if (_sessions.TryGetValue(requestedId, out var active))
                {
                    if (active.Profile.Id != profile.Id || active.IdentityAtSave != identity)
                        throw new InvalidOperationException("The active session does not match the requested profile identity.");
                    return new(active.Snapshot, null, IsNewRegistration: false);
                }
                if (_reconnectsInFlight.TryGetValue(requestedId, out var pending))
                {
                    concurrentReconnect = pending.Task;
                }
                else
                {
                    if (!_disconnectedTabs.TryGetValue(requestedId, out replacedTab))
                        throw new KeyNotFoundException($"Disconnected session tab {requestedId} was not found.");
                    if (replacedTab.ProfileId != profile.Id || replacedTab.IdentityAtSave != identity)
                        throw new InvalidOperationException("The disconnected session tab does not match the requested profile identity.");
                    tabOrder = replacedTab.Order;
                    localPath = replacedTab.LocalPath;
                    remotePath = replacedTab.RemotePath;
                    reconnectCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = reconnectCompletion.Task.ContinueWith(
                        static completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    _reconnectsInFlight.Add(requestedId, reconnectCompletion);
                }
            }
            else
            {
                if (_sessions.Count + _disconnectedTabs.Count + _closingSessions.Count + _reconnectsInFlight.Count >= DurableJobStore.MaximumSessionTabs)
                    throw new InvalidOperationException($"No more than {DurableJobStore.MaximumSessionTabs} session tabs can be open.");
                tabOrder = ReserveNextTabOrder();
                localPath = !string.IsNullOrWhiteSpace(profile.InitialLocalPath) && Directory.Exists(profile.InitialLocalPath)
                    ? Path.GetFullPath(profile.InitialLocalPath)
                    : Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                remotePath = NormalizeRemotePath(profile.InitialRemotePath);
            }
        }

        if (concurrentReconnect is not null)
        {
            var concurrentSnapshot = await concurrentReconnect.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (concurrentSnapshot.ProfileId != profile.Id)
                throw new InvalidOperationException("The concurrent reconnect completed for a different profile.");
            return new(concurrentSnapshot, null, IsNewRegistration: false);
        }

        ILftpSession? browse = null;
        WorkspaceSession? session = null;
        try
        {
            browse = await StartConnectedProcessAsync(id, profile, credential, "browse", cancellationToken).ConfigureAwait(false);
            var snapshot = new SessionSnapshot(
                id,
                profile.Id,
                profile.Name,
                true,
                new(PaneKind.Local, localPath),
                new(PaneKind.Remote, remotePath),
                DateTimeOffset.UtcNow);
            session = new WorkspaceSession(this, profile, identity, credential, browse, snapshot, tabOrder);
            bool accepted;
            lock (_lifecycleGate)
            {
                accepted = !_closing;
                if (accepted && existingSessionId is not null)
                {
                    accepted = _disconnectedTabs.TryGetValue(id, out var currentTab) &&
                        currentTab == replacedTab && _disconnectedTabs.TryRemove(id, out _);
                    if (accepted) _disconnectedDisplayNames.TryRemove(id, out _);
                }
                if (accepted && !_sessions.TryAdd(id, session)) accepted = false;
                if (existingSessionId is not null) _reconnectsInFlight.Remove(id);
            }
            if (!accepted)
            {
                if (existingSessionId is not null && replacedTab is not null)
                    _disconnectedTabs.TryAdd(id, replacedTab);
                throw new ObjectDisposedException(nameof(SessionRegistry),
                    "The session registry closed or the tab changed before the connection could be registered.");
            }
            reconnectCompletion?.TrySetResult(snapshot);
            return new(snapshot, replacedTab, IsNewRegistration: true);
        }
        catch (Exception exception)
        {
            lock (_lifecycleGate)
            {
                if (existingSessionId is not null) _reconnectsInFlight.Remove(id);
            }
            reconnectCompletion?.TrySetException(exception);
            if (session is not null)
            {
                TryRemoveSession(id, session);
                if (replacedTab is not null)
                {
                    _disconnectedTabs.TryAdd(id, replacedTab);
                    _disconnectedDisplayNames[id] = profile.Name;
                }
            }
            if (browse is not null)
                try
                {
                    if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                    else await browse.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposalException)
                {
                    throw new AggregateException("The failed connection process could not be stopped cleanly.", exception, disposalException);
                }
            throw;
        }
    }

    public async Task<bool> DisconnectAsync(Guid sessionId)
    {
        if (GetClosing(sessionId) is { } closing)
        {
            await DisposeDetachedAsync(closing).ConfigureAwait(false);
            return true;
        }
        var preparation = PrepareDisconnect(sessionId);
        if (preparation is null) return false;
        var detached = DetachDisconnect(preparation);
        if (detached is null) return preparation.ActiveSession is null;
        await DisposeDetachedAsync(detached).ConfigureAwait(false);
        return true;
    }

    internal SessionDisconnectPreparation? PrepareDisconnect(Guid sessionId)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_reconnectsInFlight.ContainsKey(sessionId))
                throw new InvalidOperationException("The session tab is currently reconnecting.");
            if (_disconnectedTabs.TryGetValue(sessionId, out var disconnected))
                return new(disconnected, null);
            return _sessions.TryGetValue(sessionId, out var session)
                ? new(session.ToPersistedTab(), session)
                : null;
        }
    }

    internal IReadOnlyList<SessionDisconnectPreparation> PrepareDisconnectProfile(Guid profileId)
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_reconnectsInFlight.Keys.Any(id =>
                _disconnectedTabs.TryGetValue(id, out var tab) && tab.ProfileId == profileId))
            {
                throw new InvalidOperationException("A profile session is currently reconnecting.");
            }
            return _sessions.Values.Where(session => session.Profile.Id == profileId)
                .Select(session => new SessionDisconnectPreparation(session.ToPersistedTab(), session))
                .Concat(_disconnectedTabs.Values.Where(tab => tab.ProfileId == profileId)
                    .Select(static tab => new SessionDisconnectPreparation(tab, null)))
                .OrderBy(static item => item.PersistedTab.Order)
                .ToArray();
        }
    }

    internal WorkspaceSession? DetachDisconnect(SessionDisconnectPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var sessionId = preparation.PersistedTab.SessionId;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (preparation.ActiveSession is null)
            {
                if (!_disconnectedTabs.TryGetValue(sessionId, out var current) || current != preparation.PersistedTab ||
                    !_disconnectedTabs.TryRemove(sessionId, out _))
                {
                    throw new InvalidOperationException("The disconnected session tab changed before it could be detached.");
                }
                _disconnectedDisplayNames.TryRemove(sessionId, out _);
                return null;
            }

            if (!_sessions.TryGetValue(sessionId, out var active) || !ReferenceEquals(active, preparation.ActiveSession) ||
                _closingSessions.ContainsKey(sessionId))
            {
                throw new InvalidOperationException("The connected session changed before it could be detached.");
            }
            active.MarkClosing();
            if (!_sessions.TryRemove(sessionId, out var removed) || !ReferenceEquals(removed, active) ||
                !_closingSessions.TryAdd(sessionId, active))
            {
                throw new InvalidOperationException("The connected session could not be tombstoned for cleanup.");
            }
            return active;
        }
    }

    internal async Task DisposeDetachedAsync(WorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.DisposeAsync().ConfigureAwait(false);
        _closingSessions.TryRemove(new KeyValuePair<Guid, WorkspaceSession>(session.Snapshot.SessionId, session));
    }

    public async Task DisconnectProfileAsync(Guid profileId)
    {
        var preparations = PrepareDisconnectProfile(profileId);
        var errors = new List<Exception>();
        foreach (var preparation in preparations)
        {
            try
            {
                var detached = DetachDisconnect(preparation);
                if (detached is not null) await DisposeDetachedAsync(detached).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
            {
                errors.Add(exception);
            }
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Multiple profile sessions failed to stop cleanly.", errors);
    }

    internal void RestoreDisconnectedTabs(IEnumerable<PersistedSessionTab> tabs, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            foreach (var tab in tabs)
            {
                if (_sessions.ContainsKey(tab.SessionId) || !_disconnectedTabs.TryAdd(tab.SessionId, tab))
                    throw new InvalidOperationException($"Session tab {tab.SessionId} could not be restored after a persistence failure.");
                _disconnectedDisplayNames[tab.SessionId] = displayName ?? "Disconnected session";
                _nextTabOrder = Math.Max(_nextTabOrder, tab.Order + 1);
            }
        }
    }

    internal async Task RollbackConnectionAsync(SessionConnectionResult connection)
    {
        if (!connection.IsNewRegistration) return;
        if (!_sessions.TryGetValue(connection.Snapshot.SessionId, out var session))
            throw new InvalidOperationException("The connected session could not be found for persistence rollback.");
        var preparation = new SessionDisconnectPreparation(session.ToPersistedTab(), session);
        var detached = DetachDisconnect(preparation)
            ?? throw new InvalidOperationException("The connected session could not be detached for persistence rollback.");
        if (connection.ReplacedTab is not null)
            RestoreDisconnectedTabs([connection.ReplacedTab], connection.Snapshot.DisplayName);
        await DisposeDetachedAsync(detached).ConfigureAwait(false);
    }

    internal Task<ILftpSession> StartAuxiliaryAsync(WorkspaceSession session, string role, CancellationToken cancellationToken) =>
        StartConnectedProcessAsync(session.Snapshot.SessionId, session.Profile, session.Credential, role, cancellationToken);

    internal Task<ILftpSession> StartEphemeralAsync(WorkspaceSession session, string role, CancellationToken cancellationToken) =>
        StartConnectedProcessAsync(session.Snapshot.SessionId, session.Profile, session.Credential, role, cancellationToken);

    internal async Task<NativeTransferQueue> StartTransferQueueAsync(WorkspaceSession session, CancellationToken cancellationToken)
    {
        var process = await StartConnectedProcessAsync(
            session.Snapshot.SessionId,
            session.Profile,
            session.Credential,
            "transfer-queue",
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await NativeTransferQueue.CreateAsync(
                process,
                _options.TransferQueueParallelism,
                _options.ConnectTimeout,
                _options.TransferTimeout,
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<NativeTransferQueue> StartIsolatedTransferQueueAsync(
        WorkspaceSession session,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var process = await StartConnectedProcessAsync(
            session.Snapshot.SessionId,
            session.Profile,
            session.Credential,
            $"transfer-policy-{operationId:N}",
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await NativeTransferQueue.CreateAsync(
                process,
                1,
                _options.ConnectTimeout,
                _options.TransferTimeout,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<T> WithRemoteTransferAsync<T>(
        WorkspaceSession source,
        WorkspaceSession destination,
        Guid operationId,
        Func<ILftpSession, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (source.Profile.Id == destination.Profile.Id) throw new ArgumentException("Remote transfer profiles must be distinct.");
        if (source.Profile.Protocol == ConnectionProtocol.Sftp || destination.Profile.Protocol == ConnectionProtocol.Sftp)
            throw new NotSupportedException("One LFTP process cannot safely bind two SFTP endpoints to distinct pinned host-key files.");
        using var sourceLease = source.AcquireOperationLease();
        using var destinationLease = destination.AcquireOperationLease();
        var secrets = new[] { source.Credential, destination.Credential }
            .Where(static secret => !string.IsNullOrEmpty(secret)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        await using var process = await StartProcessAsync(
            operationId,
            Path.Combine("remote-transfers", operationId.ToString("N")),
            "remote-transfer",
            secrets,
            cancellationToken).ConfigureAwait(false);
        var settings = await process.ExecuteAsync(CommonSettings, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(settings, "Remote transfer session initialization");
        await InitializeSlotAsync(process, operationId, "source", source.Profile, source.Credential, cancellationToken).ConfigureAwait(false);
        await InitializeSlotAsync(process, operationId, "destination", destination.Profile, destination.Credential, cancellationToken).ConfigureAwait(false);
        var selected = await process.ExecuteAsync("slot source", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(selected, "Remote transfer source selection");
        return await operation(process).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Task drain;
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _closing = true;
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_connectsInFlight == 0)
            {
                drain = Task.CompletedTask;
            }
            else
            {
                _connectsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _connectsDrained.Task;
            }
        }
        _ = CompleteDisposeAsync(drain, completion);
        return new(completion.Task);
    }

    private IDisposable AcquireConnectReservation()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _connectsInFlight++;
            return new ConnectReservation(this);
        }
    }

    private async Task CompleteDisposeAsync(Task drain, TaskCompletionSource completion)
    {
        try
        {
            await drain.ConfigureAwait(false);
            var errors = new List<Exception>();
            foreach (var pair in _sessions.ToArray())
            {
                try
                {
                    await pair.Value.DisposeAsync().ConfigureAwait(false);
                    TryRemoveSession(pair.Key, pair.Value);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
                {
                    errors.Add(exception);
                }
            }
            foreach (var pair in _closingSessions.ToArray())
            {
                try
                {
                    await pair.Value.DisposeAsync().ConfigureAwait(false);
                    _closingSessions.TryRemove(new KeyValuePair<Guid, WorkspaceSession>(pair.Key, pair.Value));
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
                {
                    errors.Add(exception);
                }
            }
            if (errors.Count == 1) throw errors[0];
            if (errors.Count > 1) throw new AggregateException("Multiple workspace sessions failed to stop cleanly.", errors);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (_lifecycleGate)
            {
                completion.TrySetException(exception);
                if (ReferenceEquals(_disposeCompletion, completion)) _disposeCompletion = null;
            }
        }
    }

    private void ReleaseConnectReservation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleGate)
        {
            if (_connectsInFlight <= 0) throw new InvalidOperationException("The session registry connection reservation count is inconsistent.");
            _connectsInFlight--;
            if (_closing && _connectsInFlight == 0) drained = _connectsDrained;
        }
        drained?.TrySetResult();
    }

    private int ReserveNextTabOrder()
    {
        var used = _sessions.Values.Select(static session => session.TabOrder)
            .Concat(_disconnectedTabs.Values.Select(static tab => tab.Order))
            .ToHashSet();
        for (var candidate = Math.Max(0, _nextTabOrder); candidate < DurableJobStore.MaximumSessionTabs; candidate++)
        {
            if (!used.Contains(candidate))
            {
                _nextTabOrder = candidate + 1;
                return candidate;
            }
        }
        for (var candidate = 0; candidate < Math.Max(0, _nextTabOrder); candidate++)
        {
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("No durable session-tab order is available.");
    }

    private static string NormalizeRemotePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!ProfileValidator.IsCanonicalRemotePath(value))
        {
            throw new ArgumentException("The initial remote path must be a canonical, bounded absolute path.", nameof(path));
        }
        return value;
    }

    private SessionSnapshot CreateDisconnectedSnapshot(PersistedSessionTab tab) => new(
        tab.SessionId,
        tab.ProfileId,
        _disconnectedDisplayNames.TryGetValue(tab.SessionId, out var displayName) ? displayName : "Disconnected session",
        false,
        new(PaneKind.Local, tab.LocalPath),
        new(PaneKind.Remote, tab.RemotePath),
        DateTimeOffset.UtcNow,
        tab.ReconnectRequested
            ? new("agent-restarted", "Reconnect this restored tab to resume remote operations.", IsTransient: true)
            : null);

    private bool TryRemoveSession(Guid sessionId, WorkspaceSession session)
    {
        if (!_sessions.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, session)) return false;
        return _sessions.TryRemove(sessionId, out _);
    }

    private sealed class ConnectReservation(SessionRegistry owner) : IDisposable
    {
        private SessionRegistry? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseConnectReservation();
    }

    private async Task<ILftpSession> StartConnectedProcessAsync(Guid sessionId, ConnectionProfile profile, string? credential, string role, CancellationToken cancellationToken)
    {
        SftpKnownHostsMaterialization? trustedHost = null;
        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            trustedHost = await _hostKeys.MaterializeAsync(
                profile,
                GetTemporaryRoot(sessionId, role),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        var process = await StartProcessAsync(
            sessionId,
            profile.Id.ToString("N"),
            role,
            string.IsNullOrEmpty(credential) ? [] : [credential],
            cancellationToken).ConfigureAwait(false);
        try
        {
            var open = LftpCommandBuilder.BuildOpen(
                profile,
                credential,
                trustedHost?.KnownHostsPath,
                trustedHost?.HostKeyAlias);
            var initialized = await process.ExecuteAsync($"{CommonSettings}; {open}", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(initialized, "Connection initialization");
            var verified = await process.ExecuteAsync("cls -lad .", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(verified, "Connection verification");
            return process;
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ILftpSession> StartProcessAsync(
        Guid sessionId,
        string storageScope,
        string role,
        IReadOnlyList<string> secrets,
        CancellationToken cancellationToken)
    {
        var runtime = await _runtimeProvider.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!runtime.IsAuthenticated && !runtime.IsTestOverride)
            throw new InvalidDataException("The LFTP runtime is not authenticated and is not an explicit test override.");

        var profileRoot = Path.Combine(_options.RuntimeHomeRoot, storageScope);
        var home = Path.Combine(profileRoot, "home");
        var cache = Path.Combine(_options.CacheRoot, "lftp", storageScope);
        var temporary = GetTemporaryRoot(sessionId, role);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(temporary);
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = home,
            ["TMP"] = temporary,
            ["TEMP"] = temporary,
            ["XDG_CACHE_HOME"] = cache,
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["TERM"] = "dumb",
            ["MSYS"] = "disable_pcon",
            ["CYGWIN"] = "disable_pcon",
            ["MSYS2_PATH_TYPE"] = "inherit",
            ["CHERE_INVOKING"] = "1",
            ["PATH"] = runtime.BinaryDirectory,
            ["SSH_AUTH_SOCK"] = null,
        };
        var start = new LftpProcessStartOptions(
            runtime.ExecutablePath,
            runtime.RuntimeRoot,
            environment,
            ["--norc"],
            secrets,
            role);
        return await _processHost.StartAsync(start, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeSlotAsync(
        ILftpSession process,
        Guid operationId,
        string slot,
        ConnectionProfile profile,
        string? credential,
        CancellationToken cancellationToken)
    {
        var selected = await process.ExecuteAsync($"slot {slot}", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(selected, $"Remote transfer {slot} slot selection");
        SftpKnownHostsMaterialization? trustedHost = null;
        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            trustedHost = await _hostKeys.MaterializeAsync(
                profile,
                GetTemporaryRoot(operationId, "remote-transfer"),
                $"known_hosts-{slot}",
                cancellationToken).ConfigureAwait(false);
        }
        var open = LftpCommandBuilder.BuildOpen(
            profile,
            credential,
            trustedHost?.KnownHostsPath,
            trustedHost?.HostKeyAlias);
        var initialized = await process.ExecuteAsync(open, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(initialized, $"Remote transfer {slot} connection initialization");
        var verified = await process.ExecuteAsync("cls -lad .", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(verified, $"Remote transfer {slot} connection verification");
    }

    private string GetTemporaryRoot(Guid sessionId, string role) =>
        Path.Combine(_options.TemporaryRoot, "sessions", sessionId.ToString("N"), role);

    internal static void ThrowIfFailed(LftpCommandResult result, string operation)
    {
        if (result.TimedOut) throw new TimeoutException($"{operation} timed out.");
        if (result.Failure is not null) throw new InvalidOperationException($"{operation} failed: {result.Failure}");
        if (result.Truncated) throw new InvalidDataException($"{operation} produced more output than can be processed safely.");
        var error = LftpOutputParser.FirstError(result.Lines);
        if (error is not null) throw new InvalidOperationException($"{operation} failed: {error}");
    }
}

internal sealed record SessionConnectionResult(
    SessionSnapshot Snapshot,
    PersistedSessionTab? ReplacedTab,
    bool IsNewRegistration);

internal sealed record SessionDisconnectPreparation(
    PersistedSessionTab PersistedTab,
    WorkspaceSession? ActiveSession);

internal sealed class WorkspaceSession : IAsyncDisposable
{
    private readonly SessionRegistry _registry;
    private readonly object _lifecycleGate = new();
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _roleGate = new(1, 1);
    private readonly SemaphoreSlim _transferOperationGate = new(1, 1);
    private readonly SemaphoreSlim _validationOperationGate = new(1, 1);
    private readonly ILftpSession _browse;
    private ILftpSession? _transfer;
    private ILftpSession? _console;
    private ILftpSession? _validation;
    private NativeTransferQueue? _nativeTransferQueue;
    private SessionSnapshot _snapshot;
    private TaskCompletionSource? _operationsDrained;
    private TaskCompletionSource? _disposeCompletion;
    private int _activeOperations;
    private volatile bool _closing;

    public WorkspaceSession(
        SessionRegistry registry,
        ConnectionProfile profile,
        ConnectionIdentity identityAtSave,
        string? credential,
        ILftpSession browse,
        SessionSnapshot snapshot,
        int tabOrder)
    {
        _registry = registry;
        Profile = profile;
        IdentityAtSave = identityAtSave;
        Credential = credential;
        _browse = browse;
        _snapshot = snapshot;
        TabOrder = tabOrder;
    }

    public ConnectionProfile Profile { get; }
    public ConnectionIdentity IdentityAtSave { get; }
    public string? Credential { get; private set; }
    public int TabOrder { get; }
    public SessionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsActive => !_closing && Snapshot.IsConnected && _browse.IsRunning;

    public PersistedSessionTab ToPersistedTab()
    {
        var snapshot = Snapshot;
        return new(
            snapshot.SessionId,
            snapshot.ProfileId,
            IdentityAtSave,
            Path.GetFullPath(snapshot.LocalLocation.Path),
            snapshot.RemoteLocation.Path,
            TabOrder,
            ReconnectRequested: true);
    }

    internal void MarkClosing()
    {
        lock (_lifecycleGate) _closing = true;
    }

    public void SetLocation(PaneKind kind, string path)
    {
        using var lease = AcquireOperationLease();
        SetLocationCore(kind, path);
    }

    private void SetLocationCore(PaneKind kind, string path)
    {
        lock (_snapshotGate)
        {
            var snapshot = _snapshot;
            var updated = kind == PaneKind.Local
                ? snapshot with { LocalLocation = new(PaneKind.Local, path), UpdatedAt = DateTimeOffset.UtcNow }
                : snapshot with { RemoteLocation = new(PaneKind.Remote, path), UpdatedAt = DateTimeOffset.UtcNow };
            Volatile.Write(ref _snapshot, updated);
        }
    }

    internal void RestoreSnapshot(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var lease = AcquireOperationLease();
        if (snapshot.SessionId != Snapshot.SessionId || snapshot.ProfileId != Profile.Id)
            throw new InvalidOperationException("A different session snapshot cannot be restored.");
        lock (_snapshotGate) Volatile.Write(ref _snapshot, snapshot);
    }

    public async Task<T> WithBrowseSessionAsync<T>(
        Func<ILftpSession, Task<T>> operation,
        PaneLocation? successfulLocation = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = AcquireOperationLease();
        var result = await operation(_browse).ConfigureAwait(false);
        if (successfulLocation is not null) SetLocationCore(successfulLocation.Kind, successfulLocation.Path);
        return result;
    }

    public async Task<T> WithConsoleSessionAsync<T>(
        Func<ILftpSession, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = AcquireOperationLease();
        var console = await GetRoleAsync("console", cancellationToken).ConfigureAwait(false);
        return await operation(console).ConfigureAwait(false);
    }

    public async Task<T> WithEphemeralSessionAsync<T>(
        string role,
        Func<ILftpSession, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = AcquireOperationLease();
        await using var process = await _registry.StartEphemeralAsync(this, role, cancellationToken).ConfigureAwait(false);
        return await operation(process).ConfigureAwait(false);
    }

    public async Task ExecuteQueuedTransferAsync(
        TransferPlan plan,
        Guid jobId,
        Func<ILftpSession, CancellationToken, Task<long?>> preSubmit,
        Action<TransferProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preSubmit);
        using var lease = AcquireOperationLease();
        if (plan.SourceKind == TransferSourceKind.Directory)
            throw new InvalidOperationException("Directory transfers require the guarded foreground transfer session.");
        if (plan.RateLimitBytesPerSecond is not null ||
            plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip)
        {
            await using var isolated = await _registry.StartIsolatedTransferQueueAsync(this, plan.Id, cancellationToken).ConfigureAwait(false);
            await isolated.ExecuteAsync(plan, jobId, preSubmit, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        var queue = await GetTransferQueueAsync(cancellationToken).ConfigureAwait(false);
        await queue.ExecuteAsync(plan, jobId, preSubmit, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> WithTransferSessionAsync<T>(Func<ILftpSession, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = AcquireOperationLease();
        await _transferOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transfer = await GetRoleAsync("transfer", cancellationToken).ConfigureAwait(false);
            return await operation(transfer).ConfigureAwait(false);
        }
        finally
        {
            _transferOperationGate.Release();
        }
    }

    public async Task<T> WithValidationSessionAsync<T>(Func<ILftpSession, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = AcquireOperationLease();
        await _validationOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var validation = await GetRoleAsync("validation", cancellationToken).ConfigureAwait(false);
            return await operation(validation).ConfigureAwait(false);
        }
        finally
        {
            _validationOperationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task drain;
        TaskCompletionSource completion;
        lock (_lifecycleGate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            _closing = true;
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeOperations == 0)
            {
                drain = Task.CompletedTask;
            }
            else
            {
                _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                drain = _operationsDrained.Task;
            }
        }
        _ = CompleteDisposeAsync(drain, completion);
        return new(completion.Task);
    }

    internal IDisposable AcquireOperationLease()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _activeOperations++;
            return new OperationLease(this);
        }
    }

    private async Task CompleteDisposeAsync(Task drain, TaskCompletionSource completion)
    {
        try
        {
            await drain.ConfigureAwait(false);
            Credential = null;
            var disposalErrors = new List<Exception>();
            await TryDisposeChildAsync(_browse, disposalErrors).ConfigureAwait(false);
            await TryDisposeChildAsync(_transfer, disposalErrors).ConfigureAwait(false);
            await TryDisposeChildAsync(_console, disposalErrors).ConfigureAwait(false);
            await TryDisposeChildAsync(_validation, disposalErrors).ConfigureAwait(false);
            await TryDisposeChildAsync(_nativeTransferQueue, disposalErrors).ConfigureAwait(false);
            if (disposalErrors.Count == 1) throw disposalErrors[0];
            if (disposalErrors.Count > 1) throw new AggregateException("Multiple LFTP session children failed to stop cleanly.", disposalErrors);
            _roleGate.Dispose();
            _transferOperationGate.Dispose();
            _validationOperationGate.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (_lifecycleGate)
            {
                completion.TrySetException(exception);
                if (ReferenceEquals(_disposeCompletion, completion)) _disposeCompletion = null;
            }
        }
    }

    private static async Task TryDisposeChildAsync(IAsyncDisposable? child, List<Exception> errors)
    {
        if (child is null) return;
        try
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
        {
            errors.Add(exception);
        }
    }

    private void ReleaseOperationLease()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleGate)
        {
            if (_activeOperations <= 0) throw new InvalidOperationException("The workspace session operation lease count is inconsistent.");
            _activeOperations--;
            if (_closing && _activeOperations == 0) drained = _operationsDrained;
        }
        drained?.TrySetResult();
    }

    private sealed class OperationLease(WorkspaceSession owner) : IDisposable
    {
        private WorkspaceSession? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOperationLease();
    }

    private async Task<NativeTransferQueue> GetTransferQueueAsync(CancellationToken cancellationToken)
    {
        await _roleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_nativeTransferQueue?.IsAvailable == true) return _nativeTransferQueue;
            if (_nativeTransferQueue is not null) await _nativeTransferQueue.DisposeAsync().ConfigureAwait(false);
            _nativeTransferQueue = await _registry.StartTransferQueueAsync(this, cancellationToken).ConfigureAwait(false);
            return _nativeTransferQueue;
        }
        finally
        {
            _roleGate.Release();
        }
    }

    private async Task<ILftpSession> GetRoleAsync(string role, CancellationToken cancellationToken)
    {
        await _roleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = role switch
            {
                "transfer" => _transfer,
                "console" => _console,
                "validation" => _validation,
                _ => throw new ArgumentOutOfRangeException(nameof(role), "The persistent LFTP session role is unsupported."),
            };
            if (current?.IsRunning == true) return current;
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
            current = await _registry.StartAuxiliaryAsync(this, role, cancellationToken).ConfigureAwait(false);
            if (role == "transfer") _transfer = current;
            else if (role == "console") _console = current;
            else _validation = current;
            return current;
        }
        finally
        {
            _roleGate.Release();
        }
    }
}
