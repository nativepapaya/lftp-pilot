using System.Collections.Concurrent;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class JobCoordinatorTests
{
    [Fact]
    public void JobMovesThroughAllowedLifecycleAndPublishesChanges()
    {
        var coordinator = new JobCoordinator();
        var changes = new List<JobState>();
        coordinator.JobChanged += (_, job) => changes.Add(job.State);
        var job = Job(JobState.Queued);
        coordinator.Enqueue(job);
        coordinator.Transition(job.Id, JobState.Running);
        coordinator.Transition(job.Id, JobState.Completed, "Done");
        Assert.Equal([JobState.Queued, JobState.Running, JobState.Completed], changes);
        Assert.Equal(JobState.Completed, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public void CompletedTransitionSetsProgressToOne()
    {
        var coordinator = new JobCoordinator();
        var queued = coordinator.Enqueue(Job(JobState.Queued));
        coordinator.Transition(queued.Id, JobState.Running);

        var completed = coordinator.Transition(queued.Id, JobState.Completed, "Done");

        Assert.Equal(1, completed.Progress);
    }

    [Fact]
    public void InvalidTransitionAndFailureWithoutErrorAreRejected()
    {
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));
        Assert.Throws<InvalidOperationException>(() => coordinator.Transition(job.Id, JobState.Completed));
        coordinator.Transition(job.Id, JobState.Running);
        Assert.Throws<ArgumentException>(() => coordinator.Transition(job.Id, JobState.Failed));
    }

    [Fact]
    public void QueuedJobCanFailWhenPostCommitExecutionAdmissionFails()
    {
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));

        var failed = coordinator.Transition(
            job.Id,
            JobState.Failed,
            "Execution admission failed",
            new("execution-admission-failed", "The committed job could not be tracked."));

        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal("execution-admission-failed", failed.Error?.Code);
    }

    [Fact]
    public void CancellationIsIdempotentlyDeniedForTerminalJobs()
    {
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));
        Assert.True(coordinator.TryCancel(job.Id));
        Assert.False(coordinator.TryCancel(job.Id));
    }

    [Fact]
    public void ThrowingSubscriberCannotInterruptCancellationOrLaterSubscribers()
    {
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));
        JobSnapshot? observed = null;
        coordinator.JobChanged += static (_, _) => throw new InvalidOperationException("simulated observer failure");
        coordinator.JobChanged += (_, changed) => observed = changed;

        Assert.True(coordinator.TryCancel(job.Id, "User cancelled"));

        Assert.NotNull(observed);
        Assert.Equal(JobState.Cancelled, observed.State);
        Assert.Equal("User cancelled", observed.Status);
        Assert.Equal(JobState.Cancelled, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public void ThrowingSubscriberCannotInterruptEnqueueTransitionOrRetryPublications()
    {
        var coordinator = new JobCoordinator();
        var observed = new List<JobState>();
        coordinator.JobChanged += static (_, _) => throw new IOException("simulated observer failure");
        coordinator.JobChanged += (_, changed) => observed.Add(changed.State);
        var job = Job(JobState.Queued) with { RetryAvailable = true };

        coordinator.Enqueue(job);
        coordinator.Transition(job.Id, JobState.Running);
        coordinator.Transition(job.Id, JobState.Failed, error: new("network", "Dropped"));
        var retried = coordinator.Retry(job.Id);

        Assert.Equal([JobState.Queued, JobState.Running, JobState.Failed, JobState.Queued], observed);
        Assert.Equal(JobState.Queued, retried.State);
        Assert.Equal(JobState.Queued, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public async Task ConcurrentCancellationHasExactlyOneAtomicWinnerAndPublication()
    {
        const int workers = 16;
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));
        var publications = new ConcurrentBag<JobSnapshot>();
        coordinator.JobChanged += (_, changed) =>
        {
            if (changed.State == JobState.Cancelled) publications.Add(changed);
        };
        using var start = new Barrier(workers);
        var attempts = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait(TestContext.Current.CancellationToken);
                return coordinator.TryCancel(job.Id, "Concurrent cancellation");
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, static result => result);
        var published = Assert.Single(publications);
        Assert.Equal("Concurrent cancellation", published.Status);
        Assert.Equal(JobState.Cancelled, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public void RetryAtomicallyResetsFailedAttemptState()
    {
        var coordinator = new JobCoordinator();
        var runAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var job = Job(JobState.Scheduled, runAt) with { RetryAvailable = true };
        coordinator.Enqueue(job);
        coordinator.Transition(job.Id, JobState.Queued, "Due");
        coordinator.Transition(job.Id, JobState.Running, "Running");
        coordinator.Transition(job.Id, JobState.Failed, "Failed", new("network", "Dropped", IsTransient: true));

        var retried = coordinator.Retry(job.Id);

        Assert.Equal(JobState.Queued, retried.State);
        Assert.Null(retried.RunAt);
        Assert.Null(retried.Progress);
        Assert.Null(retried.Error);
        Assert.False(retried.CanRetry);
        Assert.Throws<InvalidOperationException>(() => coordinator.Retry(job.Id));
    }

    [Fact]
    public void RetryRejectsFailedJobWithoutAgentOperation()
    {
        var coordinator = new JobCoordinator();
        var job = Job(JobState.Queued);
        coordinator.Enqueue(job);
        coordinator.Transition(job.Id, JobState.Running);
        coordinator.Transition(job.Id, JobState.Failed, error: new("failure", "No retained operation"));

        Assert.Throws<InvalidOperationException>(() => coordinator.Retry(job.Id));
        Assert.Equal(JobState.Failed, coordinator.GetJobs().Single().State);
    }

    [Fact]
    public void MutationsKeepUpdatedTimestampAtOrAfterFutureCreatedTimestamp()
    {
        var coordinator = new JobCoordinator();
        var future = DateTimeOffset.UtcNow.AddSeconds(30);
        var job = coordinator.Enqueue(Job(JobState.Queued) with
        {
            CreatedAt = future,
            UpdatedAt = future,
        });

        var running = coordinator.Transition(job.Id, JobState.Running, "Running");
        Assert.True(running.UpdatedAt >= future);
        Assert.True(coordinator.TryCancel(job.Id, "Cancelled"));
        Assert.True(Assert.Single(coordinator.GetJobs()).UpdatedAt >= future);
    }

    [Fact]
    public void EnqueueRejectsTimestampsBeyondFutureSkewBeforeCommit()
    {
        var coordinator = new JobCoordinator();
        var future = DateTimeOffset.UtcNow + JobSnapshotPolicy.MaximumFutureTimestampSkew + TimeSpan.FromMinutes(1);

        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(Job(JobState.Queued) with
        {
            CreatedAt = future,
            UpdatedAt = future,
        }));
        Assert.Empty(coordinator.GetJobs());
    }

    [Fact]
    public void CoordinatorRejectsNonCanonicalTextBeforeCommittingMutation()
    {
        var coordinator = new JobCoordinator();
        var malformed = Job(JobState.Queued) with { DisplayName = "unsafe\nname" };

        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(malformed));
        Assert.Empty(coordinator.GetJobs());

        var job = coordinator.Enqueue(Job(JobState.Queued));
        Assert.Throws<ArgumentException>(() => coordinator.Transition(job.Id, JobState.Running, "unsafe\tstatus"));
        Assert.Equal(JobState.Queued, Assert.Single(coordinator.GetJobs()).State);
    }

    [Fact]
    public void DerivedJobTextIsControlFreeBoundedAndSurrogateSafe()
    {
        var display = JobSnapshotPolicy.CanonicalizeDerivedDisplayName(
            new string('d', JobSnapshotPolicy.MaximumDisplayNameLength - 1) + "\ud83d\ude80\tignored",
            "Transfer");
        var error = JobSnapshotPolicy.CanonicalizeDerivedError(
            "test-error",
            new string('e', JobSnapshotPolicy.MaximumErrorMessageLength) + "\r\nmore");
        var job = Job(JobState.Queued) with { DisplayName = display };

        JobSnapshotPolicy.Validate(job);
        Assert.True(display.Length <= JobSnapshotPolicy.MaximumDisplayNameLength);
        Assert.DoesNotContain(display, static character => char.IsControl(character));
        Assert.False(char.IsHighSurrogate(display[^1]));
        Assert.True(error.Message.Length <= JobSnapshotPolicy.MaximumErrorMessageLength);
        Assert.DoesNotContain(error.Message, static character => char.IsControl(character));
    }

    internal static JobSnapshot Job(JobState state, DateTimeOffset? runAt = null) => new(
        Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "Transfer", state,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, runAt);
}
