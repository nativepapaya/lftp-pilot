namespace LFTPPilot.Core;

public sealed class JobCoordinator : IJobCoordinator
{
    private static readonly IReadOnlyDictionary<JobState, HashSet<JobState>> AllowedTransitions =
        new Dictionary<JobState, HashSet<JobState>>
        {
            [JobState.Scheduled] = [JobState.Queued, JobState.Cancelled, JobState.Missed],
            [JobState.Queued] = [JobState.Running, JobState.Cancelled],
            [JobState.Running] = [JobState.Paused, JobState.Completed, JobState.Failed, JobState.Cancelled],
            [JobState.Paused] = [JobState.Queued, JobState.Cancelled],
            [JobState.Completed] = [],
            [JobState.Failed] = [JobState.Queued],
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

        lock (_gate)
        {
            if (!_jobs.TryAdd(job.Id, job)) throw new InvalidOperationException($"Job {job.Id} already exists.");
        }
        JobChanged?.Invoke(this, job);
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
            updated = current with { State = state, Status = status ?? current.Status, Error = error, UpdatedAt = DateTimeOffset.UtcNow };
            _jobs[jobId] = updated;
        }
        JobChanged?.Invoke(this, updated);
        return updated;
    }

    public bool TryCancel(Guid jobId, string? reason = null)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || !AllowedTransitions[job.State].Contains(JobState.Cancelled)) return false;
        }
        Transition(jobId, JobState.Cancelled, reason ?? "Cancelled");
        return true;
    }

    public void Restore(IEnumerable<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        lock (_gate)
        {
            foreach (var job in jobs) _jobs[job.Id] = job;
        }
    }
}
