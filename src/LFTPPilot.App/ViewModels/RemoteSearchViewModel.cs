using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class RemoteSearchViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CancellationRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly IAgentWorkspaceClient _agent;
    private readonly Guid _sessionId;
    private readonly Func<string> _getRemoteRoot;
    private readonly Func<RemoteSearchMatch, Task> _navigate;
    private CancellationTokenSource? _activeCancellation;
    private RemoteSearchSpec? _activeSearch;
    private RemoteSearchResultItem? _selectedResult;
    private string _query = string.Empty;
    private string _scopePath = "/";
    private string _status = "Search remote file and folder names recursively.";
    private string? _error;
    private int _maxDepth = RemoteSearchPolicy.DefaultMaxDepth;
    private bool _matchCase;
    private bool _isOpen;
    private bool _isSearching;
    private bool _hasSearched;
    private bool _wasLimited;
    private bool _isConnected;
    private long _generation;
    private bool _disposed;

    public RemoteSearchViewModel(
        IAgentWorkspaceClient agent,
        Guid sessionId,
        Func<string> getRemoteRoot,
        Func<RemoteSearchMatch, Task> navigate,
        bool isConnected)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        _sessionId = sessionId;
        _getRemoteRoot = getRemoteRoot ?? throw new ArgumentNullException(nameof(getRemoteRoot));
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        _isConnected = isConnected;
        OpenCommand = new RelayCommand(_ => Open(), _ => IsConnected);
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(), _ => CanSearch, ReportUnexpectedError);
        CancelCommand = new AsyncRelayCommand(_ => CancelAsync(), _ => IsSearching, ReportUnexpectedError);
        CloseCommand = new AsyncRelayCommand(_ => CloseAsync(), null, ReportUnexpectedError);
        OpenLocationCommand = new AsyncRelayCommand(_ => OpenSelectedLocationAsync(), _ => CanOpenSelectedLocation, ReportUnexpectedError);
    }

    public ObservableCollection<RemoteSearchResultItem> Results { get; } = [];
    public RelayCommand OpenCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand CloseCommand { get; }
    public AsyncRelayCommand OpenLocationCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    public string ScopePath { get => _scopePath; private set => SetProperty(ref _scopePath, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;

    public int MaxDepth
    {
        get => _maxDepth;
        set
        {
            if (!SetProperty(ref _maxDepth, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    public bool MatchCase { get => _matchCase; set => SetProperty(ref _matchCase, value); }
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }
    public bool HasSearched { get => _hasSearched; private set => SetProperty(ref _hasSearched, value); }
    public bool WasLimited { get => _wasLimited; private set => SetProperty(ref _wasLimited, value); }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            SearchCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            OpenCommand.NotifyCanExecuteChanged();
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanSearch => IsConnected && !IsSearching &&
        !string.IsNullOrWhiteSpace(Query) && Query.Length <= RemoteSearchPolicy.MaximumQueryCharacters &&
        MaxDepth is >= 1 and <= RemoteSearchPolicy.MaximumMaxDepth;

    public RemoteSearchResultItem? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetProperty(ref _selectedResult, value)) return;
            OnPropertyChanged(nameof(CanOpenSelectedLocation));
            OpenLocationCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanOpenSelectedLocation => SelectedResult is not null && IsConnected && !IsSearching;

    public void Open()
    {
        if (!IsConnected) return;
        var root = _getRemoteRoot();
        if (!string.Equals(ScopePath, root, StringComparison.Ordinal))
        {
            Results.Clear();
            SelectedResult = null;
            Error = null;
            WasLimited = false;
            HasSearched = false;
            Status = "Search remote file and folder names recursively.";
        }
        ScopePath = root;
        IsOpen = true;
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        OnPropertyChanged(nameof(CanOpenSelectedLocation));
        OpenLocationCommand.NotifyCanExecuteChanged();
        if (!connected && IsSearching) _ = CancelAsync();
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanSearch) return;
        var query = Query.Trim();
        var root = _getRemoteRoot();
        var search = new RemoteSearchSpec(Guid.NewGuid(), _sessionId, root, query, MaxDepth, MatchCase);
        RemoteSearchPolicy.Validate(search);

        var generation = Interlocked.Increment(ref _generation);
        await CancelActiveCoreAsync().ConfigureAwait(true);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCancellation = linkedCancellation;
        _activeSearch = search;
        ScopePath = root;
        Error = null;
        WasLimited = false;
        HasSearched = true;
        Results.Clear();
        SelectedResult = null;
        IsSearching = true;
        Status = $"Searching {root}…";

        try
        {
            var page = await StartOrRecoverAsync(search, linkedCancellation.Token).ConfigureAwait(true);
            ThrowIfSuperseded(generation, linkedCancellation.Token);
            while (page.State is RemoteSearchState.Queued or RemoteSearchState.Running)
            {
                await Task.Delay(PollInterval, linkedCancellation.Token).ConfigureAwait(true);
                page = await _agent.GetRemoteSearchAsync(search, cancellationToken: linkedCancellation.Token).ConfigureAwait(true);
                ThrowIfSuperseded(generation, linkedCancellation.Token);
            }

            if (page.State == RemoteSearchState.Failed)
            {
                Error = page.Error?.Message ?? "The Agent could not complete the remote search.";
                Status = "Remote search failed.";
                return;
            }
            if (page.State == RemoteSearchState.Cancelled)
            {
                Status = "Remote search cancelled.";
                return;
            }
            if (page.State != RemoteSearchState.Completed)
                throw new InvalidDataException("The Agent returned an unsupported terminal remote-search state.");

            var expectedTotal = page.TotalMatches
                ?? throw new InvalidDataException("The completed remote search omitted its total match count.");
            var expectedScannedEntries = page.ScannedEntries;
            var expectedWasLimited = page.WasLimited;
            var expectedStartedAt = page.StartedAt;
            var expectedUpdatedAt = page.UpdatedAt;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenContinuationTokens = new HashSet<string>(StringComparer.Ordinal);
            ThrowIfSuperseded(generation, linkedCancellation.Token);
            AppendPage(page, seen);
            var continuation = page.ContinuationToken;
            while (continuation is not null)
            {
                if (!seenContinuationTokens.Add(continuation))
                    throw new InvalidDataException("The Agent repeated a remote-search continuation token.");
                page = await _agent.GetRemoteSearchAsync(
                    search, continuation, cancellationToken: linkedCancellation.Token).ConfigureAwait(true);
                ThrowIfSuperseded(generation, linkedCancellation.Token);
                if (page.State != RemoteSearchState.Completed || page.TotalMatches != expectedTotal ||
                    page.ScannedEntries != expectedScannedEntries || page.WasLimited != expectedWasLimited ||
                    page.StartedAt != expectedStartedAt || page.UpdatedAt != expectedUpdatedAt)
                    throw new InvalidDataException("The remote-search result snapshot changed while paging.");
                AppendPage(page, seen);
                continuation = page.ContinuationToken;
            }
            if (Results.Count != expectedTotal)
                throw new InvalidDataException("The Agent returned an incomplete remote-search result snapshot.");

            ThrowIfSuperseded(generation, linkedCancellation.Token);
            WasLimited = expectedWasLimited;
            Status = Results.Count switch
            {
                0 => $"No names containing “{query}” were found under {root}.",
                1 => $"1 match under {root}.",
                _ => $"{Results.Count:N0} matches under {root}.",
            };
            if (WasLimited) Status += " The Agent safety limit was reached; narrow the root, name, or depth for a complete result.";
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _generation)) Status = "Remote search cancelled.";
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException))
        {
            if (generation == Volatile.Read(ref _generation))
            {
                Results.Clear();
                SelectedResult = null;
                WasLimited = false;
                Error = exception.Message;
                Status = "Remote search failed.";
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _activeSearch = null;
                _activeCancellation = null;
                IsSearching = false;
            }
        }
    }

    public async Task CancelAsync()
    {
        Interlocked.Increment(ref _generation);
        await CancelActiveCoreAsync().ConfigureAwait(true);
        Results.Clear();
        SelectedResult = null;
        WasLimited = false;
        IsSearching = false;
        Status = "Remote search cancelled.";
    }

    public async Task CloseAsync()
    {
        if (IsSearching) await CancelAsync().ConfigureAwait(true);
        IsOpen = false;
    }

    public async Task OpenSelectedLocationAsync()
    {
        if (!CanOpenSelectedLocation || SelectedResult is null) return;
        await _navigate(SelectedResult.Match).ConfigureAwait(true);
        Status = SelectedResult.Match.IsDirectory
            ? $"Opened {SelectedResult.FullPath}."
            : $"Opened the current location of {SelectedResult.Name}.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        await CancelActiveCoreAsync().ConfigureAwait(false);
    }

    private async Task<RemoteSearchPage> StartOrRecoverAsync(
        RemoteSearchSpec search,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _agent.StartRemoteSearchAsync(search, cancellationToken).ConfigureAwait(true);
        }
        catch (AgentRequestOutcomeUnknownException)
        {
            try
            {
                return await _agent.GetRemoteSearchAsync(search, cancellationToken: cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                // The first start either committed or did not. Reusing the same opaque ID
                // makes this retry converge without launching a duplicate LFTP process.
                return await _agent.StartRemoteSearchAsync(search, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private void AppendPage(RemoteSearchPage page, HashSet<string> seen)
    {
        foreach (var match in page.EffectiveMatches)
        {
            if (!seen.Add(match.FullPath))
                throw new InvalidDataException("The Agent repeated a remote-search path across result pages.");
            Results.Add(new(match));
            if (Results.Count > RemoteSearchPolicy.MaximumMatches)
                throw new InvalidDataException("The Agent exceeded the remote-search result limit.");
        }
    }

    private void ThrowIfSuperseded(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation))
            throw new OperationCanceledException("The remote search was superseded by a newer request.", cancellationToken);
    }

    private async Task CancelActiveCoreAsync()
    {
        var active = _activeSearch;
        var cancellation = _activeCancellation;
        _activeSearch = null;
        _activeCancellation = null;
        if (active is null) return;
        cancellation?.Cancel();
        using var timeout = new CancellationTokenSource(CancellationRequestTimeout);
        try
        {
            _ = await _agent.CancelRemoteSearchAsync(active.SearchId, active.SessionId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private void ReportUnexpectedError(Exception exception)
    {
        Error = exception.Message;
        Status = "Remote search failed.";
    }
}
