using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class SessionViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private bool _isConnected;
    private string _statusText;

    public SessionViewModel(IAgentWorkspaceClient agent, WorkspaceSessionSeed seed, ConnectionProfile profile)
    {
        _agent = agent;
        SessionId = seed.Snapshot.SessionId;
        ProfileId = seed.Snapshot.ProfileId;
        DisplayName = seed.Snapshot.DisplayName;
        _isConnected = seed.Snapshot.IsConnected;
        _statusText = IsConnected ? "Connected" : "Disconnected";
        LocalPane = new FilePaneViewModel(agent, SessionId, PaneKind.Local, seed.Snapshot.LocalLocation.Path, seed.LocalEntries);
        RemotePane = new FilePaneViewModel(agent, SessionId, PaneKind.Remote, seed.Snapshot.RemoteLocation.Path, seed.RemoteEntries, profile);
        DownloadCommand = new AsyncRelayCommand(_ => TransferAsync(TransferDirection.Download), _ => RemotePane.HasSelection, ReportError);
        UploadCommand = new AsyncRelayCommand(_ => TransferAsync(TransferDirection.Upload), _ => LocalPane.HasSelection, ReportError);
        RefreshCommand = new AsyncRelayCommand(_ => Task.WhenAll(LocalPane.NavigateAsync(LocalPane.Path), RemotePane.NavigateAsync(RemotePane.Path)), null, ReportError);
        LocalPane.SelectedEntries.CollectionChanged += (_, _) => UploadCommand.NotifyCanExecuteChanged();
        RemotePane.SelectedEntries.CollectionChanged += (_, _) => DownloadCommand.NotifyCanExecuteChanged();
    }

    public Guid SessionId { get; }
    public Guid ProfileId { get; }
    public string DisplayName { get; }
    public FilePaneViewModel LocalPane { get; }
    public FilePaneViewModel RemotePane { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public event EventHandler<JobSnapshot>? JobQueued;

    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectionGlyph => IsConnected ? "\uE701" : "\uE711";

    public void UpdateProfile(ConnectionProfile profile)
    {
        if (profile.Id == ProfileId) RemotePane.UpdateProfile(profile);
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
        if (entry.Entry.Kind != EntryKind.File || !RemotePane.SelectedEntries.Contains(entry) || RemotePane.SelectedEntries.Count != 1)
            throw new InvalidOperationException("Select exactly one regular remote file to edit.");
        StatusText = $"Preparing managed copy of {entry.Name}…";
        var edit = await _agent.StartRemoteEditAsync(SessionId, entry.FullPath).ConfigureAwait(true);
        StatusText = $"Editing {entry.Name} in a managed local cache";
        return edit;
    }

    public async Task QueueSourcesAsync(
        TransferDirection direction,
        IReadOnlyList<FilePaneTransferSource> sources,
        TransferUiOptions? options = null)
    {
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
            System.Diagnostics.Debug.WriteLine(
                $"A {failure.Source.Kind.ToString().ToLowerInvariant()} transfer enqueue attempt failed before Agent acceptance.");
        }

        var result = execution.ToResult();
        StatusText = TransferQueueAggregation.FormatStatus(result, options.RunAt is not null);
        if (result.FailedCount > 0) throw new TransferQueueException(result, execution.Failures);

        async Task<JobSnapshot> EnqueueOneAsync(FilePaneTransferSource source)
        {
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
            var job = await _agent.EnqueueTransferAsync(SessionId, plan).ConfigureAwait(true);
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

            return job;
        }
    }

    private async Task TransferAsync(TransferDirection direction)
    {
        var sources = GetSelectedSources(direction);
        if (sources.Count == 0) return;
        await QueueSourcesAsync(direction, sources, TransferUiOptions.Defaults(direction)).ConfigureAwait(true);
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

    private void ReportError(Exception exception) => StatusText = exception.Message;
}
