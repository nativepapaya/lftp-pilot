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
    public void ReviewFingerprintBindsEveryReviewedFieldButNotApprovalToken()
    {
        using var planner = new MirrorPlanner(approvalKey: Enumerable.Repeat((byte)7, 32).ToArray());
        var preview = planner.CreatePreview(
            CoreValidationTests.Mirror(delete: true),
            ["Transferring file `file.txt'", "Removing old file `stale.txt'"],
            Now);
        var fingerprint = MirrorPlanner.ReviewFingerprint(preview);

        Assert.Equal(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { ApprovalToken = "different-token" }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { Id = Guid.NewGuid() }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { DefinitionId = Guid.NewGuid() }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { GeneratedAt = preview.GeneratedAt.AddTicks(1) }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { ExpiresAt = preview.ExpiresAt.AddTicks(1) }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with { DefinitionFingerprint = "different" }));
        Assert.NotEqual(fingerprint, MirrorPlanner.ReviewFingerprint(preview with
        {
            Actions = preview.Actions.Add(new(MirrorActionKind.Download, "another.txt")),
        }));
    }

    [Fact]
    public void DefinitionFingerprintSeparatesStringContentsFromArrayBoundaries()
    {
        var singleItem = CoreValidationTests.Mirror(delete: true) with
        {
            Includes = ["ab"],
            Excludes = ["same"],
        };
        var separateItems = singleItem with { Includes = ["a", "b"] };

        Assert.NotEqual(
            MirrorPlanner.Fingerprint(singleItem),
            MirrorPlanner.Fingerprint(separateItems));
        Assert.NotEqual(
            LftpCommandBuilder.BuildMirror(singleItem, dryRun: false),
            LftpCommandBuilder.BuildMirror(separateItems, dryRun: false));
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

    [Theory]
    [InlineData("Removing old local file `collision'", MirrorActionKind.DeleteFile)]
    [InlineData("Removing old remote file `collision'", MirrorActionKind.DeleteFile)]
    [InlineData("Removing old local directory `collision'", MirrorActionKind.DeleteDirectory)]
    [InlineData("Removing old remote directory `collision'", MirrorActionKind.DeleteDirectory)]
    public void QualifiedRemovalOutputBecomesDeletionActions(string output, MirrorActionKind expectedKind)
    {
        using var planner = new MirrorPlanner();

        var preview = planner.CreatePreview(CoreValidationTests.Mirror(), [output], Now);

        Assert.Equal(expectedKind, Assert.Single(preview.Actions).Kind);
    }

    [Fact]
    public void GeneratedLocalDeletionScriptsAreRootBoundAndDeduplicatedWithDescriptions()
    {
        using var planner = new MirrorPlanner();
        var preview = planner.CreatePreview(CoreValidationTests.Mirror(),
        [
            "rm file:/c/Data/stale%20file.txt",
            "Removing old local file `/c/Data/stale file.txt'",
            "rm -r file:/c/Data/stale%20dir",
            "Removing old local directory `/c/Data/stale dir'",
            "rm file:/c/Data/encoded%3Bname.txt",
        ], Now);

        Assert.Collection(preview.Actions,
            action => Assert.Equal(new(MirrorActionKind.DeleteFile, "stale file.txt"), action),
            action => Assert.Equal(new(MirrorActionKind.DeleteDirectory, "stale dir"), action),
            action => Assert.Equal(new(MirrorActionKind.DeleteFile, "encoded;name.txt"), action));
    }

    [Fact]
    public void CaseDistinctLocalDeletionActionsAreNeverCollapsed()
    {
        using var planner = new MirrorPlanner();

        var preview = planner.CreatePreview(CoreValidationTests.Mirror(),
        [
            "rm file:/c/Data/A.txt",
            "Removing old local file `/c/Data/A.txt'",
            "rm file:/c/Data/a.txt",
            "Removing old local file `/c/Data/a.txt'",
        ], Now);

        Assert.Collection(preview.Actions,
            action => Assert.Equal(new(MirrorActionKind.DeleteFile, "A.txt"), action),
            action => Assert.Equal(new(MirrorActionKind.DeleteFile, "a.txt"), action));
    }

    [Fact]
    public void LocalDeletionRootBindingRequiresExactCase()
    {
        using var planner = new MirrorPlanner();

        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(), ["rm file:/c/data/stale.txt"], Now));
    }

    [Fact]
    public void GeneratedRemoteDeletionScriptsAreRootBoundAndDeduplicatedWithDescriptions()
    {
        using var planner = new MirrorPlanner();
        var definition = CoreValidationTests.Mirror(MirrorDirection.Upload);
        var preview = planner.CreatePreview(definition,
        [
            "rm sftp://example.test/srv/data/stale%20file.txt",
            "Removing old remote file `/srv/data/stale file.txt'",
            "rmdir ftp://example.test/srv/data/stale%20dir",
            "Removing old remote directory `/srv/data/stale dir'",
        ], Now);

        Assert.Collection(preview.Actions,
            action => Assert.Equal(new(MirrorActionKind.DeleteFile, "stale file.txt"), action),
            action => Assert.Equal(new(MirrorActionKind.DeleteDirectory, "stale dir"), action));
    }

    [Fact]
    public void RootBindingUsesReviewedCanonicalRoots()
    {
        using var planner = new MirrorPlanner();
        var localDefinition = CoreValidationTests.Mirror();
        var remoteDefinition = CoreValidationTests.Mirror(MirrorDirection.Upload);

        Assert.Equal("stale.txt", Assert.Single(planner.CreatePreview(
            localDefinition, ["rm file:/c/Data/stale.txt"], Now).Actions).Path);
        Assert.Equal("stale.txt", Assert.Single(planner.CreatePreview(
            remoteDefinition, ["rm sftp://example.test/srv/data/stale.txt"], Now).Actions).Path);
    }

    [Theory]
    [InlineData("rm -f file:/c/Data/stale.txt")]
    [InlineData("rm file:/c/Data/one file:/c/Data/two")]
    [InlineData("rm file:/c/Data/stale.txt;quit")]
    [InlineData("rm file:/c/Data/stale.txt|quit")]
    [InlineData("rm file:/c/Outside/stale.txt")]
    [InlineData("rm file:/c/Data/%2e%2e/outside.txt")]
    [InlineData("rm file:/c/Data/bad%ZZescape")]
    [InlineData("rm file:/c/Data/encoded%00control")]
    [InlineData("rm file:/c/Data")]
    [InlineData("rmdir --")]
    [InlineData("rm https://example.test/c/Data/stale.txt")]
    public void GeneratedLocalDeletionScriptsRejectOptionsCommandsAndEscapes(string output)
    {
        using var planner = new MirrorPlanner();

        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(CoreValidationTests.Mirror(), [output], Now));
    }

    [Theory]
    [InlineData("rm https://example.test/srv/data/stale.txt")]
    [InlineData("rm sftp://example.test/srv/other/stale.txt")]
    [InlineData("rm sftp://example.test/srv/data")]
    [InlineData("rm sftp://example.test/srv/data/%2e%2e/outside.txt")]
    [InlineData("rm sftp://example.test/srv/data/stale.txt?command=quit")]
    [InlineData("rm sftp://example.test/srv/data/stale.txt#fragment")]
    public void GeneratedRemoteDeletionScriptsRejectSchemesAndPathsOutsideReview(string output)
    {
        using var planner = new MirrorPlanner();

        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(MirrorDirection.Upload), [output], Now));
    }

    [Fact]
    public void RejectedGeneratedDeletionDoesNotEchoPotentialUrlCredentials()
    {
        using var planner = new MirrorPlanner();
        const string output = "rm -f sftp://alice:do-not-log@example.test/srv/data/stale.txt";

        var exception = Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(MirrorDirection.Upload), [output], Now));

        Assert.DoesNotContain("alice", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDetectedDeletionRequiresApprovalWithoutDeleteExtraneous()
    {
        using var planner = new MirrorPlanner(approvalKey: Enumerable.Repeat((byte)5, 32).ToArray());
        var definition = CoreValidationTests.Mirror();
        var preview = planner.CreatePreview(definition, ["Removing old local file `collision'"], Now);

        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, null, Now));
        Assert.Throws<InvalidOperationException>(() => planner.BuildExecutionCommand(definition, preview, "tampered", Now));
        var command = planner.BuildExecutionCommand(definition, preview, preview.ApprovalToken, Now);
        Assert.DoesNotContain("--delete", command, StringComparison.Ordinal);
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
    public void PreviewWithoutDeletionsStillRequiresFreshMatchingDefinition()
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
    [InlineData("rm file:/target/stale.txt")]
    [InlineData("rmdir stale-directory")]
    [InlineData("purge /stale")]
    [InlineData("Transferring file `safe.txt'; rm file:/c/Data/stale.txt")]
    [InlineData("Making directory `safe'; delete stale-directory")]
    public void UnknownDeletionLikeDryRunOutputFailsClosed(string output)
    {
        using var planner = new MirrorPlanner();
        Assert.Throws<InvalidDataException>(() => planner.CreatePreview(
            CoreValidationTests.Mirror(delete: true),
            [output],
            Now));
    }
}
