namespace LFTPPilot.Core;

public static class TransferProgressPolicy
{
    public static void Validate(TransferProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.JobId == Guid.Empty)
            throw new ArgumentException("Transfer progress requires a non-empty job identifier.", nameof(progress));
        if (progress.TotalBytes <= 0 || progress.BytesTransferred < 0 || progress.BytesTransferred > progress.TotalBytes)
            throw new ArgumentException("Transfer progress requires a bounded byte position and positive total.", nameof(progress));
        if (progress.BytesPerSecond is { } rate && (!double.IsFinite(rate) || rate < 0))
            throw new ArgumentException("Transfer progress contains an invalid transfer rate.", nameof(progress));
        if (progress.ObservedAt == default || progress.ObservedAt.Offset != TimeSpan.Zero || progress.ObservedAt.Year is < 2000 or > 9998)
            throw new ArgumentException("Transfer progress contains an invalid observation timestamp.", nameof(progress));
    }
}
