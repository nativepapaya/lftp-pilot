using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

public sealed class AgentWorkspaceService : IAsyncDisposable
{
    private const int MaximumDirectoryEntries = 10_000;
    private const int MaximumBrowsePageSize = 1_000;
    private const int MaximumBrowsePageEstimatedBytes = 512 * 1024;
    private const int MaximumBrowseSnapshots = 8;
    private const int MaximumRetryableTransfers = 10_000;
    private const int MaximumTransferSubmissions = 10_000;
    private const int MaximumMirrorPreviews = 1_024;
    private const int MaximumMirrorApprovals = 1_024;
    private const int MaximumRemoteTransferPlans = 1_024;
    private static readonly TimeSpan BrowseSnapshotLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MirrorApprovalReplayLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RemoteTransferPlanLifetime = TimeSpan.FromMinutes(15);
    private readonly IProfileStore _profileStore;
    private readonly ISecretStore _secretStore;
    private readonly SftpHostKeyManager _hostKeys;
    private readonly JobCoordinator _jobs;
    private readonly SessionRegistry _sessions;
    private readonly ILftpRuntimeProvider _runtimeProvider;
    private readonly IMirrorPlanner _mirrorPlanner;
    private readonly AgentWorkspaceOptions _options;
    private readonly Action<EngineEventKind, string, object?, Guid?, Guid?>? _publish;
    private readonly RunOnceScheduler? _scheduler;
    private readonly RemoteEditManager _remoteEdits;
    private readonly DurableJobStore? _stateStore;
    private readonly ConcurrentDictionary<Guid, StoredMirrorPreview> _previews = [];
    private readonly Dictionary<Guid, StoredMirrorApproval> _mirrorApprovals = [];
    private readonly ConcurrentDictionary<Guid, StoredBrowseSnapshot> _browseSnapshots = [];
    private readonly ConcurrentDictionary<Guid, TrackedJobCancellation> _jobCancellations = [];
    private readonly ConcurrentDictionary<Guid, ImmutableHashSet<Guid>> _activeJobProfileDependencies = [];
    private readonly Dictionary<Guid, StoredRemoteTransferPlan> _remoteTransferPlans = [];
    private readonly Dictionary<Guid, StoredTransferSubmission> _transferSubmissions = [];
    private readonly Lock _retryGate = new();
    private readonly Dictionary<Guid, TransferRetryContext> _transferRetries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _profileTrustGate = new(1, 1);
    private readonly SemaphoreSlim _remoteTransferGate = new(1, 1);
    private readonly SemaphoreSlim _sessionStateGate = new(1, 1);
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _operations = [];
    private readonly object _requestGate = new();
    private int _activeRequests;
    private TaskCompletionSource? _requestsDrained;
    private Task? _disposeTask;
    private bool _disposed;

    public AgentWorkspaceService(
        IProfileStore profileStore,
        ISecretStore secretStore,
        SftpHostKeyManager hostKeys,
        ILftpProcessHost processHost,
        ILftpRuntimeProvider runtimeProvider,
        JobCoordinator jobs,
        IMirrorPlanner mirrorPlanner,
        AgentWorkspaceOptions options,
        Action<EngineEventKind, string, object?, Guid?, Guid?>? publish = null,
        RunOnceScheduler? scheduler = null,
        DurableJobStore? stateStore = null)
    {
        _profileStore = profileStore;
        _secretStore = secretStore;
        _hostKeys = hostKeys;
        _jobs = jobs;
        _runtimeProvider = runtimeProvider;
        _mirrorPlanner = mirrorPlanner;
        _options = options;
        _publish = publish;
        _scheduler = scheduler;
        _stateStore = stateStore;
        _sessions = new(processHost, runtimeProvider, hostKeys, options);
        _remoteEdits = new(
            Path.Combine(options.CacheRoot, "remote-edits"),
            new LftpRemoteEditTransport(_sessions, options),
            publish: publish);
    }

