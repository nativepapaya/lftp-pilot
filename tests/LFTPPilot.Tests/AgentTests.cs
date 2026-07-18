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

    [Fact]
    public async Task DurableStoreMigratesVersionOneWithEmptySessionTabs()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var job = JobCoordinatorTests.Job(JobState.Queued);
        var versionOne = JsonSerializer.SerializeToUtf8Bytes(
            new { Version = 1, SavedAt = DateTimeOffset.UtcNow, Jobs = new[] { job } },
            FramedJsonStream.SerializerOptions);
        await File.WriteAllBytesAsync(path, versionOne, TestContext.Current.CancellationToken);

        var state = await new DurableJobStore(path).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AgentState.CurrentVersion, state.Version);
        Assert.Equal(job, Assert.Single(state.Jobs));
        Assert.Empty(state.EffectiveSessionTabs);
    }

    [Fact]
    public async Task DurableStoreSerializesJobsAndTabsThroughOneNoLostUpdateGate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var store = new DurableJobStore(path);
        var original = JobCoordinatorTests.Job(JobState.Queued);
        var replacement = JobCoordinatorTests.Job(JobState.Failed) with
        {
            Error = new("test-failure", "The replacement job failed."),
        };
        var tab = SessionTab(directory.Path);
        await store.SaveAsync([original], TestContext.Current.CancellationToken);

        await Task.WhenAll(
            store.SaveAsync([replacement], TestContext.Current.CancellationToken),
            store.SaveSessionTabsAsync([tab], TestContext.Current.CancellationToken));

        var state = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(replacement, Assert.Single(state.Jobs));
        Assert.Equal(tab, Assert.Single(state.EffectiveSessionTabs));
        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableStoreRejectsDuplicateOverLimitAndMalformedSessionTabs()
    {
        using var directory = new TemporaryDirectory();
        var duplicate = SessionTab(directory.Path);
        var duplicateOrder = duplicate with { Order = 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DurableJobStore(
            Path.Combine(directory.Path, "duplicate.json")).SaveSessionTabsAsync(
                [duplicate, duplicateOrder], TestContext.Current.CancellationToken));

        var excessive = Enumerable.Range(0, DurableJobStore.MaximumSessionTabs + 1)
            .Select(index => SessionTab(directory.Path) with { SessionId = Guid.NewGuid(), Order = index })
            .ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DurableJobStore(
            Path.Combine(directory.Path, "excessive.json")).SaveSessionTabsAsync(
                excessive, TestContext.Current.CancellationToken));

        var malformed = duplicate with { RemotePath = "/unsafe/../path" };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DurableJobStore(
            Path.Combine(directory.Path, "malformed.json")).SaveSessionTabsAsync(
            [malformed], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DurableStoreRejectsMalformedLoadedJobCollectionsAsInvalidData()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var valid = new JobSnapshot(
            Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "Valid transfer", JobState.Queued,
            now, now, Status: "Queued");
        var failed = valid with
        {
            Id = Guid.NewGuid(),
            State = JobState.Failed,
            Error = new("transfer-failed", "The transfer failed."),
        };
        var malformedStates = new (string Name, object? Jobs)[]
        {
            ("null collection", null),
            ("null member", new object?[] { null }),
            ("duplicate identifier", new[] { valid, valid }),
            ("empty identifier", new[] { valid with { Id = Guid.Empty } }),
            ("undefined kind", new[] { valid with { Kind = (JobKind)int.MaxValue } }),
            ("undefined state", new[] { valid with { State = (JobState)int.MaxValue } }),
            ("missing profile", new[] { valid with { ProfileId = null } }),
            ("empty profile", new[] { valid with { ProfileId = Guid.Empty } }),
            ("control in display name", new[] { valid with { DisplayName = "unsafe\nname" } }),
            ("oversized display name", new[] { valid with { DisplayName = new string('n', 257) } }),
            ("control in status", new[] { valid with { Status = "unsafe\rstatus" } }),
            ("oversized status", new[] { valid with { Status = new string('s', 2_049) } }),
            ("invalid progress", new[] { valid with { Progress = 1.01 } }),
            ("default created timestamp", new[] { valid with { CreatedAt = default } }),
            ("reverse timestamps", new[] { valid with { UpdatedAt = now.AddSeconds(-1) } }),
            ("non-UTC timestamp", new[] { valid with { UpdatedAt = now.ToOffset(TimeSpan.FromHours(1)) } }),
            ("future timestamps", new[] { valid with { CreatedAt = now.AddHours(1), UpdatedAt = now.AddHours(1) } }),
            ("scheduled without run time", new[] { valid with { State = JobState.Scheduled } }),
            ("non-transfer run time", new[] { valid with { Kind = JobKind.Mirror, RunAt = now.AddHours(1) } }),
            ("non-transfer retry", new[] { valid with { Kind = JobKind.Mirror, RetryAvailable = true } }),
            ("failed without error", new[] { failed with { Error = null } }),
            ("error on non-failure", new[] { valid with { Error = failed.Error } }),
            ("control in error code", new[] { failed with { Error = failed.Error! with { Code = "bad\ncode" } } }),
            ("oversized error message", new[] { failed with { Error = failed.Error! with { Message = new string('e', 4_097) } } }),
            ("control in error detail", new[] { failed with { Error = failed.Error! with { Detail = "unsafe\tdetail" } } }),
        };

        foreach (var malformed in malformedStates)
        {
            var path = Path.Combine(directory.Path, $"{Guid.NewGuid():N}.json");
            await WriteRawStateAsync(path, malformed.Jobs);

            var exception = await Record.ExceptionAsync(() =>
                new DurableJobStore(path).LoadAsync(TestContext.Current.CancellationToken));

            Assert.True(
                exception is InvalidDataException,
                $"{malformed.Name} returned {exception?.GetType().Name ?? "no error"} instead of InvalidDataException.");
        }
    }

    [Fact]
    public async Task DurableStoreDoesNotLetDelayedOlderCoordinatorCaptureOverwriteNewerJobsOrTabs()
    {
        using var directory = new TemporaryDirectory();
        var store = new DurableJobStore(Path.Combine(directory.Path, "state.json"));
        var tab = SessionTab(directory.Path);
        var coordinator = new JobCoordinator();
        var queued = JobCoordinatorTests.Job(JobState.Queued);
        coordinator.Enqueue(queued);
        var olderCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testCancellation = TestContext.Current.CancellationToken;

        var delayedOlderWrite = Task.Run(() => store.SaveAsync(() =>
        {
            var captured = coordinator.GetJobs();
            olderCaptured.TrySetResult();
            releaseOlder.Task.Wait(testCancellation);
            return captured;
        }, testCancellation), testCancellation);
        await olderCaptured.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var running = coordinator.Transition(queued.Id, JobState.Running, "Running");
        try
        {
            await Task.WhenAll(
                store.SaveAsync(coordinator.GetJobs, TestContext.Current.CancellationToken),
                store.SaveSessionTabsAsync([tab], TestContext.Current.CancellationToken));
        }
        finally
        {
            releaseOlder.TrySetResult();
        }
        await delayedOlderWrite.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var state = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(running, Assert.Single(state.Jobs));
        Assert.Equal(tab, Assert.Single(state.EffectiveSessionTabs));
    }

    [Fact]
    public async Task DisposingHostBeforeRunPreservesPreexistingDurableState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var store = new DurableJobStore(path);
        var job = JobCoordinatorTests.Job(JobState.Queued);
        var tab = SessionTab(directory.Path);
        await store.SaveAsync([job], TestContext.Current.CancellationToken);
        await store.SaveSessionTabsAsync([tab], TestContext.Current.CancellationToken);

        var host = new AgentHost(path);
        await host.DisposeAsync();

        var state = await new DurableJobStore(path).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(job, Assert.Single(state.Jobs));
        Assert.Equal(tab, Assert.Single(state.EffectiveSessionTabs));
    }

    [Fact]
    public async Task CancelledStartupDoesNotClaimOrOverwritePreexistingDurableState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var job = JobCoordinatorTests.Job(JobState.Queued);
        await new DurableJobStore(path).SaveAsync([job], TestContext.Current.CancellationToken);
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var host = new AgentHost(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.RunAsync(cancelled.Token));
        await host.DisposeAsync();

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedStartupDoesNotRewriteUnreadableDurableState()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var original = "{\"version\":2,\"savedAt\":\"not-a-timestamp\",\"jobs\":[],\"sessionTabs\":[]}"u8.ToArray();
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var host = new AgentHost(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            host.RunAsync(TestContext.Current.CancellationToken));
        await host.DisposeAsync();

        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
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
    public async Task IdleExitCountdownUsesTheReliableControlClientLifetime()
    {
        using var directory = new TemporaryDirectory();
        using var runCancellation = new CancellationTokenSource();
        await using var host = new AgentHost(Path.Combine(directory.Path, "jobs.json"));
        var run = host.RunAsync(runCancellation.Token);

        Task idleExit;
        await using (var client = new NamedPipeEngineClient(Environment.ProcessId))
        {
            _ = await client.RequestAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
            idleExit = host.WaitForIdleExitAsync(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
            await Task.Delay(60, TestContext.Current.CancellationToken);
            Assert.False(idleExit.IsCompleted);
        }

        await idleExit.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        runCancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
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
        var unsupported = await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync("jobs.enqueue", scheduled, TestContext.Current.CancellationToken));
        Assert.Equal("jobs.enqueue", unsupported.Method);
        Assert.Contains("executable transfer payload", unsupported.Message, StringComparison.Ordinal);

        var stopping = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(stopping.GetProperty("stopping").GetBoolean());
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DirectJobRequestsRejectNonCanonicalDataBeforeCoordinatorCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var host = new AgentHost(Path.Combine(directory.Path, "state.json"));
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        var invalid = JobCoordinatorTests.Job(JobState.Queued) with { DisplayName = "unsafe\nname" };

        await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync("jobs.enqueue", invalid, TestContext.Current.CancellationToken));
        var future = JobCoordinatorTests.Job(JobState.Queued) with
        {
            CreatedAt = DateTimeOffset.UtcNow.AddHours(1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync("jobs.enqueue", future, TestContext.Current.CancellationToken));
        Assert.Empty((await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions)!);

        var acceptedFuture = DateTimeOffset.UtcNow.AddSeconds(30);
        var valid = JobCoordinatorTests.Job(JobState.Queued) with
        {
            CreatedAt = acceptedFuture,
            UpdatedAt = acceptedFuture,
        };
        _ = await client.RequestAsync("jobs.enqueue", valid, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<EngineRequestRejectedException>(() => client.RequestAsync(
            "jobs.transition",
            new JobTransitionRequest(valid.Id, JobState.Running, "unsafe\rstatus"),
            TestContext.Current.CancellationToken));
        var unchanged = Assert.Single((await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions)!);
        Assert.Equal(JobState.Queued, unchanged.State);
        var cancel = await client.RequestAsync(
            "jobs.cancel",
            new JobCancelRequest(valid.Id),
            TestContext.Current.CancellationToken);
        Assert.True(cancel.GetProperty("cancelled").GetBoolean());
        var cancelled = Assert.Single((await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions)!);
        Assert.True(cancelled.UpdatedAt >= acceptedFuture);

        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FireAndForgetJobPersistenceFailureIsObservedAsSanitizedEvent()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        await using var host = new AgentHost(statePath);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        _ = await client.RequestAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
        await using var eventClient = new NamedPipeEngineClient(Environment.ProcessId);
        await using var events = eventClient.Events(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var nextEvent = events.MoveNextAsync().AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);

        try
        {
            await Assert.ThrowsAsync<EngineRequestRejectedException>(() => client.RequestAsync(
                "jobs.enqueue",
                JobCoordinatorTests.Job(JobState.Queued),
                TestContext.Current.CancellationToken));

            EngineEvent? failure = null;
            for (var attempt = 0; attempt < 4 && failure is null; attempt++)
            {
                Assert.True(await nextEvent.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                if (events.Current.Name == "job.persistence-failed") failure = events.Current;
                else nextEvent = events.MoveNextAsync().AsTask();
            }
            Assert.NotNull(failure);
            Assert.Equal(EngineEventKind.Error, failure.Kind);
            Assert.DoesNotContain(statePath, failure.Payload?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(statePath);
        }

        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopStillShutsDownWhenItsReplyWriteFails()
    {
        using var directory = new TemporaryDirectory();
        var responseWriteAttempted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new AgentHost(
            Path.Combine(directory.Path, "jobs.json"),
            (_, _, _) =>
            {
                responseWriteAttempted.TrySetResult(true);
                return ValueTask.FromException(new IOException("The stop reply pipe was deliberately broken."));
            });
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken));
        await responseWriteAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ShutdownAwaitsInFlightWorkspaceRequestBeforeDisposingItsLifecycleGates()
    {
        using var directory = new TemporaryDirectory();
        var profiles = new BlockingProfileStore();
        var hostKeys = new SftpHostKeyManager(new NoopHostKeyStore(), new NoopHostKeyProbe());
        var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(directory.Path, "runtime"));
        var host = new AgentHost(
            Path.Combine(directory.Path, "jobs.json"),
            profileStore: profiles,
            secretStore: new NoopSecretStore(),
            hostKeyManager: hostKeys,
            processHost: new NoopProcessHost(),
            runtimeProvider: new NoopRuntimeProvider(),
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: options);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        _ = await client.RequestAsync("ping", cancellationToken: TestContext.Current.CancellationToken);
        profiles.Arm();
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Blocking", ConnectionProtocol.Ftp, "files.example", 21,
            "anonymous", AuthenticationKind.Anonymous);

        var save = client.RequestAsync(
            WorkspaceMethods.ProfileSave,
            new ProfileSaveRequest(profile),
            TestContext.Current.CancellationToken);
        await profiles.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var dispose = host.DisposeAsync().AsTask();
        await profiles.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);

        profiles.Release.TrySetResult(true);
        await dispose.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        try { _ = await save; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException) { }
    }

    [Fact]
    public async Task ShutdownPersistenceFailureStillDisposesWorkspaceAndLftpSession()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "state.json");
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Cleanup", ConnectionProtocol.Ftp, "files.example", 21,
            "anonymous", AuthenticationKind.Anonymous);
        var processHost = new TrackingProcessHost();
        var planner = new TrackingMirrorPlanner();
        var host = new AgentHost(
            statePath,
            profileStore: new StaticProfileStore(profile),
            secretStore: new NoopSecretStore(),
            hostKeyManager: new SftpHostKeyManager(new NoopHostKeyStore(), new NoopHostKeyProbe()),
            processHost: processHost,
            runtimeProvider: new TrackingRuntimeProvider(directory.Path),
            mirrorPlanner: planner,
            workspaceOptions: AgentWorkspaceOptions.CreateDefault(Path.Combine(directory.Path, "runtime")));
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        _ = await client.RequestAsync(
            WorkspaceMethods.SessionConnect,
            new SessionConnectRequest(ConnectionIdentity.FromProfile(profile)),
            TestContext.Current.CancellationToken);
        await processHost.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        File.Delete(statePath);
        Directory.CreateDirectory(statePath);

        var failure = await Record.ExceptionAsync(() => host.DisposeAsync().AsTask());

        Assert.NotNull(failure);
        Assert.True(failure is IOException or AggregateException);
        Assert.True(planner.IsDisposed);
        await processHost.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Directory.Delete(statePath);
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

    [Fact]
    public async Task StartupClearsRetryAdvertisementWhenExecutablePlanWasNotRestored()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "jobs.json");
        var store = new DurableJobStore(path);
        var now = DateTimeOffset.UtcNow;
        var failed = new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "Retry me", JobState.Failed,
            now, now, Error: new("network", "Dropped", IsTransient: true), RetryAvailable: true);
        await store.SaveAsync([failed], TestContext.Current.CancellationToken);
        await using var host = new AgentHost(path);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);

        var restored = Assert.Single((await client.RequestAsync("jobs.list", cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<JobSnapshot[]>(FramedJsonStream.SerializerOptions)!);
        Assert.Equal(JobState.Failed, restored.State);
        Assert.False(restored.RetryAvailable);
        Assert.False(restored.CanRetry);

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

    private static PersistedSessionTab SessionTab(string localPath)
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Stored tab", ConnectionProtocol.Ftp, "files.example", 21,
            "anonymous", AuthenticationKind.Anonymous);
        return new(
            Guid.NewGuid(),
            profile.Id,
            ConnectionIdentity.FromProfile(profile),
            Path.GetFullPath(localPath),
            "/remote",
            0,
            ReconnectRequested: true);
    }

    private static async Task WriteRawStateAsync(string path, object? jobs)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Version = AgentState.CurrentVersion,
                SavedAt = DateTimeOffset.UtcNow,
                Jobs = jobs,
                SessionTabs = Array.Empty<object>(),
            },
            FramedJsonStream.SerializerOptions);
        await File.WriteAllBytesAsync(path, payload, TestContext.Current.CancellationToken);
    }

    private sealed class BlockingProfileStore : IProfileStore
    {
        private int _armed;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public async Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0) return [];
            Entered.TrySetResult(true);
            // Deliberately model an in-flight store operation that cannot be
            // interrupted until its underlying I/O completes.
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult(true));
            await Release.Task;
            return [];
        }

        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopSecretStore : ISecretStore
    {
        public Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopHostKeyStore : IHostKeyStore
    {
        public Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default) =>
            Task.FromResult<TrustedSftpHostKey?>(null);

        public Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopHostKeyProbe : ISshHostKeyProbe
    {
        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TrustedSftpHostKey>(new NotSupportedException("The shutdown test does not probe SSH hosts."));
    }

    private sealed class NoopProcessHost : ILftpProcessHost
    {
        public Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default) =>
            Task.FromException<ILftpSession>(new NotSupportedException("The shutdown test does not start LFTP."));
    }

    private sealed class NoopRuntimeProvider : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<LftpRuntimeDescriptor>(new NotSupportedException("The shutdown test does not resolve LFTP."));
    }

    private sealed class StaticProfileStore(ConnectionProfile profile) : IProfileStore
    {
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConnectionProfile>>([profile]);
        }

        public Task SaveAsync(ConnectionProfile savedProfile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingRuntimeProvider(string root) : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpRuntimeDescriptor(
                root,
                Path.Combine(root, "lftp.exe"),
                root,
                IsAuthenticated: false,
                Source: "test",
                IsTestOverride: true));
        }
    }

    private sealed class TrackingProcessHost : ILftpProcessHost
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started.TrySetResult();
            return Task.FromResult<ILftpSession>(new TrackingSession(Disposed));
        }
    }

    private sealed class TrackingSession(TaskCompletionSource disposed) : ILftpSession
    {
        public int ProcessId => 41;
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

        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingMirrorPlanner : IMirrorPlanner, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public MirrorPreview CreatePreview(
            MirrorDefinition definition,
            IEnumerable<string> dryRunOutput,
            DateTimeOffset? now = null) => throw new NotSupportedException();

        public string BuildExecutionCommand(
            MirrorDefinition definition,
            MirrorPreview preview,
            string? approvalToken,
            DateTimeOffset? now = null) => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;
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
