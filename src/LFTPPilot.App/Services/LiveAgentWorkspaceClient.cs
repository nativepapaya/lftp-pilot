using System.Collections.Immutable;
using System.Text.Json;
using LFTPPilot.App.Models;
using LFTPPilot.Core;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.App.Services;

public sealed class LiveAgentWorkspaceClient : IAgentWorkspaceClient
{
    private const int BrowsePageSize = 512;
    private const int MaximumBrowsePages = 2_048;
    private const int MaximumBrowseEntries = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly AgentProcessManager _processManager;
    private readonly IAppUpdateService _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private NamedPipeEngineClient? _client;
    private int? _agentProcessId;
    private Task? _eventPump;
    private bool _connected;
    private volatile bool _disposed;
    private volatile bool _stopping;
    private bool _hasConnected;
    private int? _eventSequenceProcessId;
    private long _lastEventSequence;

    public LiveAgentWorkspaceClient(AgentProcessManager processManager, IAppUpdateService updates)
    {
        _processManager = processManager;
        _updates = updates;
    }

    public event EventHandler<EngineEvent>? EventReceived;
    public event EventHandler? StateInvalidated;
    public bool IsConnected => _connected;

    public async Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var bootstrap = await RequestAsync<WorkspaceBootstrap>(WorkspaceMethods.Bootstrap, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        await RequestAsync<ConnectionProfile>(WorkspaceMethods.ProfileSave, new ProfileSaveRequest(profile, credential), cancellationToken).ConfigureAwait(false);

    public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        await RequestAsync<bool>(WorkspaceMethods.ProfileDelete, new ProfileDeleteRequest(profileId), cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await RequestAsync<SessionSnapshot>(WorkspaceMethods.SessionConnect,
            new SessionConnectRequest(profile.Id, ephemeralCredential), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        var local = await BrowseAsync(snapshot.SessionId, PaneKind.Local, snapshot.LocalLocation.Path, cancellationToken).ConfigureAwait(false);
        var remote = await BrowseAsync(snapshot.SessionId, PaneKind.Remote, snapshot.RemoteLocation.Path, cancellationToken).ConfigureAwait(false);
        return new(snapshot, local, remote);
    }

    public async Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        await RequestAsync<bool>(WorkspaceMethods.SessionDisconnect, new SessionDisconnectRequest(sessionId), cancellationToken).ConfigureAwait(false);

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
                new BrowseRequest(sessionId, path, ContinuationToken: continuationToken, PageSize: BrowsePageSize), cancellationToken).ConfigureAwait(false);
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
            new CreateDirectoryRequest(pane, path, sessionId), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);

    public async Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        await RequestAsync<FileMutationResult>(WorkspaceMethods.FileMove,
            new MoveEntryRequest(pane, sourcePath, destinationPath, sessionId), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);

    public async Task<FileMutationResult> DeleteEntriesAsync(
        Guid sessionId,
        PaneKind pane,
        IReadOnlyList<string> paths,
        bool recursive,
        bool confirmed,
        CancellationToken cancellationToken = default) =>
        await RequestAsync<FileMutationResult>(WorkspaceMethods.FileDelete,
            new DeleteEntriesRequest(pane, paths.ToImmutableArray(), sessionId, recursive, confirmed), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);

    public async Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync<TransferEnqueueResult>(WorkspaceMethods.TransferEnqueue,
            new TransferEnqueueRequest(sessionId, plan), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        var result = await RequestAsync<JobCancelResult>("jobs.cancel", new JobCancelRequest(jobId, "Cancelled by user"), cancellationToken).ConfigureAwait(false);
        return result.Cancelled;
    }

    public async Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        var result = await RequestAsync<JobRetryResult>(WorkspaceMethods.JobRetry,
            new JobRetryRequest(jobId), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default)
    {
        var sessionId = await FindSessionIdAsync(definition.ProfileId, cancellationToken).ConfigureAwait(false);
        var preview = await RequestAsync<MirrorPreview>(WorkspaceMethods.MirrorPreview, new MirrorPreviewRequest(sessionId, definition), cancellationToken).ConfigureAwait(false);
        return new(definition, preview);
    }

