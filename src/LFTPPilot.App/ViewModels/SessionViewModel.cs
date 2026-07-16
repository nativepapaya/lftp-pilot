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

    public IReadOnlyList<string> GetSelectedPaths(TransferDirection direction)
    {
        var pane = direction == TransferDirection.Upload ? LocalPane : RemotePane;
        return pane.SelectedEntries.Select(static item => item.FullPath).ToArray();
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

    public async Task QueuePathsAsync(
        TransferDirection direction,
        IReadOnlyList<string> paths,
        TransferUiOptions? options = null)
    {
        if (paths.Count == 0) return;

        options ??= TransferUiOptions.Defaults(direction);
        StatusText = direction == TransferDirection.Upload ? "Queueing upload…" : "Queueing download…";
        var destinationRoot = direction == TransferDirection.Upload ? RemotePane.Path : LocalPane.Path;
        var segments = direction == TransferDirection.Download ? Math.Clamp(options.DownloadSegments, 1, 16) : 1;
        var queued = 0;

        foreach (var sourcePath in paths)
        {
            var destinationPath = direction == TransferDirection.Upload
                ? CombineRemote(destinationRoot, Path.GetFileName(sourcePath))
                : Path.Combine(destinationRoot, RemoteFileName(sourcePath));
            var plan = new TransferPlan(
                Guid.NewGuid(),
                ProfileId,
                direction,
                sourcePath,
                destinationPath,
                options.Mode,
                segments,
                options.RateLimitBytesPerSecond,
                options.RunAt);
            var job = await _agent.EnqueueTransferAsync(SessionId, plan).ConfigureAwait(true);
            JobQueued?.Invoke(this, job);
            queued++;
        }

        var status = options.RunAt is null ? "Queued" : "Scheduled";
        StatusText = $"{status} {queued} item{(queued == 1 ? string.Empty : "s")}";
    }

    private async Task TransferAsync(TransferDirection direction)
    {
        var paths = GetSelectedPaths(direction);
        if (paths.Count == 0) return;
        await QueuePathsAsync(direction, paths, TransferUiOptions.Defaults(direction)).ConfigureAwait(true);
    }

    private static string CombineRemote(string root, string name) =>
        root.EndsWith("/", StringComparison.Ordinal) ? $"{root}{name}" : $"{root}/{name}";

    private static string RemoteFileName(string path) =>
        path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "download";

    private void ReportError(Exception exception) => StatusText = exception.Message;
}
