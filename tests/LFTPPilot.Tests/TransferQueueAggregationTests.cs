using System.Collections.Immutable;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class TransferQueueAggregationTests
{
    [Fact]
    public async Task SecondSourceFailureDoesNotPreventLaterAttemptsAndProducesExactResult()
    {
        FilePaneTransferSource[] sources =
        [
            new(@"C:\staging\one.bin", TransferSourceKind.File),
            new(@"C:\staging\two", TransferSourceKind.Directory),
            new(@"C:\staging\three.bin", TransferSourceKind.File),
        ];
        var attempted = new List<FilePaneTransferSource>();

        var execution = await TransferQueueAggregation.ExecuteAsync(sources, async source =>
        {
            attempted.Add(source);
            await Task.Yield();
            if (source == sources[1]) throw new IOException("simulated second-source failure");
            return source.Path;
        });

        Assert.Equal(sources, attempted);
        Assert.Equal([sources[0], sources[2]], execution.Successes.Select(static success => success.Source));
        var failure = Assert.Single(execution.Failures);
        Assert.Equal(sources[1], failure.Source);
        Assert.IsType<IOException>(failure.Error);

        var result = execution.ToResult();
        Assert.Equal(3, result.RequestedCount);
        Assert.Equal(2, result.QueuedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.IsPartialSuccess);
        Assert.Equal("Queued 2 of 3; 1 failed", TransferQueueAggregation.FormatStatus(result, scheduled: false));

        var exception = new TransferQueueException(result, execution.Failures);
        Assert.StartsWith("queued 2 of 3.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("two", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("Retry only the failed items.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMessageBoundsNamesAndReportsOmittedCount()
    {
        var failed = Enumerable.Range(1, 7)
            .Select(index => new FilePaneTransferSource(
                $@"C:\staging\{new string((char)('a' + index), 100)}-{index}.bin",
                TransferSourceKind.File))
            .ToImmutableArray();
        var result = new TransferQueueResult([], failed);

        var message = TransferQueueAggregation.FormatFailureMessage(result);

        Assert.StartsWith("queued 0 of 7.", message, StringComparison.Ordinal);
        Assert.Contains("and 2 more", message, StringComparison.Ordinal);
        Assert.EndsWith("Retry only the failed items.", message, StringComparison.Ordinal);
        Assert.True(message.Length <= 512, $"Message length was {message.Length}.");
        Assert.DoesNotContain(failed[0].Path, message, StringComparison.Ordinal);
        Assert.Equal("Queued 0 of 7; 7 failed", TransferQueueAggregation.FormatStatus(result, scheduled: false));
    }

    [Fact]
    public void ReviewedMirrorCauseIsPreservedWithoutLeakingDetailsOrSuggestingRetry()
    {
        var source = new FilePaneTransferSource(@"C:\private\release-directory", TransferSourceKind.Directory);
        var result = new TransferQueueResult([], [source]);
        TransferQueueFailure[] failures =
        [
            new(source, new InvalidOperationException(
                "The directory transfer dry-run proposed deletion or type-collision replacement. " +
                "Use the reviewed Mirror workflow instead. sftp://admin:super-secret@example.test/C:/private")),
        ];

        var exception = new TransferQueueException(result, failures);

        Assert.Equal(
            "The directory safety preview found a deletion or type-collision replacement. Use the reviewed Mirror workflow instead.",
            exception.ActionableCause);
        Assert.Contains("Use the reviewed Mirror workflow instead.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry only", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sftp://", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(exception.Message.Length <= 512, $"Message length was {exception.Message.Length}.");
    }

    [Fact]
    public void CommonSafeCauseIsShownButArbitraryAgentTextIsNeverEchoed()
    {
        FilePaneTransferSource[] sources =
        [
            new(@"C:\staging\missing-local.bin", TransferSourceKind.File),
            new("/missing-remote.bin", TransferSourceKind.File),
        ];
        var result = new TransferQueueResult([], sources.ToImmutableArray());
        TransferQueueFailure[] failures =
        [
            new(sources[0], new InvalidOperationException("The local upload source must exist before the transfer can run. password=hunter2")),
            new(sources[1], new InvalidOperationException("The remote transfer source was not found. token=private-token")),
        ];

        var exception = new TransferQueueException(result, failures);

        Assert.Equal("The source item no longer exists.", exception.ActionableCause);
        Assert.Contains("Cause: The source item no longer exists.", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith("Retry only the failed items.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedReviewedMirrorAndUnknownCausesStillSuppressRetryGuidance()
    {
        FilePaneTransferSource[] sources =
        [
            new(@"C:\staging\directory", TransferSourceKind.Directory),
            new(@"C:\staging\file.bin", TransferSourceKind.File),
        ];
        var result = new TransferQueueResult([], sources.ToImmutableArray());
        TransferQueueFailure[] failures =
        [
            new(sources[0], new InvalidOperationException(
                "The directory transfer dry-run could not be proven non-destructive. Use the reviewed Mirror workflow instead.")),
            new(sources[1], new InvalidOperationException("server said password=do-not-leak")),
        ];

        var exception = new TransferQueueException(result, failures);

        Assert.Equal("One or more failed directory transfers require the reviewed Mirror workflow.", exception.ActionableCause);
        Assert.DoesNotContain("Retry only", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootWideQuickTransferRedirectsToReviewedMirrorWithoutRetryGuidance()
    {
        var source = new FilePaneTransferSource(@"C:\", TransferSourceKind.Directory);
        var result = new TransferQueueResult([], [source]);
        TransferQueueFailure[] failures =
        [
            new(source, new NotSupportedException(
                "Quick directory transfers cannot use a local filesystem root, UNC share root, or the remote server root. " +
                "Use the reviewed Mirror workflow instead. C:\\private")),
        ];

        var exception = new TransferQueueException(result, failures);

        Assert.Equal("Root-wide folder transfers require the reviewed Mirror workflow.", exception.ActionableCause);
        Assert.DoesNotContain("Retry only", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
