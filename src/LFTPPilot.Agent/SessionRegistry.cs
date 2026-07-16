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
    private readonly object _lifecycleGate = new();
    private TaskCompletionSource? _connectsDrained;
    private TaskCompletionSource? _disposeCompletion;
    private int _connectsInFlight;
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
        if (options.ConnectTimeout <= TimeSpan.Zero || options.BrowseTimeout <= TimeSpan.Zero || options.TransferTimeout <= TimeSpan.Zero ||
            options.MirrorPreviewTimeout <= TimeSpan.Zero || options.ConsoleTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Agent operation timeouts must be positive.", nameof(options));
        if (options.TransferQueueParallelism is < 1 or > 8)
            throw new ArgumentException("The per-profile LFTP transfer queue parallelism must be between 1 and 8.", nameof(options));
    }

    public IReadOnlyList<SessionSnapshot> GetSnapshots() => _sessions.Values
        .Select(static session => session.Snapshot)
        .OrderBy(static session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    internal WorkspaceSession Get(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(_closing, this);
        return _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException($"Session {sessionId} was not found.");
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

    public async Task<SessionSnapshot> ConnectAsync(ConnectionProfile profile, string? credential, CancellationToken cancellationToken = default)
    {
        ProfileValidator.ThrowIfInvalid(profile);
        using var reservation = AcquireConnectReservation();
        var id = Guid.NewGuid();
        var browse = await StartConnectedProcessAsync(id, profile, credential, "browse", cancellationToken).ConfigureAwait(false);
        var localPath = !string.IsNullOrWhiteSpace(profile.InitialLocalPath) && Directory.Exists(profile.InitialLocalPath)
            ? profile.InitialLocalPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var remotePath = string.IsNullOrWhiteSpace(profile.InitialRemotePath) ? "/" : profile.InitialRemotePath;
        var snapshot = new SessionSnapshot(id, profile.Id, profile.Name, true, new(PaneKind.Local, localPath), new(PaneKind.Remote, remotePath), DateTimeOffset.UtcNow);
        var session = new WorkspaceSession(this, profile, credential, browse, snapshot);
        bool accepted;
        lock (_lifecycleGate)
        {
            accepted = !_closing;
            if (!_sessions.TryAdd(id, session))
                accepted = false;
        }
        if (!accepted)
        {
            Exception? disposalError = null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
                TryRemoveSession(id, session);
            }
            catch (Exception exception)
            {
                disposalError = exception;
            }
            if (_closing)
            {
                var closed = new ObjectDisposedException(nameof(SessionRegistry), "The session registry closed before the connection could be registered.");
                if (disposalError is not null)
                    throw new AggregateException(closed.Message, closed, disposalError);
                throw closed;
            }
            if (disposalError is not null) throw disposalError;
            throw new InvalidOperationException("Could not register the connected session.");
        }
        return snapshot;
    }

    public async Task<bool> DisconnectAsync(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return false;
        await session.DisposeAsync().ConfigureAwait(false);
        return TryRemoveSession(sessionId, session);
    }

    public async Task DisconnectProfileAsync(Guid profileId)
    {
        var ids = _sessions.Where(pair => pair.Value.Profile.Id == profileId).Select(static pair => pair.Key).ToArray();
        var errors = new List<Exception>();
        foreach (var id in ids)
        {
            try
            {
                await DisconnectAsync(id).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
            {
                errors.Add(exception);
            }
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Multiple profile sessions failed to stop cleanly.", errors);
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
                profile.Authentication == AuthenticationKind.SshKey ? null : credential,
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
            profile.Authentication == AuthenticationKind.SshKey ? null : credential,
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

internal sealed class WorkspaceSession : IAsyncDisposable
{
    private readonly SessionRegistry _registry;
    private readonly object _lifecycleGate = new();
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

    public WorkspaceSession(SessionRegistry registry, ConnectionProfile profile, string? credential, ILftpSession browse, SessionSnapshot snapshot)
    {
        _registry = registry;
        Profile = profile;
        Credential = credential;
        _browse = browse;
        _snapshot = snapshot;
    }

    public ConnectionProfile Profile { get; }
    public string? Credential { get; private set; }
    public SessionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsActive => !_closing && Snapshot.IsConnected && _browse.IsRunning;

    public void SetLocation(PaneKind kind, string path)
    {
        using var lease = AcquireOperationLease();
        SetLocationCore(kind, path);
    }

    private void SetLocationCore(PaneKind kind, string path)
    {
        var snapshot = Snapshot;
        var updated = kind == PaneKind.Local
            ? snapshot with { LocalLocation = new(PaneKind.Local, path), UpdatedAt = DateTimeOffset.UtcNow }
            : snapshot with { RemoteLocation = new(PaneKind.Remote, path), UpdatedAt = DateTimeOffset.UtcNow };
        Volatile.Write(ref _snapshot, updated);
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
        Func<ILftpSession, CancellationToken, Task> preSubmit,
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
            await isolated.ExecuteAsync(plan, preSubmit, cancellationToken).ConfigureAwait(false);
            return;
        }
        var queue = await GetTransferQueueAsync(cancellationToken).ConfigureAwait(false);
        await queue.ExecuteAsync(plan, preSubmit, cancellationToken).ConfigureAwait(false);
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
