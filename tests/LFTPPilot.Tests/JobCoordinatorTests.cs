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
    public void InvalidTransitionAndFailureWithoutErrorAreRejected()
    {
        var coordinator = new JobCoordinator();
        var job = coordinator.Enqueue(Job(JobState.Queued));
        Assert.Throws<InvalidOperationException>(() => coordinator.Transition(job.Id, JobState.Completed));
        coordinator.Transition(job.Id, JobState.Running);
        Assert.Throws<ArgumentException>(() => coordinator.Transition(job.Id, JobState.Failed));
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

    internal static JobSnapshot Job(JobState state, DateTimeOffset? runAt = null) => new(
        Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "Transfer", state,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, runAt);
}
