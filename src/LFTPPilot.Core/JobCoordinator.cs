namespace LFTPPilot.Core;

public sealed class JobCoordinator : IJobCoordinator
{
    private static readonly IReadOnlyDictionary<JobState, HashSet<JobState>> AllowedTransitions =
        new Dictionary<JobState, HashSet<JobState>>
        {
            [JobState.Scheduled] = [JobState.Queued, JobState.Failed, JobState.Cancelled, JobState.Missed],
            [JobState.Queued] = [JobState.Running, JobState.Failed, JobState.Cancelled],
            [JobState.Running] = [JobState.Paused, JobState.Completed, JobState.Failed, JobState.Cancelled],
            [JobState.Paused] = [JobState.Queued, JobState.Cancelled],
            [JobState.Completed] = [],
            [JobState.Failed] = [],
            [JobState.Cancelled] = [JobState.Queued],
            [JobState.Missed] = [],
        };

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, JobSnapshot> _jobs = [];

    public event EventHandler<JobSnapshot>? JobChanged;

    public IReadOnlyList<JobSnapshot> GetJobs()
    {
        lock (_gate)
            return _jobs.Values.OrderByDescending(static job => job.CreatedAt).ToArray();
    }

    public JobSnapshot Enqueue(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Id == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(job));
        if (job.State is not (JobState.Queued or JobState.Scheduled))
            throw new ArgumentException("New jobs must be queued or scheduled.", nameof(job));
        JobSnapshotPolicy.ValidateForEnqueue(job, DateTimeOffset.UtcNow);

        lock (_gate)
        {
            if (!_jobs.TryAdd(job.Id, job)) throw new InvalidOperationException($"Job {job.Id} already exists.");
        }
        PublishJobChanged(job);
        return job;
    }

    public JobSnapshot Transition(Guid jobId, JobState state, string? status = null, EngineError? error = null)
    {
        JobSnapshot updated;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current)) throw new KeyNotFoundException($"Job {jobId} was not found.");
            if (current.State != state && !AllowedTransitions[current.State].Contains(state))
                throw new InvalidOperationException($"A job cannot transition from {current.State} to {state}.");
            if (state == JobState.Failed && error is null)
                throw new ArgumentException("A failed job requires an error.", nameof(error));
            updated = current with
            {
                State = state,
                Progress = state == JobState.Completed ? 1 : current.Progress,
                Status = status ?? current.Status,
                Error = error,
                UpdatedAt = NextUpdatedAt(current),
            };
            JobSnapshotPolicy.Validate(updated);
            _jobs[jobId] = updated;
        }
        PublishJobChanged(updated);
        return updated;
    }

    public bool TryCancel(Guid jobId, string? reason = null)
    {
        JobSnapshot updated;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || !AllowedTransitions[job.State].Contains(JobState.Cancelled)) return false;
            updated = job with
            {
                State = JobState.Cancelled,
                Status = reason ?? "Cancelled",
                Error = null,
                UpdatedAt = NextUpdatedAt(job),
            };
            JobSnapshotPolicy.Validate(updated);
            _jobs[jobId] = updated;
        }
        PublishJobChanged(updated);
        return true;
    }

    public JobSnapshot Retry(Guid jobId, string? status = null)
    {
        JobSnapshot updated;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current)) throw new KeyNotFoundException($"Job {jobId} was not found.");
            if (current.State != JobState.Failed) throw new InvalidOperationException("Only a failed job can be retried.");
            if (!current.RetryAvailable) throw new InvalidOperationException("This failed job no longer has a retryable Agent operation.");
            updated = current with
            {
                State = JobState.Queued,
                RunAt = null,
                Progress = null,
                Status = status ?? "Retry queued.",
                Error = null,
                UpdatedAt = NextUpdatedAt(current),
            };
            JobSnapshotPolicy.Validate(updated);
            _jobs[jobId] = updated;
        }
        PublishJobChanged(updated);
        return updated;
    }

    public void Restore(IEnumerable<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var snapshots = jobs.ToArray();
        foreach (var snapshot in snapshots) JobSnapshotPolicy.Validate(snapshot);
        if (snapshots.Select(static snapshot => snapshot.Id).Distinct().Count() != snapshots.Length)
            throw new ArgumentException("Restored jobs cannot contain duplicate identifiers.", nameof(jobs));
        lock (_gate)
        {
            foreach (var job in snapshots) _jobs[job.Id] = job;
        }
    }

    private static DateTimeOffset NextUpdatedAt(JobSnapshot current)
    {
        var now = DateTimeOffset.UtcNow;
        if (current.CreatedAt > now) now = current.CreatedAt;
        if (current.UpdatedAt > now) now = current.UpdatedAt;
        return now;
    }

    private void PublishJobChanged(JobSnapshot job)
    {
        var handlers = JobChanged;
        if (handlers is null) return;
        foreach (EventHandler<JobSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, job);
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                // Observers must not unwind a committed state mutation or
                // prevent lifecycle work, such as process cancellation, that
                // the coordinator's caller performs after publication.
            }
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;
}
