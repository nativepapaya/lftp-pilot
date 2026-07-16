using System.Collections.Concurrent;
using System.Collections.Immutable;
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
    private static readonly TimeSpan BrowseSnapshotLifetime = TimeSpan.FromMinutes(2);
    private readonly IProfileStore _profileStore;
    private readonly ISecretStore _secretStore;
    private readonly JobCoordinator _jobs;
    private readonly SessionRegistry _sessions;
    private readonly ILftpRuntimeProvider _runtimeProvider;
    private readonly IMirrorPlanner _mirrorPlanner;
    private readonly AgentWorkspaceOptions _options;
    private readonly Action<EngineEventKind, string, object?, Guid?, Guid?>? _publish;
    private readonly RunOnceScheduler? _scheduler;
    private readonly RemoteEditManager _remoteEdits;
    private readonly ConcurrentDictionary<Guid, StoredMirrorPreview> _previews = [];
    private readonly ConcurrentDictionary<Guid, StoredBrowseSnapshot> _browseSnapshots = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _jobCancellations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _operationGate = new();
    private readonly HashSet<Task> _operations = [];
    private bool _disposed;

    public AgentWorkspaceService(
        IProfileStore profileStore,
        ISecretStore secretStore,
        ILftpProcessHost processHost,
        ILftpRuntimeProvider runtimeProvider,
        JobCoordinator jobs,
        IMirrorPlanner mirrorPlanner,
        AgentWorkspaceOptions options,
        Action<EngineEventKind, string, object?, Guid?, Guid?>? publish = null,
        RunOnceScheduler? scheduler = null)
    {
        _profileStore = profileStore;
        _secretStore = secretStore;
        _jobs = jobs;
        _runtimeProvider = runtimeProvider;
        _mirrorPlanner = mirrorPlanner;
        _options = options;
        _publish = publish;
        _scheduler = scheduler;
        _sessions = new(processHost, runtimeProvider, options);
        _remoteEdits = new(
            Path.Combine(options.CacheRoot, "remote-edits"),
            new LftpRemoteEditTransport(_sessions, options),
            publish: publish);
    }

    public async Task<JsonElement> HandleAsync(string method, JsonElement arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return method switch
        {
            WorkspaceMethods.Bootstrap => ToJson(await BootstrapAsync(cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.ProfileList => ToJson(await ListProfilesAsync(cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.ProfileSave => ToJson(await SaveProfileAsync(Required<ProfileSaveRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.ProfileDelete => ToJson(await DeleteProfileAsync(Required<ProfileDeleteRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.SessionConnect => ToJson(await ConnectAsync(Required<SessionConnectRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.SessionDisconnect => ToJson(await DisconnectAsync(Required<SessionDisconnectRequest>(arguments)).ConfigureAwait(false)),
            WorkspaceMethods.BrowseLocal => ToJson(await BrowseLocalAsync(Required<BrowseRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.BrowseRemote => ToJson(await BrowseRemoteAsync(Required<BrowseRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.FileCreateDirectory => ToJson(await CreateDirectoryAsync(Required<CreateDirectoryRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.FileMove => ToJson(await MoveEntryAsync(Required<MoveEntryRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.FileDelete => ToJson(await DeleteEntriesAsync(Required<DeleteEntriesRequest>(arguments), cancellationToken).ConfigureAwait(false)),
            WorkspaceMethods.TransferEnqueue => ToJson(await EnqueueTransferAsync(Required<TransferEnqueueRequest>(arguments), cancellationToken).ConfigureAwait(false)),
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
        return new(AgentProtocol.CurrentVersion, runtime,
            (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToImmutableArray(),
            _sessions.GetSnapshots().ToImmutableArray(),
            _jobs.GetJobs().ToImmutableArray(),
            _remoteEdits.GetSnapshots().ToImmutableArray());
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
        var previous = (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(profile => profile.Id == request.Profile.Id);
        var identityChanged = previous is not null && !SecretBindingFor(previous).Equals(SecretBindingFor(request.Profile));
        await _profileStore.SaveAsync(request.Profile, cancellationToken).ConfigureAwait(false);
        if (identityChanged) await _secretStore.DeleteAsync(request.Profile.Id, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(request.Credential))
        {
            await _secretStore.SaveAsync(new(SecretBindingFor(request.Profile), request.Credential), cancellationToken).ConfigureAwait(false);
        }
        _publish?.Invoke(EngineEventKind.Session, "profile.saved", request.Profile, null, null);
        return request.Profile;
    }

    public async Task<bool> DeleteProfileAsync(ProfileDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile identifier is required.", nameof(request));
        ThrowIfProfileHasActiveRemoteEdits(request.ProfileId);
        ThrowIfProfileHasDependentJobs(request.ProfileId);
        await _sessions.DisconnectProfileAsync(request.ProfileId).ConfigureAwait(false);
        await _profileStore.DeleteAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        await _secretStore.DeleteAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        _publish?.Invoke(EngineEventKind.Session, "profile.deleted", request.ProfileId, null, null);
        return true;
    }

    public async Task<SessionSnapshot> ConnectAsync(SessionConnectRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProfileId == Guid.Empty) throw new ArgumentException("A profile identifier is required.", nameof(request));
        ValidateCredential(request.EphemeralCredential);
        var profile = await FindProfileAsync(request.ProfileId, cancellationToken).ConfigureAwait(false);
        var credential = await ResolveCredentialAsync(profile, request.EphemeralCredential, cancellationToken).ConfigureAwait(false);
        var snapshot = await _sessions.ConnectAsync(profile, credential, cancellationToken).ConfigureAwait(false);
        _publish?.Invoke(EngineEventKind.Session, "session.connected", snapshot, null, snapshot.SessionId);
        return snapshot;
    }

    public async Task<bool> DisconnectAsync(SessionDisconnectRequest request)
    {
        if (request.SessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(request));
        var session = _sessions.Get(request.SessionId);
        if (_remoteEdits.HasActiveSession(request.SessionId))
            throw new InvalidOperationException("Finish or cancel every active remote edit for this session before disconnecting it.");
        ThrowIfProfileHasDependentJobs(session.Profile.Id);
        var disconnected = await _sessions.DisconnectAsync(request.SessionId).ConfigureAwait(false);
        if (disconnected) _publish?.Invoke(EngineEventKind.Session, "session.disconnected", request.SessionId, null, request.SessionId);
        return disconnected;
    }

    private void ThrowIfProfileHasDependentJobs(Guid profileId)
    {
        if (_jobs.GetJobs().Any(job => job.ProfileId == profileId &&
            job.State is JobState.Scheduled or JobState.Queued or JobState.Running))
        {
            throw new InvalidOperationException(
                "Cancel scheduled or active jobs for this profile before disconnecting its session or deleting the profile.");
        }
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

    public Task<BrowseResult> BrowseLocalAsync(BrowseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateBrowseRequest(request);
        if (!Path.IsPathFullyQualified(request.Path)) throw new ArgumentException("The local browse path must be fully qualified.", nameof(request));
        var localPath = Path.GetFullPath(request.Path);
        if (request.ContinuationToken is not null)
            return Task.FromResult(ContinueBrowse(request, PaneKind.Local, localPath));
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
        if (request.SessionId is { } sessionId) _sessions.Get(sessionId).SetLocation(PaneKind.Local, localPath);
        return Task.FromResult(CreateBrowsePage(new(PaneKind.Local, localPath), request.SessionId, ordered, request.PageSize));
    }

    public async Task<BrowseResult> BrowseRemoteAsync(BrowseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateBrowseRequest(request);
        if (request.SessionId is not { } sessionId || sessionId == Guid.Empty) throw new ArgumentException("A connected session is required.", nameof(request));
        ValidateRemotePath(request.Path, nameof(request));
        var session = _sessions.Get(sessionId);
        if (request.ContinuationToken is not null)
            return ContinueBrowse(request, PaneKind.Remote, request.Path);
        var result = await session.Browse.ExecuteAsync(LftpCommandBuilder.BuildList(request.Path, request.Fresh), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Remote listing");
        var entries = LftpOutputParser.ParseLongListing(result.Lines.Select(static line => line.Line), request.Path);
        if (entries.Length == 0 && result.Lines.Any(static line => !string.IsNullOrWhiteSpace(line.Line) && !line.Line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)))
        {
            var fallback = await session.Browse.ExecuteAsync(LftpCommandBuilder.BuildNameList(request.Path, request.Fresh), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(fallback, "Remote listing fallback");
            entries = LftpOutputParser.ParseClassifiedNames(fallback.Lines.Select(static line => line.Line), request.Path);
        }
        if (entries.Length > MaximumDirectoryEntries)
            throw new InvalidDataException($"The remote directory contains more than {MaximumDirectoryEntries} entries. Narrow the directory before browsing it.");
        session.SetLocation(PaneKind.Remote, request.Path);
        var ordered = entries.OrderBy(static entry => entry.Kind == EntryKind.Directory ? 0 : 1)
            .ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
        return CreateBrowsePage(new(PaneKind.Remote, request.Path), sessionId, ordered, request.PageSize);
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
        if (await TryStatRemoteAsync(session, remotePath, cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException("A remote entry already exists at the requested directory path.");
        var result = await session.Browse.ExecuteAsync(LftpCommandBuilder.BuildCreateDirectory(remotePath), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Remote directory creation");
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
        _ = await TryStatRemoteAsync(session, remoteSource, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The remote move source was not found.", remoteSource);
        if (await TryStatRemoteAsync(session, remoteDestination, cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException("The remote move destination already exists.");
        var result = await session.Browse.ExecuteAsync(LftpCommandBuilder.BuildMove(remoteSource, remoteDestination), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Remote move");
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
        var remoteEntries = new List<(string Path, FileEntry Entry)>();
        foreach (var path in remotePaths)
        {
            var entry = await TryStatRemoteAsync(remoteSession, path, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("A remote delete target was not found.", path);
            remoteEntries.Add((path, entry));
        }
        var affected = new List<string>();
        try
        {
            foreach (var target in remoteEntries)
            {
                var command = LftpCommandBuilder.BuildDelete(target.Path, target.Entry.IsDirectory, request.Recursive);
                var result = await remoteSession.Browse.ExecuteAsync(command, _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(result, "Remote deletion");
                affected.Add(target.Path);
            }
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
        PlanValidator.Validate(request.Plan);
        var session = _sessions.Get(request.SessionId);
        if (session.Profile.Id != request.Plan.ProfileId) throw new ArgumentException("The transfer profile does not match the connected session.", nameof(request));
        ValidateTransferPaths(request.Plan);
        var now = _scheduler?.UtcNow ?? DateTimeOffset.UtcNow;
        if (request.Plan.RunAt is { } runAt)
        {
            if (_scheduler is null) throw new InvalidOperationException("Run-once transfers require the Agent scheduler.");
            if (runAt <= now) throw new ArgumentException("A run-once transfer requires a future run time.", nameof(request));
            var scheduled = _jobs.Enqueue(new(
                Guid.NewGuid(),
                JobKind.Transfer,
                session.Profile.Id,
                $"{Path.GetFileName(request.Plan.SourcePath)} -> {Path.GetFileName(request.Plan.DestinationPath)}",
                JobState.Scheduled,
                now,
                now,
                RunAt: runAt,
                Status: "Waiting for the selected run-once time."));
            await _scheduler.ScheduleAsync(
                scheduled,
                token => RunScheduledTransferAsync(session, request.Plan, scheduled.Id, token),
                cancellationToken).ConfigureAwait(false);
            return new(scheduled);
        }

        var shouldSkip = request.Plan.Mode == TransferMode.Skip &&
            await DestinationExistsAsync(session, request.Plan, cancellationToken).ConfigureAwait(false);
        var job = _jobs.Enqueue(new(Guid.NewGuid(), JobKind.Transfer, session.Profile.Id,
            $"{Path.GetFileName(request.Plan.SourcePath)} -> {Path.GetFileName(request.Plan.DestinationPath)}",
            JobState.Queued, now, now));
        if (shouldSkip)
        {
            _jobs.Transition(job.Id, JobState.Running, "Checking destination");
            job = _jobs.Transition(job.Id, JobState.Completed, "Skipped because the destination already exists");
            return new(job);
        }
        TrackJob(job.Id, token => RunTransferAsync(session, request.Plan, job.Id, token));
        return new(job);
    }

    public async Task<MirrorPreview> PreviewMirrorAsync(MirrorPreviewRequest request, CancellationToken cancellationToken = default)
    {
        PlanValidator.Validate(request.Definition);
        var session = _sessions.Get(request.SessionId);
        if (session.Profile.Id != request.Definition.ProfileId) throw new ArgumentException("The mirror profile does not match the connected session.", nameof(request));
        await using var previewSession = await session.CreateEphemeralAsync("mirror-preview", cancellationToken).ConfigureAwait(false);
        var result = await previewSession.ExecuteAsync(LftpCommandBuilder.BuildMirror(request.Definition, dryRun: true), _options.MirrorPreviewTimeout, cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Mirror preview");
        var preview = _mirrorPlanner.CreatePreview(request.Definition, result.Lines.Select(static line => line.Line));
        _previews[preview.Id] = new(request.SessionId, request.Definition, preview);
        PurgeExpiredPreviews();
        return preview;
    }

    public Task<MirrorApproveResult> ApproveMirrorAsync(MirrorApproveRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Definition.DeleteExtraneous && !request.DeletionsApproved)
            throw new InvalidOperationException("Deletion-capable mirrors require a separate explicit deletion approval.");
        if (!_previews.TryGetValue(request.PreviewId, out var stored) || stored.SessionId != request.SessionId)
            throw new InvalidOperationException("The mirror preview was not found. Generate a fresh preview.");
        var command = _mirrorPlanner.BuildExecutionCommand(request.Definition, stored.Preview, request.ApprovalToken);
        var session = _sessions.Get(request.SessionId);
        if (session.Profile.Id != request.Definition.ProfileId) throw new ArgumentException("The mirror profile does not match the connected session.", nameof(request));
        if (!_previews.TryRemove(request.PreviewId, out _)) throw new InvalidOperationException("The mirror preview was already approved.");
        var now = DateTimeOffset.UtcNow;
        var job = _jobs.Enqueue(new(Guid.NewGuid(), JobKind.Mirror, session.Profile.Id, request.Definition.Name, JobState.Queued, now, now));
        TrackJob(job.Id, token => request.Definition.DeleteExtraneous
            ? RunApprovedMirrorAsync(session, request.Definition, stored.Preview, command, job.Id, token)
            : RunCommandJobAsync(session, command, job.Id, _options.TransferTimeout, token));
        return Task.FromResult(new MirrorApproveResult(job));
    }

    public async Task<ConsoleExecuteResult> ExecuteConsoleAsync(ConsoleExecuteRequest request, CancellationToken cancellationToken = default)
    {
        var decision = SafeConsolePolicy.Evaluate(request.Command, localShellEnabled: false);
        if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
        var session = _sessions.Get(request.SessionId);
        var console = await session.GetConsoleAsync(cancellationToken).ConfigureAwait(false);
        var result = await console.ExecuteAsync(request.Command, _options.ConsoleTimeout, cancellationToken).ConfigureAwait(false);
        return new(result);
    }

    public async Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SourceProfileId == Guid.Empty || request.DestinationProfileId == Guid.Empty || request.SourceProfileId == request.DestinationProfileId)
            throw new ArgumentException("Distinct source and destination profiles are required.", nameof(request));
        ValidateRemotePath(request.SourcePath, nameof(request));
        ValidateRemotePath(request.DestinationPath, nameof(request));
        var source = await FindProfileAsync(request.SourceProfileId, cancellationToken).ConfigureAwait(false);
        var destination = await FindProfileAsync(request.DestinationProfileId, cancellationToken).ConfigureAwait(false);
        var plan = new RemoteTransferPlan(Guid.NewGuid(), source.Id, destination.Id, request.SourcePath, request.DestinationPath,
            ComputeRemoteTransferMode(source.Protocol, destination.Protocol), request.Overwrite);
        PlanValidator.Validate(plan);
        return plan;
    }

    public async Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(
        RemoteTransferEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PlanValidator.Validate(request.Plan);
        var source = _sessions.GetActiveProfile(request.Plan.SourceProfileId);
        var destination = _sessions.GetActiveProfile(request.Plan.DestinationProfileId);
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
        var now = DateTimeOffset.UtcNow;
        var job = _jobs.Enqueue(new(
            Guid.NewGuid(),
            JobKind.RemoteTransfer,
            source.Profile.Id,
            $"{RemoteName(request.Plan.SourcePath)} -> {RemoteName(request.Plan.DestinationPath)}",
            JobState.Queued,
            now,
            now,
            Status: routingNote));
        TrackJob(job.Id, token => RunRemoteTransferAsync(source, destination, request.Plan, job.Id, routingNote, token));
        return new(job, expectedMode, routingNote);
    }

    public Task<RemoteEditSession> StartRemoteEditAsync(RemoteEditStartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = _sessions.Get(request.SessionId);
        return _remoteEdits.StartAsync(request, cancellationToken);
    }

    public Task<RemoteEditReview> ReviewRemoteEditAsync(RemoteEditReviewRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.ReviewAsync(request, cancellationToken);

    public Task<RemoteEditActionResult> ResolveRemoteEditAsync(RemoteEditResolveRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.ResolveAsync(request, cancellationToken);

    public Task<bool> CompleteRemoteEditAsync(RemoteEditCompleteRequest request, CancellationToken cancellationToken = default) =>
        _remoteEdits.CompleteAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Task[] operations;
        lock (_operationGate) operations = _operations.ToArray();
        try { await Task.WhenAll(operations).ConfigureAwait(false); } catch (OperationCanceledException) { }
        await _remoteEdits.DisposeAsync().ConfigureAwait(false);
        await _sessions.DisposeAsync().ConfigureAwait(false);
        if (_mirrorPlanner is IDisposable disposable) disposable.Dispose();
        _lifetime.Dispose();
    }

    public bool TryCancelOperation(Guid jobId, string? reason = null)
    {
        if (_scheduler?.TryCancel(jobId, reason) == true) return true;
        if (!_jobCancellations.TryGetValue(jobId, out var cancellation)) return false;
        if (!_jobs.TryCancel(jobId, reason)) return false;
        cancellation.Cancel();
        return true;
    }

    private async Task RunTransferAsync(WorkspaceSession session, TransferPlan plan, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            if (plan.Direction == TransferDirection.Download) Directory.CreateDirectory(Path.GetDirectoryName(plan.DestinationPath)!);
            _jobs.Transition(jobId, JobState.Running, "Submitted to the per-site LFTP transfer queue.");
            await session.ExecuteQueuedTransferAsync(plan, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Transition(jobId, JobState.Completed, "Completed through the per-site LFTP transfer queue.");
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

    private async Task RunScheduledTransferAsync(
        WorkspaceSession session,
        TransferPlan plan,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateTransferPaths(plan);
            if (plan.Mode == TransferMode.Skip && await DestinationExistsAsync(session, plan, cancellationToken).ConfigureAwait(false))
            {
                _jobs.Transition(jobId, JobState.Running, "Checking destination at the scheduled time.");
                _jobs.Transition(jobId, JobState.Completed, "Skipped because the destination already exists.");
                return;
            }
            await RunTransferAsync(session, plan, jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
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
            await using var process = await _sessions.StartRemoteTransferAsync(source, destination, jobId, cancellationToken).ConfigureAwait(false);
            var result = await process.ExecuteAsync(LftpCommandBuilder.BuildRemoteTransfer(plan), _options.TransferTimeout, cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "Remote-to-remote transfer");
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

        var result = await session.Browse.ExecuteAsync(
            $"cls -1 {LftpCommandBuilder.Quote(LftpCommandBuilder.DashSafe(plan.DestinationPath))}",
            _options.BrowseTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) throw new TimeoutException("The remote collision check timed out.");
        if (result.Failure is not null) throw new IOException($"The remote collision check failed: {result.Failure}");
        var error = LftpOutputParser.FirstError(result.Lines);
        if (error is null) return true;
        if (error.Contains("no such", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return false;
        throw new IOException($"The remote collision check failed closed: {error}");
    }

    private async Task RunCommandJobAsync(WorkspaceSession session, string command, Guid jobId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var result = await session.WithTransferSessionAsync(
                transfer =>
                {
                    _jobs.Transition(jobId, JobState.Running, "Running");
                    return transfer.ExecuteAsync(command, timeout, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(result, "LFTP job");
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Transition(jobId, JobState.Completed, "Completed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _jobs.TryCancel(jobId, "Cancelled");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            TryFailJob(jobId, exception);
        }
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
                var verificationResult = await transfer.ExecuteAsync(
                    LftpCommandBuilder.BuildMirror(definition, dryRun: true),
                    _options.MirrorPreviewTimeout,
                    cancellationToken).ConfigureAwait(false);
                SessionRegistry.ThrowIfFailed(verificationResult, "Mirror approval verification");
                var verification = _mirrorPlanner.CreatePreview(definition, verificationResult.Lines.Select(static line => line.Line));
                if (!verification.Actions.SequenceEqual(reviewedPreview.Actions))
                    throw new InvalidOperationException("The mirror actions changed after review. Generate and approve a new preview.");

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

    private void TrackJob(Guid jobId, Func<CancellationToken, Task> operation)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (!_jobCancellations.TryAdd(jobId, cancellation))
        {
            cancellation.Dispose();
            throw new InvalidOperationException($"Job {jobId} is already running.");
        }
        var task = RunTrackedOperationAsync(jobId, operation, cancellation.Token);
        lock (_operationGate) _operations.Add(task);
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            lock (_operationGate) _operations.Remove(completed);
            if (_jobCancellations.TryRemove(jobId, out var source)) source.Dispose();
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
        try { _jobs.Transition(jobId, JobState.Failed, "Failed", new("lftp-job-failed", exception.Message)); }
        catch (InvalidOperationException) { }
    }

    private async Task<ConnectionProfile> FindProfileAsync(Guid id, CancellationToken cancellationToken) =>
        (await _profileStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(profile => profile.Id == id)
        ?? throw new KeyNotFoundException($"Profile {id} was not found.");

    private WorkspaceSession GetRemoteMutationSession(Guid? sessionId)
    {
        if (sessionId is not { } value || value == Guid.Empty)
            throw new ArgumentException("A connected session is required for a remote file operation.", nameof(sessionId));
        return _sessions.Get(value);
    }

    private async Task<FileEntry?> TryStatRemoteAsync(WorkspaceSession session, string path, CancellationToken cancellationToken)
    {
        var result = await session.Browse.ExecuteAsync(LftpCommandBuilder.BuildStat(path), _options.BrowseTimeout, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) throw new TimeoutException("The remote path check timed out.");
        if (result.Failure is not null) throw new IOException($"The remote path check failed: {result.Failure}");
        if (result.Truncated) throw new InvalidDataException("The remote path check produced too much output.");
        var error = LftpOutputParser.FirstError(result.Lines);
        if (error is not null)
        {
            if (error.Contains("no such", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase)) return null;
            throw new IOException($"The remote path check failed closed: {error}");
        }
        var entries = LftpOutputParser.ParseLongListing(result.Lines.Select(static line => line.Line), RemoteParent(path));
        if (entries.Length != 1)
            throw new InvalidDataException("The server did not return one parseable file entry for the remote path check.");
        return entries[0];
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

    private static string RemoteParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

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
        var scheme = profile.Protocol switch { ConnectionProtocol.Sftp => "sftp", ConnectionProtocol.FtpsImplicit => "ftps", _ => "ftp" };
        return new(profile.Id, $"{scheme}://{profile.Host.ToLowerInvariant()}:{profile.Port}", profile.UserName, $"login-{profile.Authentication.ToString().ToLowerInvariant()}");
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

    private static void ValidateTransferPaths(TransferPlan plan)
    {
        if (plan.Direction == TransferDirection.Download)
        {
            ValidateRemotePath(plan.SourcePath, nameof(plan));
            if (!Path.IsPathFullyQualified(plan.DestinationPath)) throw new ArgumentException("A download destination must be a fully qualified local path.", nameof(plan));
        }
        else
        {
            if (plan.Mode == TransferMode.Skip)
                throw new NotSupportedException("No-overwrite upload cannot be guaranteed portably across the supported protocols; choose resume or overwrite explicitly.");
            if (!Path.IsPathFullyQualified(plan.SourcePath) || !File.Exists(plan.SourcePath)) throw new ArgumentException("An upload source must be an existing fully qualified local file.", nameof(plan));
            ValidateRemotePath(plan.DestinationPath, nameof(plan));
        }
    }

    private static void ValidateRemotePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A remote absolute path without protocol control characters is required.", parameterName);
    }

    private void PurgeExpiredPreviews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _previews) if (pair.Value.Preview.ExpiresAt < now) _previews.TryRemove(pair.Key, out _);
    }

    private static T Required<T>(JsonElement element) where T : class =>
        element.Deserialize<T>(FramedJsonStream.SerializerOptions) ?? throw new ArgumentException($"A {typeof(T).Name} payload is required.");

    private static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, FramedJsonStream.SerializerOptions);

    private sealed record StoredMirrorPreview(Guid SessionId, MirrorDefinition Definition, MirrorPreview Preview);
    private sealed record StoredBrowseSnapshot(
        Guid Id,
        PaneLocation Location,
        Guid? SessionId,
        DateTimeOffset CreatedAt,
        ImmutableArray<FileEntry> Entries);
}
