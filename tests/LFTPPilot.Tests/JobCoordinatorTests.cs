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

    internal static JobSnapshot Job(JobState state, DateTimeOffset? runAt = null) => new(
        Guid.NewGuid(), JobKind.Transfer, Guid.NewGuid(), "Transfer", state,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, runAt);
}