    internal async Task RestoreSessionTabsAsync(
        IReadOnlyList<PersistedSessionTab> tabs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profiles = (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(static profile => profile.Id);
            _sessions.RestoreTabs(tabs, profiles);
            await PersistSessionTabsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<JsonElement> HandleAsync(string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        AdmitRequest();
        try
        {
            return method switch
            {
                WorkspaceMethods.Bootstrap => ToJson(await BootstrapAsync(cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.ProfileList => ToJson(await ListProfilesAsync(cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.ProfileSave => ToJson(await SaveProfileAsync(Required<ProfileSaveRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.ProfileDelete => ToJson(await DeleteProfileAsync(Required<ProfileDeleteRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.SftpHostKeyInspect => ToJson(await InspectSftpHostKeyAsync(Required<SftpHostKeyInspectRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.SftpHostKeyApprove => ToJson(await ApproveSftpHostKeyAsync(Required<SftpHostKeyApproveRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.SessionConnect => ToJson(await ConnectAsync(Required<SessionConnectRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.SessionDisconnect => ToJson(await DisconnectAsync(Required<SessionDisconnectRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.BrowseLocal => ToJson(await BrowseLocalAsync(Required<BrowseRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.BrowseRemote => ToJson(await BrowseRemoteAsync(Required<BrowseRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.FileCreateDirectory => ToJson(await CreateDirectoryAsync(Required<CreateDirectoryRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.FileMove => ToJson(await MoveEntryAsync(Required<MoveEntryRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.FileDelete => ToJson(await DeleteEntriesAsync(Required<DeleteEntriesRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.TransferEnqueue => ToJson(await EnqueueTransferAsync(Required<TransferEnqueueRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.JobRetry => ToJson(await RetryJobAsync(Required<JobRetryRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.MirrorPreview => ToJson(await PreviewMirrorAsync(Required<MirrorPreviewRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.MirrorApprove => ToJson(await ApproveMirrorAsync(Required<MirrorApproveRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.ConsoleExecute => ToJson(await ExecuteConsoleAsync(Required<ConsoleExecuteRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteTransferPlan => ToJson(await PlanRemoteTransferAsync(Required<RemoteTransferPlanRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteTransferEnqueue => ToJson(await EnqueueRemoteTransferAsync(Required<RemoteTransferEnqueueRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteEditStart => ToJson(await StartRemoteEditAsync(Required<RemoteEditStartRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteEditReview => ToJson(await ReviewRemoteEditAsync(Required<RemoteEditReviewRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteEditResolve => ToJson(await ResolveRemoteEditAsync(Required<RemoteEditResolveRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                WorkspaceMethods.RemoteEditComplete => ToJson(await CompleteRemoteEditAsync(Required<RemoteEditCompleteRequest>(arguments), cancellationToken).ConfigureAwait(false)),
                _ => throw new ArgumentException($"Unknown workspace method '{method}'."),
            };
        }
        finally
        {
            ReleaseRequest();
        }
    }

    public async Task<WorkspaceBootstrap> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        RuntimeStatus runtime;
        try
        {
            var descriptor = await _runtimeProvider.ResolveAsync(cancellationToken).ConfigureAwait(false);
            runtime = new(true, descriptor.IsAuthenticated, descriptor.Source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            runtime = new(false, false, "unavailable", exception.Message);
        }
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _remoteTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _sessionStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return new(AgentProtocol.CurrentVersion, runtime,
                        (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToImmutableArray(),
                        _sessions.GetSnapshots().ToImmutableArray(),
                        _jobs.GetJobs().ToImmutableArray(),
                        _remoteEdits.GetSnapshots().ToImmutableArray());
                }
                finally
                {
                    _sessionStateGate.Release();
                }
            }
            finally
            {
                _remoteTransferGate.Release();
            }
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public Task<IReadOnlyList<ConnectionProfile>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
        _profileStore.GetAllAsync(cancellationToken);

    public async Task<ConnectionProfile> SaveProfileAsync(ProfileSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProfileValidator.ThrowIfInvalid(request.Profile);
        ValidateCredential(request.Credential);
        if (!string.IsNullOrEmpty(request.Credential) && request.Profile.Authentication != AuthenticationKind.Password)
            throw new NotSupportedException("Only password credentials can currently be persisted. Ask-on-connect credentials remain ephemeral and SSH key passphrases are not yet supported.");
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(profile => profile.Id == request.Profile.Id);
            var hostKeyBindingChanged = previous is not null && HostKeyBindingChanged(previous, request.Profile);
            var connectionIdentityChanged = previous is not null &&
                ConnectionIdentity.FromProfile(previous) != ConnectionIdentity.FromProfile(request.Profile);
            var credentialSupplied = !string.IsNullOrEmpty(request.Credential);
            if (credentialSupplied && request.Profile.Protocol == ConnectionProtocol.Sftp)
            {
                if (previous is null || hostKeyBindingChanged)
                {
                    throw new InvalidOperationException(
                        "Save the SFTP profile metadata without a credential, inspect and approve its host key, then save the credential.");
                }
            }
            if (credentialSupplied && !ProfileIsQuiescent(request.Profile.Id))
            {
                throw new InvalidOperationException(
                    "Disconnect every session and finish or cancel all jobs, schedules, and remote edits for this profile before saving a credential.");
            }
            if (previous is not null && connectionIdentityChanged && !ProfileIsQuiescent(previous.Id))
            {
                throw new InvalidOperationException(
                    "Disconnect every session and finish or cancel all jobs, schedules, and remote edits for this profile before changing its endpoint, protocol, username, authentication mode, or SSH key.");
            }
            if (credentialSupplied && request.Profile.Protocol == ConnectionProtocol.Sftp)
                _ = await _hostKeys.RequireTrustedAsync(request.Profile, cancellationToken).ConfigureAwait(false);
            if (connectionIdentityChanged)
                await RemoveProfileSessionTabsAsync(request.Profile.Id, cancellationToken).ConfigureAwait(false);
            var identityChanged = previous is not null && !SecretBindingFor(previous).Equals(SecretBindingFor(request.Profile));
            if (identityChanged) await _secretStore.DeleteAsync(request.Profile.Id, cancellationToken).ConfigureAwait(false);
            if (hostKeyBindingChanged)
                await _hostKeys.DeleteAsync(request.Profile.Id, cancellationToken).ConfigureAwait(false);
            // Invalidate the old identity before publishing replacement metadata.
            // If persistence fails, losing old trust/credentials is safer than
            // allowing a later identity cycle to resurrect them.
            await _profileStore.SaveAsync(request.Profile, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(request.Credential))
            {
                await _secretStore.SaveAsync(new(SecretBindingFor(request.Profile), request.Credential), cancellationToken).ConfigureAwait(false);
            }
            _publish?.Invoke(EngineEventKind.Session, "profile.saved", request.Profile, null, null);
            return request.Profile;
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<bool> DeleteProfileAsync(ProfileDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile identifier is required.", nameof(request));
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfProfileHasActiveRemoteEdits(request.ProfileId);
            ThrowIfProfileHasDependentJobs(request.ProfileId);
            await RemoveProfileSessionTabsAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            await _secretStore.DeleteAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            await _hostKeys.DeleteAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            // Revoke security state before deleting discoverable profile metadata.
            // A metadata failure may leave the profile visible, but it must not
            // retain credentials or trust that could later be resurrected.
            await _profileStore.DeleteAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            _publish?.Invoke(EngineEventKind.Session, "profile.deleted", request.ProfileId, null, null);
            return true;
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<SessionSnapshot> ConnectAsync(SessionConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCredential(request.EphemeralCredential);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await FindExpectedProfileAsync(request.ExpectedIdentity, cancellationToken).ConfigureAwait(false);
            if (request.ExistingSessionId == Guid.Empty)
                throw new ArgumentException("An existing session identifier cannot be empty.", nameof(request));
            if (request.ExistingSessionId is { } existingSessionId &&
                _sessions.GetActiveSnapshot(existingSessionId, request.ExpectedIdentity) is { } activeSnapshot)
            {
                return activeSnapshot;
            }
            if (profile.Protocol == ConnectionProtocol.Sftp)
            {
                // Keep the no-active-work replacement decision atomic with
                // creation of a newly registered SFTP session. This also
                // preserves the stronger ordering that trust precedes
                // credential lookup.
                _ = await _hostKeys.RequireTrustedAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            return await ConnectTrustedProfileAsync(
                profile,
                request.EphemeralCredential,
                cancellationToken,
                request.ExistingSessionId).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(
        SftpHostKeyInspectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await FindExpectedProfileAsync(request.ExpectedIdentity, cancellationToken).ConfigureAwait(false);
            if (profile.Protocol != ConnectionProtocol.Sftp)
                throw new NotSupportedException("Host-key review is available only for SFTP profiles.");
            return await _hostKeys.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(
        SftpHostKeyApproveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile identifier is required.", nameof(request));
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await FindProfileAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
            if (profile.Protocol != ConnectionProtocol.Sftp)
                throw new NotSupportedException("Host-key review is available only for SFTP profiles.");
            return await _hostKeys.ApproveAsync(
                profile,
                request,
                ProfileIsQuiescent(profile.Id),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private async Task<SessionSnapshot> ConnectTrustedProfileAsync(
        ConnectionProfile profile,
        string? ephemeralCredential,
        CancellationToken cancellationToken,
        Guid? existingSessionId = null)
    {
        var credential = await ResolveCredentialAsync(profile, ephemeralCredential, cancellationToken).ConfigureAwait(false);
        var connection = await _sessions.ConnectAsync(
            profile,
            credential,
            existingSessionId,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistSessionTabsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception persistenceException)
        {
            try { await _sessions.RollbackConnectionAsync(connection).ConfigureAwait(false); }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "The session-tab commit failed and its uncommitted LFTP process did not stop cleanly.",
                    persistenceException,
                    cleanupException);
            }
            ExceptionDispatchInfo.Capture(persistenceException).Throw();
            throw;
        }
        if (connection.IsNewRegistration)
            _publish?.Invoke(EngineEventKind.Session, "session.connected", connection.Snapshot, null, connection.Snapshot.SessionId);
        return connection.Snapshot;
    }

    public async Task<bool> DisconnectAsync(
        SessionDisconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(request));
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.GetClosing(request.SessionId) is { } closing)
            {
                await _sessions.DisposeDetachedAsync(closing).ConfigureAwait(false);
            }
            var preparation = _sessions.PrepareDisconnect(request.SessionId)
                ?? (_sessions.GetClosing(request.SessionId) is null
                    ? null
                    : throw new InvalidOperationException("The session is still closing."));
            if (preparation is null) return true;
            if (preparation.ActiveSession is not null && _remoteEdits.HasActiveSession(request.SessionId))
                throw new InvalidOperationException("Finish or cancel every active remote edit for this session before disconnecting it.");
            ThrowIfProfileHasDependentJobs(preparation.PersistedTab.ProfileId);
            WorkspaceSession? detached;
            await _sessionStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PersistSessionTabsCoreAsync(
                    _sessions.GetPersistedTabs().Where(tab => tab.SessionId != request.SessionId),
                    cancellationToken).ConfigureAwait(false);
                detached = _sessions.DetachDisconnect(preparation);
            }
            finally
            {
                _sessionStateGate.Release();
            }
            _publish?.Invoke(EngineEventKind.Session, "session.disconnected", request.SessionId, null, request.SessionId);
            if (detached is not null) await _sessions.DisposeDetachedAsync(detached).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private void ThrowIfProfileHasDependentJobs(Guid profileId)
    {
        if (ProfileHasDependentWork(profileId))
        {
            throw new InvalidOperationException(
                "Cancel scheduled or active jobs for this profile before disconnecting its session or deleting the profile.");
        }
    }

    private async Task RemoveProfileSessionTabsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        if (_sessions.HasClosingProfile(profileId))
            throw new InvalidOperationException("A profile session is still closing. Retry cleanup before changing or deleting the profile.");
        SessionDisconnectPreparation[] preparations;
        WorkspaceSession[] detached;
        await _sessionStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            preparations = _sessions.PrepareDisconnectProfile(profileId).ToArray();
            if (preparations.Length == 0) return;
            var removedIds = preparations.Select(static item => item.PersistedTab.SessionId).ToHashSet();
            await PersistSessionTabsCoreAsync(
                _sessions.GetPersistedTabs().Where(tab => !removedIds.Contains(tab.SessionId)),
                cancellationToken).ConfigureAwait(false);
            detached = preparations.Select(_sessions.DetachDisconnect).OfType<WorkspaceSession>().ToArray();
        }
        finally
        {
            _sessionStateGate.Release();
        }

        foreach (var preparation in preparations)
            _publish?.Invoke(EngineEventKind.Session, "session.disconnected", preparation.PersistedTab.SessionId, null, preparation.PersistedTab.SessionId);

        var errors = new List<Exception>();
        foreach (var session in detached)
        {
            try { await _sessions.DisposeDetachedAsync(session).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
            {
                errors.Add(exception);
            }
        }
        if (errors.Count == 1) throw errors[0];
        if (errors.Count > 1) throw new AggregateException("Multiple profile sessions failed to stop cleanly.", errors);
    }

    private async Task PersistSessionTabsAsync(CancellationToken cancellationToken)
    {
        await _sessionStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistSessionTabsCoreAsync(_sessions.GetPersistedTabs(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionStateGate.Release();
        }
    }

    private async Task CommitSessionLocationAsync(
        WorkspaceSession session,
        PaneKind kind,
        string path,
        CancellationToken cancellationToken)
    {
        await _sessionStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = session.Snapshot;
            session.SetLocation(kind, path);
            try
            {
                await PersistSessionTabsCoreAsync(_sessions.GetPersistedTabs(), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                session.RestoreSnapshot(previous);
                throw;
            }
        }
        finally
        {
            _sessionStateGate.Release();
        }
    }

    private Task PersistSessionTabsCoreAsync(
        IEnumerable<PersistedSessionTab> sessionTabs,
        CancellationToken cancellationToken)
    {
        var captured = sessionTabs.ToArray();
        return _stateStore is null
            ? Task.CompletedTask
            : _stateStore.SaveSessionTabsAsync(captured, cancellationToken);
    }

    private void ThrowIfProfileHasActiveRemoteEdits(Guid profileId)
    {
        if (_sessions.GetSnapshots().Any(session =>
            session.ProfileId == profileId && _remoteEdits.HasActiveSession(session.SessionId)))
        {
            throw new InvalidOperationException(
                "Finish or cancel every active remote edit for this profile before deleting it.");
        }
    }

    private bool ProfileIsQuiescent(Guid profileId)
    {
        if (_sessions.HasClosingProfile(profileId)) return false;
        if (_sessions.GetSnapshots().Any(session => session.ProfileId == profileId && session.IsConnected)) return false;
        if (ProfileHasDependentWork(profileId)) return false;
        return !_sessions.GetSnapshots().Any(session =>
            session.ProfileId == profileId && _remoteEdits.HasActiveSession(session.SessionId));
    }

    private bool ProfileHasDependentWork(Guid profileId)
    {
        if (_activeJobProfileDependencies.Values.Any(profileIds => profileIds.Contains(profileId))) return true;
        return _jobs.GetJobs().Any(job =>
            job.ProfileId == profileId &&
            (job.State is JobState.Scheduled or JobState.Queued or JobState.Running or JobState.Paused ||
             _jobCancellations.ContainsKey(job.Id) ||
             _scheduler?.IsRegistered(job.Id) == true));
    }

    public async Task<BrowseResult> BrowseLocalAsync(BrowseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateBrowseRequest(request);
        if (!Path.IsPathFullyQualified(request.Path)) throw new ArgumentException("The local browse path must be fully qualified.", nameof(request));
        var localPath = Path.GetFullPath(request.Path);
        var session = request.SessionId is { } sessionId ? _sessions.Get(sessionId) : null;
        if (request.ContinuationToken is not null)
        {
            var continuation = ContinueBrowse(request, PaneKind.Local, localPath);
            if (session is not null && continuation.ContinuationToken is null)
                await CommitSessionLocationAsync(session, PaneKind.Local, localPath, cancellationToken).ConfigureAwait(false);
            return continuation;
        }
        if (!Directory.Exists(localPath)) throw new DirectoryNotFoundException(localPath);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(localPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumDirectoryEntries) throw new InvalidDataException("The local directory contains too many entries to display safely.");
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var isLink = (attributes & FileAttributes.ReparsePoint) != 0;
            var kind = isLink ? EntryKind.SymbolicLink : isDirectory ? EntryKind.Directory : EntryKind.File;
            var info = new FileInfo(path);
            entries.Add(new(info.Name, info.FullName, kind, isDirectory ? null : info.Length, info.LastWriteTimeUtc,
                LinkTarget: isLink ? info.LinkTarget : null));
        }
        var ordered = entries.OrderBy(static entry => entry.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
        var page = CreateBrowsePage(new(PaneKind.Local, localPath), request.SessionId, ordered, request.PageSize);
        if (session is not null && page.ContinuationToken is null)
            await CommitSessionLocationAsync(session, PaneKind.Local, localPath, cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<BrowseResult> BrowseRemoteAsync(BrowseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateBrowseRequest(request);
        if (request.SessionId is not { } sessionId || sessionId == Guid.Empty) throw new ArgumentException("A connected session is required.", nameof(request));
        ValidateRemotePath(request.Path, nameof(request));
        var session = _sessions.Get(sessionId);
        if (request.ContinuationToken is not null)
        {
            var continuation = ContinueBrowse(request, PaneKind.Remote, request.Path);
            if (continuation.ContinuationToken is null)
                await CommitSessionLocationAsync(session, PaneKind.Remote, request.Path, cancellationToken).ConfigureAwait(false);
            return continuation;
        }
        var page = await session.WithBrowseSessionAsync(async browse =>
        {
            var result = await browse.ExecuteAsync(
                LftpCommandBuilder.BuildList(request.Path, request.Fresh),
                _options.BrowseTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Remote listing");
            var parsed = LftpOutputParser.ParseLongListing(result.Lines.Select(static line => line.Line), request.Path);
            if (parsed.Length == 0 && result.Lines.Any(static line =>
                !string.IsNullOrWhiteSpace(line.Line) && !line.Line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)))
            {
                var fallback = await browse.ExecuteAsync(
                    LftpCommandBuilder.BuildNameList(request.Path, request.Fresh),
                    _options.BrowseTimeout,
                    cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(fallback, "Remote listing fallback");
                parsed = LftpOutputParser.ParseClassifiedNames(fallback.Lines.Select(static line => line.Line), request.Path);
            }
            if (parsed.Length > MaximumDirectoryEntries)
                throw new InvalidDataException($"The remote directory contains more than {MaximumDirectoryEntries} entries. Narrow the directory before browsing it.");
            var ordered = parsed.OrderBy(static entry => entry.Kind == EntryKind.Directory ? 0 : 1)
                .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
            return CreateBrowsePage(new(PaneKind.Remote, request.Path), sessionId, ordered, request.PageSize);
        }).ConfigureAwait(false);
        if (page.ContinuationToken is null)
            await CommitSessionLocationAsync(session, PaneKind.Remote, request.Path, cancellationToken).ConfigureAwait(false);
        return page;
    }

    public async Task<FileMutationResult> CreateDirectoryAsync(CreateDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePaneKind(request.Kind);
        if (request.Kind == PaneKind.Local)
        {
            var path = ValidateLocalMutationPath(request.Path, allowRoot: false);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A file-system entry already exists at the requested directory path.");
            Directory.CreateDirectory(path);
            InvalidateBrowseSnapshots(PaneKind.Local, request.SessionId);
            PublishDirectoryChange(PaneKind.Local, request.SessionId, [path]);
            return new(PaneKind.Local, [path]);
        }

        var session = GetRemoteMutationSession(request.SessionId);
        var remotePath = ValidateRemoteMutationPath(request.Path, allowRoot: false);
        await session.WithBrowseSessionAsync(async browse =>
        {
            if (await TryStatRemoteAsync(browse, remotePath, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("A remote entry already exists at the requested directory path.");
            var result = await browse.ExecuteAsync(
                LftpCommandBuilder.BuildCreateDirectory(remotePath),
                _options.BrowseTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Remote directory creation");
            return true;
        }).ConfigureAwait(false);
        InvalidateBrowseSnapshots(PaneKind.Remote, request.SessionId);
        PublishDirectoryChange(PaneKind.Remote, request.SessionId, [remotePath]);
        return new(PaneKind.Remote, [remotePath]);
    }

    public async Task<FileMutationResult> MoveEntryAsync(MoveEntryRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePaneKind(request.Kind);
        if (request.Kind == PaneKind.Local)
        {
            var source = ValidateLocalMutationPath(request.SourcePath, allowRoot: false);
            var destination = ValidateLocalMutationPath(request.DestinationPath, allowRoot: false);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Source and destination paths must differ.", nameof(request));
            var attributes = File.GetAttributes(source);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The move destination already exists.");
            if ((attributes & FileAttributes.Directory) != 0) Directory.Move(source, destination);
            else File.Move(source, destination);
            InvalidateBrowseSnapshots(PaneKind.Local, request.SessionId);
            PublishDirectoryChange(PaneKind.Local, request.SessionId, [source, destination]);
            return new(PaneKind.Local, [source, destination]);
        }

        var session = GetRemoteMutationSession(request.SessionId);
        var remoteSource = ValidateRemoteMutationPath(request.SourcePath, allowRoot: false);
        var remoteDestination = ValidateRemoteMutationPath(request.DestinationPath, allowRoot: false);
        if (string.Equals(remoteSource, remoteDestination, StringComparison.Ordinal)) throw new ArgumentException("Source and destination paths must differ.", nameof(request));
        await session.WithBrowseSessionAsync(async browse =>
        {
            _ = await TryStatRemoteAsync(browse, remoteSource, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The remote move source was not found.", remoteSource);
            if (await TryStatRemoteAsync(browse, remoteDestination, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("The remote move destination already exists.");
            var result = await browse.ExecuteAsync(
                LftpCommandBuilder.BuildMove(remoteSource, remoteDestination),
                _options.BrowseTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Remote move");
            return true;
        }).ConfigureAwait(false);
        InvalidateBrowseSnapshots(PaneKind.Remote, request.SessionId);
        PublishDirectoryChange(PaneKind.Remote, request.SessionId, [remoteSource, remoteDestination]);
        return new(PaneKind.Remote, [remoteSource, remoteDestination]);
    }

    public async Task<FileMutationResult> DeleteEntriesAsync(DeleteEntriesRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePaneKind(request.Kind);
        if (!request.Confirmed) throw new InvalidOperationException("File deletion requires explicit confirmation.");
        if (request.EffectivePaths.Length is < 1 or > 100) throw new ArgumentException("Delete between 1 and 100 entries at a time.", nameof(request));

        if (request.Kind == PaneKind.Local)
        {
            var paths = request.EffectivePaths.Select(path => ValidateLocalMutationPath(path, allowRoot: false))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length != request.EffectivePaths.Length) throw new ArgumentException("The delete request repeats a local path.", nameof(request));
            var entries = paths.Select(path => (Path: path, Attributes: File.GetAttributes(path))).ToArray();
            var localAffected = new List<string>();
            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                        Directory.Delete(entry.Path, recursive: request.Recursive && !isLink);
                    }
                    else
                    {
                        File.Delete(entry.Path);
                    }
                    localAffected.Add(entry.Path);
                }
            }
            finally
            {
                if (localAffected.Count != 0)
                {
                    InvalidateBrowseSnapshots(PaneKind.Local, request.SessionId);
                    PublishDirectoryChange(PaneKind.Local, request.SessionId, localAffected);
                }
            }
            return new(PaneKind.Local, localAffected.ToImmutableArray());
        }

        var remoteSession = GetRemoteMutationSession(request.SessionId);
        var remotePaths = request.EffectivePaths.Select(path => ValidateRemoteMutationPath(path, allowRoot: false))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (remotePaths.Length != request.EffectivePaths.Length) throw new ArgumentException("The delete request repeats a remote path.", nameof(request));
        var affected = new List<string>();
        try
        {
            await remoteSession.WithBrowseSessionAsync(async browse =>
            {
                var remoteEntries = new List<(string Path, FileEntry Entry)>();
                foreach (var path in remotePaths)
                {
                    var entry = await TryStatRemoteAsync(browse, path, cancellationToken).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("A remote delete target was not found.", path);
                    remoteEntries.Add((path, entry));
                }
                foreach (var target in remoteEntries)
                {
                    var command = LftpCommandBuilder.BuildDelete(target.Path, target.Entry.IsDirectory, request.Recursive);
                    var result = await browse.ExecuteAsync(command, _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
                    SessionRegistry.ThrowIfFailed(result, "Remote deletion");
                    affected.Add(target.Path);
                }
                return true;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (affected.Count != 0)
            {
                InvalidateBrowseSnapshots(PaneKind.Remote, request.SessionId);
                PublishDirectoryChange(PaneKind.Remote, request.SessionId, affected);
            }
        }
        return new(PaneKind.Remote, affected.ToImmutableArray());
    }

    public async Task<TransferEnqueueResult> EnqueueTransferAsync(TransferEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnqueueTransferUnderProfileGateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private async Task<TransferEnqueueResult> EnqueueTransferUnderProfileGateAsync(
        TransferEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        var plan = CanonicalizeTransferPlan(request.Plan) with
        {
            RunAt = request.Plan.RunAt?.ToUniversalTime(),
        };
        PlanValidator.Validate(plan);
        ValidateTransferPaths(plan);
        var canonicalRequest = new TransferEnqueueRequest(request.SessionId, plan);
        if (_transferSubmissions.TryGetValue(plan.Id, out var priorSubmission))
        {
            if (priorSubmission.Request != canonicalRequest)
                throw new ArgumentException("The transfer identifier is already bound to different session or plan details. Create a new transfer plan.", nameof(request));
            if (priorSubmission.Result is { } priorResult)
                return priorResult with { Job = FindJob(plan.Id) ?? priorResult.Job };
            if (priorSubmission.Failure is { } priorFailure)
                priorFailure.Throw();
            throw new InvalidOperationException("The transfer submission is still being resolved. Refresh job state before submitting it again.");
        }
        if (FindJob(plan.Id) is not null)
            throw new InvalidOperationException("This transfer plan was already consumed by this or a previous Agent process. Refresh job state before creating a fresh plan.");

        MakeRoomForTransferSubmission();
        var registration = new StoredTransferSubmission(canonicalRequest, DateTimeOffset.UtcNow);
        _transferSubmissions.Add(plan.Id, registration);
        try
        {
            var result = await EnqueueFirstTransferSubmissionAsync(canonicalRequest, cancellationToken).ConfigureAwait(false);
            _transferSubmissions[plan.Id] = registration with { Result = result };
            return result;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            _transferSubmissions[plan.Id] = registration with { Failure = ExceptionDispatchInfo.Capture(exception) };
            throw;
        }
    }

    private async Task<TransferEnqueueResult> EnqueueFirstTransferSubmissionAsync(
        TransferEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var now = _scheduler?.UtcNow ?? DateTimeOffset.UtcNow;
        var initialState = plan.RunAt is null ? JobState.Queued : JobState.Scheduled;
        WorkspaceSession session;
        bool retryAvailable;
        try
        {
            session = _sessions.Get(request.SessionId);
            if (session.Profile.Id != plan.ProfileId)
                throw new ArgumentException("The transfer profile does not match the connected session.", nameof(request));
            if (plan.RunAt is { } runAt)
            {
                if (_scheduler is null) throw new InvalidOperationException("Run-once transfers require the Agent scheduler.");
                if (runAt <= now) throw new ArgumentException("A run-once transfer requires a future run time.", nameof(request));
            }
            retryAvailable = TryRememberTransfer(plan.Id, session, plan);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var rejected = _jobs.Enqueue(new(
                plan.Id,
                JobKind.Transfer,
                plan.ProfileId,
                JobSnapshotPolicy.CanonicalizeDerivedDisplayName(
                    $"{Path.GetFileName(plan.SourcePath)} -> {Path.GetFileName(plan.DestinationPath)}",
                    "Transfer"),
                initialState,
                now,
                now,
                RunAt: plan.RunAt,
                Status: "Transfer submission rejected before validation."));
            rejected = _jobs.Transition(
                rejected.Id,
                JobState.Failed,
                "Transfer submission rejected.",
                JobSnapshotPolicy.CanonicalizeDerivedError("transfer-submission-rejected", exception.Message));
            return new(rejected);
        }

        var job = _jobs.Enqueue(new(
            plan.Id,
            JobKind.Transfer,
            session.Profile.Id,
            JobSnapshotPolicy.CanonicalizeDerivedDisplayName(
                $"{Path.GetFileName(plan.SourcePath)} -> {Path.GetFileName(plan.DestinationPath)}",
                "Transfer"),
            initialState,
            now,
            now,
            RunAt: plan.RunAt,
            Status: initialState == JobState.Scheduled
                ? "Validating before the selected run-once time is registered."
                : "Validating transfer submission.",
            RetryAvailable: retryAvailable));
        try
        {
            await RevalidateTransferAsync(session, plan, cancellationToken).ConfigureAwait(false);
            if (plan.RunAt is { } runAt)
            {
                await _scheduler!.ScheduleAsync(
                    job,
                    token => RunScheduledTransferAsync(session, plan, job.Id, token),
                    cancellationToken).ConfigureAwait(false);
                return new(FindJob(job.Id) ?? job);
            }

            var shouldSkip = plan.Mode == TransferMode.Skip &&
                await DestinationExistsAsync(session, plan, cancellationToken).ConfigureAwait(false);
            if (shouldSkip)
            {
                _jobs.Transition(job.Id, JobState.Running, "Checking destination");
                job = _jobs.Transition(job.Id, JobState.Completed, "Skipped because the destination already exists");
                ForgetTransferRetry(job.Id);
                return new(job);
            }
            TrackJob(job.Id, token => RunTransferAsync(session, plan, job.Id, token));
            return new(job);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            var current = FindJob(job.Id) ?? job;
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                _jobs.TryCancel(job.Id, "Transfer submission cancelled.");
                ForgetTransferRetry(job.Id);
            }
            else if (current.State is JobState.Queued or JobState.Scheduled)
            {
                _jobs.Transition(
                    job.Id,
                    JobState.Failed,
                    "Transfer submission failed.",
                    JobSnapshotPolicy.CanonicalizeDerivedError("transfer-submission-failed", exception.Message));
            }
            if (FindJob(job.Id)?.State is JobState.Missed or JobState.Cancelled)
                ForgetTransferRetry(job.Id);
            return new(FindJob(job.Id) ?? current);
        }
    }

    public async Task<JobRetryResult> RetryJobAsync(JobRetryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.JobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(request));
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RetryJobUnderProfileGateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private async Task<JobRetryResult> RetryJobUnderProfileGateAsync(
        JobRetryRequest request,
        CancellationToken cancellationToken)
    {
        var current = _jobs.GetJobs().FirstOrDefault(job => job.Id == request.JobId)
            ?? throw new KeyNotFoundException($"Job {request.JobId} was not found.");
        if (current.Kind != JobKind.Transfer)
            throw new NotSupportedException("Only failed file or directory transfers can currently be retried. Mirrors require a fresh preview, and remote-to-remote transfers require a new route review.");
        if (current.State != JobState.Failed) throw new InvalidOperationException("Only a failed transfer can be retried.");
        if (!current.RetryAvailable || !TryGetTransferRetry(request.JobId, out var retryContext))
            throw new InvalidOperationException("The Agent no longer has the validated transfer details needed to retry this job. Queue a new transfer instead.");

        await WaitForPriorAttemptCleanupAsync(request.JobId, cancellationToken).ConfigureAwait(false);
        var retryPlan = retryContext.Plan with { Id = Guid.NewGuid(), RunAt = null };
        PlanValidator.Validate(retryPlan);
        var session = _sessions.GetActive(retryContext.SessionId);
        if (session.Profile.Id != retryPlan.ProfileId)
            throw new InvalidOperationException("The originating transfer session no longer matches the reviewed profile. Queue a new transfer instead.");
        await RevalidateTransferAsync(session, retryPlan, cancellationToken).ConfigureAwait(false);
        var shouldSkip = retryPlan.Mode == TransferMode.Skip &&
            await DestinationExistsAsync(session, retryPlan, cancellationToken).ConfigureAwait(false);

        var retried = _jobs.Retry(request.JobId, "Retry queued after fresh path and session validation.");
        if (shouldSkip)
        {
            _jobs.Transition(request.JobId, JobState.Running, "Checking destination before retry.");
            retried = _jobs.Transition(request.JobId, JobState.Completed, "Retry skipped because the destination now exists.");
            ForgetTransferRetry(request.JobId);
            return new(retried);
        }

        try
        {
            TrackJob(request.JobId, token => RunTransferAsync(session, retryPlan, request.JobId, token));
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            TryFailJob(request.JobId, exception);
            retried = FindJob(request.JobId) ?? retried;
        }
        return new(retried);
    }

    public async Task<MirrorPreview> PreviewMirrorAsync(MirrorPreviewRequest request, CancellationToken cancellationToken = default)
    {
        PlanValidator.Validate(request.Definition);
        ValidateMirrorLocalRoot(request.Definition);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _sessions.Get(request.SessionId);
            if (session.Profile.Id != request.Definition.ProfileId) throw new ArgumentException("The mirror profile does not match the connected session.", nameof(request));
            var preview = await session.WithEphemeralSessionAsync("mirror-preview", async previewSession =>
            {
                await ValidateMirrorRemoteRootAsync(previewSession, request.Definition, cancellationToken).ConfigureAwait(false);
                var result = await previewSession.ExecuteAsync(
                    LftpCommandBuilder.BuildMirror(request.Definition, dryRun: true),
                    _options.MirrorPreviewTimeout,
                    cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(result, "Mirror preview");
                return _mirrorPlanner.CreatePreview(request.Definition, result.Lines.Select(static line => line.Line));
            }, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            PurgeExpiredMirrorRegistrations(now);
            MakeRoomForMirrorPreview();
            if (_mirrorApprovals.ContainsKey(preview.Id) || FindJob(preview.Id) is not null ||
                !_previews.TryAdd(preview.Id, new(request.SessionId, request.Definition, preview)))
            {
                throw new InvalidOperationException("The generated mirror preview identifier is already in use. Generate a fresh preview.");
            }
            return preview;
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public async Task<MirrorApproveResult> ApproveMirrorAsync(MirrorApproveRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ApproveMirrorUnderProfileGate(request);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private MirrorApproveResult ApproveMirrorUnderProfileGate(MirrorApproveRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        PurgeExpiredMirrorRegistrations(now);
        if (_mirrorApprovals.TryGetValue(request.PreviewId, out var priorApproval))
            return ReplayMirrorApproval(request, priorApproval);
        if (FindJob(request.PreviewId) is not null)
        {
            throw new InvalidOperationException(
                "This mirror preview was already consumed by this or a previous Agent process. Refresh job state before creating a fresh preview.");
        }
        if (!_previews.TryGetValue(request.PreviewId, out var stored) || stored.SessionId != request.SessionId)
            throw new InvalidOperationException("The mirror preview was not found. Generate a fresh preview.");
        var reviewFingerprint = MirrorPlanner.ReviewFingerprint(stored.Preview);
        if (!IsValidReviewFingerprint(request.ReviewFingerprint) ||
            !string.Equals(request.ReviewFingerprint, reviewFingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The reviewed mirror preview actions or metadata do not match the Agent-held preview. Generate and review a fresh preview.",
                nameof(request));
        }
        var requiresDeletionReview = stored.Definition.DeleteExtraneous || stored.Preview.ContainsDeletions;
        if (requiresDeletionReview && !request.DeletionsApproved)
            throw new InvalidOperationException("A mirror preview containing deletion actions requires separate explicit deletion approval.");
        var command = _mirrorPlanner.BuildExecutionCommand(request.Definition, stored.Preview, request.ApprovalToken);
        var session = _sessions.Get(request.SessionId);
        if (session.Profile.Id != request.Definition.ProfileId) throw new ArgumentException("The mirror profile does not match the connected session.", nameof(request));
        if (!_previews.TryRemove(request.PreviewId, out _)) throw new InvalidOperationException("The mirror preview was already approved.");

        // Consumption is terminal before any durable or process-side work. A
        // retry can only retrieve the result of this exact approval; it can
        // never execute the reviewed preview a second time.
        MakeRoomForMirrorApproval();
        var registration = new StoredMirrorApproval(
            request.SessionId,
            MirrorPlanner.Fingerprint(request.Definition),
            reviewFingerprint,
            HashMirrorApprovalToken(request.ApprovalToken),
            request.DeletionsApproved,
            now,
            now + MirrorApprovalReplayLifetime);
        _mirrorApprovals.Add(request.PreviewId, registration);

        var job = _jobs.Enqueue(new(
            request.PreviewId,
            JobKind.Mirror,
            session.Profile.Id,
            request.Definition.Name,
            JobState.Queued,
            now,
            now));
        var result = new MirrorApproveResult(job);
        _mirrorApprovals[request.PreviewId] = registration with { Result = result };
        try
        {
            TrackJob(job.Id, token => RunApprovedMirrorAsync(
                session, request.Definition, stored.Preview, command, job.Id, token));
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            TryFailJob(job.Id, exception);
            result = new(FindJob(job.Id) ?? job);
            _mirrorApprovals[request.PreviewId] = registration with { Result = result };
        }
        return result;
    }

    private MirrorApproveResult ReplayMirrorApproval(
        MirrorApproveRequest request,
        StoredMirrorApproval registration)
    {
        if (registration.SessionId != request.SessionId ||
            !string.Equals(registration.DefinitionFingerprint, MirrorPlanner.Fingerprint(request.Definition), StringComparison.Ordinal) ||
            !IsValidReviewFingerprint(request.ReviewFingerprint) ||
            !string.Equals(registration.ReviewFingerprint, request.ReviewFingerprint, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                registration.ApprovalTokenHash,
                HashMirrorApprovalToken(request.ApprovalToken)) ||
            registration.DeletionsApproved != request.DeletionsApproved)
        {
            throw new ArgumentException(
                "The mirror approval differs from the already consumed review. Refresh job state and create a fresh preview.",
                nameof(request));
        }
        if (registration.Result is null)
        {
            throw new InvalidOperationException(
                "This mirror preview was consumed but did not produce a replayable job result. Refresh workspace state before creating a fresh preview.");
        }
        var currentJob = FindJob(request.PreviewId)
            ?? throw new InvalidOperationException("The consumed mirror preview no longer has a corresponding job. Refresh workspace state.");
        return registration.Result with { Job = currentJob };
    }

    private static byte[] HashMirrorApprovalToken(string approvalToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(approvalToken));

    private static bool IsValidReviewFingerprint(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<ConsoleExecuteResult> ExecuteConsoleAsync(ConsoleExecuteRequest request, CancellationToken cancellationToken = default)
    {
        var decision = SafeConsolePolicy.Evaluate(request.Command, localShellEnabled: false);
        if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
        var session = _sessions.Get(request.SessionId);
        var result = await session.WithConsoleSessionAsync(
            console => console.ExecuteAsync(request.Command, _options.ConsoleTimeout, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new(result);
    }

    public async Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SourceProfileId == Guid.Empty || request.DestinationProfileId == Guid.Empty || request.SourceProfileId == request.DestinationProfileId)
            throw new ArgumentException("Distinct source and destination profiles are required.", nameof(request));
        ValidateRemotePath(request.SourcePath, nameof(request));
        ValidateRemotePath(request.DestinationPath, nameof(request));
        ConnectionProfile source;
        ConnectionProfile destination;
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            source = await FindProfileAsync(request.SourceProfileId, cancellationToken).ConfigureAwait(false);
            destination = await FindProfileAsync(request.DestinationProfileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
        ThrowIfRemoteTransferNeedsIsolatedSftpRelay(source.Protocol, destination.Protocol);
        await _remoteTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            PurgeExpiredRemoteTransferPlans(now);
            MakeRoomForRemoteTransferPlan();
            Guid planId;
            do { planId = Guid.NewGuid(); }
            while (_remoteTransferPlans.ContainsKey(planId) || FindJob(planId) is not null);
            var plan = new RemoteTransferPlan(planId, source.Id, destination.Id, request.SourcePath, request.DestinationPath,
                ComputeRemoteTransferMode(source.Protocol, destination.Protocol), request.Overwrite);
            PlanValidator.Validate(plan);
            _remoteTransferPlans.Add(plan.Id, new(
                plan,
                ConnectionIdentity.FromProfile(source),
                ConnectionIdentity.FromProfile(destination),
                now,
                now + RemoteTransferPlanLifetime));
            return plan;
        }
        finally
        {
            _remoteTransferGate.Release();
        }
    }

    public async Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(
        RemoteTransferEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PlanValidator.Validate(request.Plan);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _remoteTransferGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await EnqueueRemoteTransferUnderProfileGateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _remoteTransferGate.Release();
            }
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    private async Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferUnderProfileGateAsync(
        RemoteTransferEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        PurgeExpiredRemoteTransferPlans(now);
        if (!_remoteTransferPlans.TryGetValue(request.Plan.Id, out var registration))
        {
            if (FindJob(request.Plan.Id) is not null)
                throw new InvalidOperationException("This remote-transfer plan was already consumed by this or a previous Agent process. Refresh job state before creating a fresh plan.");
            throw new InvalidOperationException("The remote-transfer plan was not issued by this Agent or has expired. Create and review a fresh route plan.");
        }
        if (registration.Plan != request.Plan)
            throw new ArgumentException("The remote-transfer plan changed after review. Create and review a fresh route plan.", nameof(request));
        if (registration.Result is { } priorResult)
        {
            var currentJob = FindJob(request.Plan.Id)
                ?? throw new InvalidOperationException("The consumed remote-transfer plan no longer has a corresponding job. Refresh workspace state.");
            return priorResult with { Job = currentJob };
        }
        if (registration.Consumed)
            throw new InvalidOperationException("This remote-transfer plan was already submitted and did not create a job. Create and review a fresh route plan.");
        if (FindJob(request.Plan.Id) is not null)
            throw new InvalidOperationException("This remote-transfer plan was already consumed, but its replay result is unavailable. Refresh job state before creating a fresh plan.");
        registration = registration with { Consumed = true };
        _remoteTransferPlans[request.Plan.Id] = registration;

        var source = _sessions.GetActiveProfile(request.Plan.SourceProfileId);
        var destination = _sessions.GetActiveProfile(request.Plan.DestinationProfileId);
        if (ConnectionIdentity.FromProfile(source.Profile) != registration.SourceIdentity ||
            ConnectionIdentity.FromProfile(destination.Profile) != registration.DestinationIdentity)
        {
            throw new InvalidOperationException(
                "A source or destination connection identity changed after route review. Create and review a fresh remote-transfer plan.");
        }
        ThrowIfRemoteTransferNeedsIsolatedSftpRelay(source.Profile.Protocol, destination.Profile.Protocol);
        var expectedMode = ComputeRemoteTransferMode(source.Profile.Protocol, destination.Profile.Protocol);
        if (request.Plan.Mode != expectedMode)
            throw new ArgumentException("The remote transfer routing mode does not match the active profile protocols.", nameof(request));

        var sourceEntry = await TryStatRemoteAsync(source, request.Plan.SourcePath, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The remote transfer source was not found.", request.Plan.SourcePath);
        if (sourceEntry.Kind == EntryKind.Directory)
            throw new NotSupportedException("Remote-to-remote directory copies are not supported yet. Select a file.");
        if (sourceEntry.Kind != EntryKind.File)
            throw new NotSupportedException("Remote-to-remote copies currently require a regular file; links and special entries are not followed.");
        var destinationEntry = await TryStatRemoteAsync(destination, request.Plan.DestinationPath, cancellationToken).ConfigureAwait(false);
        if (destinationEntry?.Kind == EntryKind.Directory)
            throw new NotSupportedException("The remote transfer destination is a directory. Select a destination file path.");
        if (destinationEntry is not null && !request.Plan.Overwrite)
            throw new IOException("The remote transfer destination already exists and overwrite was not approved.");

        var routingNote = expectedMode == RemoteTransferMode.Fxp
            ? "FXP preferred between FTP-family servers; LFTP will relay through this client if FXP is unavailable."
            : "Client-relay routing through LFTP is required for this protocol combination.";
        var job = _jobs.Enqueue(new(
            request.Plan.Id,
            JobKind.RemoteTransfer,
            source.Profile.Id,
            JobSnapshotPolicy.CanonicalizeDerivedDisplayName(
                $"{RemoteName(request.Plan.SourcePath)} -> {RemoteName(request.Plan.DestinationPath)}",
                "Remote transfer"),
            JobState.Queued,
            now,
            now,
            Status: routingNote));
        var result = new RemoteTransferEnqueueResult(job, expectedMode, routingNote);
        try
        {
            TrackJob(
                job.Id,
                token => RunRemoteTransferAsync(source, destination, request.Plan, job.Id, routingNote, token),
                destination.Profile.Id);
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            TryFailJob(job.Id, exception);
            var committedResult = result with { Job = FindJob(job.Id) ?? job };
            _remoteTransferPlans[request.Plan.Id] = registration with { Result = committedResult };
            return committedResult;
        }
        _remoteTransferPlans[request.Plan.Id] = registration with { Result = result };
        return result;
    }

    public async Task<RemoteEditSession> StartRemoteEditAsync(RemoteEditStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _profileTrustGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _sessions.Get(request.SessionId);
            return await _remoteEdits.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileTrustGate.Release();
        }
    }

    public Task<RemoteEditReview> ReviewRemoteEditAsync(RemoteEditReviewRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.ReviewAsync(request, cancellationToken);

    public Task<RemoteEditActionResult> ResolveRemoteEditAsync(RemoteEditResolveRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.ResolveAsync(request, cancellationToken);

    public Task<bool> CompleteRemoteEditAsync(RemoteEditCompleteRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.CompleteAsync(request, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Task disposal;
        Task admittedRequests;
        TaskCompletionSource? completion = null;
        lock (_requestGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            admittedRequests = _activeRequests == 0
                ? Task.CompletedTask
                : (_requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            _disposeTask = disposal;
        }

        _ = CompleteDisposalAsync(admittedRequests, completion);
        return new(disposal);
    }

    private async Task CompleteDisposalAsync(Task admittedRequests, TaskCompletionSource completion)
    {
        try
        {
            _lifetime.Cancel();
            await admittedRequests.ConfigureAwait(false);
            await DisposeOwnedStateAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeOwnedStateAsync()
    {
        Task[] operations;
        lock (_operationGate) operations = _operations.ToArray();
        try { await Task.WhenAll(operations).ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _remoteEdits.DisposeAsync().ConfigureAwait(false);
        await _sessions.DisposeAsync().ConfigureAwait(false);
        if (_mirrorPlanner is IDisposable disposable) disposable.Dispose();
        _sessionStateGate.Dispose();
        _remoteTransferGate.Dispose();
        _profileTrustGate.Dispose();
        _lifetime.Dispose();
    }

    private void AdmitRequest()
    {
        lock (_requestGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked { _activeRequests++; }
        }
    }

    private void ReleaseRequest()
    {
        TaskCompletionSource? drained = null;
        lock (_requestGate)
        {
            _activeRequests--;
            if (_disposed && _activeRequests == 0) drained = _requestsDrained;
        }
        drained?.TrySetResult();
    }

    public bool TryCancelOperation(Guid jobId, string? reason = null)
    {
        if (_jobCancellations.TryGetValue(jobId, out var cancellation))
        {
            using var cancellationLease = cancellation.TryAcquire();
            if (cancellationLease is null) return false;
            if (!_jobs.TryCancel(jobId, reason)) return false;
            ForgetTransferRetry(jobId);
            cancellationLease.Cancel();
            return true;
        }
        if (_scheduler?.TryCancel(jobId, reason) != true) return false;
        ForgetTransferRetry(jobId);
        return true;
    }

    private async Task RunTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (plan.SourceKind == TransferSourceKind.Directory)
            {
                _jobs.Transition(jobId, JobState.Running, "Waiting for the guarded foreground directory transfer session.");
                await RunGuardedDirectoryTransferAsync(session, plan, jobId, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _jobs.Transition(jobId, JobState.Completed, "Completed through the guarded foreground directory transfer session.");
                return;
            }
            _jobs.Transition(jobId, JobState.Running, "Waiting for a reserved per-site LFTP transfer slot.");
            await session.ExecuteQueuedTransferAsync(
                plan,
                async (_, token) =>
                {
                    _jobs.Transition(jobId, JobState.Running, "Reserved an LFTP transfer slot; revalidating source and destination.");
                    await session.WithValidationSessionAsync(async validation =>
                    {
                        await RevalidateTransferEndpointsAsync(validation, plan, token).ConfigureAwait(false);
                        if (plan.Mode == TransferMode.Skip &&
                            await DestinationExistsAsync(session, plan, token).ConfigureAwait(false))
                        {
                            throw new TransferSkippedException();
                        }
                        if (plan.Direction == TransferDirection.Download &&
                            Path.GetDirectoryName(plan.DestinationPath) is { Length: > 0 } destinationParent)
                        {
                            Directory.CreateDirectory(destinationParent);
                            RequireLocalDestinationKind(plan.DestinationPath, plan.SourceKind);
                        }
                        return true;
                    }, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Transition(jobId, JobState.Completed, "Completed through the per-site LFTP transfer queue.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (TransferSkippedException)
        {
            _jobs.Transition(jobId, JobState.Completed, "Skipped because the destination already exists");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
        }
    }

    private async Task RunScheduledTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (plan.SourceKind == TransferSourceKind.Directory)
            {
                await RunTransferAsync(session, plan, jobId, cancellationToken).ConfigureAwait(false);
                return;
            }
            await RunTransferAsync(session, plan, jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
        }
        finally
        {
            if (_jobs.GetJobs().FirstOrDefault(job => job.Id == jobId)?.State != JobState.Failed)
                ForgetTransferRetry(jobId);
        }
    }

    private async Task RunRemoteTransferAsync(
        WorkspaceSession source,
        WorkspaceSession destination,
        RemoteTransferPlan plan,
        Guid jobId,
        string routingNote,
        CancellationToken cancellationToken)
    {
        try
        {
            _jobs.Transition(jobId, JobState.Running, routingNote);
            await _sessions.WithRemoteTransferAsync(source, destination, jobId, async process =>
            {
                var result = await process.ExecuteAsync(
                    LftpCommandBuilder.BuildRemoteTransfer(plan),
                    _options.TransferTimeout,
                    cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(result, "Remote-to-remote transfer");
                return true;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Transition(jobId, JobState.Completed, $"Completed. {routingNote}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or NotSupportedException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
        }
    }

    private async Task<bool> DestinationExistsAsync(WorkspaceSession session, TransferPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Direction == TransferDirection.Download)
        {
            if (Directory.Exists(plan.DestinationPath))
                throw new IOException("The download destination is a directory; select a file path.");
            return File.Exists(plan.DestinationPath);
        }

        var result = await session.WithBrowseSessionAsync(browse => browse.ExecuteAsync(
                $"cls -1 {LftpCommandBuilder.Quote(LftpCommandBuilder.DashSafe(plan.DestinationPath))}",
                _options.BrowseTimeout,
                cancellationToken))
            .ConfigureAwait(false);
        if (result.TimedOut) throw new TimeoutException("The remote collision check timed out.");
        if (result.Failure is not null) throw new IOException($"The remote collision check failed: {result.Failure}");
        var error = LftpOutputParser.FirstError(result.Lines);
        if (error is null) return true;
        if (error.Contains("no such", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return false;
        throw new IOException($"The remote collision check failed closed: {error}");
    }

    private async Task RunApprovedMirrorAsync(
        WorkspaceSession session,
        MirrorDefinition definition,
        MirrorPreview reviewedPreview,
        string executionCommand,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.WithTransferSessionAsync(async transfer =>
            {
                _jobs.Transition(jobId, JobState.Running, "Revalidating reviewed mirror actions");
                ValidateMirrorLocalRoot(definition);
                await ValidateMirrorRemoteRootAsync(transfer, definition, cancellationToken).ConfigureAwait(false);
                var verificationResult = await transfer.ExecuteAsync(
                    LftpCommandBuilder.BuildMirror(definition, dryRun: true),
                    _options.MirrorPreviewTimeout,
                    cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(verificationResult, "Mirror approval verification");
                var verification = _mirrorPlanner.CreatePreview(definition, verificationResult.Lines.Select(static line => line.Line));
                if (!verification.Actions.SequenceEqual(reviewedPreview.Actions))
                    throw new InvalidOperationException("The mirror actions changed after review. Generate and approve a new preview.");
                ValidateMirrorLocalRoot(definition);
                await ValidateMirrorRemoteRootAsync(transfer, definition, cancellationToken).ConfigureAwait(false);

                var executionResult = await transfer.ExecuteAsync(executionCommand, _options.TransferTimeout, cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(executionResult, "LFTP mirror job");
                return true;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Transition(jobId, JobState.Completed, "Completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
        }
    }

    private void TrackJob(
        Guid jobId,
        Func<CancellationToken, Task> operation,
        params Guid[] additionalProfileIds)
    {
        var job = _jobs.GetJobs().FirstOrDefault(candidate => candidate.Id == jobId)
            ?? throw new KeyNotFoundException($"Job {jobId} was not found before tracking began.");
        var dependencies = ImmutableHashSet.CreateBuilder<Guid>();
        if (job.ProfileId is { } primaryProfileId && primaryProfileId != Guid.Empty)
            dependencies.Add(primaryProfileId);
        foreach (var profileId in additionalProfileIds)
        {
            if (profileId == Guid.Empty)
                throw new ArgumentException("Tracked job profile dependencies cannot be empty.", nameof(additionalProfileIds));
            dependencies.Add(profileId);
        }
        if (!_activeJobProfileDependencies.TryAdd(jobId, dependencies.ToImmutable()))
            throw new InvalidOperationException($"Job {jobId} already has active profile dependencies.");

        var cancellation = new TrackedJobCancellation(_lifetime.Token);
        if (!_jobCancellations.TryAdd(jobId, cancellation))
        {
            _activeJobProfileDependencies.TryRemove(jobId, out _);
            cancellation.Complete();
            throw new InvalidOperationException($"Job {jobId} is already running.");
        }
        var task = RunTrackedOperationAsync(jobId, operation, cancellation.Token);
        lock (_operationGate) _operations.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_operationGate) _operations.Remove(completed);
            if (_jobs.GetJobs().FirstOrDefault(job => job.Id == jobId)?.State != JobState.Failed)
                ForgetTransferRetry(jobId);
            if (_jobCancellations.TryRemove(jobId, out var source)) source.Complete();
            _activeJobProfileDependencies.TryRemove(jobId, out _);
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunTrackedOperationAsync(
        Guid jobId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
            var state = _jobs.GetJobs().FirstOrDefault(job => job.Id == jobId)?.State;
            if (state is JobState.Queued or JobState.Running)
                TryFailJob(jobId, new InvalidOperationException("The tracked operation ended without a terminal job state."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            TryFailJob(jobId, exception);
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private void TryFailJob(Guid jobId, Exception exception)
    {
        var state = _jobs.GetJobs().FirstOrDefault(job => job.Id == jobId)?.State;
        if (state is not (JobState.Queued or JobState.Running)) return;
        try
        {
            _jobs.Transition(
                jobId,
                JobState.Failed,
                "Failed",
                JobSnapshotPolicy.CanonicalizeDerivedError("lftp-job-failed", exception.Message));
        }
        catch (InvalidOperationException) { }
    }

    private bool TryRememberTransfer(Guid jobId, WorkspaceSession session, TransferPlan plan)
    {
        lock (_retryGate)
        {
            if (_transferRetries.Count >= MaximumRetryableTransfers) return false;
            return _transferRetries.TryAdd(jobId, new(session.Snapshot.SessionId, plan with { RunAt = null }));
        }
    }

    private bool TryGetTransferRetry(Guid jobId, out TransferRetryContext context)
    {
        lock (_retryGate) return _transferRetries.TryGetValue(jobId, out context!);
    }

    private void ForgetTransferRetry(Guid jobId)
    {
        lock (_retryGate) _transferRetries.Remove(jobId);
    }

    private async Task WaitForPriorAttemptCleanupAsync(Guid jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && HasPriorAttempt(jobId); attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        if (HasPriorAttempt(jobId))
            throw new InvalidOperationException("The failed transfer is still finishing cleanup. Try the retry again in a moment.");
    }

    private bool HasPriorAttempt(Guid jobId) =>
        _jobCancellations.ContainsKey(jobId) || _scheduler?.IsRegistered(jobId) == true;

    private async Task<ConnectionProfile> FindProfileAsync(Guid id, CancellationToken cancellationToken) =>
        (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(profile => profile.Id == id)
        ?? throw new KeyNotFoundException($"Profile {id} was not found.");

    private JobSnapshot? FindJob(Guid jobId) =>
        _jobs.GetJobs().FirstOrDefault(job => job.Id == jobId);

    private void MakeRoomForTransferSubmission()
    {
        while (_transferSubmissions.Count >= MaximumTransferSubmissions)
        {
            var oldestTerminal = _transferSubmissions.Values
                .Where(static submission => submission.Result is not null || submission.Failure is not null)
                .OrderBy(static submission => submission.SubmittedAt)
                .FirstOrDefault();
            if (oldestTerminal is null)
                throw new InvalidOperationException("Too many transfer submissions are still being resolved. Refresh job state before queueing more work.");
            _transferSubmissions.Remove(oldestTerminal.Request.Plan.Id);
        }
    }

    private void PurgeExpiredRemoteTransferPlans(DateTimeOffset now)
    {
        foreach (var registration in _remoteTransferPlans.Values.Where(registration => registration.ExpiresAt <= now).ToArray())
            _remoteTransferPlans.Remove(registration.Plan.Id);
    }

    private void MakeRoomForRemoteTransferPlan()
    {
        while (_remoteTransferPlans.Count >= MaximumRemoteTransferPlans)
        {
            var oldestConsumed = _remoteTransferPlans.Values
                .Where(static registration => registration.Consumed)
                .OrderBy(static registration => registration.IssuedAt)
                .FirstOrDefault();
            if (oldestConsumed is null)
                throw new InvalidOperationException("Too many remote-transfer plans are awaiting review. Use an existing plan or wait for an older plan to expire.");
            _remoteTransferPlans.Remove(oldestConsumed.Plan.Id);
        }
    }

    private WorkspaceSession GetRemoteMutationSession(Guid? sessionId)
    {
        if (sessionId is not { } value || value == Guid.Empty)
            throw new ArgumentException("A connected session is required for a remote file operation.", nameof(sessionId));
        return _sessions.Get(value);
    }

    private async Task<FileEntry?> TryStatRemoteAsync(WorkspaceSession session, string path, CancellationToken cancellationToken) =>
        await session.WithBrowseSessionAsync(process => TryStatRemoteAsync(process, path, cancellationToken)).ConfigureAwait(false);

    private async Task<FileEntry?> TryStatRemoteAsync(ILftpSession process, string path, CancellationToken cancellationToken)
    {
        var validatedPath = ValidateRemoteTransferPath(path, nameof(path));
        var result = await process.ExecuteAsync(LftpCommandBuilder.BuildStat(validatedPath, fresh: true), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
        return FreshRemoteStatParser.Parse(result, validatedPath, "The fresh remote path check");
    }

    private void InvalidateBrowseSnapshots(PaneKind kind, Guid? sessionId)
    {
        foreach (var pair in _browseSnapshots)
        {
            if (pair.Value.Location.Kind == kind && (kind == PaneKind.Local || pair.Value.SessionId == sessionId))
                _browseSnapshots.TryRemove(pair.Key, out _);
        }
    }

    private void PublishDirectoryChange(PaneKind kind, Guid? sessionId, IEnumerable<string> paths) =>
        _publish?.Invoke(EngineEventKind.Directory, "directory.changed", new { Kind = kind, Paths = paths.ToArray() }, null, sessionId);

    private static void ValidatePaneKind(PaneKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), "The pane kind is unsupported.");
    }

    private static string ValidateLocalMutationPath(string path, bool allowRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded fully qualified local path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!allowRoot && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A local file-system root cannot be mutated.", nameof(path));
        return fullPath;
    }

    private static string ValidateRemoteMutationPath(string path, bool allowRoot)
    {
        ValidateRemotePath(path, nameof(path));
        if (path.Length > 4096 || path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            throw new ArgumentException("The remote path is ambiguous or too long.", nameof(path));
        var normalized = path.Length == 1 ? path : path.TrimEnd('/');
        if (!allowRoot && normalized == "/") throw new ArgumentException("The remote root cannot be mutated.", nameof(path));
        return normalized;
    }

    private static RemoteTransferMode ComputeRemoteTransferMode(ConnectionProtocol source, ConnectionProtocol destination) =>
        source != ConnectionProtocol.Sftp && destination != ConnectionProtocol.Sftp
            ? RemoteTransferMode.Fxp
            : RemoteTransferMode.ClientRelay;

    private static void ThrowIfRemoteTransferNeedsIsolatedSftpRelay(
        ConnectionProtocol source,
        ConnectionProtocol destination)
    {
        if (source != ConnectionProtocol.Sftp && destination != ConnectionProtocol.Sftp) return;
        throw new NotSupportedException(
            "SFTP and mixed-protocol remote-to-remote transfers are blocked until client relay uses distinct, separately pinned LFTP processes. FTP-family FXP remains available.");
    }

    private static string NormalizeRemotePath(string path) => path == "/" ? path : path.TrimEnd('/');

    private static string RemoteName(string path)
    {
        var normalized = path.TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private async Task<string?> ResolveCredentialAsync(ConnectionProfile profile, string? ephemeral, CancellationToken cancellationToken)
    {
        return profile.Authentication switch
        {
            AuthenticationKind.Anonymous => null,
            AuthenticationKind.AskOnConnect => !string.IsNullOrEmpty(ephemeral) ? ephemeral : throw new InvalidOperationException("This profile requires an ask-on-connect credential."),
            AuthenticationKind.SshKey => !string.IsNullOrEmpty(ephemeral)
                ? throw new NotSupportedException("Encrypted SSH key passphrases are not yet supported; use an unencrypted key for this milestone.")
                : null,
            AuthenticationKind.Password => !string.IsNullOrEmpty(ephemeral)
                ? ephemeral
                : await _secretStore.GetAsync(SecretBindingFor(profile), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No password is stored for this profile."),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    private static SecretBinding SecretBindingFor(ConnectionProfile profile)
    {
        var identity = ConnectionIdentity.FromProfile(profile);
        return new(profile.Id, identity.CanonicalEndpoint, profile.UserName, $"login-{profile.Authentication.ToString().ToLowerInvariant()}");
    }

    private async Task<ConnectionProfile> FindExpectedProfileAsync(
        ConnectionIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (expectedIdentity is null || expectedIdentity.ProfileId == Guid.Empty)
            throw new ArgumentException("A complete expected connection identity is required.", nameof(expectedIdentity));

        var profile = await FindProfileAsync(expectedIdentity.ProfileId, cancellationToken).ConfigureAwait(false);
        if (ConnectionIdentity.FromProfile(profile) != expectedIdentity)
        {
            throw new InvalidOperationException(
                "The connection profile changed after it was selected. Refresh the workspace and review the current connection before continuing.");
        }
        return profile;
    }

    private static bool HostKeyBindingChanged(ConnectionProfile previous, ConnectionProfile current)
    {
        if (previous.Protocol != current.Protocol)
            return previous.Protocol == ConnectionProtocol.Sftp || current.Protocol == ConnectionProtocol.Sftp;
        if (current.Protocol != ConnectionProtocol.Sftp) return false;
        return SftpHostKeyManager.CreateBinding(previous) != SftpHostKeyManager.CreateBinding(current);
    }

    private static void ValidateCredential(string? credential)
    {
        if (credential is null) return;
        if (credential.Length is < 1 or > 4096 || credential.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("The credential has an invalid length or contains a protocol control character.");
    }

    private static void ValidateBrowseRequest(BrowseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path) || request.Path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A browse path without protocol control characters is required.", nameof(request));
        if (request.PageSize is < 1 or > MaximumBrowsePageSize)
            throw new ArgumentOutOfRangeException(nameof(request), $"Browse pages must contain between 1 and {MaximumBrowsePageSize} entries.");
        if (request.ContinuationToken is { Length: > 80 })
            throw new ArgumentException("The browse continuation token is invalid.", nameof(request));
        if (request.ContinuationToken is not null && request.Fresh)
            throw new ArgumentException("A continued browse snapshot cannot also request a fresh server listing.", nameof(request));
    }

    private BrowseResult CreateBrowsePage(PaneLocation location, Guid? sessionId, ImmutableArray<FileEntry> entries, int pageSize)
    {
        PurgeExpiredBrowseSnapshots();
        while (_browseSnapshots.Count >= MaximumBrowseSnapshots)
        {
            var oldest = _browseSnapshots.Values.OrderBy(static snapshot => snapshot.CreatedAt).FirstOrDefault();
            if (oldest is null || !_browseSnapshots.TryRemove(oldest.Id, out _)) break;
        }
        var snapshot = new StoredBrowseSnapshot(Guid.NewGuid(), location, sessionId, DateTimeOffset.UtcNow, entries);
        _browseSnapshots[snapshot.Id] = snapshot;
        return BuildBrowsePage(snapshot, 0, pageSize);
    }

    private BrowseResult ContinueBrowse(BrowseRequest request, PaneKind expectedKind, string expectedPath)
    {
        PurgeExpiredBrowseSnapshots();
        var parts = request.ContinuationToken?.Split(':', 2);
        if (parts is not { Length: 2 } || !Guid.TryParseExact(parts[0], "N", out var snapshotId) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var offset))
            throw new InvalidDataException("The browse continuation token is invalid or expired.");
        if (!_browseSnapshots.TryGetValue(snapshotId, out var snapshot) || snapshot.Location.Kind != expectedKind || snapshot.SessionId != request.SessionId ||
            !PathsEqual(expectedKind, snapshot.Location.Path, expectedPath) || offset <= 0 || offset >= snapshot.Entries.Length)
            throw new InvalidDataException("The browse continuation token does not match this directory snapshot.");
        return BuildBrowsePage(snapshot, offset, request.PageSize);
    }

    private BrowseResult BuildBrowsePage(StoredBrowseSnapshot snapshot, int offset, int pageSize)
    {
        var page = ImmutableArray.CreateBuilder<FileEntry>();
        var estimatedBytes = 1024;
        for (var index = offset; index < snapshot.Entries.Length && page.Count < pageSize; index++)
        {
            var entry = snapshot.Entries[index];
            var entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, FramedJsonStream.SerializerOptions).Length + 1;
            if (entryBytes > MaximumBrowsePageEstimatedBytes)
                throw new InvalidDataException("A directory entry is too large for the bounded Agent protocol.");
            if (page.Count != 0 && estimatedBytes + entryBytes > MaximumBrowsePageEstimatedBytes) break;
            estimatedBytes += entryBytes;
            page.Add(entry);
        }
        if (page.Count == 0 && offset < snapshot.Entries.Length)
            throw new InvalidDataException("The browse page could not fit within the bounded Agent protocol.");
        var nextOffset = offset + page.Count;
        var continuation = nextOffset < snapshot.Entries.Length ? $"{snapshot.Id:N}:{nextOffset}" : null;
        if (continuation is null) _browseSnapshots.TryRemove(snapshot.Id, out _);
        return new(snapshot.Location, page.ToImmutable(), continuation, snapshot.Entries.Length);
    }

    private void PurgeExpiredBrowseSnapshots()
    {
        var threshold = DateTimeOffset.UtcNow - BrowseSnapshotLifetime;
        foreach (var pair in _browseSnapshots)
            if (pair.Value.CreatedAt < threshold) _browseSnapshots.TryRemove(pair.Key, out _);
    }

    private static bool PathsEqual(PaneKind kind, string left, string right) => string.Equals(
        left,
        right,
        kind == PaneKind.Local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static TransferPlan CanonicalizeTransferPlan(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Direction switch
        {
            TransferDirection.Download => plan with
            {
                SourcePath = ValidateRemoteTransferPath(plan.SourcePath, nameof(plan)),
                DestinationPath = CanonicalizeLocalTransferPath(plan.DestinationPath, nameof(plan)),
            },
            TransferDirection.Upload => plan with
            {
                SourcePath = CanonicalizeLocalTransferPath(plan.SourcePath, nameof(plan)),
                DestinationPath = ValidateRemoteTransferPath(plan.DestinationPath, nameof(plan)),
            },
            _ => plan,
        };
    }

    private static void ValidateTransferPaths(TransferPlan plan)
    {
        if (plan.SourceKind == TransferSourceKind.Directory &&
            (IsQuickDirectoryRoot(plan.SourcePath) || IsQuickDirectoryRoot(plan.DestinationPath)))
        {
            throw new NotSupportedException(
                "Quick directory transfers cannot use a local filesystem root, UNC share root, or the remote server root. Use the reviewed Mirror workflow instead.");
        }
        if (plan.Direction == TransferDirection.Download)
        {
            _ = ValidateRemoteTransferPath(plan.SourcePath, nameof(plan));
            _ = CanonicalizeLocalTransferPath(plan.DestinationPath, nameof(plan));
        }
        else
        {
            if (plan.Mode == TransferMode.Skip)
                throw new NotSupportedException("No-overwrite upload cannot be guaranteed portably across the supported protocols; choose resume or overwrite explicitly.");
            _ = CanonicalizeLocalTransferPath(plan.SourcePath, nameof(plan));
            _ = ValidateRemoteTransferPath(plan.DestinationPath, nameof(plan));
        }
    }

    private static bool IsQuickDirectoryRoot(string path)
    {
        if (string.Equals(path, "/", StringComparison.Ordinal)) return true;
        if (!Path.IsPathFullyQualified(path)) return false;
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return root is not null && string.Equals(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task RevalidateTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        await session.WithValidationSessionAsync(async validation =>
        {
            await RevalidateTransferEndpointsAsync(validation, plan, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        await PreflightDirectoryTransferAsync(session, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task RevalidateTransferEndpointsAsync(
        ILftpSession process,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        ValidateTransferPaths(plan);
        if (plan.Direction == TransferDirection.Download)
        {
            await ValidateRemoteEndpointTreeAsync(
                process, plan.SourcePath, plan.SourceKind, required: true, "download source", cancellationToken).ConfigureAwait(false);
            RequireLocalDestinationKind(plan.DestinationPath, plan.SourceKind);
            return;
        }

        RequireLocalSourceKind(plan.SourcePath, plan.SourceKind);
        await ValidateRemoteEndpointTreeAsync(
            process, plan.DestinationPath, plan.SourceKind, required: false, "upload destination", cancellationToken).ConfigureAwait(false);
    }

    private async Task PreflightDirectoryTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.SourceKind != TransferSourceKind.Directory) return;
        await session.WithEphemeralSessionAsync(
            "directory-transfer-preview",
            async previewSession =>
            {
                await ValidateDirectoryTransferPreviewAsync(previewSession, plan, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateDirectoryTransferPreviewAsync(
        ILftpSession process,
        TransferPlan plan,
        CancellationToken cancellationToken)
    {
        var definition = new MirrorDefinition(
            plan.Id,
            plan.ProfileId,
            "Automatic directory transfer safety preflight",
            plan.Direction == TransferDirection.Download ? MirrorDirection.Download : MirrorDirection.Upload,
            plan.Direction == TransferDirection.Download ? plan.DestinationPath : plan.SourcePath,
            plan.Direction == TransferDirection.Download ? plan.SourcePath : plan.DestinationPath,
            ParallelFiles: 1,
            SegmentsPerFile: plan.Segments);
        var result = await process.ExecuteAsync(
            LftpCommandBuilder.BuildDirectoryTransferPreview(plan),
            _options.MirrorPreviewTimeout,
            cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Directory transfer safety preview");
        MirrorPreview preview;
        try
        {
            preview = _mirrorPlanner.CreatePreview(definition, result.Lines.Select(static line => line.Line));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                "The directory transfer dry-run could not be proven non-destructive. Use the reviewed Mirror workflow instead.",
                exception);
        }
        if (preview.ContainsDeletions)
            throw new InvalidOperationException(
                "The directory transfer dry-run proposed deletion or type-collision replacement. Use the reviewed Mirror workflow instead.");
    }

    private async Task RunGuardedDirectoryTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await session.WithTransferSessionAsync(async transfer =>
        {
            _jobs.Transition(jobId, JobState.Running, "Revalidating the directory transfer on its guarded LFTP session.");
            await RevalidateTransferEndpointsAsync(transfer, plan, cancellationToken).ConfigureAwait(false);
            if (plan.Direction == TransferDirection.Download &&
                Path.GetDirectoryName(plan.DestinationPath) is { Length: > 0 } destinationParent)
            {
                Directory.CreateDirectory(destinationParent);
                RequireLocalDestinationKind(plan.DestinationPath, plan.SourceKind);
            }
            await ValidateDirectoryTransferPreviewAsync(transfer, plan, cancellationToken).ConfigureAwait(false);
            await RevalidateTransferEndpointsAsync(transfer, plan, cancellationToken).ConfigureAwait(false);
            _jobs.Transition(jobId, JobState.Running, "Running the guarded foreground directory transfer.");
            var result = await transfer.ExecuteAsync(
                LftpCommandBuilder.BuildTransfer(plan, background: false),
                _options.TransferTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Guarded directory transfer");
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void RequireLocalSourceKind(string path, TransferSourceKind expectedKind)
    {
        ValidateLocalTransferAncestors(path);
        var actualKind = TryGetLocalTransferKind(path)
            ?? throw new ArgumentException("The local upload source must exist before the transfer can run.", nameof(path));
        if (actualKind != expectedKind)
            throw new IOException($"The local upload source is a {Describe(actualKind)}, but the transfer declares a {Describe(expectedKind)} source.");
    }

    private static void RequireLocalDestinationKind(string path, TransferSourceKind expectedKind)
    {
        ValidateLocalTransferAncestors(path);
        var actualKind = TryGetLocalTransferKind(path);
        if (actualKind is not null && actualKind != expectedKind)
            throw new IOException($"The existing local download destination is a {Describe(actualKind.Value)}, but this transfer requires a {Describe(expectedKind)} destination.");
    }

    private static TransferSourceKind? TryGetLocalTransferKind(string path)
    {
        var attributes = TryGetLocalTransferAttributes(path);
        return attributes is { } value ? ClassifyLocalTransferAttributes(value) : null;
    }

    private static FileAttributes? TryGetLocalTransferAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ValidateLocalTransferAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("A rooted local transfer path is required.", nameof(path));
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            if (TryGetLocalTransferAttributes(current) is { } attributes)
                ValidateLocalTransferAncestorAttributes(attributes);
            if (string.Equals(
                    current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    internal static void ValidateLocalTransferAncestorAttributes(FileAttributes attributes)
    {
        if (ClassifyLocalTransferAttributes(attributes) != TransferSourceKind.Directory)
            throw new IOException("A local transfer path has a non-directory ancestor.");
    }

    internal static TransferSourceKind ClassifyLocalTransferAttributes(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new NotSupportedException("Local reparse points and special entries are not followed for transfers.");
        return (attributes & FileAttributes.Directory) != 0
            ? TransferSourceKind.Directory
            : TransferSourceKind.File;
    }

    private static void RequireRemoteTransferKind(FileEntry entry, TransferSourceKind expectedKind, string role)
    {
        var actualKind = entry.Kind switch
        {
            EntryKind.File => TransferSourceKind.File,
            EntryKind.Directory => TransferSourceKind.Directory,
            EntryKind.SymbolicLink or EntryKind.Other => throw new NotSupportedException(
                $"The remote {role} is a symbolic link or special entry. LFTP Pilot does not follow these entries for transfers."),
            _ => throw new InvalidDataException($"The remote {role} has an unsupported entry kind."),
        };
        if (actualKind != expectedKind)
            throw new IOException($"The remote {role} is a {Describe(actualKind)}, but this transfer requires a {Describe(expectedKind)} entry.");
    }

    private static string Describe(TransferSourceKind kind) => kind == TransferSourceKind.Directory ? "directory" : "file";

    private static string CanonicalizeLocalTransferPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 ||
            path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path) || IsDeviceNamespacePath(path))
        {
            throw new ArgumentException("A bounded, fully qualified, non-device local path is required.", parameterName);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The local transfer path could not be canonicalized safely.", parameterName, exception);
        }

        if (fullPath.Length > 32_767 || IsDeviceNamespacePath(fullPath))
            throw new ArgumentException("A bounded, fully qualified, non-device local path is required.", parameterName);
        return fullPath;
    }

    private static void ValidateMirrorLocalRoot(MirrorDefinition definition)
    {
        if (ContainsLocalDotSegment(definition.LocalRoot))
            throw new ArgumentException("The mirror local root cannot contain current-directory or parent-directory segments.", nameof(definition));
        var localRoot = CanonicalizeLocalTransferPath(definition.LocalRoot, nameof(definition));

        ValidateLocalTransferAncestors(localRoot);
        var kind = TryGetLocalTransferKind(localRoot);
        if (definition.Direction == MirrorDirection.Upload && kind is null)
            throw new DirectoryNotFoundException("The local upload mirror root does not exist.");
        if (kind is not null && kind != TransferSourceKind.Directory)
            throw new IOException("The mirror local root must be a directory and cannot be a link or special entry.");
    }

    private static bool ContainsLocalDotSegment(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.None).Any(static segment => segment is "." or "..");

    private async Task ValidateMirrorRemoteRootAsync(
        ILftpSession process,
        MirrorDefinition definition,
        CancellationToken cancellationToken)
    {
        await ValidateRemoteEndpointTreeAsync(
            process,
            definition.RemoteRoot,
            TransferSourceKind.Directory,
            required: definition.Direction == MirrorDirection.Download,
            "mirror root",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateRemoteEndpointTreeAsync(
        ILftpSession process,
        string path,
        TransferSourceKind expectedKind,
        bool required,
        string role,
        CancellationToken cancellationToken)
    {
        path = ValidateRemoteTransferPath(path, nameof(path));
        if (path == "/")
        {
            if (expectedKind != TransferSourceKind.Directory)
                throw new IOException($"The remote {role} is the server root directory, but this transfer requires a file.");
            return;
        }

        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = string.Empty;
        for (var index = 0; index < components.Length; index++)
        {
            prefix += "/" + components[index];
            var entry = await TryStatRemoteAsync(process, prefix, cancellationToken).ConfigureAwait(false);
            var isTarget = index == components.Length - 1;
            if (entry is null)
            {
                if (required)
                    throw new FileNotFoundException($"The remote {role} or one of its ancestors was not found.", prefix);
                return;
            }

            if (isTarget)
            {
                RequireRemoteTransferKind(entry, expectedKind, role);
                continue;
            }

            if (entry.Kind != EntryKind.Directory)
            {
                if (entry.Kind is EntryKind.SymbolicLink or EntryKind.Other)
                    throw new NotSupportedException($"A remote ancestor of the {role} is a symbolic link or special entry. LFTP Pilot does not follow remote ancestors for transfers.");
                throw new IOException($"A remote ancestor of the {role} is not a directory.");
            }
        }
    }

    private static bool IsDeviceNamespacePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("//?/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("//./", StringComparison.OrdinalIgnoreCase);

    private static string ValidateRemoteTransferPath(string path, string parameterName)
    {
        ValidateRemotePath(path, parameterName);
        if (path.Length > 4096 || path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or "..") ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 128 ||
            path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A canonical remote path of at most 4096 characters is required; duplicate separators, dot segments, and trailing separators are not allowed.",
                parameterName);
        }
        return path;
    }

    private static void ValidateRemotePath(string path, string parameterName)
    {
        if (!ProfileValidator.IsCanonicalRemotePath(path))
            throw new ArgumentException("A bounded canonical remote path is required.", parameterName);
    }

    private void PurgeExpiredMirrorRegistrations(DateTimeOffset now)
    {
        foreach (var pair in _previews)
        {
            if (pair.Value.Preview.ExpiresAt <= now) _previews.TryRemove(pair.Key, out _);
        }
        foreach (var pair in _mirrorApprovals.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
            _mirrorApprovals.Remove(pair.Key);
    }

    private void MakeRoomForMirrorPreview()
    {
        while (_previews.Count >= MaximumMirrorPreviews)
        {
            var oldest = _previews.Values.MinBy(static registration => registration.Preview.GeneratedAt)
                ?? throw new InvalidOperationException("The mirror preview registry could not be bounded safely.");
            _previews.TryRemove(oldest.Preview.Id, out _);
        }
    }

    private void MakeRoomForMirrorApproval()
    {
        while (_mirrorApprovals.Count >= MaximumMirrorApprovals)
        {
            var oldest = _mirrorApprovals.MinBy(static pair => pair.Value.RegisteredAt);
            if (oldest.Key == Guid.Empty)
                throw new InvalidOperationException("The mirror approval registry could not be bounded safely.");
            _mirrorApprovals.Remove(oldest.Key);
        }
    }

    private static T Required<T>(JsonElement element) where T : class =>
        element.Deserialize<T>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException($"A {typeof(T).Name} payload is required.");

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, FramedJsonStream.SerializerOptions);

    private sealed record StoredMirrorPreview(Guid SessionId, MirrorDefinition Definition, MirrorPreview Preview);
    private sealed record StoredMirrorApproval(
        Guid SessionId,
        string DefinitionFingerprint,
        string ReviewFingerprint,
        byte[] ApprovalTokenHash,
        bool DeletionsApproved,
        DateTimeOffset RegisteredAt,
        DateTimeOffset ExpiresAt,
        MirrorApproveResult? Result = null);
    private sealed record TransferRetryContext(Guid SessionId, TransferPlan Plan);
    private sealed record StoredTransferSubmission(
        TransferEnqueueRequest Request,
        DateTimeOffset SubmittedAt,
        TransferEnqueueResult? Result = null,
        ExceptionDispatchInfo? Failure = null);
    private sealed class TransferSkippedException : Exception { }
    private sealed class TrackedJobCancellation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private int _leases;
        private bool _completed;

        public TrackedJobCancellation(CancellationToken lifetime) =>
            _source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        public CancellationToken Token => _source.Token;

        public Lease? TryAcquire()
        {
            lock (_gate)
            {
                if (_completed) return null;
                _leases++;
                return new(this);
            }
        }

        public void Complete()
        {
            var dispose = false;
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                dispose = _leases == 0;
            }
            if (dispose) _source.Dispose();
        }

        private void Release()
        {
            var dispose = false;
            lock (_gate)
            {
                if (_leases <= 0) throw new InvalidOperationException("The tracked cancellation lease count is inconsistent.");
                _leases--;
                dispose = _completed && _leases == 0;
            }
            if (dispose) _source.Dispose();
        }

        internal sealed class Lease(TrackedJobCancellation owner) : IDisposable
        {
            private TrackedJobCancellation? _owner = owner;

            public void Cancel() => (_owner ?? throw new ObjectDisposedException(nameof(Lease)))._source.Cancel();

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed record StoredBrowseSnapshot(
        Guid Id,
        PaneLocation Location,
        Guid? SessionId,
        DateTimeOffset CreatedAt,
        ImmutableArray<FileEntry> Entries);

    private sealed record StoredRemoteTransferPlan(
        RemoteTransferPlan Plan,
        ConnectionIdentity SourceIdentity,
        ConnectionIdentity DestinationIdentity,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        RemoteTransferEnqueueResult? Result = null,
        bool Consumed = false);
}
