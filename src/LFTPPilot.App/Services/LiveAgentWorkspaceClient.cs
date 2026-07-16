using System.Collections.Immutable;
using System.Text.Json;
using LFTPPilot.App.Models;
using LFTPPilot.Core;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.App.Services;

internal sealed class AgentRequestOutcomeUnknownException : IOException
{
    public AgentRequestOutcomeUnknownException(string method, Exception innerException)
        : base(CreateMessage(method), innerException)
    {
        Method = method;
    }

    public string Method { get; }

    private static string CreateMessage(string method)
    {
        var outcome = method switch
        {
            WorkspaceMethods.ProfileSave => "The profile and credential-storage state may have changed",
            WorkspaceMethods.SessionConnect => "A connection may have opened and any submitted credential may have been used",
            WorkspaceMethods.ProfileDelete => "The profile and its sessions may have been removed",
            _ => "The operation may have completed",
        };
        return $"The {method} request may have reached the Agent, but a complete and valid result was not available. {outcome}; the outcome is unknown. Workspace state is being refreshed.";
    }
}

internal sealed class AgentRequestRejectedException : InvalidOperationException
{
    public AgentRequestRejectedException(EngineRequestRejectedException innerException)
        : base(innerException.Message, innerException)
    {
        Method = innerException.Method;
        ErrorCode = innerException.ErrorCode;
    }

    public string Method { get; }
    public string? ErrorCode { get; }
}

public sealed class LiveAgentWorkspaceClient : IAgentWorkspaceClient
{
    private const int BrowsePageSize = 512;
    private const int MaximumBrowsePages = 2_048;
    private const int MaximumBrowseEntries = 1_000_000;
    private const int MaximumMirrorPreviewActions = 10_000;
    private const int MaximumMirrorPreviewTextCharacters = 512 * 1024;
    private const int MaximumMirrorActionValueLength = 4_096;
    private const int MaximumMirrorApprovalTokenLength = 512;
    private static readonly TimeSpan MaximumMirrorPreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumMirrorClockSkew = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
    private readonly IAppUpdateService _updates;
    private readonly Func<IReadOnlyList<int>> _findAgentProcesses;
    private readonly Func<int> _launchAgent;
    private readonly Action<int> _recordConnectedProcess;
    private readonly Func<int, IEngineClient> _createEngineClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _requestAdmission = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _disposeSync = new();
    private IEngineClient? _client;
    private int? _agentProcessId;
    private Task? _eventPump;
    private Task? _disposeTask;
    private bool _connected;
    private volatile bool _disposed;
    private volatile bool _stopping;
    private bool _hasConnected;
    private int? _eventSequenceProcessId;
    private long _lastEventSequence;

    public LiveAgentWorkspaceClient(AgentProcessManager processManager, IAppUpdateService updates)
        : this(
            updates,
            (processManager ?? throw new ArgumentNullException(nameof(processManager))).FindTrustedRunningAgentProcessIds,
            processManager.Launch,
            processManager.RecordConnectedProcess,
            static processId => new NamedPipeEngineClient(processId))
    {
    }