    public async Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default)
    {
        var sessionId = await FindSessionIdAsync(preview.Definition.ProfileId, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync<MirrorApproveResult>(WorkspaceMethods.MirrorApprove,
            new MirrorApproveRequest(sessionId, preview.Definition, preview.Preview.Id, preview.Preview.ApprovalToken, deletionsApproved), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        return result.Job;
    }

    public async Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync<ConsoleExecuteResult>(WorkspaceMethods.ConsoleExecute, new ConsoleExecuteRequest(sessionId, command), cancellationToken).ConfigureAwait(false);
        return result.Result.Lines.Select(line => line.Stream == "stderr" ? $"! {line.Line}" : line.Line).ToArray();
    }

    public async Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) =>
        await RequestAsync<RemoteTransferPlan>(WorkspaceMethods.RemoteTransferPlan,
            new RemoteTransferPlanRequest(plan.SourceProfileId, plan.DestinationProfileId, plan.SourcePath, plan.DestinationPath, plan.Overwrite), cancellationToken).ConfigureAwait(false);

    public async Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) =>
        await RequestAsync<RemoteTransferEnqueueResult>(WorkspaceMethods.RemoteTransferEnqueue,
            new RemoteTransferEnqueueRequest(plan), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);

    public async Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var edit = await RequestAsync<RemoteEditSession>(WorkspaceMethods.RemoteEditStart,
            new RemoteEditStartRequest(sessionId, remotePath), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        ValidateRemoteEditSession(edit, sessionId, remotePath);
        return edit;
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
        var result = await RequestAsync<RemoteEditActionResult>(WorkspaceMethods.RemoteEditResolve,
            new RemoteEditResolveRequest(editId, reviewToken, resolution), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
        if (!string.Equals(result.Session.EditId, editId, StringComparison.Ordinal))
            throw new InvalidDataException("The Agent returned an action result for a different managed edit.");
        ValidateManagedCachePath(result.Session.LocalPath);
        return result;
    }

    public async Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default)
    {
        ValidateOpaqueEditValue(editId, nameof(editId));
        return await RequestAsync<bool>(WorkspaceMethods.RemoteEditComplete,
            new RemoteEditCompleteRequest(editId), cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
    }

    public async Task StopAgentAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected) return;
        _stopping = true;
        try
        {
            var result = await RequestAsync<AgentStopResult>(AgentProtocol.StopMethod, cancellationToken: cancellationToken, retryOnDisconnect: false).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        if (_eventPump is not null)
        {
            try { await _eventPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected) return;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected) return;
            Exception? lastError = null;
            var launched = false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = _processManager.FindTrustedRunningAgentProcessIds();
                if (candidates.Count == 0 && !launched)
                {
                    candidates = [_processManager.Launch()];
                    launched = true;
                }

                foreach (var processId in candidates)
                {
                    NamedPipeEngineClient? candidate = null;
                    try
                    {
                        candidate = _agentProcessId == processId ? _client : new NamedPipeEngineClient(processId);
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

                        var isReconnect = _hasConnected;
                        if (!ReferenceEquals(candidate, _client))
                        {
                            var previous = _client;
                            _client = candidate;
                            _agentProcessId = processId;
                            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
                        }
                        _processManager.RecordConnectedProcess(processId);
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
        bool retryOnDisconnect = true)
    {
        if (ensureConnected) await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var client = _client ?? throw new InvalidOperationException("The authenticated Agent connection is unavailable.");
        JsonElement element;
        try
        {
            element = await client.RequestAsync(method, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ensureConnected && exception is IOException or TimeoutException)
        {
            _connected = false;
            if (!retryOnDisconnect) throw;
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            client = _client ?? throw new InvalidOperationException("The authenticated Agent connection is unavailable.");
            element = await client.RequestAsync(method, payload, cancellationToken).ConfigureAwait(false);
        }
        return element.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException($"The Agent returned an empty {typeof(T).Name} response.");
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
        var workspace = await RequestAsync<WorkspaceBootstrap>(WorkspaceMethods.Bootstrap, cancellationToken: cancellationToken).ConfigureAwait(false);
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
}
