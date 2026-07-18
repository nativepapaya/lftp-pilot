using System.Collections.Concurrent;
using System.Collections.Immutable;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class RemoteSearchCoordinatorTests
{
    [Fact]
    public async Task CompletedSearchIsIdempotentAndPagedFromOneRetainedSnapshot()
    {
        await using var fixture = await SearchFixture.CreateAsync(
            "/root/",
            "/root/file-01.txt",
            "/root/file-folder/",
            "/root/other.txt",
            "/root/sub/file-02.txt",
            "/root/sub/file-03.txt");
        await using var coordinator = new RemoteSearchCoordinator(
            fixture.Registry, TimeSpan.FromSeconds(2));
        var search = new RemoteSearchSpec(
            Guid.NewGuid(), fixture.SessionId, "/root", "file", MaxDepth: 9);

        _ = coordinator.Start(new(search));
        var completed = await WaitForTerminalAsync(coordinator, search);
        var repeated = coordinator.Start(new(search));

        Assert.Equal(RemoteSearchState.Completed, completed.State);
        Assert.Equal(RemoteSearchState.Completed, repeated.State);
        Assert.Equal(4, completed.TotalMatches);
        Assert.Single(fixture.Host.Starts, start =>
            start.Tag == $"remote-search-{search.SearchId:N}");
        Assert.Contains(
            LftpCommandBuilder.BuildRemoteFind(search.Root, search.MaxDepth),
            fixture.Host.SearchCommands);

        var first = coordinator.Get(new(search.SearchId, search.SessionId, PageSize: 2));
        Assert.Equal(2, first.Matches.Length);
        Assert.NotNull(first.ContinuationToken);
        var second = coordinator.Get(new(
            search.SearchId, search.SessionId, first.ContinuationToken, PageSize: 2));
        Assert.Equal(2, second.Matches.Length);
        Assert.Null(second.ContinuationToken);
        Assert.Equal(4, first.Matches.Concat(second.Matches)
            .Select(static match => match.FullPath).Distinct(StringComparer.Ordinal).Count());

        Assert.Throws<InvalidDataException>(() => coordinator.Get(new(
            search.SearchId,
            search.SessionId,
            $"{Guid.NewGuid():N}:2",
            PageSize: 2)));
        Assert.Throws<InvalidOperationException>(() => coordinator.Start(new(
            search with { Query = "different" })));
    }

    [Fact]
    public async Task CancellationStopsTheIsolatedProcessAndPublishesCancelledState()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        fixture.Host.HoldSearch = true;
        await using var coordinator = new RemoteSearchCoordinator(
            fixture.Registry, TimeSpan.FromSeconds(30));
        var search = new RemoteSearchSpec(
            Guid.NewGuid(), fixture.SessionId, "/root", "file");

        var started = coordinator.Start(new(search));
        await fixture.Host.SearchEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var cancelled = await coordinator.CancelAsync(
            new(search.SearchId, search.SessionId), TestContext.Current.CancellationToken);
        var page = coordinator.Get(new(search.SearchId, search.SessionId));

        Assert.True(cancelled);
        Assert.True(started.State is RemoteSearchState.Queued or RemoteSearchState.Running);
        Assert.Equal(RemoteSearchState.Cancelled, page.State);
        Assert.Contains($"remote-search-{search.SearchId:N}", fixture.Host.DisposedRoles);
    }

    [Fact]
    public async Task ParserFailureIsRetainedAsBoundedTerminalError()
    {
        await using var fixture = await SearchFixture.CreateAsync("/root/", "/outside/file.txt");
        await using var coordinator = new RemoteSearchCoordinator(
            fixture.Registry, TimeSpan.FromSeconds(2));
        var search = new RemoteSearchSpec(
            Guid.NewGuid(), fixture.SessionId, "/root", "file");

        _ = coordinator.Start(new(search));
        var page = await WaitForTerminalAsync(coordinator, search);

        Assert.Equal(RemoteSearchState.Failed, page.State);
        Assert.NotNull(page.Error);
        Assert.Contains("root", page.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Matches);
        Assert.Null(page.ContinuationToken);
    }

    [Fact]
    public async Task SessionCleanupCancelsAndForgetsActiveSearch()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        fixture.Host.HoldSearch = true;
        await using var coordinator = new RemoteSearchCoordinator(
            fixture.Registry, TimeSpan.FromSeconds(30));
        var search = new RemoteSearchSpec(
            Guid.NewGuid(), fixture.SessionId, "/root", "file");

        _ = coordinator.Start(new(search));
        await fixture.Host.SearchEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await coordinator.CancelSessionAsync(
            fixture.SessionId, TestContext.Current.CancellationToken);

        Assert.Throws<KeyNotFoundException>(() => coordinator.Get(new(
            search.SearchId, search.SessionId)));
        Assert.Contains($"remote-search-{search.SearchId:N}", fixture.Host.DisposedRoles);
    }

    private static async Task<RemoteSearchPage> WaitForTerminalAsync(
        RemoteSearchCoordinator coordinator,
        RemoteSearchSpec search)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (true)
        {
            var page = coordinator.Get(new(search.SearchId, search.SessionId));
            if (page.State is not RemoteSearchState.Queued and not RemoteSearchState.Running)
                return page;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The remote search did not reach a terminal state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class SearchFixture : IAsyncDisposable
    {
        private SearchFixture(string root, SearchProcessHost host, SessionRegistry registry, Guid sessionId)
        {
            Root = root;
            Host = host;
            Registry = registry;
            SessionId = sessionId;
        }

        public string Root { get; }
        public SearchProcessHost Host { get; }
        public SessionRegistry Registry { get; }
        public Guid SessionId { get; }

        public static async Task<SearchFixture> CreateAsync(params string[] searchOutput)
        {
            var root = Path.Combine(Path.GetTempPath(), "LFTPPilot.RemoteSearchTests", Guid.NewGuid().ToString("N"));
            var host = new SearchProcessHost(searchOutput);
            var options = AgentWorkspaceOptions.CreateDefault(root) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                RemoteSearchTimeout = TimeSpan.FromSeconds(2),
            };
            var registry = new SessionRegistry(
                host,
                new TestRuntimeProvider(),
                new SftpHostKeyManager(new NullHostKeyStore(), new NullHostKeyProbe()),
                options);
            var profile = new ConnectionProfile(
                Guid.NewGuid(), "Search", ConnectionProtocol.Ftp, "files.example", 21,
                "anonymous", AuthenticationKind.Anonymous);
            try
            {
                var snapshot = await registry.ConnectAsync(
                    profile, null, TestContext.Current.CancellationToken);
                return new(root, host, registry, snapshot.SessionId);
            }
            catch
            {
                await registry.DisposeAsync();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class SearchProcessHost(IEnumerable<string> output) : ILftpProcessHost
    {
        private readonly ImmutableArray<string> _output = [.. output];
        private int _nextProcessId = 100;
        public ConcurrentBag<LftpProcessStartOptions> Starts { get; } = [];
        public ConcurrentBag<string> SearchCommands { get; } = [];
        public ConcurrentBag<string> DisposedRoles { get; } = [];
        public TaskCompletionSource<bool> SearchEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldSearch { get; set; }

        public Task<ILftpSession> StartAsync(
            LftpProcessStartOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts.Add(options);
            return Task.FromResult<ILftpSession>(new SearchSession(
                Interlocked.Increment(ref _nextProcessId),
                options.Tag,
                _output,
                SearchCommands,
                DisposedRoles,
                SearchEntered,
                () => HoldSearch));
        }
    }

    private sealed class SearchSession(
        int processId,
        string role,
        ImmutableArray<string> output,
        ConcurrentBag<string> searchCommands,
        ConcurrentBag<string> disposedRoles,
        TaskCompletionSource<bool> searchEntered,
        Func<bool> holdSearch) : ILftpSession
    {
        public int ProcessId { get; } = processId;
        public bool IsRunning { get; private set; } = true;
        public event EventHandler<LftpOutputLine>? OutputReceived { add { } remove { } }
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput { add { } remove { } }

        public Task<LftpCommandResult> ExecuteAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpCommandResult([]));
        }

        public async Task<LftpCommandResult> ExecuteToExitAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            searchCommands.Add(command);
            searchEntered.TrySetResult(true);
            if (holdSearch())
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return new(output.Select(static line => new LftpOutputLine("stdout", line)).ToImmutableArray());
        }

        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            disposedRoles.Add(role);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestRuntimeProvider : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpRuntimeDescriptor(
                @"C:\fake", @"C:\fake\bin\lftp.exe", @"C:\fake\bin",
                false, "test", true));
        }
    }

    private sealed class NullHostKeyStore : IHostKeyStore
    {
        public Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedSftpHostKey?>(null);
        public Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NullHostKeyProbe : ISshHostKeyProbe
    {
        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TrustedSftpHostKey>(new NotSupportedException());
    }
}