    internal LiveAgentWorkspaceClient(
        IAppUpdateService updates,
        Func<IReadOnlyList<int>> findAgentProcesses,
        Func<int> launchAgent,
        Action<int> recordConnectedProcess,
        Func<int, IEngineClient> createEngineClient)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _findAgentProcesses = findAgentProcesses ?? throw new ArgumentNullException(nameof(findAgentProcesses));
        _launchAgent = launchAgent ?? throw new ArgumentNullException(nameof(launchAgent));
        _recordConnectedProcess = recordConnectedProcess ?? throw new ArgumentNullException(nameof(recordConnectedProcess));
        _createEngineClient = createEngineClient ?? throw new ArgumentNullException(nameof(createEngineClient));
    }

    public event EventHandler<EngineEvent>? EventReceived;
    public event EventHandler? StateInvalidated;
    public bool IsConnected => _connected;

    public async Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bootstrap = await RequestAsync<WorkspaceBootstrap>(WorkspaceMethods.Bootstrap,
            cancellationToken: cancellationToken, retryOnDisconnect: true).ConfigureAwait(false);
        var sessions = new List<WorkspaceSessionSeed>();
        foreach (var snapshot in bootstrap.Sessions)
        {
            var local = await TryBrowseAsync(snapshot.SessionId, PaneKind.Local, snapshot.LocalLocation.Path, cancellationToken).ConfigureAwait(false);
            var remote = await TryBrowseAsync(snapshot.SessionId, PaneKind.Remote, snapshot.RemoteLocation.Path, cancellationToken).ConfigureAwait(false);
            sessions.Add(new(snapshot, local, remote));
        }

        var runtimeStatus = bootstrap.Runtime.Available
            ? bootstrap.Runtime.Authenticated ? "Agent connected · LFTP runtime authenticated" : "Agent connected · LFTP runtime is not authenticated"
            : $"Agent connected · LFTP runtime unavailable: {bootstrap.Runtime.Error}";
        IReadOnlyList<ActivityLogEntry> log =
        [
            new(DateTimeOffset.Now, bootstrap.Runtime.Available ? "Info" : "Error", "Agent", runtimeStatus),
        ];
        return new(bootstrap.Profiles, sessions, bootstrap.Jobs, bootstrap.RemoteEdits, [], log, false, runtimeStatus);
    }

    public async Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default) =>
        await RequestAsync<ConnectionProfile>(WorkspaceMethods.ProfileSave,
            new ProfileSaveRequest(profile, credential), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: response => ValidateSavedProfile(profile, response)).ConfigureAwait(false);

    public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        await RequestAsync<bool>(WorkspaceMethods.ProfileDelete,
            new ProfileDeleteRequest(profileId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);

    public async Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        var expectedIdentity = ConnectionIdentity.FromProfile(profile);
        var inspection = await RequestAsync<SftpHostKeyInspection>(WorkspaceMethods.SftpHostKeyInspect,
            new SftpHostKeyInspectRequest(expectedIdentity), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        SftpHostKeyWireValidation.ValidateInspection(inspection, expectedIdentity);
        return inspection;
    }

    public async Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(
        SftpHostKeyReview review,
        bool replaceExisting,
        CancellationToken cancellationToken = default)
    {
        SftpHostKeyWireValidation.ValidateReview(review, review.ProfileId);
        if (review.State == SftpHostKeyState.EnrollmentRequired && replaceExisting)
            throw new ArgumentException("A new host-key enrollment cannot replace an existing key.", nameof(replaceExisting));
        if (review.State == SftpHostKeyState.Changed && !replaceExisting)
            throw new ArgumentException("A changed host key requires an explicit replacement decision.", nameof(replaceExisting));

        return await RequestAsync<SftpHostKeyApproveResult>(WorkspaceMethods.SftpHostKeyApprove,
            new SftpHostKeyApproveRequest(review.ProfileId, review.ReviewId, review.ApprovalToken, replaceExisting),
            cancellationToken, retryOnDisconnect: false, semantics: RequestSemantics.Mutation,
            responseValidator: result =>
            {
                if (result.ProfileId != review.ProfileId ||
                    !string.Equals(result.Endpoint, review.Endpoint, StringComparison.Ordinal) ||
                    !string.Equals(result.FingerprintSha256, review.PresentedFingerprintSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The Agent approved a different SFTP host key than the one reviewed by the user.");
                }
            }).ConfigureAwait(false);
    }

    public async Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            var inspection = await InspectSftpHostKeyAsync(profile, cancellationToken).ConfigureAwait(false);
            if (inspection.State != SftpHostKeyState.Trusted)
                throw new InvalidOperationException("This SFTP connection requires explicit host-key review in Connections before any credential can be sent.");
        }

        var snapshot = await RequestAsync<SessionSnapshot>(WorkspaceMethods.SessionConnect,
            new SessionConnectRequest(ConnectionIdentity.FromProfile(profile), ephemeralCredential), cancellationToken,
            retryOnDisconnect: false, semantics: RequestSemantics.Mutation,
            responseValidator: result =>
            {
                if (result.SessionId == Guid.Empty || result.ProfileId != profile.Id || !result.IsConnected ||
                    string.IsNullOrWhiteSpace(result.DisplayName) ||
                    result.LocalLocation.Kind != PaneKind.Local || string.IsNullOrWhiteSpace(result.LocalLocation.Path) ||
                    result.RemoteLocation.Kind != PaneKind.Remote || string.IsNullOrWhiteSpace(result.RemoteLocation.Path))
                {
                    throw new InvalidDataException("The Agent returned an invalid or mismatched connected session.");
                }
            }).ConfigureAwait(false);
        // SessionConnect is the commit point. A pane hydration failure must not hide a
        // confirmed session (or cause the App to submit the credential/connect again).
        // TryBrowseAsync preserves caller cancellation while degrading ordinary browse
        // failures to an empty pane; transport loss also invalidates state in RequestAsync.
        var local = await TryBrowseAsync(snapshot.SessionId, PaneKind.Local, snapshot.LocalLocation.Path, cancellationToken).ConfigureAwait(false);
        var remote = await TryBrowseAsync(snapshot.SessionId, PaneKind.Remote, snapshot.RemoteLocation.Path, cancellationToken).ConfigureAwait(false);
        return new(snapshot, local, remote);
    }

    public async Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await RequestAsync<bool>(WorkspaceMethods.SessionDisconnect,
            new SessionDisconnectRequest(sessionId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);

    public async Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default)
    {
        var method = pane == PaneKind.Local ? WorkspaceMethods.BrowseLocal : WorkspaceMethods.BrowseRemote;
        var entries = new List<FileEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;
        int? totalCount = null;
        for (var page = 0; page < MaximumBrowsePages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RequestAsync<BrowseResult>(method,
                new BrowseRequest(sessionId, path, ContinuationToken: continuationToken, PageSize: BrowsePageSize),
                cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
            if (result.Location.Kind != pane) throw new InvalidDataException("The Agent returned a directory page for the wrong pane.");
            if (result.TotalCount is < 0 or > MaximumBrowseEntries) throw new InvalidDataException("The directory contains more entries than the App safety limit.");
            if (result.Entries.Length > BrowsePageSize) throw new InvalidDataException("The Agent returned an oversized directory page.");
            totalCount ??= result.TotalCount;
            if (result.TotalCount != totalCount) throw new InvalidDataException("The directory changed while its stable page snapshot was being read.");
            entries.AddRange(result.Entries);
            if (entries.Count > MaximumBrowseEntries || entries.Count > totalCount)
                throw new InvalidDataException("The Agent returned an invalid directory page count.");

            continuationToken = result.ContinuationToken;
            if (continuationToken is null)
            {
                if (entries.Count != totalCount) throw new InvalidDataException("The Agent returned an incomplete directory snapshot.");
                return entries;
            }
            if (continuationToken.Length > 512) throw new InvalidDataException("The Agent returned an oversized directory continuation token.");
            if (!seenTokens.Add(continuationToken)) throw new InvalidDataException("The Agent repeated a directory continuation token.");
        }

        throw new InvalidDataException("The directory exceeded the maximum number of browse pages.");
    }

    public async Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) =>
        await RequestAsync<FileMutationResult>(WorkspaceMethods.FileCreateDirectory,
            new CreateDirectoryRequest(pane, path, sessionId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);

    public async Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        await RequestAsync<FileMutationResult>(WorkspaceMethods.FileMove,
            new MoveEntryRequest(pane, sourcePath, destinationPath, sessionId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);

    public async Task<FileMutationResult> DeleteEntriesAsync(
        Guid sessionId,
        PaneKind pane,
        IReadOnlyList<string> paths,
        bool recursive,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        await RequestAsync<FileMutationResult>(WorkspaceMethods.FileDelete,
            new DeleteEntriesRequest(pane, paths.ToImmutableArray(), sessionId, recursive, confirmed), cancellationToken,
            retryOnDisconnect: false, semantics: RequestSemantics.Mutation).ConfigureAwait(false);

    public async Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync<TransferEnqueueResult>(WorkspaceMethods.TransferEnqueue,
            new TransferEnqueueRequest(sessionId, plan), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: response => ValidateMutationJob(response.Job, plan.Id, JobKind.Transfer, plan.ProfileId)).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        var result = await RequestAsync<JobCancelResult>("jobs.cancel",
            new JobCancelRequest(jobId, "Cancelled by user"), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);
        return result.Cancelled;
    }

    public async Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        var result = await RequestAsync<JobRetryResult>(WorkspaceMethods.JobRetry,
            new JobRetryRequest(jobId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: response => ValidateMutationJob(response.Job, jobId, JobKind.Transfer)).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default)
    {
        var sessionId = await FindSessionIdAsync(definition.ProfileId, cancellationToken).ConfigureAwait(false);
        var preview = await RequestAsync<MirrorPreview>(WorkspaceMethods.MirrorPreview,
            new MirrorPreviewRequest(sessionId, definition), cancellationToken, retryOnDisconnect: false,
            responseValidator: response => ValidateMirrorPreview(definition, response, requireFresh: true)).ConfigureAwait(false);
        return new(definition, preview);
    }

    public async Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default)
    {
        ValidateMirrorPreview(preview.Definition, preview.Preview, requireFresh: false);
        var sessionId = await FindSessionIdAsync(preview.Definition.ProfileId, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync<MirrorApproveResult>(WorkspaceMethods.MirrorApprove,
            new MirrorApproveRequest(
                sessionId,
                preview.Definition,
                preview.Preview.Id,
                preview.Preview.ApprovalToken,
                MirrorPlanner.ReviewFingerprint(preview.Preview),
                deletionsApproved),
            cancellationToken, retryOnDisconnect: false, semantics: RequestSemantics.Mutation,
            responseValidator: response => ValidateMutationJob(
                response.Job, preview.Preview.Id, JobKind.Mirror, preview.Definition.ProfileId)).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync<ConsoleExecuteResult>(WorkspaceMethods.ConsoleExecute,
            new ConsoleExecuteRequest(sessionId, command), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        return result.Result.Lines.Select(line => line.Stream == "stderr" ? $"! {line.Line}" : line.Line).ToArray();
    }

    public async Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
    {
        var request = new RemoteTransferPlanRequest(
            plan.SourceProfileId,
            plan.DestinationProfileId,
            plan.SourcePath,
            plan.DestinationPath,
            plan.Overwrite);
        return await RequestAsync<RemoteTransferPlan>(WorkspaceMethods.RemoteTransferPlan,
            request, cancellationToken, retryOnDisconnect: false,
            responseValidator: response => ValidateRemoteTransferPlan(response, request)).ConfigureAwait(false);
    }

    public async Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) =>
        await RequestAsync<RemoteTransferEnqueueResult>(WorkspaceMethods.RemoteTransferEnqueue,
            new RemoteTransferEnqueueRequest(plan), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: response =>
            {
                ValidateMutationJob(response.Job, plan.Id, JobKind.RemoteTransfer, plan.SourceProfileId);
                if (response.Mode != plan.Mode)
                    throw new InvalidDataException("The Agent returned a remote-transfer routing mode that did not match the reviewed plan.");
            }).ConfigureAwait(false);

    public async Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        return await RequestAsync<RemoteEditSession>(WorkspaceMethods.RemoteEditStart,
            new RemoteEditStartRequest(sessionId, remotePath), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: edit => ValidateRemoteEditSession(edit, sessionId, remotePath)).ConfigureAwait(false);
    }

    public async Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default)
    {
        ValidateOpaqueEditValue(editId, nameof(editId));
        var review = await RequestAsync<RemoteEditReview>(WorkspaceMethods.RemoteEditReview,
            new RemoteEditReviewRequest(editId), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        if (!string.Equals(review.EditId, editId, StringComparison.Ordinal))
            throw new InvalidDataException("The Agent returned a review for a different managed edit.");
        ValidateOpaqueEditValue(review.ReviewToken, "reviewToken");
        return review;
    }

    public async Task<RemoteEditActionResult> ResolveRemoteEditAsync(
        string editId,
        string reviewToken,
        RemoteEditResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ValidateOpaqueEditValue(editId, nameof(editId));
        ValidateOpaqueEditValue(reviewToken, nameof(reviewToken));
        if (!Enum.IsDefined(resolution)) throw new ArgumentOutOfRangeException(nameof(resolution));
        return await RequestAsync<RemoteEditActionResult>(WorkspaceMethods.RemoteEditResolve,
            new RemoteEditResolveRequest(editId, reviewToken, resolution), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation,
            responseValidator: result =>
            {
                if (!string.Equals(result.Session.EditId, editId, StringComparison.Ordinal))
                    throw new InvalidDataException("The Agent returned an action result for a different managed edit.");
                ValidateManagedCachePath(result.Session.LocalPath);
            }).ConfigureAwait(false);
    }

    public async Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default)
    {
        ValidateOpaqueEditValue(editId, nameof(editId));
        return await RequestAsync<bool>(WorkspaceMethods.RemoteEditComplete,
            new RemoteEditCompleteRequest(editId), cancellationToken, retryOnDisconnect: false,
            semantics: RequestSemantics.Mutation).ConfigureAwait(false);
    }

    public async Task StopAgentAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected) return;
        _stopping = true;
        try
        {
            var result = await RequestAsync<AgentStopResult>(AgentProtocol.StopMethod, cancellationToken: cancellationToken,
                retryOnDisconnect: false, semantics: RequestSemantics.Mutation).ConfigureAwait(false);
            if (!result.Stopping) throw new InvalidOperationException("The Agent declined the shutdown request.");
        }
        finally
        {
            _connected = false;
        }
    }

    public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => _updates.CheckAsync(cancellationToken);
    public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) => _updates.OpenInstallerAsync(cancellationToken);

    private static void ValidateRemoteEditSession(RemoteEditSession edit, Guid expectedSessionId, string expectedRemotePath)
    {
        if (edit.SessionId != expectedSessionId || !string.Equals(edit.RemotePath, expectedRemotePath.TrimEnd('/'), StringComparison.Ordinal))
            throw new InvalidDataException("The Agent returned a managed edit for a different remote file.");
        ValidateOpaqueEditValue(edit.EditId, "editId");
        ValidateManagedCachePath(edit.LocalPath);
        if (!File.Exists(edit.LocalPath) || (File.GetAttributes(edit.LocalPath) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The Agent did not return a regular managed-cache file.");
        if (new FileInfo(edit.LocalPath).Length != edit.Baseline.Size || !string.Equals(edit.Baseline.CanonicalPath, edit.RemotePath, StringComparison.Ordinal))
            throw new InvalidDataException("The Agent returned a managed copy that did not match its remote baseline.");
    }

    private static void ValidateManagedCachePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The Agent returned an invalid managed-cache path.");
        var root = Path.GetFullPath(PackageDataPaths.CreateDefault().RemoteEdits);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (relative.Length == 0 || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("The Agent returned a path outside its package-scoped managed cache.");
    }

    private static void ValidateOpaqueEditValue(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 32 or > 128 || value.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("A valid opaque remote-edit value is required.", parameterName);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            _lifetime.Cancel();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_eventPump is not null)
        {
            try { await _eventPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _requestAdmission.WaitAsync().ConfigureAwait(false);
        try
        {
            // Request admission owns every EnsureConnectedAsync call. Taking both gates closes
            // late admission and proves no connection candidate can still be installed.
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var client = _client;
                _client = null;
                _connected = false;
                _agentProcessId = null;
                if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                _lifetime.Dispose();
            }
            finally
            {
                _connectGate.Release();
            }
        }
        finally
        {
            // Keep both semaphores alive: a caller that passed its first disposed check before
            // shutdown may still be queued and must reach the second check safely.
            _requestAdmission.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connected) return;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connected) return;
            Exception? lastError = null;
            var launched = false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = _findAgentProcesses();
                if (candidates.Count == 0 && !launched)
                {
                    candidates = [_launchAgent()];
                    launched = true;
                }

                foreach (var processId in candidates)
                {
                    IEngineClient? candidate = null;
                    try
                    {
                        candidate = _agentProcessId == processId ? _client : _createEngineClient(processId);
                        if (candidate is null) continue;
                        using var quick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        quick.CancelAfter(TimeSpan.FromMilliseconds(500));
                        var pingElement = await candidate.RequestAsync("ping", cancellationToken: quick.Token).ConfigureAwait(false);
                        var ping = pingElement.Deserialize<AgentPing>(JsonOptions)
                            ?? throw new InvalidDataException("The Agent returned an empty ping response.");
                        if (ping.ProtocolVersion != AgentProtocol.CurrentVersion)
                            throw new InvalidDataException("The Agent protocol version does not match this application.");
                        if (ping.ProcessId != processId)
                            throw new UnauthorizedAccessException("The Agent response did not match the authenticated named-pipe server.");
                        cancellationToken.ThrowIfCancellationRequested();

                        var isReconnect = _hasConnected;
                        if (!ReferenceEquals(candidate, _client))
                        {
                            var previous = _client;
                            _client = candidate;
                            _agentProcessId = processId;
                            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
                        }
                        _recordConnectedProcess(processId);
                        _connected = true;
                        _hasConnected = true;
                        if (_eventSequenceProcessId != processId)
                        {
                            _eventSequenceProcessId = processId;
                            Interlocked.Exchange(ref _lastEventSequence, 0);
                        }
                        _eventPump ??= PumpEventsAsync(_lifetime.Token);
                        if (isReconnect) SignalStateInvalidated();
                        return;
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        lastError = exception;
                        if (!ReferenceEquals(candidate, _client) && candidate is not null)
                            await candidate.DisposeAsync().ConfigureAwait(false);
                    }
                }

                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The LFTP Pilot Agent did not accept a trusted pipe connection.", lastError);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<T> RequestAsync<T>(
        string method,
        object? payload = null,
        CancellationToken cancellationToken = default,
        bool ensureConnected = true,
        bool retryOnDisconnect = false,
        RequestSemantics semantics = RequestSemantics.ReadOnly,
        Action<T>? responseValidator = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var operationToken = operationCancellation.Token;
            if (ensureConnected) await EnsureConnectedAsync(operationToken).ConfigureAwait(false);
            var client = _client ?? throw new InvalidOperationException("The authenticated Agent connection is unavailable.");
            JsonElement element;
            try { element = await SubmitAsync(client).ConfigureAwait(false); }
            catch (Exception exception) when (ensureConnected && exception is IOException or TimeoutException)
            {
                _connected = false;
                SignalStateInvalidated();
                if (!retryOnDisconnect)
                    throw new AgentRequestOutcomeUnknownException(method, exception);
                await EnsureConnectedAsync(operationToken).ConfigureAwait(false);
                client = _client ?? throw new InvalidOperationException("The authenticated Agent connection is unavailable.");
                element = await SubmitAsync(client).ConfigureAwait(false);
            }
            try
            {
                var result = element.Deserialize<T>(JsonOptions) ??
                    throw new InvalidDataException($"The Agent returned an empty {typeof(T).Name} response.");
                responseValidator?.Invoke(result);
                return result;
            }
            catch (Exception exception) when (semantics == RequestSemantics.Mutation && !IsFatalRuntimeException(exception))
            {
                SignalStateInvalidated();
                throw new AgentRequestOutcomeUnknownException(method, exception);
            }

            async Task<JsonElement> SubmitAsync(IEngineClient engineClient)
            {
                try
                {
                    return await engineClient.RequestAsync(method, payload, operationToken).ConfigureAwait(false);
                }
                catch (EngineRequestRejectedException exception)
                {
                    throw new AgentRequestRejectedException(exception);
                }
            }
        }
        finally
        {
            _requestAdmission.Release();
        }
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = _client;
                if (client is null)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await foreach (var engineEvent in client.Events(cancellationToken).ConfigureAwait(false))
                {
                    var previous = Interlocked.Exchange(ref _lastEventSequence, engineEvent.Sequence);
                    if (previous > 0 && engineEvent.Sequence != previous + 1)
                        SignalStateInvalidated();
                    EventReceived?.Invoke(this, engineEvent);
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _lastEventSequence, 0);
                    SignalStateInvalidated();
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Interlocked.Exchange(ref _lastEventSequence, 0);
                SignalStateInvalidated();
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<FileEntry>> TryBrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken)
    {
        try { return await BrowseAsync(sessionId, pane, path, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or DirectoryNotFoundException) { return []; }
    }

    private async Task<Guid> FindSessionIdAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var workspace = await RequestAsync<WorkspaceBootstrap>(WorkspaceMethods.Bootstrap,
            cancellationToken: cancellationToken, retryOnDisconnect: true).ConfigureAwait(false);
        return workspace.Sessions.FirstOrDefault(session => session.ProfileId == profileId)?.SessionId
            ?? throw new InvalidOperationException("Connect this profile before using the operation.");
    }

    private void SignalStateInvalidated()
    {
        if (!_stopping && !_disposed) StateInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private sealed record AgentPing(int ProtocolVersion, int ProcessId, int ClientProcessId);
    private sealed record AgentStopResult(bool Stopping);
    private sealed record JobCancelRequest(Guid JobId, string? Reason);
    private sealed record JobCancelResult(bool Cancelled);

    private enum RequestSemantics
    {
        ReadOnly,
        Mutation,
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private static void ValidateMutationJob(
        JobSnapshot job,
        Guid expectedJobId,
        JobKind expectedKind,
        Guid? expectedProfileId = null)
    {
        if (job.Id != expectedJobId || job.Kind != expectedKind ||
            job.ProfileId is not { } actualProfileId || actualProfileId == Guid.Empty ||
            (expectedProfileId is { } profileId && actualProfileId != profileId) ||
            string.IsNullOrWhiteSpace(job.DisplayName) || job.CreatedAt == default || job.UpdatedAt == default)
        {
            throw new InvalidDataException("The Agent returned a job that did not match the submitted mutation.");
        }
    }

    private static void ValidateSavedProfile(ConnectionProfile submitted, ConnectionProfile response)
    {
        if (response.Id != submitted.Id ||
            !string.Equals(response.Name, submitted.Name, StringComparison.Ordinal) ||
            response.Protocol != submitted.Protocol ||
            !string.Equals(response.Host, submitted.Host, StringComparison.Ordinal) ||
            response.Port != submitted.Port ||
            !string.Equals(response.UserName, submitted.UserName, StringComparison.Ordinal) ||
            response.Authentication != submitted.Authentication ||
            !string.Equals(response.SshKeyPath, submitted.SshKeyPath, StringComparison.Ordinal) ||
            !string.Equals(response.InitialRemotePath, submitted.InitialRemotePath, StringComparison.Ordinal) ||
            !string.Equals(response.InitialLocalPath, submitted.InitialLocalPath, StringComparison.Ordinal) ||
            !response.EffectiveBookmarks.SequenceEqual(submitted.EffectiveBookmarks, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The Agent returned a profile that did not exactly match the submitted profile.");
        }
    }

    private static void ValidateRemoteTransferPlan(
        RemoteTransferPlan plan,
        RemoteTransferPlanRequest request)
    {
        try
        {
            PlanValidator.Validate(plan);
        }
        catch (ModelValidationException exception)
        {
            throw new InvalidDataException("The Agent returned an invalid remote-transfer route plan.", exception);
        }

        if (plan.SourceProfileId != request.SourceProfileId ||
            plan.DestinationProfileId != request.DestinationProfileId ||
            !string.Equals(plan.SourcePath, request.SourcePath, StringComparison.Ordinal) ||
            !string.Equals(plan.DestinationPath, request.DestinationPath, StringComparison.Ordinal) ||
            plan.Overwrite != request.Overwrite)
        {
            throw new InvalidDataException("The Agent returned a remote-transfer route plan that did not match the requested profiles, paths, or overwrite policy.");
        }
    }

    private static void ValidateMirrorPreview(MirrorDefinition definition, MirrorPreview preview, bool requireFresh)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(preview);
        var now = DateTimeOffset.UtcNow;
        if (preview.Id == Guid.Empty || preview.DefinitionId != definition.Id ||
            !string.Equals(preview.DefinitionFingerprint, MirrorPlanner.Fingerprint(definition), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Agent returned a mirror preview for a different definition.");
        }
        if (preview.ExpiresAt <= preview.GeneratedAt ||
            preview.ExpiresAt - preview.GeneratedAt > MaximumMirrorPreviewLifetime ||
            (requireFresh && (preview.GeneratedAt > now + MaximumMirrorClockSkew ||
                preview.GeneratedAt < now - MaximumMirrorPreviewLifetime || preview.ExpiresAt <= now)))
        {
            throw new InvalidDataException("The Agent returned a stale or invalid mirror preview lifetime.");
        }
        if (preview.Actions.IsDefault || preview.Actions.Length > MaximumMirrorPreviewActions)
            throw new InvalidDataException("The Agent returned an invalid number of mirror preview actions.");

        var textCharacters = 0;
        foreach (var action in preview.Actions)
        {
            if (action is null || !Enum.IsDefined(action.Kind) ||
                !IsBoundedMirrorValue(action.Path, allowNull: false) ||
                !IsBoundedMirrorValue(action.Detail, allowNull: true))
            {
                throw new InvalidDataException("The Agent returned an invalid mirror preview action.");
            }
            textCharacters += action.Path.Length + (action.Detail?.Length ?? 0);
            if (textCharacters > MaximumMirrorPreviewTextCharacters)
                throw new InvalidDataException("The Agent returned an oversized mirror preview.");
        }
        if (string.IsNullOrWhiteSpace(preview.ApprovalToken) ||
            preview.ApprovalToken.Length > MaximumMirrorApprovalTokenLength ||
            preview.ApprovalToken.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException("The Agent returned an invalid mirror approval token.");
        }
    }

    private static bool IsBoundedMirrorValue(string? value, bool allowNull)
    {
        if (value is null) return allowNull;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumMirrorActionValueLength &&
            !value.Any(static character => char.IsControl(character));
    }
}
