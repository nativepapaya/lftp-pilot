using System.Text.Json;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task DurableStoreAtomicallyRoundTripsJobs()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state", "jobs.json");
        var store = new DurableJobStore(path);
        var job = JobCoordinatorTests.Job(JobState.Queued);
        await store.SaveAsync([job], TestContext.Current.CancellationToken);
        var state = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentState.CurrentVersion, state.Version);
        Assert.Equal(job, Assert.Single(state.Jobs));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public async Task RestoreMarksEveryRunOnceJobMissedInsteadOfRunningAfterRestart(int minutesFromNow)
    {
        using var directory = new TemporaryDirectory();
        var coordinator = new JobCoordinator();
        var scheduled = JobCoordinatorTests.Job(JobState.Scheduled, DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));
        coordinator.Restore([scheduled]);
        var store = new DurableJobStore(Path.Combine(directory.Path, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store);
        await scheduler.RestoreAsync([scheduled], TestContext.Current.CancellationToken);
        var restored = Assert.Single(coordinator.GetJobs());
        Assert.Equal(JobState.Missed, restored.State);
        Assert.Contains("restarted", restored.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FutureRunOnceJobQueuesAtItsSelectedTime()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new JobCoordinator();
        var future = JobCoordinatorTests.Job(JobState.Scheduled, time.GetUtcNow().AddHours(1));
        coordinator.Enqueue(future);
        var store = new DurableJobStore(Path.Combine(directory.Path, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store, time);
        var callbackRuns = 0;
        await scheduler.ScheduleAsync(future, _ => { Interlocked.Increment(ref callbackRuns); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(() => coordinator.GetJobs().Single().State == JobState.Queued && Volatile.Read(ref callbackRuns) == 1,
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Queued, Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Jobs).State);
    }

    [Fact]
    public async Task ExplicitStopMarksPendingRunOnceJobMissedAndSuppressesItsCallback()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new JobCoordinator();
        var future = JobCoordinatorTests.Job(JobState.Scheduled, time.GetUtcNow().AddHours(1));
        coordinator.Enqueue(future);
        var store = new DurableJobStore(Path.Combine(directory.Path, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store, time);
        var callbackRuns = 0;
        await scheduler.ScheduleAsync(future, _ => { Interlocked.Increment(ref callbackRuns); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        await scheduler.MarkPendingMissedAsync("The agent was explicitly stopped.", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(2));
        await Task.Yield();

        Assert.Equal(0, Volatile.Read(ref callbackRuns));
        Assert.Equal(JobState.Missed, Assert.Single(coordinator.GetJobs()).State);
        Assert.Equal(JobState.Missed, Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Jobs).State);
    }

    [Fact]
    public async Task RunOncePersistenceFailureMarksJobMissedInsteadOfLeavingItArmed()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new JobCoordinator();
        var future = JobCoordinatorTests.Job(JobState.Scheduled, time.GetUtcNow().AddHours(1));
        coordinator.Enqueue(future);
        var blockingFile = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block", TestContext.Current.CancellationToken);
        var store = new DurableJobStore(Path.Combine(blockingFile, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store, time);

        await Assert.ThrowsAnyAsync<IOException>(() => scheduler.ScheduleAsync(
            future, _ => Task.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Equal(JobState.Missed, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public async Task RunOnceWhoseTimeElapsesDuringRegistrationIsPersistedAsMissed()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new JobCoordinator();
        var future = JobCoordinatorTests.Job(JobState.Scheduled, time.GetUtcNow().AddMinutes(1));
        coordinator.Enqueue(future);
        var store = new DurableJobStore(Path.Combine(directory.Path, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store, time);
        time.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.ScheduleAsync(
            future, _ => Task.CompletedTask, TestContext.Current.CancellationToken));

        Assert.Equal(JobState.Missed, Assert.Single(coordinator.GetJobs()).State);
        Assert.Equal(JobState.Missed, Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Jobs).State);
    }

    [Fact]
    public async Task RunOnceRegistrationWithoutExecutableCallbackFailsTerminally()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new JobCoordinator();
        var future = JobCoordinatorTests.Job(JobState.Scheduled, time.GetUtcNow().AddMinutes(1));
        coordinator.Enqueue(future);
        var store = new DurableJobStore(Path.Combine(directory.Path, "jobs.json"));
        await using var scheduler = new RunOnceScheduler(coordinator, store, time);

        await Assert.ThrowsAsync<ArgumentNullException>(() => scheduler.ScheduleAsync(
            future, null!, TestContext.Current.CancellationToken));

        Assert.Equal(JobState.Missed, Assert.Single(coordinator.GetJobs()).State);
        Assert.Equal(JobState.Missed, Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Jobs).State);
    }

    [Fact]
    public async Task CurrentUserPipeSupportsPingDurableQueueAndCancel()
    {
        using var directory = new TemporaryDirectory();
        var authorizedProcessId = 0;
        await using var host = new AgentHost(Path.Combine(directory.Path, "jobs.json"),
            clientAuthorizer: processId => { authorizedProcessId = processId; return true; });
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using (var impostorClient = new NamedPipeEngineClient(int.MaxValue))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                impostorClient.RequestAsync("ping", cancellationToken: TestContext.Current.CancellationToken));
        }
        await using (var impostorEventClient = new NamedPipeEngineClient(int.MaxValue))
        await using (var events = impostorEventClient.Events(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => events.MoveNextAsync().AsTask());
        }
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);

        var ping = await client.RequestAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentProtocol.CurrentVersion, ping.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(Environment.ProcessId, ping.GetProperty("clientProcessId").GetInt32());
        Assert.Equal(Environment.ProcessId, authorizedProcessId);

        var job = JobCoordinatorTests.Job(JobState.Queued);
        var enqueued = (await client.RequestAsync("jobs.enqueue", job, TestContext.Current.CancellationToken)).Deserialize<JobSnapshot>(FramedJsonStream.SerializerOptions);
        Assert.Equal(job.Id, enqueued?.Id);
        var jobs = (await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken)).Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions);
        Assert.Equal(job.Id, Assert.Single(jobs!).Id);
        var cancel = await client.RequestAsync("jobs.cancel", new JobCancelRequest(job.Id, "test"), TestContext.Current.CancellationToken);
        Assert.True(cancel.GetProperty("cancelled").GetBoolean());

        var scheduled = JobCoordinatorTests.Job(JobState.Scheduled, DateTimeOffset.UtcNow.AddHours(1));
        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("jobs.enqueue", scheduled, TestContext.Current.CancellationToken));
        Assert.Contains("executable transfer payload", unsupported.Message, StringComparison.Ordinal);

        var stopping = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(stopping.GetProperty("stopping").GetBoolean());
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartupNormalizesInterruptedJobsBeforeBootstrap()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "jobs.json");
        var store = new DurableJobStore(path);
        var interrupted = new[]
        {
            JobCoordinatorTests.Job(JobState.Queued),
            JobCoordinatorTests.Job(JobState.Running),
            JobCoordinatorTests.Job(JobState.Paused),
        };
        await store.SaveAsync(interrupted, TestContext.Current.CancellationToken);
        await using var host = new AgentHost(path);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);

        var restored = (await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions)!;
        Assert.All(restored, job =>
        {
            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("agent-interrupted", job.Error?.Code);
            Assert.True(job.Error?.IsTransient);
        });

        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The condition was not reached.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
