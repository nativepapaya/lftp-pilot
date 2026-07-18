using System.Collections.Immutable;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class ExplorerExportPolicyTests
{
    [Fact]
    public void StartRequiresBoundedUniqueAbsoluteRemoteFiles()
    {
        var sessionId = Guid.NewGuid();
        ExplorerExportPolicy.ValidateStart(new(
            Guid.NewGuid(), sessionId, ["/folder/one.txt", "/folder/two.txt"]));

        Assert.Throws<ArgumentException>(() => ExplorerExportPolicy.ValidateStart(new(
            Guid.NewGuid(), sessionId, ["relative.txt"])));
        Assert.Throws<ArgumentException>(() => ExplorerExportPolicy.ValidateStart(new(
            Guid.NewGuid(), sessionId, ["/folder/one.txt", "/folder/one.txt"])));
        Assert.Throws<ArgumentException>(() => ExplorerExportPolicy.ValidateStart(new(
            Guid.NewGuid(), sessionId,
            Enumerable.Range(0, ExplorerExportPolicy.MaximumFiles + 1)
                .Select(index => $"/file-{index}.bin").ToImmutableArray())));
    }

    [Fact]
    public void SnapshotExposesFilesOnlyAfterCompletedTransfer()
    {
        var exportId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var queued = new JobSnapshot(exportId, JobKind.Transfer, Guid.NewGuid(),
            "Explorer export", JobState.Queued, now, now);

        ExplorerExportPolicy.ValidateSnapshot(new(
            exportId, sessionId, queued, [], now.AddMinutes(30)));
        Assert.Throws<ArgumentException>(() => ExplorerExportPolicy.ValidateSnapshot(new(
            exportId, sessionId, queued, [Path.GetFullPath("queued.txt")], now.AddMinutes(30))));
        Assert.Throws<ArgumentException>(() => ExplorerExportPolicy.ValidateSnapshot(new(
            exportId, sessionId, queued with { State = JobState.Completed }, [], now.AddMinutes(30))));

        ExplorerExportPolicy.ValidateSnapshot(new(
            exportId,
            sessionId,
            queued with { State = JobState.Completed, Progress = 1, UpdatedAt = now.AddSeconds(1) },
            [Path.GetFullPath("completed.txt")],
            now.AddMinutes(30)));
    }
}
