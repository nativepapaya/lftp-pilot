using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class MirrorPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DryRunOutputBecomesTypedActions()
    {
        using var planner = new MirrorPlanner(approvalKey: Enumerable.Repeat((byte)7, 32).ToArray());
        var preview = planner.CreatePreview(CoreValidationTests.Mirror(delete: true),
        [
            "Transferring file `folder/file.txt'",
            "Making directory `new-folder'",
            "Removing old file `stale.txt'",
            "Removing old directory `stale-dir'",
            "unrelated status",
        ], Now);
        Assert.Collection(preview.Actions,
            action => Assert.Equal(MirrorActionKind.Download, action.Kind),
            action => Assert.Equal(MirrorActionKind.CreateDirectory, action.Kind),
            action => Assert.Equal(MirrorActionKind.DeleteFile, action.Kind),
            action => Assert.Equal(MirrorActionKind.DeleteDirectory, action.Kind));
        Assert.True(preview.ContainsDeletions);
    }

    [Fact]
    public void ReversePreviewClassifiesTransfersAsUploads()
    {
        using var planner = new MirrorPlanner();
        var preview = planner.CreatePreview(CoreValidationTests.Mirror(MirrorDirection.Upload), ["Transferring file `file.txt'"], Now);
        Assert.Equal(MirrorActionKind.Upload, Assert.Single(preview.Actions).Kind);
    }

    [Fact]
    public void DeletionMirrorRequiresMatchingExplicitApproval()
    {
        using var planner = new MirrorPlanner(approvalKey: Enumerable.Repeat((byte)9, 32).ToArray());
        var definition = CoreValidationTests.Mirror(delete: true);
        var preview = planner.CreatePreview(definition, ["Removing old file `stale.txt'"], Now);
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, null, Now));
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, "tampered", Now));
        var command = planner.BuildExecutionCommand(definition, preview, preview.ApprovalToken, Now);
        Assert.Contains("--delete", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedOrExpiredDefinitionCannotReuseApproval()
    {
        using var planner = new MirrorPlanner(TimeSpan.FromMinutes(5));
        var definition = CoreValidationTests.Mirror(delete: true);
        var preview = planner.CreatePreview(definition, [], Now);
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition with { RemoteRoot = "/changed" }, preview, preview.ApprovalToken, Now));
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, preview.ApprovalToken, Now.AddMinutes(6)));
    }

    [Fact]
    public void NonDeletingMirrorStillRequiresAValidFreshMatchingPreview()
    {
        using var planner = new MirrorPlanner();
        var definition = CoreValidationTests.Mirror();
        var preview = planner.CreatePreview(definition, [], Now);
        Assert.DoesNotContain("--delete", planner.BuildExecutionCommand(definition, preview, null, Now), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, null, Now.AddMinutes(10)));
    }

    [Fact]
    public void OversizedDryRunCannotProduceAnIncompleteApprovalSurface()
    {
        using var planner = new MirrorPlanner();
        var oversizedPath = new string('x', 4097);
        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(delete: true),
            [$"Removing old file `{oversizedPath}'"],
            Now));
    }

    [Theory]
    [InlineData("Would delete stale.txt")]
    [InlineData("Removing remote object `stale.txt'")]
    [InlineData("rmdir stale-directory")]
    [InlineData("purge /stale")]
    public void UnknownDeletionLikeDryRunOutputFailsClosed(string output)
    {
        using var planner = new MirrorPlanner();
        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(delete: true),
            [output],
            Now));
    }
}
