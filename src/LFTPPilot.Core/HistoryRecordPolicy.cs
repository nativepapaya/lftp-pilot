namespace LFTPPilot.Core;

public static class HistoryRecordPolicy
{
    public const int MaximumBootstrapRecords = 500;
    public const int RetentionLimit = 2_000;
    public const int MaximumDisplayNameLength = JobSnapshotPolicy.MaximumDisplayNameLength;
    public const int MaximumDetailLength = JobSnapshotPolicy.MaximumErrorMessageLength;

    public static void Validate(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == Guid.Empty || record.JobId == Guid.Empty)
            throw new ArgumentException("A history record requires non-empty identifiers.", nameof(record));
        if (!Enum.IsDefined(record.Kind) || record.Outcome is not
            (JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Missed))
        {
            throw new ArgumentException("A history record requires a supported job kind and terminal outcome.", nameof(record));
        }
        ValidateText(record.DisplayName, MaximumDisplayNameLength, "display name", required: true);
        ValidateTimestamp(record.StartedAt, "started-at");
        ValidateTimestamp(record.FinishedAt, "finished-at");
        if (record.FinishedAt < record.StartedAt)
            throw new ArgumentException("A history record cannot finish before it started.", nameof(record));
        if (record.BytesTransferred is < 0)
            throw new ArgumentException("A history record cannot contain a negative byte count.", nameof(record));
        ValidateText(record.Detail, MaximumDetailLength, "detail", required: false);
    }

    private static void ValidateText(string? value, int maximumLength, string field, bool required)
    {
        if (value is null)
        {
            if (required) throw new ArgumentException($"A history record requires a {field}.");
            return;
        }
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"A history record contains an invalid {field}.");
    }

    private static void ValidateTimestamp(DateTimeOffset value, string field)
    {
        if (value == default || value.Offset != TimeSpan.Zero || value.Year is < 2000 or > 9998)
            throw new ArgumentException($"A history record contains an invalid {field} timestamp.");
    }
}
