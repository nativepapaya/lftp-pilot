using System.Collections.Immutable;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class RemoteSearchViewModelTests
{
    [Fact]
    public async Task CompletedSearchLoadsEveryPageAndOpensSelectedLocation()
    {
        var agent = new SearchAgent();
        RemoteSearchMatch? opened = null;
        await using var viewModel = new RemoteSearchViewModel(
            agent,
            Guid.NewGuid(),
            () => "/scope",
            match => { opened = match; return Task.CompletedTask; },
            isConnected: true)
        {
            Query = "file",
            MaxDepth = 7,
            MatchCase = true,
        };
        agent.StartHandler = (search, _) => Task.FromResult(Page(
            search,
            RemoteSearchState.Completed,
            [Match("file-01.txt"), Match("file-folder", directory: true)],
            $"{search.SearchId:N}:2",
            total: 3));
        agent.GetHandler = (search, continuation, _, _) =>
        {
            Assert.Equal($"{search.SearchId:N}:2", continuation);
            return Task.FromResult(Page(
                search,
                RemoteSearchState.Completed,
                [Match("file-02.txt")],
                total: 3));
        };

        viewModel.Open();
        await viewModel.SearchAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsOpen);
        Assert.Equal(3, viewModel.Results.Count);
        Assert.Equal("/scope", viewModel.ScopePath);
        Assert.Contains("3 matches", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        var submitted = Assert.Single(agent.StartedSearches);
        Assert.Equal("/scope", submitted.Root);
        Assert.Equal("file", submitted.Query);
        Assert.Equal(7, submitted.MaxDepth);
        Assert.True(submitted.MatchCase);

        viewModel.SelectedResult = viewModel.Results[1];
        await viewModel.OpenSelectedLocationAsync();
        Assert.Equal(viewModel.Results[1].Match, opened);
    }

    [Fact]
    public async Task UnknownStartOutcomeRecoversWithTheSameOpaqueIdentifier()
    {
        var agent = new SearchAgent();
        var startCalls = 0;
        agent.StartHandler = (search, _) =>
        {
            startCalls++;
            if (startCalls == 1)
            {
                return Task.FromException<RemoteSearchPage>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.RemoteSearchStart,
                    new IOException("simulated lost reply")));
            }
            return Task.FromResult(Page(search, RemoteSearchState.Completed, [Match("file.txt")], total: 1));
        };
        agent.GetHandler = (_, _, _, _) =>
            Task.FromException<RemoteSearchPage>(new KeyNotFoundException("not committed"));
        await using var viewModel = Create(agent);

        await viewModel.SearchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, agent.StartedSearches.Count);
        Assert.Equal(agent.StartedSearches[0].SearchId, agent.StartedSearches[1].SearchId);
        Assert.Equal(1, agent.GetCalls);
        Assert.Single(viewModel.Results);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public async Task CancelledGenerationCannotBeOverwrittenByLateAgentResults()
    {
        var agent = new SearchAgent();
        var getEntered = NewSignal();
        var releaseGet = NewSignal();
        agent.StartHandler = (search, _) => Task.FromResult(Page(search, RemoteSearchState.Running));
        agent.GetHandler = async (search, _, _, _) =>
        {
            getEntered.TrySetResult(true);
            // Deliberately model a transport that completes after cancellation.
            await releaseGet.Task;
            return Page(search, RemoteSearchState.Completed, [Match("late-file.txt")], total: 1);
        };
        await using var viewModel = Create(agent);

        var search = viewModel.SearchAsync(TestContext.Current.CancellationToken);
        await getEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.CancelAsync();
        releaseGet.TrySetResult(true);
        await search.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Single(agent.CancelledSearches);
        Assert.Empty(viewModel.Results);
        Assert.Equal("Remote search cancelled.", viewModel.Status);
        Assert.False(viewModel.IsSearching);
    }

    [Fact]
    public async Task RepeatedPathAcrossPagesFailsClosed()
    {
        var agent = new SearchAgent();
        var duplicate = Match("file.txt");
        agent.StartHandler = (search, _) => Task.FromResult(Page(
            search,
            RemoteSearchState.Completed,
            [duplicate],
            $"{search.SearchId:N}:1",
            total: 2));
        agent.GetHandler = (search, _, _, _) => Task.FromResult(Page(
            search,
            RemoteSearchState.Completed,
            [duplicate],
            total: 2));
        await using var viewModel = Create(agent);

        await viewModel.SearchAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(viewModel.Error);
        Assert.Contains("repeated", viewModel.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Remote search failed.", viewModel.Status);
        Assert.Empty(viewModel.Results);
    }

    [Fact]
    public async Task RepeatedContinuationTokenFailsBeforeAnotherPageCanLoop()
    {
        var agent = new SearchAgent();
        agent.StartHandler = (search, _) => Task.FromResult(Page(
            search,
            RemoteSearchState.Completed,
            [Match("file-01.txt")],
            "same-token",
            total: 3));
        agent.GetHandler = (search, _, _, _) => Task.FromResult(Page(
            search,
            RemoteSearchState.Completed,
            [Match("file-02.txt")],
            "same-token",
            total: 3));
        await using var viewModel = Create(agent);

        await viewModel.SearchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, agent.GetCalls);
        Assert.Contains("continuation token", viewModel.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewModel.Results);
    }

    [Fact]
    public async Task DisconnectCancelsActiveSearchAndPreventsOpeningNewSearch()
    {
        var agent = new SearchAgent();
        var running = NewSignal();
        agent.StartHandler = (search, _) =>
        {
            running.TrySetResult(true);
            return Task.FromResult(Page(search, RemoteSearchState.Running));
        };
        agent.GetHandler = async (search, _, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Page(search, RemoteSearchState.Running);
        };
        await using var viewModel = Create(agent);

        var search = viewModel.SearchAsync(TestContext.Current.CancellationToken);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.SetConnected(false);
        await WaitUntilAsync(() => !viewModel.IsSearching);
        await search.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.Open();

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.CanSearch);
        Assert.Single(agent.CancelledSearches);
    }

    private static RemoteSearchViewModel Create(SearchAgent agent) => new(
        agent,
        Guid.NewGuid(),
        () => "/scope",
        _ => Task.CompletedTask,
        isConnected: true)
    {
        Query = "file",
    };

    private static RemoteSearchMatch Match(string name, bool directory = false) => new(
        name,
        $"/scope/{name}",
        directory ? RemoteSearchEntryKind.Directory : RemoteSearchEntryKind.Other);

    private static RemoteSearchPage Page(
        RemoteSearchSpec search,
        RemoteSearchState state,
        ImmutableArray<RemoteSearchMatch> matches = default,
        string? continuation = null,
        int? total = null,
        bool wasLimited = false,
        EngineError? error = null)
    {
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        return new(search, state, matches, continuation, total, total ?? (matches.IsDefault ? 0 : matches.Length),
            wasLimited, now, now, error);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The remote-search view model did not reach the expected state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class SearchAgent : IAgentWorkspaceClient
    {
        public Func<RemoteSearchSpec, CancellationToken, Task<RemoteSearchPage>> StartHandler { get; set; } =
            (search, _) => Task.FromResult(Page(search, RemoteSearchState.Completed, total: 0));
        public Func<RemoteSearchSpec, string?, int, CancellationToken, Task<RemoteSearchPage>> GetHandler { get; set; } =
            (search, _, _, _) => Task.FromResult(Page(search, RemoteSearchState.Completed, total: 0));
        public Func<Guid, Guid, CancellationToken, Task<bool>> CancelHandler { get; set; } =
            (_, _, _) => Task.FromResult(true);
        public List<RemoteSearchSpec> StartedSearches { get; } = [];
        public List<(Guid SearchId, Guid SessionId)> CancelledSearches { get; } = [];
        public int GetCalls { get; private set; }
        public bool IsConnected => true;
        public event EventHandler<EngineEvent>? EventReceived { add { } remove { } }
        public event EventHandler? StateInvalidated { add { } remove { } }

        public Task<RemoteSearchPage> StartRemoteSearchAsync(
            RemoteSearchSpec search,
            CancellationToken cancellationToken = default)
        {
            StartedSearches.Add(search);
            return StartHandler(search, cancellationToken);
        }

        public Task<RemoteSearchPage> GetRemoteSearchAsync(
            RemoteSearchSpec search,
            string? continuationToken = null,
            int pageSize = RemoteSearchPolicy.DefaultPageSize,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return GetHandler(search, continuationToken, pageSize, cancellationToken);
        }

        public Task<bool> CancelRemoteSearchAsync(
            Guid searchId,
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            CancelledSearches.Add((searchId, sessionId));
            return CancelHandler(searchId, sessionId, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default) => Unsupported<UiWorkspaceBootstrap>();
        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default) => Unsupported<ConnectionProfile>();
        public Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyInspection>();
        public Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(SftpHostKeyReview review, bool replaceExisting, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyApproveResult>();
        public Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default, Guid? existingSessionId = null) => Unsupported<WorkspaceSessionSeed>();
        public Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<FileEntry>>();
        public Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default) => Unsupported<MirrorUiPreview>();
        public Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<string>>();
        public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferPlan>();
        public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferEnqueueResult>();
        public Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => Unsupported<RemoteEditSession>();
        public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<RemoteEditReview>();
        public Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default) => Unsupported<RemoteEditActionResult>();
        public Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task StopAgentAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();
        public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Unsupported<AppUpdateStatus>();
        public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();

        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
