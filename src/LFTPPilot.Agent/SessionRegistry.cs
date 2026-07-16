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
    private const string CommonSettings = "set cmd:fail-exit no; set file:charset UTF-8; set cmd:cls-exact-time no; set cache:expire 2m; set net:idle 10m";
    private readonly ILftpProcessHost _processHost;
    private readonly ILftpRuntimeProvider _runtimeProvider;
    private readonly AgentWorkspaceOptions _options;
    private readonly ConcurrentDictionary<Guid, WorkspaceSession> _sessions = [];
    private bool _disposed;

    public SessionRegistry(ILftpProcessHost processHost, ILftpRuntimeProvider runtimeProvider, AgentWorkspaceOptions options)
    {
        _processHost = processHost;
        _runtimeProvider = runtimeProvider;
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new KeyNotFoundException($"Session {sessionId} was not found.");
    }

    internal WorkspaceSession GetActiveProfile(Guid profileId) => _sessions.Values
        .Where(session => session.Profile.Id == profileId && session.Snapshot.IsConnected && session.Browse.IsRunning)
        .OrderByDescending(session => session.Snapshot.UpdatedAt)
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"Profile {profileId} must be actively connected before starting a remote-to-remote transfer.");

    public async Task<SessionSnapshot> ConnectAsync(ConnectionProfile profile, string? credential, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ProfileValidator.ThrowIfInvalid(profile);
        var id = Guid.NewGuid();
        var browse = await StartConnectedProcessAsync(id, profile, credential, "browse", cancellationToken).ConfigureAwait(false);
        var localPath = !string.IsNullOrWhiteSpace(profile.InitialLocalPath) && Directory.Exists(profile.InitialLocalPath)
            ? profile.InitialLocalPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var remotePath = string.IsNullOrWhiteSpace(profile.InitialRemotePath) ? "/" : profile.InitialRemotePath;
        var snapshot = new SessionSnapshot(id, profile.Id, profile.Name, true, new(PaneKind.Local, localPath), new(PaneKind.Remote, remotePath), DateTimeOffset.UtcNow);
        var session = new WorkspaceSession(this, profile, credential, browse, snapshot);
        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Could not register the connected session.");
        }
        return snapshot;
    }

    public async Task<bool> DisconnectAsync(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async Task DisconnectProfileAsync(Guid profileId)
    {
        var ids = _sessions.Where(pair => pair.Value.Profile.Id == profileId).Select(static pair => pair.Key).ToArray();
        foreach (var id in ids) await DisconnectAsync(id).ConfigureAwait(false);
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

    internal async Task<ILftpSession> StartRemoteTransferAsync(
        WorkspaceSession source,
        WorkspaceSession destination,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (source.Profile.Id == destination.Profile.Id) throw new ArgumentException("Remote transfer profiles must be distinct.");
        var secrets = new[] { source.Credential, destination.Credential }
            .Where(static secret => !string.IsNullOrEmpty(secret)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        var process = await StartProcessAsync(
            operationId,
            Path.Combine("remote-transfers", operationId.ToString("N")),
            "remote-transfer",
            secrets,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await process.ExecuteAsync(CommonSettings, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(settings, "Remote transfer session initialization");
            await InitializeSlotAsync(process, "source", source.Profile, source.Credential, cancellationToken).ConfigureAwait(false);
            await InitializeSlotAsync(process, "destination", destination.Profile, destination.Credential, cancellationToken).ConfigureAwait(false);
            var selected = await process.ExecuteAsync("slot source", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(selected, "Remote transfer source selection");
            return process;
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        foreach (var session in sessions) await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<ILftpSession> StartConnectedProcessAsync(Guid sessionId, ConnectionProfile profile, string? credential, string role, CancellationToken cancellationToken)
    {
        var process = await StartProcessAsync(
            sessionId,
            profile.Id.ToString("N"),
            role,
            string.IsNullOrEmpty(credential) ? [] : [credential],
            cancellationToken).ConfigureAwait(false);
        try
        {
            var open = LftpCommandBuilder.BuildOpen(profile, profile.Authentication == AuthenticationKind.SshKey ? null : credential);
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
        var temporary = Path.Combine(_options.TemporaryRoot, "sessions", sessionId.ToString("N"), role);
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
        string slot,
        ConnectionProfile profile,
        string? credential,
        CancellationToken cancellationToken)
    {
        var selected = await process.ExecuteAsync($"slot {slot}", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(selected, $"Remote transfer {slot} slot selection");
        var open = LftpCommandBuilder.BuildOpen(profile, profile.Authentication == AuthenticationKind.SshKey ? null : credential);
        var initialized = await process.ExecuteAsync(open, _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(initialized, $"Remote transfer {slot} connection initialization");
        var verified = await process.ExecuteAsync("cls -lad .", _options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(verified, $"Remote transfer {slot} connection verification");
    }

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
    private readonly SemaphoreSlim _roleGate = new(1, 1);
    private readonly SemaphoreSlim _transferOperationGate = new(1, 1);
    private ILftpSession? _transfer;
    private ILftpSession? _console;
    private NativeTransferQueue? _nativeTransferQueue;
    private SessionSnapshot _snapshot;
    private bool _disposed;

    public WorkspaceSession(SessionRegistry registry, ConnectionProfile profile, string? credential, ILftpSession browse, SessionSnapshot snapshot)
    {
        _registry = registry;
        Profile = profile;
        Credential = credential;
        Browse = browse;
        _snapshot = snapshot;
    }

    public ConnectionProfile Profile { get; }
    public string? Credential { get; private set; }
    public ILftpSession Browse { get; }
    public SessionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void SetLocation(PaneKind kind, string path)
    {
        var snapshot = Snapshot;
        var updated = kind == PaneKind.Local
            ? snapshot with { LocalLocation = new(PaneKind.Local, path), UpdatedAt = DateTimeOffset.UtcNow }
            : snapshot with { RemoteLocation = new(PaneKind.Remote, path), UpdatedAt = DateTimeOffset.UtcNow };
        Volatile.Write(ref _snapshot, updated);
    }

    public Task<ILftpSession> GetConsoleAsync(CancellationToken cancellationToken) => GetRoleAsync("console", cancellationToken);
    public Task<ILftpSession> CreateEphemeralAsync(string role, CancellationToken cancellationToken) => _registry.StartEphemeralAsync(this, role, cancellationToken);

    public async Task ExecuteQueuedTransferAsync(TransferPlan plan, CancellationToken cancellationToken)
    {
        if (plan.RateLimitBytesPerSecond is not null ||
            plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip)
        {
            await using var isolated = await _registry.StartIsolatedTransferQueueAsync(this, plan.Id, cancellationToken).ConfigureAwait(false);
            await isolated.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
            return;
        }
        var queue = await GetTransferQueueAsync(cancellationToken).ConfigureAwait(false);
        await queue.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> WithTransferSessionAsync<T>(Func<ILftpSession, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Credential = null;
        await Browse.DisposeAsync().ConfigureAwait(false);
        if (_transfer is not null) await _transfer.DisposeAsync().ConfigureAwait(false);
        if (_console is not null) await _console.DisposeAsync().ConfigureAwait(false);
        if (_nativeTransferQueue is not null) await _nativeTransferQueue.DisposeAsync().ConfigureAwait(false);
        _roleGate.Dispose();
        _transferOperationGate.Dispose();
    }

    private async Task<NativeTransferQueue> GetTransferQueueAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _roleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = role == "transfer" ? _transfer : _console;
            if (current?.IsRunning == true) return current;
            if (current is not null) await current.DisposeAsync().ConfigureAwait(false);
            current = await _registry.StartAuxiliaryAsync(this, role, cancellationToken).ConfigureAwait(false);
            if (role == "transfer") _transfer = current; else _console = current;
            return current;
        }
        finally
        {
            _roleGate.Release();
        }
    }
}
