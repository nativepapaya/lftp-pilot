using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class HistoryTests
{
    [Fact]
    public void HistoryPolicyAcceptsOnlyBoundedTerminalRecords()
    {
        var record = CreateRecord(Guid.NewGuid(), JobState.Completed, DateTimeOffset.UtcNow);

        HistoryRecordPolicy.Validate(record);
        Assert.Throws<ArgumentException>(() => HistoryRecordPolicy.Validate(record with { Outcome = JobState.Running }));
        Assert.Throws<ArgumentException>(() => HistoryRecordPolicy.Validate(record with { DisplayName = "bad\rname" }));
        Assert.Throws<ArgumentException>(() => HistoryRecordPolicy.Validate(record with { BytesTransferred = -1 }));
        Assert.Throws<ArgumentException>(() => HistoryRecordPolicy.Validate(record with { FinishedAt = record.StartedAt.AddTicks(-1) }));
    }

    [Fact]
    public async Task JsonStorePersistsOrdersAndUpsertsBoundedRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        var store = new JsonHistoryStore(path);
        var now = DateTimeOffset.UtcNow;
        var older = CreateRecord(Guid.NewGuid(), JobState.Completed, now.AddMinutes(-2));
        var newer = CreateRecord(Guid.NewGuid(), JobState.Failed, now.AddMinutes(-1));

        await store.AppendAsync(newer, cancellationToken);
        await store.AppendAsync(older, cancellationToken);
        await store.AppendAsync(older with { Outcome = JobState.Cancelled, Detail = "Cancelled by tester" }, cancellationToken);

        var restored = await new JsonHistoryStore(path).GetRecentAsync(10, cancellationToken);

        Assert.Equal(2, restored.Count);
        Assert.Equal(newer.Id, restored[0].Id);
        Assert.Equal(JobState.Cancelled, restored[1].Outcome);
        Assert.Equal("Cancelled by tester", restored[1].Detail);
    }

    [Fact]
    public async Task JsonStoreRejectsInvalidPersistedRecords()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "history.json");
        await File.WriteAllTextAsync(path, "[{\"id\":\"00000000-0000-0000-0000-000000000000\"}]", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonHistoryStore(path).GetRecentAsync(10, cancellationToken));
    }

    [Fact]
    public async Task RecorderWritesOnlyTerminalJobsAndUsesObservedRunningTime()
    {
        var store = new InMemoryHistoryStore();
        var published = new List<HistoryRecord>();
        var recorder = new JobHistoryRecorder(store, published.Add);
        var id = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddMinutes(-3);
        var queued = CreateJob(id, profileId, JobState.Queued, created, created, "Queued");
        var running = queued with { State = JobState.Running, UpdatedAt = created.AddMinutes(1), Status = "Running" };
        var completed = running with { State = JobState.Completed, UpdatedAt = created.AddMinutes(2), Status = "Complete", Progress = 1 };

        recorder.Observe(queued);
        recorder.Observe(running);
        recorder.Observe(completed);
        await recorder.FlushAsync();

        var history = await store.GetRecentAsync(10, TestContext.Current.CancellationToken);
        var record = Assert.Single(history);
        Assert.Equal(id, record.Id);
        Assert.Equal(running.UpdatedAt, record.StartedAt);
        Assert.Equal(completed.UpdatedAt, record.FinishedAt);
        Assert.Equal("Complete", record.Detail);
        Assert.Single(published);
    }

    [Fact]
    public async Task RecorderContainsOneWriteFailureAndContinuesItsSerializedChain()
    {
        var store = new FailFirstHistoryStore();
        var failures = new List<Exception>();
        var recorder = new JobHistoryRecorder(store, failed: failures.Add);
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        recorder.Observe(CreateJob(Guid.NewGuid(), profileId, JobState.Completed, now, now, "First"));
        recorder.Observe(CreateJob(Guid.NewGuid(), profileId, JobState.Completed, now, now.AddSeconds(1), "Second"));
        await recorder.FlushAsync();

        Assert.Single(failures);
        Assert.Single(store.Records);
        Assert.Equal("Second", store.Records[0].DisplayName);
    }

    private static HistoryRecord CreateRecord(Guid id, JobState outcome, DateTimeOffset finishedAt) => new(
        id,
        id,
        JobKind.Transfer,
        "Transfer",
        outcome,
        finishedAt.AddSeconds(-1),
        finishedAt,
        Detail: "Finished");

    private static JobSnapshot CreateJob(
        Guid id,
        Guid profileId,
        JobState state,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string displayName) => new(
            id,
            JobKind.Transfer,
            profileId,
            displayName,
            state,
            createdAt,
            updatedAt,
            Progress: state == JobState.Completed ? 1 : null,
            Status: state.ToString());

    private sealed class FailFirstHistoryStore : IHistoryStore
    {
        private int _appendCount;
        public List<HistoryRecord> Records { get; } = [];

        public Task<IReadOnlyList<HistoryRecord>> GetRecentAsync(int maximumCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HistoryRecord>>(Records.Take(maximumCount).ToArray());

        public Task AppendAsync(HistoryRecord record, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _appendCount) == 1) throw new IOException("Simulated history failure.");
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Records.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.HistoryTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
