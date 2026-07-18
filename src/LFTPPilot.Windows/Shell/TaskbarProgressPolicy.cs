using LFTPPilot.Core;

namespace LFTPPilot.Windows.Shell;

public sealed record TaskbarProgressSummary(
    TaskbarProgressState State,
    ulong? Completed = null,
    ulong? Total = null);

public static class TaskbarProgressPolicy
{
    private const ulong Scale = 10_000;

    public static TaskbarProgressSummary Summarize(IEnumerable<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var active = jobs.Where(static job => job.State is
            JobState.Scheduled or JobState.Queued or JobState.Running or JobState.Paused).ToArray();
        if (active.Length == 0) return new(TaskbarProgressState.None);

        var running = active.Where(static job => job.State == JobState.Running).ToArray();
        if (running.Length > 0)
        {
            if (active.Any(static job => job.State is JobState.Scheduled or JobState.Queued) ||
                running.Any(static job => job.Progress is null))
                return new(TaskbarProgressState.Indeterminate);
            return FromKnownProgress(TaskbarProgressState.Normal, running);
        }

        var paused = active.Where(static job => job.State == JobState.Paused).ToArray();
        if (paused.Length > 0 && active.All(static job => job.State == JobState.Paused) &&
            paused.All(static job => job.Progress is not null))
            return FromKnownProgress(TaskbarProgressState.Paused, paused);
        return new(active.Any(static job => job.State == JobState.Paused)
            ? TaskbarProgressState.Paused
            : TaskbarProgressState.Indeterminate);
    }

    private static TaskbarProgressSummary FromKnownProgress(TaskbarProgressState state, JobSnapshot[] jobs)
    {
        var average = jobs.Average(static job => job.Progress!.Value);
        return new(state, (ulong)Math.Clamp(Math.Round(average * Scale), 0, Scale), Scale);
    }
}
