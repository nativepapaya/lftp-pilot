namespace LFTPPilot.Core;

public static class JobSnapshotPolicy
{
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumStatusLength = 2_048;
    public const int MaximumErrorCodeLength = 128;
    public const int MaximumErrorMessageLength = 4_096;
    public const int MaximumErrorDetailLength = 8_192;
    public static readonly TimeSpan MaximumFutureTimestampSkew = TimeSpan.FromMinutes(1);

    public static void Validate(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Id == Guid.Empty) throw new ArgumentException("A job requires a non-empty identifier.", nameof(job));
        if (!Enum.IsDefined(job.Kind) || !Enum.IsDefined(job.State))
            throw new ArgumentException("A job contains an unsupported kind or state.", nameof(job));
        if (job.ProfileId is not { } profileId || profileId == Guid.Empty)
            throw new ArgumentException("A job requires a non-empty profile identifier.", nameof(job));

        ValidateRequiredText(job.DisplayName, MaximumDisplayNameLength, "display name");
        ValidateOptionalText(job.Status, MaximumStatusLength, "status");
        ValidateTimestamp(job.CreatedAt, "created-at");
        ValidateTimestamp(job.UpdatedAt, "updated-at");
        if (job.UpdatedAt < job.CreatedAt)
            throw new ArgumentException("A job cannot be updated before it was created.", nameof(job));
        if (job.RunAt is { } runAt) ValidateTimestamp(runAt, "run-at");
        if (job.Progress is { } progress && (!double.IsFinite(progress) || progress is < 0 or > 1))
            throw new ArgumentException("A job progress value must be between zero and one.", nameof(job));

        if (job.State == JobState.Scheduled && (job.Kind != JobKind.Transfer || job.RunAt is null))
            throw new ArgumentException("Only a transfer with a run-once time can be scheduled.", nameof(job));
        if (job.Kind != JobKind.Transfer && job.RunAt is not null)
            throw new ArgumentException("Only transfer jobs can retain a run-once time.", nameof(job));
        if (job.Kind != JobKind.Transfer && job.RetryAvailable)
            throw new ArgumentException("Only transfer jobs can advertise an Agent-owned retry.", nameof(job));
        if (job.State == JobState.Failed && job.Error is null)
            throw new ArgumentException("A failed job requires an error.", nameof(job));
        if (job.State != JobState.Failed && job.Error is not null)
            throw new ArgumentException("Only a failed job can retain an error.", nameof(job));
        if (job.Error is { } error)
        {
            ValidateRequiredText(error.Code, MaximumErrorCodeLength, "error code");
            ValidateRequiredText(error.Message, MaximumErrorMessageLength, "error message");
            ValidateOptionalText(error.Detail, MaximumErrorDetailLength, "error detail");
        }
    }

    public static void ValidateForEnqueue(JobSnapshot job, DateTimeOffset now)
    {
        Validate(job);
        var maximumTimestamp = now.ToUniversalTime() + MaximumFutureTimestampSkew;
        if (job.CreatedAt > maximumTimestamp || job.UpdatedAt > maximumTimestamp)
            throw new ArgumentException("A new job cannot contain future-dated creation or update timestamps.", nameof(job));
    }

    public static void ValidateStatus(string? status) =>
        ValidateOptionalText(status, MaximumStatusLength, "status");

    public static bool ContainsControlCharacter(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(char.IsControl);
    }

    public static string CanonicalizeDerivedDisplayName(string? value, string fallback) =>
        CanonicalizeDerivedRequiredText(value, fallback, MaximumDisplayNameLength);

    public static EngineError CanonicalizeDerivedError(
        string code,
        string? message,
        bool isTransient = false,
        string? detail = null) => new(
            CanonicalizeDerivedRequiredText(code, "operation-failed", MaximumErrorCodeLength),
            CanonicalizeDerivedRequiredText(message, "The operation failed.", MaximumErrorMessageLength),
            isTransient,
            CanonicalizeDerivedOptionalText(detail, MaximumErrorDetailLength));

    private static string CanonicalizeDerivedRequiredText(string? value, string fallback, int maximumLength)
    {
        var sanitized = SanitizeControls(value);
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = SanitizeControls(fallback);
        if (string.IsNullOrWhiteSpace(sanitized)) throw new ArgumentException("A nonblank derived-text fallback is required.", nameof(fallback));
        return TruncateWithoutSplittingSurrogatePair(sanitized, maximumLength);
    }

    private static string? CanonicalizeDerivedOptionalText(string? value, int maximumLength)
    {
        if (value is null) return null;
        var sanitized = SanitizeControls(value);
        return string.IsNullOrWhiteSpace(sanitized)
            ? null
            : TruncateWithoutSplittingSurrogatePair(sanitized, maximumLength);
    }

    private static string SanitizeControls(string? value)
    {
        if (value is null) return string.Empty;
        char[]? replacement = null;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsControl(value[index])) continue;
            replacement ??= value.ToCharArray();
            replacement[index] = '\uFFFD';
        }
        return replacement is null ? value : new string(replacement);
    }

    private static string TruncateWithoutSplittingSurrogatePair(string value, int maximumLength)
    {
        if (value.Length <= maximumLength) return value;
        if (maximumLength == 1) return "\u2026";
        var contentLength = maximumLength - 1;
        if (contentLength > 0 && char.IsHighSurrogate(value[contentLength - 1])) contentLength--;
        return value[..contentLength] + "\u2026";
    }

    private static void ValidateRequiredText(string? value, int maximumLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || ContainsControlCharacter(value))
            throw new ArgumentException($"A job contains an invalid {field}.");
    }

    private static void ValidateOptionalText(string? value, int maximumLength, string field)
    {
        if (value is null) return;
        ValidateRequiredText(value, maximumLength, field);
    }

    private static void ValidateTimestamp(DateTimeOffset value, string field)
    {
        if (value == default || value.Offset != TimeSpan.Zero || value.Year is < 2000 or > 9998)
            throw new ArgumentException($"A job contains an invalid {field} timestamp.");
    }
}
