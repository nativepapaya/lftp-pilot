using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class FilePaneViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private readonly Guid _sessionId;
    private readonly List<FileEntry> _allEntries = [];
    private ConnectionProfile? _profile;
    private ObservableCollection<FileEntryViewModel> _entries = [];
    private string _path;
    private string _filterText = string.Empty;
    private bool _isBusy;
    private bool _sortDescending;
    private FilePaneSortColumn _sortColumn = FilePaneSortColumn.Name;
    private string? _selectedBookmark;

    public FilePaneViewModel(
        IAgentWorkspaceClient agent,
        Guid sessionId,
        PaneKind kind,
        string path,
        IEnumerable<FileEntry> entries,
        ConnectionProfile? profile = null,
        bool supportsTransfers = true)
    {
        _agent = agent;
        _sessionId = sessionId;
        Kind = kind;
        SupportsTransfers = supportsTransfers;
        _profile = kind == PaneKind.Remote ? profile : null;
        _path = path;
        Replace(entries);
        NavigateCommand = new AsyncRelayCommand(parameter => NavigateAsync(parameter as string ?? Path), null, ReportError);
        RefreshCommand = new AsyncRelayCommand(_ => NavigateAsync(Path), null, ReportError);
        SortCommand = new RelayCommand(parameter => Sort(parameter?.ToString()));
        AddCurrentBookmarkCommand = new AsyncRelayCommand(_ => AddCurrentBookmarkAsync(), _ => CanAddCurrentBookmark, ReportError);
        RemoveBookmarkCommand = new AsyncRelayCommand(RemoveBookmarkAsync, CanRemoveBookmark, ReportError);
        NavigateBookmarkCommand = new AsyncRelayCommand(NavigateBookmarkAsync, CanNavigateBookmark, ReportError);
        LoadBookmarks();
    }

    public PaneKind Kind { get; }
    public bool SupportsTransfers { get; }
    public Guid SessionId => _sessionId;
    public string PaneTitle => Kind == PaneKind.Local ? "This PC" : "Remote";
    public string PaneSubtitle => Kind == PaneKind.Local ? "Local files" : "LFTP session";
    public ObservableCollection<FileEntryViewModel> Entries
    {
        get => _entries;
        private set => SetProperty(ref _entries, value);
    }
    public ObservableCollection<FileEntryViewModel> SelectedEntries { get; } = [];
    public ObservableCollection<string> QuickAccessBookmarks { get; } = [];
    public AsyncRelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand SortCommand { get; }
    public AsyncRelayCommand AddCurrentBookmarkCommand { get; }
    public AsyncRelayCommand RemoveBookmarkCommand { get; }
    public AsyncRelayCommand NavigateBookmarkCommand { get; }
    public bool IsRemote => Kind == PaneKind.Remote;
    public bool CanAddCurrentBookmark => IsRemote && _profile is not null && Path.StartsWith("/", StringComparison.Ordinal) &&
        !QuickAccessBookmarks.Contains(Path, StringComparer.Ordinal) && QuickAccessBookmarks.Count < 128;

    public string Path
    {
        get => _path;
        set
        {
            if (!SetProperty(ref _path, value)) return;
            SelectedBookmark = QuickAccessBookmarks.FirstOrDefault(bookmark => string.Equals(bookmark, value, StringComparison.Ordinal));
            AddCurrentBookmarkCommand.NotifyCanExecuteChanged();
        }
    }

    public string? SelectedBookmark
    {
        get => _selectedBookmark;
        set
        {
            if (!SetProperty(ref _selectedBookmark, value)) return;
            RemoveBookmarkCommand.NotifyCanExecuteChanged();
            NavigateBookmarkCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasSelection => SelectedEntries.Count > 0;
    public bool CanRenameOrMove => SelectedEntries.Count == 1;
    public bool CanDeleteEntries => SelectedEntries.Count is > 0 and <= 100;
    public bool CanRemoteEdit => IsRemote && SelectedEntries.Count == 1 && SelectedEntries[0].Entry.Kind == EntryKind.File;
    public string SelectionSummary => HasSelection ? $"{SelectedEntries.Count} selected" : $"{Entries.Count} items";
    public string SortIndicator => _sortDescending ? "Descending" : "Ascending";
    public string? LastError { get; private set; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                RebuildEntries();
            }
        }
    }

    public void UpdateSelection(IEnumerable<object> selectedItems)
    {
        foreach (var item in Entries)
        {
            item.IsSelected = false;
        }

        SelectedEntries.Clear();
        foreach (var item in selectedItems.OfType<FileEntryViewModel>())
        {
            item.IsSelected = true;
            SelectedEntries.Add(item);
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRenameOrMove));
        OnPropertyChanged(nameof(CanDeleteEntries));
        OnPropertyChanged(nameof(CanRemoteEdit));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public Task OpenAsync(FileEntryViewModel item)
    {
        if (!item.IsDirectory)
        {
            UpdateSelection([item]);
            return Task.CompletedTask;
        }

        return NavigateAsync(item.FullPath);
    }

    public async Task<ExplorerExportSnapshot> PrepareExplorerExportAsync(
        Guid exportId,
        IReadOnlyList<FilePaneTransferSource> sources,
        DateTimeOffset deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (Kind != PaneKind.Remote)
            throw new InvalidOperationException("Only remote files need an Agent-owned Explorer export.");
        if (sources.Count is < 1 or > ExplorerExportPolicy.MaximumFiles ||
            sources.Any(static source => source.Kind != TransferSourceKind.File))
        {
            throw new ArgumentException($"Select between 1 and {ExplorerExportPolicy.MaximumFiles} regular remote files.", nameof(sources));
        }

        var request = new ExplorerExportStartRequest(
            exportId,
            SessionId,
            sources.Select(static source => source.Path).ToImmutableArray());
        ExplorerExportPolicy.ValidateStart(request);
        var remaining = deadline.ToUniversalTime() - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Explorer's delayed file request expired before the export could start.");

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineCancellation.CancelAfter(remaining);
        ExplorerExportSnapshot snapshot;
        try
        {
            snapshot = await _agent.StartExplorerExportAsync(request, deadlineCancellation.Token).ConfigureAwait(false);
        }
        catch (AgentRequestOutcomeUnknownException)
        {
            snapshot = await _agent.GetExplorerExportAsync(exportId, deadlineCancellation.Token).ConfigureAwait(false);
        }

        while (true)
        {
            switch (snapshot.Job.State)
            {
                case JobState.Completed:
                    return snapshot;
                case JobState.Failed:
                case JobState.Cancelled:
                case JobState.Missed:
                    throw new IOException(snapshot.Job.Error?.Message ?? snapshot.Job.Status ??
                        $"Explorer export ended in the {snapshot.Job.State} state.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), deadlineCancellation.Token).ConfigureAwait(false);
            snapshot = await _agent.GetExplorerExportAsync(exportId, deadlineCancellation.Token).ConfigureAwait(false);
        }
    }

    public Task<bool> ReleaseExplorerExportAsync(Guid exportId, CancellationToken cancellationToken = default) =>
        _agent.ReleaseExplorerExportAsync(exportId, cancellationToken);

    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var entries = await _agent.BrowseAsync(_sessionId, Kind, path).ConfigureAwait(true);
            Path = path;
            Replace(entries);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CreateDirectoryAsync(string nameOrPath)
    {
        var destination = ResolveMutationPath(nameOrPath);
        IsBusy = true;
        try
        {
            _ = await _agent.CreateDirectoryAsync(_sessionId, Kind, destination).ConfigureAwait(true);
            await ReloadCurrentPathAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameOrMoveAsync(FileEntryViewModel entry, string nameOrPath)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var destination = ResolveMutationPath(nameOrPath);
        IsBusy = true;
        try
        {
            _ = await _agent.MoveEntryAsync(_sessionId, Kind, entry.FullPath, destination).ConfigureAwait(true);
            await ReloadCurrentPathAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteEntriesAsync(IReadOnlyList<FileEntryViewModel> entries, bool recursive, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("File deletion requires explicit confirmation.");
        if (entries.Count is < 1 or > 100) throw new ArgumentException("Select between 1 and 100 entries to delete.", nameof(entries));
        IsBusy = true;
        try
        {
            Exception? mutationError = null;
            try
            {
                _ = await _agent.DeleteEntriesAsync(_sessionId, Kind, entries.Select(static entry => entry.FullPath).ToArray(), recursive, confirmed).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                mutationError = exception;
            }

            try { await ReloadCurrentPathAsync().ConfigureAwait(true); }
            catch when (mutationError is not null) { }
            if (mutationError is not null) ExceptionDispatchInfo.Capture(mutationError).Throw();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void UpdateProfile(ConnectionProfile profile)
    {
        if (!IsRemote || (_profile is not null && profile.Id != _profile.Id)) return;
        _profile = profile;
        LoadBookmarks();
    }

    public void Restore(string path, IEnumerable<FileEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A pane path is required.", nameof(path));
        ArgumentNullException.ThrowIfNull(entries);
        Path = path;
        Replace(entries);
    }

    public void Sort(string? column)
    {
        if (!Enum.TryParse<FilePaneSortColumn>(column, true, out var parsed))
        {
            parsed = FilePaneSortColumn.Name;
        }

        _sortDescending = _sortColumn == parsed && !_sortDescending;
        _sortColumn = parsed;
        ApplySort();
        OnPropertyChanged(nameof(SortIndicator));
    }

    private void Replace(IEnumerable<FileEntry> entries)
    {
        _allEntries.Clear();
        _allEntries.AddRange(entries);
        SelectedEntries.Clear();
        RebuildEntries();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanRenameOrMove));
        OnPropertyChanged(nameof(CanDeleteEntries));
        OnPropertyChanged(nameof(CanRemoteEdit));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void RebuildEntries()
    {
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _allEntries
            : _allEntries.Where(entry => entry.Name.Contains(FilterText.Trim(), StringComparison.CurrentCultureIgnoreCase));
        Entries = new ObservableCollection<FileEntryViewModel>(filtered.Select(static entry => new FileEntryViewModel(entry)));
        ApplySort();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void ApplySort()
    {
        Func<FileEntryViewModel, object?> selector = _sortColumn switch
        {
            FilePaneSortColumn.Size => item => item.Entry.Size,
            FilePaneSortColumn.Modified => item => item.Entry.ModifiedAt,
            FilePaneSortColumn.Type => item => item.TypeDisplay,
            _ => item => item.Name,
        };
        var ordered = _sortDescending
            ? Entries.OrderByDescending(item => item.IsDirectory).ThenByDescending(selector).ToList()
            : Entries.OrderByDescending(item => item.IsDirectory).ThenBy(selector).ToList();
        Entries = new ObservableCollection<FileEntryViewModel>(ordered);
    }

    private async Task AddCurrentBookmarkAsync()
    {
        if (!CanAddCurrentBookmark || _profile is null) return;
        await SaveBookmarksAsync(_profile.EffectiveBookmarks.Add(Path)).ConfigureAwait(true);
        SelectedBookmark = Path;
    }

    private bool CanRemoveBookmark(object? parameter) => IsRemote && _profile is not null &&
        (parameter as string ?? SelectedBookmark) is { } bookmark && QuickAccessBookmarks.Contains(bookmark, StringComparer.Ordinal);

    private async Task RemoveBookmarkAsync(object? parameter)
    {
        if (_profile is null) return;
        var bookmark = parameter as string ?? SelectedBookmark;
        if (bookmark is null) return;
        await SaveBookmarksAsync(_profile.EffectiveBookmarks.Where(item => !string.Equals(item, bookmark, StringComparison.Ordinal)).ToImmutableArray()).ConfigureAwait(true);
    }

    private bool CanNavigateBookmark(object? parameter) => IsRemote &&
        (parameter as string ?? SelectedBookmark) is { } bookmark && QuickAccessBookmarks.Contains(bookmark, StringComparer.Ordinal);

    private async Task NavigateBookmarkAsync(object? parameter)
    {
        var bookmark = parameter as string ?? SelectedBookmark;
        if (bookmark is null || string.Equals(bookmark, Path, StringComparison.Ordinal)) return;
        await NavigateAsync(bookmark).ConfigureAwait(true);
    }

    private async Task SaveBookmarksAsync(ImmutableArray<string> bookmarks)
    {
        if (_profile is null) return;
        var distinct = bookmarks.Distinct(StringComparer.Ordinal).Take(128).ToImmutableArray();
        _profile = await _agent.SaveProfileAsync(_profile with { Bookmarks = distinct }).ConfigureAwait(true);
        LoadBookmarks();
    }

    private void LoadBookmarks()
    {
        QuickAccessBookmarks.Clear();
        if (_profile is not null)
        {
            foreach (var bookmark in _profile.EffectiveBookmarks.Distinct(StringComparer.Ordinal)) QuickAccessBookmarks.Add(bookmark);
        }
        SelectedBookmark = QuickAccessBookmarks.FirstOrDefault(bookmark => string.Equals(bookmark, Path, StringComparison.Ordinal));
        AddCurrentBookmarkCommand.NotifyCanExecuteChanged();
        RemoveBookmarkCommand.NotifyCanExecuteChanged();
        NavigateBookmarkCommand.NotifyCanExecuteChanged();
    }

    private void ReportError(Exception exception)
    {
        LastError = exception.Message;
        OnPropertyChanged(nameof(LastError));
    }

    private async Task ReloadCurrentPathAsync()
    {
        var entries = await _agent.BrowseAsync(_sessionId, Kind, Path).ConfigureAwait(true);
        Replace(entries);
    }

    private string ResolveMutationPath(string nameOrPath)
    {
        var value = nameOrPath.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter a name or destination path.", nameof(nameOrPath));
        if (Kind == PaneKind.Local)
        {
            var combined = System.IO.Path.IsPathFullyQualified(value) ? value : System.IO.Path.Combine(Path, value);
            return System.IO.Path.GetFullPath(combined);
        }

        if (value.StartsWith("/", StringComparison.Ordinal)) return value;
        return Path == "/" ? "/" + value : Path.TrimEnd('/') + "/" + value;
    }
}
