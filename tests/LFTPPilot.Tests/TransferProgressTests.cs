using System.Collections.Immutable;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class TransferProgressTests
{
    [Fact]
    public void ParserReadsNativeGetPutAndSegmentedPgetStatus()
    {
        var observations = LftpJobProgressParser.Parse([
            "[0] get /remote.bin -o /c/target.bin  -- 64.4 KiB/s",
            "\tftp://example.test/",
            "\t`/remote.bin' at 193433 (4%) 64.4K/s eta:61s [Receiving data]",
            "[1] put /c/source.bin -o /uploaded.bin  -- 2.5 MiB/s",
            "\t`/c/source.bin' at 1048576 (25%) 2.5M/s eta:1s [Sending data]",
            "[2] pget -n 4 /large.bin -o /c/large.bin",
            "\t`/large.bin', got 1537113 of 4194304 (36%) 513.9K/s eta:5s",
            " \\chunk 0-1048576",
            "\t`/large.bin' at 389764 (9%) 128.3K/s eta:5s [Receiving data]",
        ]);

        Assert.Equal(3, observations.Length);
        Assert.Equal(new("/remote.bin", 193433, null, 4, 64.4 * 1024), observations[0]);
        Assert.Equal(new("/c/source.bin", 1048576, null, 25, 2.5 * 1024 * 1024), observations[1]);
        Assert.Equal(new("/large.bin", 1537113, 4194304, 36, 513.9 * 1024), observations[2]);
    }

    [Fact]
    public void ParserRejectsInconsistentOrUnboundedStatus()
    {
        Assert.Throws<InvalidDataException>(() => LftpJobProgressParser.Parse([
            "`/remote.bin', got 20 of 10 (50%)",
        ]));
        Assert.Throws<InvalidDataException>(() => LftpJobProgressParser.Parse([
            $"`/{new string('x', 33_000)}' at 1 (1%)",
        ]));
        Assert.Empty(LftpJobProgressParser.Parse(["unrelated queue output"]));
    }

    [Fact]
    public void PolicyRequiresBoundedPositiveByteProgress()
    {
        var valid = new TransferProgressSnapshot(Guid.NewGuid(), 5, 10, 1024, DateTimeOffset.UtcNow);

        TransferProgressPolicy.Validate(valid);
        Assert.Equal(0.5, valid.Progress);
        Assert.Throws<ArgumentException>(() => TransferProgressPolicy.Validate(valid with { JobId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => TransferProgressPolicy.Validate(valid with { BytesTransferred = 11 }));
        Assert.Throws<ArgumentException>(() => TransferProgressPolicy.Validate(valid with { TotalBytes = 0 }));
        Assert.Throws<ArgumentException>(() => TransferProgressPolicy.Validate(valid with { BytesPerSecond = double.NaN }));
    }

    [Fact]
    public async Task NativeQueuePublishesMatchingProgressWithoutAffectingCompletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new ProgressSession([
            "[0] queue ()",
            "\tNow executing: [1] pget -n 4 /remote.bin -o /c/target.bin",
            " [1] pget -n 4 /remote.bin -o /c/target.bin -- 513.9 KiB/s",
            "\t`/remote.bin', got 1537113 of 4194304 (36%) 513.9K/s eta:5s",
        ]);
        await using var queue = await NativeTransferQueue.CreateAsync(
            session,
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            false,
            cancellationToken,
            TimeSpan.FromMilliseconds(100));
        var jobId = Guid.NewGuid();
        var plan = Plan(jobId, segments: 4);
        var progressCompletion = new TaskCompletionSource<TransferProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = queue.ExecuteAsync(
            plan,
            jobId,
            static (_, _) => Task.FromResult<long?>(4194304),
            progress => progressCompletion.TrySetResult(progress),
            cancellationToken);
        var progress = await progressCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        session.Complete();
        await execution;

        Assert.Equal(jobId, progress.JobId);
        Assert.Equal(1537113, progress.BytesTransferred);
        Assert.Equal(4194304, progress.TotalBytes);
        Assert.Equal(513.9 * 1024, progress.BytesPerSecond);
    }

    [Fact]
    public async Task ThrowingProgressObserverCannotFailAQueuedTransfer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new ProgressSession([
            "[0] get /remote.bin -o /c/target.bin",
            "\t`/remote.bin' at 1024 (1%) 1K/s eta:1s [Receiving data]",
        ]);
        await using var queue = await NativeTransferQueue.CreateAsync(
            session,
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            false,
            cancellationToken,
            TimeSpan.FromMilliseconds(100));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = Guid.NewGuid();

        var execution = queue.ExecuteAsync(
            Plan(jobId, segments: 1),
            jobId,
            static (_, _) => Task.FromResult<long?>(100_000),
            _ =>
            {
                observed.TrySetResult();
                throw new IOException("Simulated UI observer failure.");
            },
            cancellationToken);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        session.Complete();

        await execution;
    }

    [Fact]
    public async Task NativeQueueIgnoresProgressThatDisagreesWithTheValidatedTotal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = new ProgressSession([
            "[0] get /remote.bin -o /c/target.bin",
            "\t`/remote.bin' at 50000 (90%) 1K/s eta:1s [Receiving data]",
        ]);
        await using var queue = await NativeTransferQueue.CreateAsync(
            session,
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            false,
            cancellationToken,
            TimeSpan.FromMilliseconds(100));
        var published = 0;
        var jobId = Guid.NewGuid();
        var execution = queue.ExecuteAsync(
            Plan(jobId, segments: 1),
            jobId,
            static (_, _) => Task.FromResult<long?>(100_000),
            _ => Interlocked.Increment(ref published),
            cancellationToken);

        await session.JobsPolled.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        session.Complete();
        await execution;

        Assert.Equal(0, Volatile.Read(ref published));
    }

    private static TransferPlan Plan(Guid id, int segments) => new(
        id,
        Guid.NewGuid(),
        TransferDirection.Download,
        "/remote.bin",
        @"C:\target.bin",
        TransferMode.Resume,
        Segments: segments,
        SourceKind: TransferSourceKind.File);

    private sealed class ProgressSession(ImmutableArray<string> jobsOutput) : ILftpSession
    {
        private string? _completionMarker;
        public int ProcessId => 42;
        public bool IsRunning { get; private set; } = true;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput { add { } remove { } }
        public TaskCompletionSource JobsPolled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LftpCommandResult> ExecuteAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal))
            {
                _completionMarker = FindMarker(command, "_OK", submission: false);
                var submissionMarker = FindMarker(command, "_SUBMIT_OK", submission: true);
                return Task.FromResult(new LftpCommandResult([new("stdout", submissionMarker)]));
            }
            if (command == "jobs -vv")
            {
                JobsPolled.TrySetResult();
                return Task.FromResult(new LftpCommandResult(jobsOutput.Select(static line => new LftpOutputLine("stdout", line)).ToImmutableArray()));
            }
            return Task.FromResult(new LftpCommandResult([]));
        }

        public void Complete()
        {
            var marker = _completionMarker ?? throw new InvalidOperationException("No queued transfer is pending.");
            OutputReceived?.Invoke(this, new("stdout", marker));
        }

        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        private static string FindMarker(string command, string suffix, bool submission)
        {
            var offset = 0;
            while ((offset = command.IndexOf("__LFTPPILOT_QUEUE_", offset, StringComparison.Ordinal)) >= 0)
            {
                var end = offset;
                while (end < command.Length && (char.IsAsciiLetterOrDigit(command[end]) || command[end] is '_' or '-')) end++;
                var marker = command[offset..end];
                var isSubmission = marker.Contains("_SUBMIT_", StringComparison.Ordinal);
                if (isSubmission == submission && marker.EndsWith(suffix, StringComparison.Ordinal)) return marker;
                offset = end;
            }
            throw new InvalidDataException("The queued command did not contain its expected marker.");
        }
    }
}
