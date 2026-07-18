using LFTPPilot.Core;

namespace LFTPPilot.Windows.Shell;

public sealed record JobNotification(string Title, string Message, string Tag, string Group);

public static class JobNotificationPolicy
{
    private const int MaximumMessageLength = 512;

    public static JobNotification? Create(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        JobSnapshotPolicy.Validate(job);
        var outcome = job.State switch
        {
            JobState.Completed => ("Transfer activity completed", job.Status ?? "Completed successfully."),
            JobState.Failed => ("Transfer activity failed", job.Error?.Message ?? "The operation failed."),
            JobState.Missed => ("Scheduled transfer missed", job.Status ?? "The scheduled transfer did not run."),
            _ => default,
        };
        if (outcome == default) return null;
        var message = $"{job.DisplayName} · {outcome.Item2}";
        if (message.Length > MaximumMessageLength) message = message[..(MaximumMessageLength - 1)] + "…";
        return new(outcome.Item1, message, job.Id.ToString("N"), "jobs");
    }
}
