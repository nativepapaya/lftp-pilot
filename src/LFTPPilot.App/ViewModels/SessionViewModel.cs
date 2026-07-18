using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed record UnconfirmedTransferSubmission(Guid SessionId, TransferPlan Plan);

public sealed class SessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAgentWorkspaceClient _agent;
    private readonly HashSet<Guid> _unconfirmedTransferIds = [];
    private ConnectionProfile _profile;
    private EngineError? _connectionError;
    private bool _isConnected;
    private bool _isReconnecting;
    private string _displayName;
    private string _statusText;

    public SessionViewModel(IAgentWorkspaceClient agent, WorkspaceSessionSeed seed, ConnectionProfile profile)
    {
        _agent = agent;
        _profile = profile;
        SessionId = seed.Snapshot.SessionId;
        ProfileId = seed.Snapshot.ProfileId;
        _displayName = profile.Name;
        _isConnected = seed.Snapshot.IsConnected;
        _connectionError = seed.Snapshot.Error;
        _statusText = CreateConnectionStatus();
        LocalPane = new FilePaneViewModel(agent, SessionId, PaneKind.Local, seed.Snapshot.LocalLocation.Path, seed.LocalEntries);
        RemotePane = new FilePaneViewModel(agent, SessionId, PaneKind.Remote, seed.Snapshot.RemoteLocation.Path, seed.RemoteEntries, profile);
        Search = new RemoteSearchViewModel(
            agent,
            SessionId,
            () => RemotePane.Path,
            NavigateToSearchMatchAsync,
            _isConnected);
        DownloadCommand = new AsyncRelayCommand(
            _ => TransferAsync(TransferDirection.Download),
            _ => IsConnected && RemotePane.HasSelection && !HasUnconfirmedTransfers,
            ReportError);
        UploadCommand = new AsyncRelayCommand(
            _ => TransferAsync(TransferDirection.Upload),
            _ => IsConnected && LocalPane.HasSelection && !HasUnconfirmedTransfers,
            ReportError);
        RefreshCommand = new AsyncRelayCommand(
            _ => Task.WhenAll(LocalPane.NavigateAsync(LocalPane.Path), RemotePane.NavigateAsync(RemotePane.Path)),
            _ => IsConnected,
            ReportError);
        LocalPane.SelectedEntries.CollectionChanged += (_, _) => UploadCommand.NotifyCanExecuteChanged();
        RemotePane.SelectedEntries.CollectionChanged += (_, _) => DownloadCommand.NotifyCanExecuteChanged();
    }

    public Guid SessionId { get; }
    public Guid ProfileId { get; }
    public string DisplayName { get => _displayName; private set => SetProperty(ref _displayName, value); }
    public FilePaneViewModel LocalPane { get; }
    public FilePaneViewModel RemotePane { get; }
    public RemoteSearchViewModel Search { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public event EventHandler<JobSnapshot>? JobQueued;
    public event EventHandler<UnconfirmedTransferSubmission>? TransferOutcomeUnconfirmed;
    public event EventHandler? StateRefreshRequested;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(ConnectionGlyph));
            OnPropertyChanged(nameof(CanReconnect));
            OnPropertyChanged(nameof(ReconnectDescription));
            RefreshCommand.NotifyCanExecuteChanged();
            UploadCommand.NotifyCanExecuteChanged();
            DownloadCommand.NotifyCanExecuteChanged();
            Search.SetConnected(value);
        }
    }

    public bool IsDisconnected => !IsConnected;

    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set
        {
            if (!SetProperty(ref _isReconnecting, value)) return;
            OnPropertyChanged(nameof(CanReconnect));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectionGlyph => IsConnected ? "\uE701" : "\uE711";
    public bool HasUnconfirmedTransfers => _unconfirmedTransferIds.Count != 0;
    public bool RequiresCredentialForReconnect => _profile.Authentication == AuthenticationKind.AskOnConnect;
    public bool CanReconnect => IsDisconnected && !IsReconnecting;
    public string ReconnectDescription => RequiresCredentialForReconnect
        ? "This restored tab needs its credential again. LFTP Pilot did not store the ask-on-connect credential."
        : _connectionError is not null
            ? $"The prior connection could not be restored: {_connectionError.Message}"
            : "The saved tab is disconnected. Reconnect to resume remote browsing and transfers.";

    public void UpdateProfile(ConnectionProfile profile)
    {
        if (profile.Id != ProfileId) return;
        _profile = profile;
        DisplayName = profile.Name;
        RemotePane.UpdateProfile(profile);
        OnPropertyChanged(nameof(RequiresCredentialForReconnect));
        OnPropertyChanged(nameof(ReconnectDescription));
    }

    public void ApplySeed(WorkspaceSessionSeed seed, ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(profile);
        if (seed.Snapshot.SessionId != SessionId || seed.Snapshot.ProfileId != ProfileId || profile.Id != ProfileId)
            throw new InvalidDataException("The refreshed session tab did not match its existing session identity.");
        if (seed.Snapshot.LocalLocation.Kind != PaneKind.Local || seed.Snapshot.RemoteLocation.Kind != PaneKind.Remote)
            throw new InvalidDataException("The refreshed session tab returned invalid pane locations.");

        _profile = profile;
        _connectionError = seed.Snapshot.Error;
        DisplayName = profile.Name;
        RemotePane.UpdateProfile(profile);
        LocalPane.Restore(seed.Snapshot.LocalLocation.Path, seed.LocalEntries);
        RemotePane.Restore(seed.Snapshot.RemoteLocation.Path, seed.RemoteEntries);
        IsConnected = seed.Snapshot.IsConnected;
        StatusText = CreateConnectionStatus();
        OnPropertyChanged(nameof(RequiresCredentialForReconnect));
        OnPropertyChanged(nameof(ReconnectDescription));
    }

    public async Task ReconnectAsync(string? ephemeralCredential = null, CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;
        if (IsReconnecting) throw new InvalidOperationException("This saved tab is already reconnecting.");
        if (RequiresCredentialForReconnect && string.IsNullOrEmpty(ephemeralCredential))
            throw new ArgumentException("Enter the ask-on-connect credential before reconnecting.", nameof(ephemeralCredential));

        IsReconnecting = true;
        StatusText = _profile.Protocol == ConnectionProtocol.Sftp
            ? "Inspecting the SFTP host key before reconnectingâ€¦"
            : "Reconnectingâ€¦";
        try
        {
            var seed = await _agent.ConnectAsync(
                _profile,
                RequiresCredentialForReconnect ? ephemeralCredential : null,
                cancellationToken,
                existingSessionId: SessionId).ConfigureAwait(true);
            ApplySeed(seed, _profile);
        }
        catch (AgentRequestOutcomeUnknownException exception)
        {
            StatusText = $"Reconnect outcome unconfirmed. {exception.Message}";
            RequestStateRefresh();
            throw;
        }
        catch (Exception exception)
        {
            StatusText = $"Reconnect failed. {exception.Message}";
            throw;
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    public void SetUnconfirmedTransferIds(IEnumerable<Guid> planIds)
    {
        ArgumentNullException.ThrowIfNull(planIds);
        _unconfirmedTransferIds.Clear();
        foreach (var planId in planIds.Where(static id => id != Guid.Empty))
            _unconfirmedTransferIds.Add(planId);
        OnPropertyChanged(nameof(HasUnconfirmedTransfers));
        UploadCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyList<FilePaneTransferSource> GetSelectedSources(TransferDirection direction)
    {
        var pane = direction == TransferDirection.Upload ? LocalPane : RemotePane;
        var sources = new List<FilePaneTransferSource>(pane.SelectedEntries.Count);
        foreach (var item in pane.SelectedEntries)
        {
            var kind = item.Entry.Kind switch
            {
                EntryKind.File => TransferSourceKind.File,
                EntryKind.Directory => TransferSourceKind.Directory,
                _ => (TransferSourceKind?)null,
            };
            if (kind is null)
            {
                StatusText = "Only regular files and directories can be transferred";
                return [];
            }

            sources.Add(new(item.FullPath, kind.Value));
        }

        return sources;
    }

    public async Task<RemoteEditSession> StartRemoteEditAsync(FileEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsConnected)
            throw new InvalidOperationException("Reconnect this saved tab before editing a remote file.");
        if (entry.Entry.Kind != EntryKind.File || !RemotePane.SelectedEntries.Contains(entry) || RemotePane.SelectedEntries.Count != 1)
            throw new InvalidOperationException("Select exactly one regular remote file to edit.");
        StatusText = $"Preparing managed copy of {entry.Name}…";
        var edit = await _agent.StartRemoteEditAsync(SessionId, entry.FullPath).ConfigureAwait(true);
        StatusText = $"Editing {entry.Name} in a managed local cache";
        return edit;
    }

    public ValueTask DisposeAsync() => Search.DisposeAsync();

    public async Task QueueSourcesAsync(
        TransferDirection direction,
        IReadOnlyList<FilePaneTransferSource> sources,
        TransferUiOptions? options = null)
    {
        if (!IsConnected)
        {
            StatusText = "Reconnect this saved tab before queueing a transfer.";
            throw new InvalidOperationException(StatusText);
        }
        if (HasUnconfirmedTransfers)
        {
            StatusText = "A prior transfer submission is still unconfirmed. Activity must reconcile its original plan ID before another transfer can be queued.";
            throw new InvalidOperationException(StatusText);
        }

        if (sources is null)
        {
            StatusText = "No items were queued";
            throw new ArgumentNullException(nameof(sources));
        }

        if (sources.Count == 0)
        {
            StatusText = "No items selected";
            return;
        }

        if (!FilePaneDragDropRegistry.TryCopyValidSources(sources, out var validatedSources))
        {
            StatusText = "No items were queued";
            throw new ArgumentException("Select between 1 and 100 regular files or directories with valid paths.", nameof(sources));
        }

        options ??= TransferUiOptions.Defaults(direction);
        if (validatedSources.Any(static source => source.Kind == TransferSourceKind.Directory) &&
            options.Mode is not TransferMode.Auto and not TransferMode.Resume)
        {
            StatusText = "No items were queued";
            throw new ArgumentException("Directory transfers support only Auto or Resume mode.", nameof(options));
        }

        StatusText = direction == TransferDirection.Upload ? "Queueing upload…" : "Queueing download…";
        var destinationRoot = direction == TransferDirection.Upload ? RemotePane.Path : LocalPane.Path;
        var segments = direction == TransferDirection.Download ? Math.Clamp(options.DownloadSegments, 1, 16) : 1;
        var execution = await TransferQueueAggregation.ExecuteAsync<JobSnapshot>(validatedSources, EnqueueOneAsync).ConfigureAwait(true);
        foreach (var failure in execution.Failures)
        {
            var outcome = failure.IsOutcomeUnknown
                ? "has unknown Agent acceptance; Activity is refreshing before any retry"
                : failure.IsConfirmedTerminal
                    ? "was confirmed terminal and is available in Activity"
                    : "did not produce a confirmed Agent-accepted job";
            System.Diagnostics.Debug.WriteLine(
                $"A {failure.Source.Kind.ToString().ToLowerInvariant()} transfer enqueue attempt {outcome}.");
        }

        var result = execution.ToResult();
        StatusText = TransferQueueAggregation.FormatStatus(result, options.RunAt is not null);
        if (result.IssueCount > 0) throw new TransferQueueException(result, execution.Failures);

        async Task<JobSnapshot> EnqueueOneAsync(FilePaneTransferSource source)
        {
            if (HasUnconfirmedTransfers)
                throw new InvalidOperationException("A prior transfer submission is still unconfirmed; no new transfer plan was created.");
            var sourcePath = source.Path;
            var destinationLeaf = TransferLeafName(direction, sourcePath);
            var destinationPath = direction == TransferDirection.Upload
                ? CombineRemote(destinationRoot, destinationLeaf)
                : Path.Combine(destinationRoot, destinationLeaf);
            var plan = new TransferPlan(
                Guid.NewGuid(),
                ProfileId,
                direction,
                sourcePath,
                destinationPath,
                options.Mode,
                segments,
                options.RateLimitBytesPerSecond,
                options.RunAt,
                SourceKind: source.Kind);
            JobSnapshot job;
            try
            {
                job = await _agent.EnqueueTransferAsync(SessionId, plan).ConfigureAwait(true);
            }
            catch (AgentRequestOutcomeUnknownException)
            {
                _unconfirmedTransferIds.Add(plan.Id);
                OnPropertyChanged(nameof(HasUnconfirmedTransfers));
                UploadCommand.NotifyCanExecuteChanged();
                DownloadCommand.NotifyCanExecuteChanged();
                try
                {
                    TransferOutcomeUnconfirmed?.Invoke(this, new(SessionId, plan));
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                }
                throw;
            }
            try
            {
                JobQueued?.Invoke(this, job);
            }
            catch (Exception exception)
            {
                // A presentation subscriber cannot turn an Agent-accepted job into
                // a failed transfer or cause the user to queue it a second time.
                System.Diagnostics.Debug.WriteLine(exception);
            }

            if (job.State is JobState.Failed or JobState.Missed or JobState.Cancelled)
                throw new TransferSubmissionTerminalException(job);

            return job;
        }
    }

    private async Task TransferAsync(TransferDirection direction)
    {
        var sources = GetSelectedSources(direction);
        if (sources.Count == 0) return;
        await QueueSourcesAsync(direction, sources, TransferUiOptions.Defaults(direction)).ConfigureAwait(true);
    }

    private async Task NavigateToSearchMatchAsync(RemoteSearchMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!IsConnected)
            throw new InvalidOperationException("Reconnect this saved tab before opening a remote-search result.");
        var destination = match.IsDirectory ? match.FullPath : RemoteParent(match.FullPath);
        await RemotePane.NavigateAsync(destination).ConfigureAwait(true);
        StatusText = match.IsDirectory
            ? $"Opened remote folder {match.FullPath}"
            : $"Opened the current remote location of {match.Name}";
    }

    private static string RemoteParent(string path)
    {
        if (!ProfileValidator.IsCanonicalRemotePath(path) || path == "/")
            throw new InvalidDataException("The remote-search result did not contain a canonical item path.");
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string CombineRemote(string root, string name) =>
        root.EndsWith("/", StringComparison.Ordinal) ? $"{root}{name}" : $"{root}/{name}";

    private static string TransferLeafName(TransferDirection direction, string path)
    {
        var name = direction == TransferDirection.Upload
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                "Root-wide folder transfers require the reviewed Mirror workflow; select an item below the root instead.");
        }
        return name;
    }

    private string CreateConnectionStatus() => IsConnected
        ? "Connected"
        : RequiresCredentialForReconnect
            ? "Disconnected Â· Credential required"
            : _connectionError is not null
                ? $"Disconnected Â· {_connectionError.Message}"
                : "Disconnected";

    private void RequestStateRefresh()
    {
        try
        {
            StateRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private void ReportError(Exception exception) => StatusText = exception.Message;
}
