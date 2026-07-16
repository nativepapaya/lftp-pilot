using System.Collections.Immutable;

namespace LFTPPilot.App.Services;

internal sealed record TransferQueueSuccess<TResult>(FilePaneTransferSource Source, TResult Value);

internal sealed record TransferQueueFailure(FilePaneTransferSource Source, Exception Error);

internal sealed record TransferQueueExecution<TResult>(
    ImmutableArray<TransferQueueSuccess<TResult>> Successes,
    ImmutableArray<TransferQueueFailure> Failures)
{
    internal TransferQueueResult ToResult() => new(
        Successes.Select(static success => success.Source).ToImmutableArray(),
        Failures.Select(static failure => failure.Source).ToImmutableArray());
}

internal sealed record TransferQueueResult(
    ImmutableArray<FilePaneTransferSource> QueuedSources,
    ImmutableArray<FilePaneTransferSource> FailedSources)
{
    internal int RequestedCount => QueuedSources.Length + FailedSources.Length;
    internal int QueuedCount => QueuedSources.Length;
    internal int FailedCount => FailedSources.Length;
    internal bool IsPartialSuccess => QueuedCount > 0 && FailedCount > 0;
}

internal sealed class TransferQueueException : Exception
{
    internal TransferQueueException(TransferQueueResult result)
        : this(result, [])
    {
    }

    internal TransferQueueException(TransferQueueResult result, IReadOnlyList<TransferQueueFailure> failures)
        : base(TransferQueueAggregation.FormatFailureMessage(result, failures))
    {
        Result = result;
        ActionableCause = TransferQueueAggregation.ActionableCause(failures);
    }

    internal TransferQueueResult Result { get; }
    internal string? ActionableCause { get; }
}

internal static class TransferQueueAggregation
{
    private const int MaximumListedFailureNames = 5;
    private const int MaximumFailureNameLength = 64;
    private const int MaximumInspectedCauseLength = 4_096;
    private const string ReviewedMirrorReplacementCause =
        "The directory safety preview found a deletion or type-collision replacement. Use the reviewed Mirror workflow instead.";
    private const string ReviewedMirrorProofCause =
        "The directory transfer could not be proven non-destructive. Use the reviewed Mirror workflow instead.";
    private const string ReviewedMirrorRootCause =
        "Root-wide folder transfers require the reviewed Mirror workflow.";

    internal static async Task<TransferQueueExecution<TResult>> ExecuteAsync<TResult>(
        IReadOnlyList<FilePaneTransferSource> sources,
        Func<FilePaneTransferSource, Task<TResult>> attempt)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(attempt);
        var successes = ImmutableArray.CreateBuilder<TransferQueueSuccess<TResult>>(sources.Count);
        var failures = ImmutableArray.CreateBuilder<TransferQueueFailure>(sources.Count);
        foreach (var source in sources)
        {
            try
            {
                successes.Add(new(source, await attempt(source).ConfigureAwait(true)));
            }
            catch (Exception exception)
            {
                failures.Add(new(source, exception));
            }
        }

        return new(successes.ToImmutable(), failures.ToImmutable());
    }

    internal static string FormatStatus(TransferQueueResult result, bool scheduled)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.FailedCount == 0)
        {
            var verb = scheduled ? "Scheduled" : "Queued";
            return $"{verb} {result.QueuedCount} item{(result.QueuedCount == 1 ? string.Empty : "s")}";
        }

        var partialVerb = scheduled ? "Scheduled" : "Queued";
        return $"{partialVerb} {result.QueuedCount} of {result.RequestedCount}; {result.FailedCount} failed";
    }

    internal static string FormatFailureMessage(
        TransferQueueResult result,
        IReadOnlyList<TransferQueueFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.FailedCount == 0) throw new ArgumentException("A queue failure message requires at least one failed source.", nameof(result));

        var listedNames = result.FailedSources
            .Take(MaximumListedFailureNames)
            .Select(static source => DisplayName(source.Path));
        var remaining = result.FailedCount - MaximumListedFailureNames;
        var omitted = remaining > 0 ? $", and {remaining} more" : string.Empty;
        var cause = failures is null ? null : ActionableCause(failures);
        var recovery = cause is null
            ? " Retry only the failed items."
            : cause.Contains("reviewed Mirror workflow", StringComparison.Ordinal)
                ? $" Cause: {cause}"
                : $" Cause: {cause} Retry only the failed items.";
        return $"queued {result.QueuedCount} of {result.RequestedCount}. Failed: {string.Join(", ", listedNames)}{omitted}.{recovery}";
    }

    internal static string? ActionableCause(IReadOnlyList<TransferQueueFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0) return null;
        string? common = null;
        var allCommon = true;
        var hasReviewedMirrorCause = false;
        foreach (var failure in failures)
        {
            var cause = SanitizedActionableCause(failure.Error);
            if (cause is null)
            {
                allCommon = false;
            }
            else
            {
                hasReviewedMirrorCause |= cause.Contains("reviewed Mirror workflow", StringComparison.Ordinal);
                if (common is null)
                {
                    common = cause;
                }
                else if (!string.Equals(common, cause, StringComparison.Ordinal))
                {
                    allCommon = false;
                }
            }
        }

        if (allCommon && common is not null) return common;
        return hasReviewedMirrorCause
            ? "One or more failed directory transfers require the reviewed Mirror workflow."
            : null;
    }

    private static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        var name = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        if (string.IsNullOrWhiteSpace(name)) name = path;
        name = new(name.Select(static character => char.IsControl(character) ? '\uFFFD' : character).ToArray());
        return name.Length <= MaximumFailureNameLength
            ? name
            : name[..(MaximumFailureNameLength - 1)] + "\u2026";
    }

    private static string? SanitizedActionableCause(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
        {
            var message = current.Message;
            if (message.Length > MaximumInspectedCauseLength) message = message[..MaximumInspectedCauseLength];
            if (message.StartsWith(
                    "The directory transfer dry-run proposed deletion or type-collision replacement.",
                    StringComparison.OrdinalIgnoreCase))
                return ReviewedMirrorReplacementCause;
            if (message.StartsWith(
                    "The directory transfer dry-run could not be proven non-destructive.",
                    StringComparison.OrdinalIgnoreCase))
                return ReviewedMirrorProofCause;
            if (message.StartsWith(
                    "Quick directory transfers cannot use a local filesystem root, UNC share root, or the remote server root.",
                    StringComparison.OrdinalIgnoreCase))
                return ReviewedMirrorRootCause;
            if (message.Contains("local upload source must exist", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("remote transfer source was not found", StringComparison.OrdinalIgnoreCase))
                return "The source item no longer exists.";
            if (message.Contains("local upload source is a", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("remote download source is a", StringComparison.OrdinalIgnoreCase))
                return "The source item type changed after it was selected.";
            if (message.Contains("existing local download destination is a", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("remote upload destination is a", StringComparison.OrdinalIgnoreCase))
                return "The destination item type conflicts with the selected source.";
            if (message.Contains("reparse points and special entries", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("symbolic link or special entry", StringComparison.OrdinalIgnoreCase))
                return "A selected item is a link or special entry, which LFTP Pilot does not follow for transfers.";
            if (message.Contains("non-directory ancestor", StringComparison.OrdinalIgnoreCase))
                return "A local transfer path passes through an item that is not a directory.";
            if (message.Contains("transfer profile does not match", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("session no longer matches", StringComparison.OrdinalIgnoreCase))
                return "The active connection changed after the item was selected.";
            if (message.Contains("authenticated Agent connection is unavailable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("did not accept a trusted pipe connection", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("agent closed the control pipe", StringComparison.OrdinalIgnoreCase))
                return "The authenticated Agent connection is unavailable.";
            if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                return "The Agent operation timed out.";
        }

        return null;
    }
}
