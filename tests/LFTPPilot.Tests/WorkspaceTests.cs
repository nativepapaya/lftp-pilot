using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task ProfileCredentialStaysInAgentAndConnectUsesHardenedLaunch()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile, "hunter2"), TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsConnected);
        var start = Assert.Single(fixture.ProcessHost.Starts);
        Assert.Equal(["--norc"], start.Arguments);
        Assert.Equal("C.UTF-8", start.Environment!["LC_ALL"]);
        Assert.Equal("1", start.Environment["CHERE_INVOKING"]);
        Assert.Equal(@"C:\fake\bin", start.Environment["PATH"]);
        Assert.Contains("hunter2", start.Secrets!);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.Contains("set xfer:make-backup no", StringComparison.Ordinal) &&
            command.Contains("set xfer:use-temp-file yes", StringComparison.Ordinal));

        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        var serialized = JsonSerializer.Serialize(bootstrap, FramedJsonStream.SerializerOptions);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileIdentityChangeInvalidatesBoundCredential()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile, "hunter2"), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile with { Host = "changed.example" }), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AskOnConnectCredentialIsEphemeralAndNeverPersisted()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile() with { Authentication = AuthenticationKind.AskOnConnect };
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken));
        await fixture.Service.ConnectAsync(new(profile.Id, "once"), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Secrets.Values);
    }

    [Fact]
    public async Task LocalAndRemoteBrowsingReturnTypedSortedEntries()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        var localRoot = Path.Combine(fixture.Directory.Path, "local");
        Directory.CreateDirectory(Path.Combine(localRoot, "folder"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "file.txt"), "data", TestContext.Current.CancellationToken);
        var local = await fixture.Service.BrowseLocalAsync(new(session.SessionId, localRoot), TestContext.Current.CancellationToken);
        Assert.Equal(EntryKind.Directory, local.Entries[0].Kind);
        Assert.Equal("file.txt", local.Entries[1].Name);

        var remote = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/home"), TestContext.Current.CancellationToken);
        Assert.Equal("folder", remote.Entries[0].Name);
        Assert.True(remote.Entries[0].IsDirectory);
        Assert.Equal("曲.txt", remote.Entries[1].Name);
        Assert.Equal(12, remote.Entries[1].Size);
    }

    [Fact]
    public async Task LocalFileMutationsRequireExplicitDeleteConfirmation()
    {
        await using var fixture = new WorkspaceFixture();
        var root = Path.Combine(fixture.Directory.Path, "mutations");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "renamed.txt");
        var createdDirectory = Path.Combine(root, "created");
        await File.WriteAllTextAsync(source, "keep until approved", TestContext.Current.CancellationToken);

        var created = await fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Local, createdDirectory), TestContext.Current.CancellationToken);
        var moved = await fixture.Service.MoveEntryAsync(
            new(PaneKind.Local, source, destination), TestContext.Current.CancellationToken);

        Assert.Equal([createdDirectory], created.AffectedPaths);
        Assert.Equal([source, destination], moved.AffectedPaths);
        Assert.True(Directory.Exists(createdDirectory));
        Assert.True(File.Exists(destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Local, [destination, createdDirectory]), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(destination));
        Assert.True(Directory.Exists(createdDirectory));

        var deleted = await fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Local, [destination, createdDirectory], Confirmed: true), TestContext.Current.CancellationToken);
        Assert.Equal(2, deleted.AffectedPaths.Length);
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(createdDirectory));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Local, "relative"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoteFileMutationsUseTypedCommandsAndRejectUnconfirmedOrAmbiguousPaths()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        await fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Remote, "/created", session.SessionId), TestContext.Current.CancellationToken);
        await fixture.Service.MoveEntryAsync(
            new(PaneKind.Remote, "/source.txt", "/renamed.txt", session.SessionId), TestContext.Current.CancellationToken);
        var beforeUnconfirmedDelete = fixture.ProcessHost.Commands.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Remote, ["/delete.txt"], session.SessionId), TestContext.Current.CancellationToken));
        Assert.Equal(beforeUnconfirmedDelete, fixture.ProcessHost.Commands.Count);

        var deleted = await fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Remote, ["/delete.txt", "/empty-dir", "/tree"], session.SessionId, Recursive: true, Confirmed: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(3, deleted.AffectedPaths.Length);
        Assert.Contains("mkdir -p \"/created\"", fixture.ProcessHost.Commands);
        Assert.Contains("mv \"/source.txt\" \"/renamed.txt\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm \"/delete.txt\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm -r \"/empty-dir\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm -r \"/tree\"", fixture.ProcessHost.Commands);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Remote, "/safe/../escape", session.SessionId), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.MoveEntryAsync(
            new(PaneKind.Remote, "/source.txt", "/bad\nquit", session.SessionId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TransferUsesLazyPersistentTransferSessionAndCompletesJob()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "downloads", "file.bin");
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination, TransferMode.Resume, 4);
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
        Assert.Contains("set cmd:queue-parallel 2; queue; queue start", fixture.ProcessHost.Commands);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) && command.Contains("pget -n 4 -c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedDirectoryTransfersRemainTransferJobsAndCompleteThroughTheGuardedForegroundSession()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var localSource = Path.Combine(fixture.Directory.Path, "upload-directory");
        Directory.CreateDirectory(localSource);
        await File.WriteAllTextAsync(Path.Combine(localSource, "nested.txt"), "data", TestContext.Current.CancellationToken);

        var download = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source",
            Path.Combine(fixture.Directory.Path, "download-directory"), TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        var upload = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Upload, localSource, "/remote/new-directory-target",
            TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Where(job => job.Id == download.Job.Id || job.Id == upload.Job.Id)
            .All(job => job.State == JobState.Completed), TestContext.Current.CancellationToken);
        Assert.Equal(JobKind.Transfer, fixture.Jobs.GetJobs().Single(job => job.Id == download.Job.Id).Kind);
        Assert.Equal(JobKind.Transfer, fixture.Jobs.GetJobs().Single(job => job.Id == upload.Job.Id).Kind);
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror --continue", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("directory-transfer-source", StringComparison.Ordinal));
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror --reverse --continue", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("upload-directory", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("mirror", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransferEnqueueRequiresExistingMatchingRegularSources()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "download.bin");

        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/missing-transfer-source", destination)),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/transfer-link", destination)),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/transfer-special", destination)),
            TestContext.Current.CancellationToken));

        var missingLocal = Path.Combine(fixture.Directory.Path, "missing-upload.bin");
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, missingLocal, "/remote/target.bin")),
            TestContext.Current.CancellationToken));
        var localFile = Path.Combine(fixture.Directory.Path, "upload.bin");
        await File.WriteAllTextAsync(localFile, "data", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/target.bin",
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/transfer-link")),
            TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public void LocalTransferAttributeClassificationRejectsReparsePointsAndSpecialEntries()
    {
        Assert.Equal(TransferSourceKind.File,
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Normal));
        Assert.Equal(TransferSourceKind.Directory,
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Directory));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.ReparsePoint));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Device));
        AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Directory);
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.ReparsePoint));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Device));
        Assert.Throws<IOException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Normal));
    }

    [Fact]
    public async Task TransferEnqueueRejectsExistingDestinationsOfTheWrongKind()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var localDirectory = Path.Combine(fixture.Directory.Path, "existing-directory");
        Directory.CreateDirectory(localDirectory);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", localDirectory)),
            TestContext.Current.CancellationToken));

        var localFile = Path.Combine(fixture.Directory.Path, "existing-file.bin");
        await File.WriteAllTextAsync(localFile, "data", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source", localFile,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin",
                Path.Combine(localFile, "child.bin"))), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/directory-destination")),
            TestContext.Current.CancellationToken));

        var uploadDirectory = Path.Combine(fixture.Directory.Path, "upload-directory");
        Directory.CreateDirectory(uploadDirectory);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, uploadDirectory, "/remote/existing-file.bin",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task TransferEnqueueRejectsNonCanonicalRemoteAndDeviceLocalPathsWithoutSideEffects()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        var startsBefore = fixture.ProcessHost.Starts.Count;
        var destination = Path.Combine(fixture.Directory.Path, "invalid.bin");
        var invalidRemotePaths = new[]
        {
            "/safe/../outside",
            "/safe/./outside",
            "/safe//outside",
            "/safe/outside/",
            "/" + new string('a', 4096),
            "/" + string.Join('/', Enumerable.Repeat("part", 129)),
        };

        foreach (var remotePath in invalidRemotePaths)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Download, remotePath, destination)),
                TestContext.Current.CancellationToken));
        }

        foreach (var devicePath in new[] { @"\\?\C:\outside.bin", "//?/C:/outside.bin", @"\\.\C:\outside.bin", "//./C:/outside.bin" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, devicePath, "/remote/device.bin")),
                TestContext.Current.CancellationToken));
        }
        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Equal(startsBefore, fixture.ProcessHost.Starts.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task TransferCanonicalizesLocalDotSegmentsOnceBeforeValidationAndExecution()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "canonical-source.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var backslashPlan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload,
            Path.Combine(fixture.Directory.Path, "reviewed", "..", "canonical-source.bin"), "/remote/canonical-backslash.bin");
        var slashPlan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload,
            backslashPlan.SourcePath.Replace('\\', '/'), "/remote/canonical-slash.bin");

        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, backslashPlan), TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, slashPlan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        var submissions = fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("canonical-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.All(submissions, item => Assert.DoesNotContain("..", item.Command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuickDirectoryTransfersRejectRemoteAndLocalRootsWithoutStartingWork()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        var startsBefore = fixture.ProcessHost.Starts.Count;
        var localDirectory = Path.Combine(fixture.Directory.Path, "non-root-source");
        Directory.CreateDirectory(localDirectory);
        var driveRoot = Path.GetPathRoot(fixture.Directory.Path)!;
        var plans = new[]
        {
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/",
                Path.Combine(fixture.Directory.Path, "server-root"), TransferMode.Resume,
                SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localDirectory, "/",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, driveRoot, "/remote/drive-root",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, @"\\server\share\", "/remote/share-root",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source", driveRoot,
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
        };

        foreach (var plan in plans)
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(
                new(session.SessionId, plan), TestContext.Current.CancellationToken));
            Assert.Contains("reviewed Mirror", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Equal(startsBefore, fixture.ProcessHost.Starts.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task RemoteAncestorLinkDriftBlocksFileAndDirectoryExecution()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var file = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/link-ancestor/file.bin",
                Path.Combine(fixture.Directory.Path, "ancestor-file.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == file.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("ancestor/file.bin", StringComparison.Ordinal));

        var directory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-link-ancestor/source",
                Path.Combine(fixture.Directory.Path, "ancestor-directory"), TransferMode.Resume,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("directory-link-ancestor/source", StringComparison.Ordinal) &&
            item.Command.Contains("mirror", StringComparison.Ordinal) && !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        Assert.All(new[] { file.Job.Id, directory.Job.Id }, id =>
            Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == id).Error?.Message,
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(MirrorDirection.Download, false)]
    [InlineData(MirrorDirection.Download, true)]
    [InlineData(MirrorDirection.Upload, false)]
    [InlineData(MirrorDirection.Upload, true)]
    public async Task MirrorPreviewRejectsLocalRootOrAncestorJunction(MirrorDirection direction, bool ancestor)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.MirrorJunction", Guid.NewGuid().ToString("N"));
        var junction = Path.Combine(fixture.Directory.Path, "junction");
        Directory.CreateDirectory(external);
        if (ancestor) Directory.CreateDirectory(Path.Combine(external, "child"));
        CreateDirectoryJunction(junction, external);
        var localRoot = ancestor ? Path.Combine(junction, "child") : junction;
        try
        {
            var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Unsafe junction", direction, localRoot, "/remote");
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.PreviewMirrorAsync(
                new(session.SessionId, definition), TestContext.Current.CancellationToken));

            Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "mirror-preview");
            Assert.Empty(fixture.Jobs.GetJobs());
        }
        finally
        {
            Directory.Delete(junction, recursive: false);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task MirrorFinalCheckRejectsLocalAncestorChangedToJunctionForBothDirections()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var blockerDefinition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Gate blocker", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var blockerPreview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, blockerDefinition), TestContext.Current.CancellationToken);
        var blocker = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, blockerDefinition, blockerPreview.Id, blockerPreview.ApprovalToken),
            TestContext.Current.CancellationToken);
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var parent = Path.Combine(fixture.Directory.Path, "reviewed-parent");
        var localRoot = Path.Combine(parent, "child");
        Directory.CreateDirectory(localRoot);
        var approved = new List<MirrorApproveResult>();
        foreach (var direction in new[] { MirrorDirection.Download, MirrorDirection.Upload })
        {
            var suffix = direction.ToString().ToLowerInvariant();
            var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, $"Junction drift {suffix}", direction,
                localRoot, $"/local-root-drift-{suffix}");
            var preview = await fixture.Service.PreviewMirrorAsync(
                new(session.SessionId, definition), TestContext.Current.CancellationToken);
            Assert.False(preview.ContainsDeletions);
            approved.Add(await fixture.Service.ApproveMirrorAsync(
                new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken));
        }

        Directory.Delete(parent, recursive: true);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.MirrorDrift", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(external, "child"));
        CreateDirectoryJunction(parent, external);
        try
        {
            fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == blocker.Job.Id).State == JobState.Completed,
                TestContext.Current.CancellationToken);
            foreach (var result in approved)
            {
                await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == result.Job.Id).State == JobState.Failed,
                    TestContext.Current.CancellationToken);
            }

            Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
                item.Command.Contains("/local-root-drift-", StringComparison.Ordinal) &&
                item.Command.Contains("mirror", StringComparison.Ordinal) &&
                !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(parent, recursive: false);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryDownloadRejectsAnExistingLocalFileSystemRootBeforeRemoteStat()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var destinationRoot = Path.GetPathRoot(fixture.Directory.Path)!;
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/directory-transfer-source",
            destinationRoot,
            TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory);

        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));

        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task FreshRemoteStatRejectsAListingForAPathOtherThanTheRequest()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/mismatched-stat",
                Path.Combine(fixture.Directory.Path, "mismatched-stat.bin"))), TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Jobs.GetJobs());
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("mismatched-stat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshRemoteStatTreatsCleanEmptyUploadDestinationAsAbsent()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "empty-stat-upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
                "/remote/zero-line-missing-target")), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.All(fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Role == "validation" && item.Command.Contains("zero-line-missing-target", StringComparison.Ordinal)),
            item => Assert.StartsWith("recls -ldB ", item.Command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkspaceFreshStatAcceptsPathlessOrCorrectlyBoundMissingDiagnosticsAndRejectsWrongPath()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "missing-diagnostic-upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        foreach (var target in new[] { "/remote/pathless-missing-target", "/remote/bound-missing-target" })
        {
            var transfer = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, target)),
                TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).State == JobState.Completed,
                TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/remote/wrong-bound-missing-target")),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecutionFreshStatRejectsDirectoryChangedToLinkWithoutQueueSubmission()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download,
            "/remote/stat-drift-directory", Path.Combine(fixture.Directory.Path, "stat-drift-directory"),
            TransferMode.Resume, SourceKind: TransferSourceKind.Directory);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("symbolic link", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        var stats = fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("stat-drift-directory", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, stats.Length);
        Assert.All(stats, item => Assert.StartsWith("recls -ldB ", item.Command, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            (item.Role == "transfer-queue" || item.Role == "transfer") &&
            item.Command.Contains("stat-drift-directory", StringComparison.Ordinal) &&
            !item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/remote/dir-over-file-directory")]
    [InlineData("/remote/unrecognized-destructive-directory")]
    public async Task AutomaticDirectoryDryRunBlocksDestructiveOrUnrecognizedOutput(string remoteSource)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, remoteSource,
            Path.Combine(fixture.Directory.Path, "dry-run-destination"), TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));

        Assert.Contains("reviewed Mirror workflow", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Jobs.GetJobs());
        var preview = Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "directory-transfer-preview" && item.Command.Contains(remoteSource, StringComparison.Ordinal));
        Assert.StartsWith("mirror --verbose=1 --dry-run", preview.Command, StringComparison.Ordinal);
        Assert.Contains("--no-symlinks --overwrite", preview.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete", preview.Command, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
    }

    [Fact]
    public async Task FailedTransferRetryIsSingleShotRevalidatedAndCompletes()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/retry-once.bin",
            Path.Combine(fixture.Directory.Path, "retry-once.bin"),
            TransferMode.Resume,
            4);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.True(failed.CanRetry);
        Assert.NotNull(failed.Error);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return await fixture.Service.RetryJobAsync(new(enqueued.Job.Id), TestContext.Current.CancellationToken);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }));
        var accepted = Assert.Single(attempts, static result => result is not null)!.Job;
        Assert.Equal(enqueued.Job.Id, accepted.Id);
        Assert.Null(accepted.Error);
        Assert.Null(accepted.Progress);
        Assert.Null(accepted.RunAt);

        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        var submissions = fixture.ProcessHost.TaggedCommands
            .Where(item => item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
                item.Command.Contains("retry-once.bin", StringComparison.Ordinal))
            .Select(static item => item.Command)
            .ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.Equal(2, submissions.Select(static command => command.Split(' ', 2)[1]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RetryPreflightFailureLeavesOriginalFailureUntouched()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        await File.WriteAllTextAsync(source, "upload", TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/retry-target.bin", TransferMode.Resume);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        File.Delete(source);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
    }

    [Fact]
    public async Task DirectoryRetryRevalidatesTheDeclaredSourceKindBeforeResettingFailure()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/retry-directory-target", TransferMode.Resume, SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Directory.Delete(source);
        await File.WriteAllTextAsync(source, "replacement file", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));
        Assert.Contains("declares a directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryRetryDryRunFailurePreservesTheOriginalFailedAttempt()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/retry-preview-target", TransferMode.Resume, SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));

        Assert.Contains("reviewed Mirror workflow", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
        Assert.Equal(3, fixture.ProcessHost.TaggedCommands.Count(item =>
            (item.Role == "directory-transfer-preview" || item.Role == "transfer") &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-preview-target", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RetryRequiresTheExactOriginatingSession()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var originalSession = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/retry-once.bin",
            Path.Combine(fixture.Directory.Path, "exact-session.bin"), TransferMode.Resume);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(originalSession.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var originalFailure = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);

        Assert.True(await fixture.Service.DisconnectAsync(new(originalSession.SessionId)));
        var replacement = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        Assert.NotEqual(originalSession.SessionId, replacement.SessionId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));

        Assert.Equal(originalFailure, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryRejectsNonTransferJobsWithoutReusingMirrorApproval()
    {
        await using var fixture = new WorkspaceFixture();
        var now = DateTimeOffset.UtcNow;
        var mirror = new JobSnapshot(Guid.NewGuid(), JobKind.Mirror, Guid.NewGuid(), "Reviewed mirror", JobState.Failed,
            now, now, Error: new("mirror-failed", "Changed after review"), RetryAvailable: true);
        fixture.Jobs.Restore([mirror]);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.RetryJobAsync(
            new(mirror.Id), TestContext.Current.CancellationToken));
        Assert.Contains("fresh preview", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobState.Failed, fixture.Jobs.GetJobs().Single().State);
    }

    [Fact]
    public async Task RunOnceTransferPersistsMetadataWaitsForSelectedTimeAndThenRuns()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.PasswordProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile, "scheduled-secret"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/scheduled.bin",
            Path.Combine(fixture.Directory.Path, "scheduled.bin"),
            RunAt: time.GetUtcNow().AddHours(1));

        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Scheduled, enqueued.Job.State);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
        var durable = Assert.Single((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Jobs);
        Assert.Equal(JobState.Scheduled, durable.State);
        Assert.Equal(plan.RunAt, durable.RunAt);
        Assert.DoesNotContain("scheduled-secret",
            await File.ReadAllTextAsync(Path.Combine(fixture.Directory.Path, "jobs.json"), TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        time.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) && command.Contains("scheduled.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledDirectoryTransferRevalidatesTheDeclaredSourceKindAtRunTime()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "scheduled-directory-source");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/scheduled-directory-target", TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(15),
            SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Directory.Delete(source);
        await File.WriteAllTextAsync(source, "replacement file", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State != JobState.Scheduled,
            TestContext.Current.CancellationToken);
        _ = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Contains("declares a directory", failed.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-directory-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledDirectoryDryRunBlocksNewTypeCollisionWithoutQueueSubmission()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "scheduled-preview-source");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/scheduled-preview-target", TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(15),
            SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State != JobState.Scheduled,
            TestContext.Current.CancellationToken);
        _ = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("reviewed Mirror workflow", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            (item.Role == "directory-transfer-preview" || item.Role == "transfer") &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-preview-target", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("scheduled-preview-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingRunOnceTransferPreventsExecutionAfterItsSelectedTime()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/never-run.bin",
            Path.Combine(fixture.Directory.Path, "never-run.bin"),
            RunAt: time.GetUtcNow().AddMinutes(30));
        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancelled before start"));
        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();

        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("never-run.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingDueTimeValidationPreservesBrowseAndRecreatesOnlyValidationRole()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var scheduled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/scheduled-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "scheduled-validation-cancel.bin"),
                RunAt: time.GetUtcNow().AddMinutes(10))), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await fixture.ProcessHost.ScheduledValidationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(fixture.Service.TryCancelOperation(scheduled.Job.Id, "Cancel due-time validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);

        var browse = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/remote"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(browse.Entries);
        var after = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-scheduled-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "after-scheduled-validation-cancel.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == after.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "browse"));
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("\"/remote/scheduled-validation-cancel.bin\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledSkipIsDecidedInsideReservedSlotWithoutSubmittingWhenDestinationAppears()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "scheduled-skip.bin");
        var scheduled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/scheduled-skip.bin", destination,
                TransferMode.Skip, RunAt: time.GetUtcNow().AddMinutes(10))), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(destination, "appeared", TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Contains("Skipped", fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).Status,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role.StartsWith("transfer-policy-", StringComparison.Ordinal) &&
            item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-skip.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledFailureCleanupCompletesBeforeRetryAndCancellationTargetsTheRetry()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download,
            "/remote/scheduled-retry-cancel.bin", Path.Combine(fixture.Directory.Path, "scheduled-retry-cancel.bin"),
            TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(10));
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var retried = await fixture.Service.RetryJobAsync(new(enqueued.Job.Id), TestContext.Current.CancellationToken);
        Assert.Null(retried.Job.RunAt);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Running,
            TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancel retried schedule"));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.ProcessHost.StoppedRoles.Contains("transfer-queue"),
            TestContext.Current.CancellationToken);
        Assert.Equal("Cancel retried schedule", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Status);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-retry-cancel.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScheduledTransferPreventsSessionOrProfileDisposalUntilCancelled()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/future.bin",
            Path.Combine(fixture.Directory.Path, "future.bin"),
            RunAt: time.GetUtcNow().AddHours(1));
        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DisconnectAsync(new(session.SessionId)));
        Assert.Contains("Cancel scheduled or active jobs", disconnect.Message, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Contains("Cancel scheduled or active jobs", delete.Message, StringComparison.Ordinal);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancel before disconnect"));
        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId)));
    }

    [Fact]
    public async Task ActiveRemoteEditSurvivesBootstrapAndBlocksSessionAndProfileRemoval()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var edit = await fixture.Service.StartRemoteEditAsync(
            new(session.SessionId, "/active.txt"), TestContext.Current.CancellationToken);

        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(bootstrap.RemoteEdits);
        Assert.Equal(edit.EditId, restored.EditId);
        Assert.False(restored.Dirty);
        Assert.False(restored.WatcherFailed);
        Assert.Null(restored.LastLocalChangeAt);

        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DisconnectAsync(new(session.SessionId)));
        Assert.Contains("active remote edit", disconnect.Message, StringComparison.OrdinalIgnoreCase);
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Contains("active remote edit", delete.Message, StringComparison.OrdinalIgnoreCase);

        var afterFailures = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.Contains(afterFailures.Profiles, candidate => candidate.Id == profile.Id);
        Assert.Contains(afterFailures.Sessions, candidate => candidate.SessionId == session.SessionId);
        Assert.Equal(edit.EditId, Assert.Single(afterFailures.RemoteEdits).EditId);

        Assert.True(await fixture.Service.CompleteRemoteEditAsync(
            new(edit.EditId), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId)));
        Assert.True(await fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RateLimitedTransfersUseParallelismOneInAnIsolatedQueueProcess()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/limited.bin",
            Path.Combine(fixture.Directory.Path, "limited.bin"),
            RateLimitBytesPerSecond: 4096);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        var isolatedRole = Assert.Single(fixture.ProcessHost.Starts,
            start => start.Tag.StartsWith("transfer-policy-", StringComparison.Ordinal)).Tag;
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == isolatedRole && item.Command == "set cmd:queue-parallel 1; queue; queue start");
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == isolatedRole && item.Command.Contains("set net:limit-rate 4096:4096", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("net:limit-rate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonfatalProcessLaunchFailureTransitionsTrackedTransferToFailed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        fixture.ProcessHost.FailRolePrefix = "transfer-queue";
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/process-failure.bin",
            Path.Combine(fixture.Directory.Path, "process-failure.bin"));

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Equal("lftp-job-failed", failed.Error?.Code);
        Assert.Contains("simulated process launch failure", failed.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipModePreflightsDestinationAndDoesNotOverwriteExistingDownload()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "existing.bin");
        await File.WriteAllTextAsync(destination, "keep", TestContext.Current.CancellationToken);

        var skipped = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination, TransferMode.Skip)),
            TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, skipped.Job.State);
        Assert.Contains("Skipped", skipped.Job.Status, StringComparison.Ordinal);
        Assert.Contains(" -> ", skipped.Job.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer");
        Assert.Equal("keep", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SkipDownloadUsesIsolatedNoClobberQueueAndSkipUploadFailsClosed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        var newDestination = Path.Combine(fixture.Directory.Path, "new.bin");
        var download = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/new.bin", newDestination, TransferMode.Skip)),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == download.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role.StartsWith("transfer-policy-", StringComparison.Ordinal) &&
            item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("set xfer:use-temp-file no", StringComparison.Ordinal) &&
            item.Command.Contains("set xfer:clobber no && get", StringComparison.Ordinal) &&
            item.Command.Split("set xfer:clobber yes", StringSplitOptions.None).Length == 3 &&
            item.Command.Split("set xfer:use-temp-file yes", StringSplitOptions.None).Length == 3);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("xfer:clobber", StringComparison.Ordinal));

        var source = Path.Combine(fixture.Directory.Path, "upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/existing.bin", TransferMode.Skip)),
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.StartsWith("cls -1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            command.Contains("put ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningTransferCancellationStopsTheOwnedOperationAndJob()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin",
            Path.Combine(fixture.Directory.Path, "blocking.bin"));
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Running, TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(queued.Job.Id, "User cancelled"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Cancelled, TestContext.Current.CancellationToken);
        Assert.Equal("User cancelled", fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).Status);
    }

    [Fact]
    public async Task NativeQueueRunsNeighborInParallelAndRecreatesSessionAfterCancellation()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin", Path.Combine(fixture.Directory.Path, "blocking.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Running, TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/next.bin", Path.Combine(fixture.Directory.Path, "next.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Next"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.StoppedRoles.Contains("transfer-queue"), TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State);
        var third = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-cancel.bin", Path.Combine(fixture.Directory.Path, "after-cancel.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == third.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task NativeQueueRevalidatesOnlyAfterReservingItsBoundedSlotAndKeepsQueueAfterValidationFailure()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/slot-block-one.bin",
                Path.Combine(fixture.Directory.Path, "slot-block-one.bin"))), TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/slot-block-two.bin",
                Path.Combine(fixture.Directory.Path, "slot-block-two.bin"))), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.QueueSlotsFilled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var drift = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file-slot-drift.bin",
                Path.Combine(fixture.Directory.Path, "file-slot-drift.bin"))), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("\"/remote/file-slot-drift.bin\"", StringComparison.Ordinal));

        fixture.ProcessHost.ReleaseReservedQueueSlots.TrySetResult(true);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == drift.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("symbolic link", fixture.Jobs.GetJobs().Single(job => job.Id == drift.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("file-slot-drift.bin", StringComparison.Ordinal));

        var afterFailure = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-validation-failure.bin",
                Path.Combine(fixture.Directory.Path, "after-validation-failure.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == afterFailure.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task CancellingFinalValidationRecreatesValidationSessionWithoutBreakingBrowseOrQueue()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var cancelled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/cancel-validation.bin",
                Path.Combine(fixture.Directory.Path, "cancel-validation.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.TaggedCommands.Any(item =>
            item.Role == "validation" && item.Command.Contains("cancel-validation.bin", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(cancelled.Job.Id, "Cancel validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == cancelled.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("cancel-validation.bin", StringComparison.Ordinal));

        var after = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "after-validation-cancel.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == after.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        var browse = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/remote"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(browse.Entries);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "browse"));
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task SharedAndIsolatedQueuesSerializeTheReusableValidationSessionAcrossCancellation()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/validation-gate-one.bin",
                Path.Combine(fixture.Directory.Path, "validation-gate-one.bin"),
                RateLimitBytesPerSecond: 1024)), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.ValidationGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var secondTask = fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/validation-gate-two.bin",
                Path.Combine(fixture.Directory.Path, "validation-gate-two.bin"),
                RateLimitBytesPerSecond: 2048)), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.Contains("validation-gate-two.bin", StringComparison.Ordinal));

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Cancel serialized validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        var second = await secondTask;
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
    }

    [Fact]
    public async Task GuardedDirectoryTransferRepeatsDryRunAfterWaitingForTransferGate()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var blockerDefinition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Gate blocker", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var blockerPreview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, blockerDefinition), TestContext.Current.CancellationToken);
        var blocker = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, blockerDefinition, blockerPreview.Id, blockerPreview.ApprovalToken),
            TestContext.Current.CancellationToken);
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var localSource = Path.Combine(fixture.Directory.Path, "guarded-drift-source");
        Directory.CreateDirectory(localSource);
        var directory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localSource, "/remote/guarded-drift-target",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "directory-transfer-preview" && item.Command.Contains("guarded-drift-target", StringComparison.Ordinal));

        fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == blocker.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("reviewed Mirror workflow", fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("guarded-drift-target", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("guarded-drift-target", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardedDirectoryTransferRevalidatesRemotePrefixesAfterFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var transfer = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download,
                "/remote/post-directory-link-ancestor/source",
                Path.Combine(fixture.Directory.Path, "post-directory-destination"),
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("post-directory-link-ancestor/source", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            item.Command.Contains("post-directory-link-ancestor/source", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InterleavedBackgroundErrorDoesNotPoisonExactQueueSubmission()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/interleaved.bin",
                Path.Combine(fixture.Directory.Path, "interleaved.bin"))), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Completed, fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State);
    }

    [Fact]
    public async Task CancellingOneActiveNativeQueueItemFailsOtherUncorrelatedItemsClosed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin", Path.Combine(fixture.Directory.Path, "blocking.bin"))),
            TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/also-blocking.bin", Path.Combine(fixture.Directory.Path, "also-blocking.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("blocking.bin", StringComparison.Ordinal)) == 2, TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Cancel one"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        var failedNeighbor = fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id);
        Assert.Contains("shared per-profile LFTP queue was retired", failedNeighbor.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteMirrorRequiresAgentHeldFreshPreviewAndNeverExecutesDryRunText()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Clean mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/remote", DeleteExtraneous: true);
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.True(preview.ContainsDeletions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, "tampered", DeletionsApproved: true), TestContext.Current.CancellationToken));

        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken, DeletionsApproved: true), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("--delete", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.Contains("Removing old file", StringComparison.Ordinal));
        Assert.Equal(1, fixture.ProcessHost.DisposedRoles.Count(role => role == "mirror-preview"));
    }

    [Fact]
    public async Task TypeCollisionDeletionInNonDeletingMirrorRequiresApprovalAndSecondPreview()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Collision mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/collision-review");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.True(preview.ContainsDeletions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken));
        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken, DeletionsApproved: true),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.ProcessHost.Commands.Count(command =>
            command.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
            command.Contains("/collision-review", StringComparison.Ordinal)));
        Assert.Single(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("/collision-review", StringComparison.Ordinal) &&
            !command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingMirrorRejectsChangesBetweenPreviewAndApproval()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Drift", MirrorDirection.Download,
            fixture.Directory.Path, "/drift", DeleteExtraneous: true);
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);

        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken, DeletionsApproved: true), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed, TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id);

        Assert.Contains("changed", failed.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("--delete", StringComparison.Ordinal) &&
            !command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitiallyCleanMirrorRejectsADeletionThatAppearsAtFinalReview()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Clean drift", MirrorDirection.Download,
            fixture.Directory.Path, "/clean-collision-drift");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);

        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("changed", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
            item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorFinalRemotePrefixWalkRejectsAnAncestorThatChangedToLink()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Remote ancestor drift", MirrorDirection.Download,
            fixture.Directory.Path, "/remote/mirror-link-ancestor/root");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);

        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorRevalidatesRemotePrefixesAfterMatchingFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Post-preview remote drift",
            MirrorDirection.Download, fixture.Directory.Path, "/remote/post-mirror-link-ancestor/root");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);
        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("post-mirror-link-ancestor/root", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            item.Command.Contains("post-mirror-link-ancestor/root", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorRevalidatesLocalAncestorsAfterMatchingFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);
        var parent = Path.Combine(fixture.Directory.Path, "post-dryrun-parent");
        var localRoot = Path.Combine(parent, "child");
        Directory.CreateDirectory(localRoot);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.PostDryRun", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(external, "child"));
        fixture.ProcessHost.FinalDryRunAction = () =>
        {
            Directory.Delete(parent, recursive: true);
            CreateDirectoryJunction(parent, external);
        };
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Post-preview local drift",
            MirrorDirection.Download, localRoot, "/local-post-dryrun");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var approved = await fixture.Service.ApproveMirrorAsync(
            new(session.SessionId, definition, preview.Id, preview.ApprovalToken), TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
                TestContext.Current.CancellationToken);

            Assert.Contains("reparse", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
                item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
                item.Command.Contains("/local-post-dryrun", StringComparison.Ordinal) &&
                !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        }
        finally
        {
            fixture.ProcessHost.FinalDryRunAction = null;
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task ConsoleIsLazyIsolatedAndEnforcesSafePolicy()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(profile.Id), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteConsoleAsync(
            new(session.SessionId, "cat x | sh"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "console");
        var result = await fixture.Service.ExecuteConsoleAsync(new(session.SessionId, "pwd"), TestContext.Current.CancellationToken);
        Assert.Contains(result.Result.Lines, line => line.Line == "/remote/home");
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "console"));
    }

    [Fact]
    public async Task RemoteTransferPlanningChoosesFxpOnlyForFtpFamily()
    {
        await using var fixture = new WorkspaceFixture();
        var ftp = fixture.AnonymousProfile(ConnectionProtocol.FtpsExplicit);
        var ftp2 = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with { Id = Guid.NewGuid(), Name = "FTP 2", Host = "two.example" };
        var sftp = fixture.AnonymousProfile(ConnectionProtocol.Sftp) with { Id = Guid.NewGuid(), Name = "SFTP", Host = "sftp.example" };
        await fixture.Profiles.SaveAsync(ftp, TestContext.Current.CancellationToken);
        await fixture.Profiles.SaveAsync(ftp2, TestContext.Current.CancellationToken);
        await fixture.Profiles.SaveAsync(sftp, TestContext.Current.CancellationToken);

        var fxp = await fixture.Service.PlanRemoteTransferAsync(new(ftp.Id, ftp2.Id, "/a", "/b"), TestContext.Current.CancellationToken);
        var relay = await fixture.Service.PlanRemoteTransferAsync(new(ftp.Id, sftp.Id, "/a", "/b"), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteTransferMode.Fxp, fxp.Mode);
        Assert.Equal(RemoteTransferMode.ClientRelay, relay.Mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(fxp), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoteTransferExecutesFxpPreferredJobWithoutPuttingCredentialsInArgumentsOrResults()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.PasswordProfile(ConnectionProtocol.FtpsExplicit, "Source", "source.example");
        var destination = fixture.PasswordProfile(ConnectionProtocol.Ftp, "Destination", "destination.example");
        await fixture.Service.SaveProfileAsync(new(source, "source-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination, "destination-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(source.Id), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(destination.Id), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteTransferMode.Fxp, enqueued.Mode);
        Assert.Contains("FXP preferred", enqueued.RoutingNote, StringComparison.Ordinal);
        var start = Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
        Assert.Equal(["--norc"], start.Arguments);
        Assert.Equal(2, start.Secrets!.Count);
        Assert.Contains("source-secret", start.Secrets);
        Assert.Contains("destination-secret", start.Secrets);
        Assert.DoesNotContain("source-secret", string.Join(' ', start.Arguments!), StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", string.Join(' ', start.Arguments!), StringComparison.Ordinal);
        var command = Assert.Single(fixture.ProcessHost.Commands,
            command => command.StartsWith("set ftp:use-fxp true", StringComparison.Ordinal));
        Assert.Contains("\"slot:source/source.bin\"", command, StringComparison.Ordinal);
        Assert.Contains("\"slot:destination/new-target.bin\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("source-secret", command, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", command, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(new { enqueued, Jobs = fixture.Jobs.GetJobs() }, FramedJsonStream.SerializerOptions);
        Assert.DoesNotContain("source-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteTransferMarksClientRelayAndRejectsDirectoriesAndUnapprovedOverwrite()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(source.Id), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(destination.Id), TestContext.Current.CancellationToken);

        var directoryPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source-folder", "/new-target.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(directoryPlan), TestContext.Current.CancellationToken));
        var collisionPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/existing.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(collisionPlan), TestContext.Current.CancellationToken));

        var relayPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(relayPlan with { Mode = RemoteTransferMode.Fxp }), TestContext.Current.CancellationToken));
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(relayPlan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteTransferMode.ClientRelay, enqueued.Mode);
        Assert.Contains("Client-relay", enqueued.RoutingNote, StringComparison.Ordinal);
        Assert.Contains(fixture.ProcessHost.Commands,
            command => command.StartsWith("set ftp:use-fxp false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningRemoteTransferCancellationStopsItsIsolatedProcessAndCancelsJob()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(source.Id), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(destination.Id), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/blocking-r2r.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Running,
            TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "User cancelled remote transfer"));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        Assert.Equal("User cancelled remote transfer", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Status);
        await WaitUntilAsync(
            () => fixture.ProcessHost.DisposedRoles.Contains("remote-transfer"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RemoteTransferExecutionFailureTransitionsJobToFailed()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(source.Id), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(destination.Id), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/failing-r2r.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Equal("lftp-job-failed", failed.Error?.Code);
        Assert.Contains("Permission denied", failed.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionedPipeExposesWorkspaceMethods()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        await using var host = new AgentHost(
            Path.Combine(fixture.Directory.Path, "jobs.json"),
            profileStore: fixture.Profiles,
            secretStore: fixture.Secrets,
            processHost: fixture.ProcessHost,
            runtimeProvider: fixture.Runtime,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: fixture.Options);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var saved = (await client.RequestAsync(WorkspaceMethods.ProfileSave, new ProfileSaveRequest(profile), TestContext.Current.CancellationToken))
            .Deserialize<ConnectionProfile>(FramedJsonStream.SerializerOptions);
        Assert.Equal(profile.Id, saved?.Id);
        var bootstrap = (await client.RequestAsync(WorkspaceMethods.Bootstrap, cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<WorkspaceBootstrap>(FramedJsonStream.SerializerOptions);
        Assert.Equal(AgentProtocol.CurrentVersion, bootstrap?.ProtocolVersion);
        Assert.True(bootstrap?.Runtime.Available);
        var directJob = JobCoordinatorTests.Job(JobState.Queued);
        var enqueueError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("jobs.enqueue", directJob, TestContext.Current.CancellationToken));
        Assert.Contains("Direct job creation is disabled", enqueueError.Message, StringComparison.Ordinal);
        var transitionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync("jobs.transition", new JobTransitionRequest(directJob.Id, JobState.Running), TestContext.Current.CancellationToken));
        Assert.Contains("Direct job mutation is disabled", transitionError.Message, StringComparison.Ordinal);
        var connected = (await client.RequestAsync(WorkspaceMethods.SessionConnect, new SessionConnectRequest(profile.Id), TestContext.Current.CancellationToken))
            .Deserialize<SessionSnapshot>(FramedJsonStream.SerializerOptions);
        Assert.True(connected?.IsConnected);
        var retryError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestAsync(WorkspaceMethods.JobRetry, new JobRetryRequest(Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Contains("not found", retryError.Message, StringComparison.OrdinalIgnoreCase);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VersionedPipePagesTenThousandUnicodeFileEntriesWithinFrameLimit()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        var directory = Path.Combine(fixture.Directory.Path, "ten-thousand");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 10_000; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var file = File.Create(Path.Combine(directory, $"曲-{index:D5}.txt"));
        }

        await using var host = new AgentHost(
            Path.Combine(fixture.Directory.Path, "paged-jobs.json"),
            profileStore: fixture.Profiles,
            secretStore: fixture.Secrets,
            processHost: fixture.ProcessHost,
            runtimeProvider: fixture.Runtime,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: fixture.Options);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        string? continuation = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        do
        {
            var element = await client.RequestAsync(WorkspaceMethods.BrowseLocal,
                new BrowseRequest(null, directory, ContinuationToken: continuation, PageSize: 1_000),
                TestContext.Current.CancellationToken);
            var page = element.Deserialize<BrowseResult>(FramedJsonStream.SerializerOptions)!;
            Assert.InRange(page.Entries.Length, 1, 1_000);
            Assert.Equal(10_000, page.TotalCount);
            foreach (var entry in page.Entries) Assert.True(names.Add(entry.Name));
            continuation = page.ContinuationToken;
            pages++;
        } while (continuation is not null);

        Assert.Equal(10_000, names.Count);
        Assert.True(pages > 1);
        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MsysPathConversionHandlesDriveUncAndInjection()
    {
        Assert.Equal("/c/Users/Alice/file.txt", LftpCommandBuilder.ToMsysPath(@"C:\Users\Alice\file.txt"));
        Assert.Equal("//server/share/file.txt", LftpCommandBuilder.ToMsysPath(@"\\server\share\file.txt"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.ToMsysPath("C:\\safe\n! calc"));
    }

    [Fact]
    public void ListingParserAcceptsFullReducedAndSymlinkLayouts()
    {
        var entries = LftpOutputParser.ParseLongListing([
            "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 full.txt",
            "-rw-r--r-- alice staff 7 2026-07-15 12:35 reduced.txt",
            "lrwxrwxrwx 4 2026-07-15 12:36 link@ -> target.txt",
        ], "/root");
        Assert.Equal(3, entries.Length);
        Assert.Equal("alice", entries[1].Owner);
        Assert.Equal(EntryKind.SymbolicLink, entries[2].Kind);
        Assert.Equal("target.txt", entries[2].LinkTarget);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The job did not finish.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The directory-junction test helper did not start.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"The directory-junction test helper failed: {process.StandardError.ReadToEnd()}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        public WorkspaceFixture(bool createService = true, TimeProvider? timeProvider = null)
        {
            Directory = new();
            Profiles = new();
            Secrets = new();
            ProcessHost = new();
            Runtime = new();
            Jobs = new();
            Store = new(Path.Combine(Directory.Path, "jobs.json"));
            Scheduler = new(Jobs, Store, timeProvider);
            Options = AgentWorkspaceOptions.CreateDefault(Directory.Path) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                BrowseTimeout = TimeSpan.FromSeconds(1),
                TransferTimeout = TimeSpan.FromSeconds(1),
                MirrorPreviewTimeout = TimeSpan.FromSeconds(1),
                ConsoleTimeout = TimeSpan.FromSeconds(1),
            };
            Service = createService
                ? new(Profiles, Secrets, ProcessHost, Runtime, Jobs, new MirrorPlanner(), Options, scheduler: Scheduler)
                : null!;
        }

        public TestDirectory Directory { get; }
        public MemoryProfileStore Profiles { get; }
        public MemorySecretStore Secrets { get; }
        public FakeProcessHost ProcessHost { get; }
        public FakeRuntimeProvider Runtime { get; }
        public JobCoordinator Jobs { get; }
        public DurableJobStore Store { get; }
        public RunOnceScheduler Scheduler { get; }
        public AgentWorkspaceOptions Options { get; }
        public AgentWorkspaceService Service { get; }

        public ConnectionProfile PasswordProfile(
            ConnectionProtocol protocol = ConnectionProtocol.Sftp,
            string name = "Secure",
            string host = "sftp.example") => new(
                Guid.NewGuid(), name, protocol, host, ProfileValidator.DefaultPort(protocol), "alice", AuthenticationKind.Password);

        public ConnectionProfile AnonymousProfile(ConnectionProtocol protocol) => new(
            Guid.NewGuid(), protocol.ToString(), protocol, "files.example", ProfileValidator.DefaultPort(protocol), "anonymous", AuthenticationKind.Anonymous);

        public async ValueTask DisposeAsync()
        {
            ProcessHost.ReleaseReservedQueueSlots.TrySetResult(true);
            ProcessHost.ReleaseTransferGate.TrySetResult(true);
            await Scheduler.DisposeAsync();
            if (Service is not null) await Service.DisposeAsync();
            Directory.Dispose();
        }
    }

    private sealed class MemoryProfileStore : IProfileStore
    {
        private readonly ConcurrentDictionary<Guid, ConnectionProfile> _profiles = [];
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
        }
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryRemove(profileId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = [];
        public Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[secret.Binding.CanonicalIdentity] = secret.Value;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.TryGetValue(binding.CanonicalIdentity, out var value) ? value : null);
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in Values.Keys.Where(key => key.StartsWith(profileId.ToString("N"), StringComparison.Ordinal))) Values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRuntimeProvider : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpRuntimeDescriptor(@"C:\fake", @"C:\fake\bin\lftp.exe", @"C:\fake\bin", false, "test", true));
        }
    }

    private sealed class FakeProcessHost : ILftpProcessHost
    {
        private int _nextId = 100;
        private readonly ConcurrentDictionary<string, int> _queueAttempts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _statAttempts = new(StringComparer.Ordinal);
        public ConcurrentBag<LftpProcessStartOptions> Starts { get; } = [];
        public ConcurrentBag<string> Commands { get; } = [];
        public ConcurrentBag<(string Role, string Command)> TaggedCommands { get; } = [];
        public ConcurrentBag<string> StoppedRoles { get; } = [];
        public ConcurrentBag<string> DisposedRoles { get; } = [];
        public TaskCompletionSource<bool> QueueSlotsFilled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReservedQueueSlots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TransferGateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseTransferGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ValidationGateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ScheduledValidationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? FinalDryRunAction { get; set; }
        public string? FailRolePrefix { get; set; }

        public Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRolePrefix is not null && options.Tag.StartsWith(FailRolePrefix, StringComparison.Ordinal))
                throw new System.ComponentModel.Win32Exception("simulated process launch failure");
            Starts.Add(options);
            return Task.FromResult<ILftpSession>(new FakeSession(
                Interlocked.Increment(ref _nextId), options.Tag, Commands, TaggedCommands, StoppedRoles, DisposedRoles,
                _queueAttempts, _statAttempts, QueueSlotsFilled, ReleaseReservedQueueSlots,
                TransferGateEntered, ReleaseTransferGate, ValidationGateEntered, ScheduledValidationEntered,
                () => FinalDryRunAction));
        }
    }

    private sealed class FakeSession(
        int processId,
        string role,
        ConcurrentBag<string> commands,
        ConcurrentBag<(string Role, string Command)> taggedCommands,
        ConcurrentBag<string> stoppedRoles,
        ConcurrentBag<string> disposedRoles,
        ConcurrentDictionary<string, int> queueAttempts,
        ConcurrentDictionary<string, int> statAttempts,
        TaskCompletionSource<bool> queueSlotsFilled,
        TaskCompletionSource<bool> releaseReservedQueueSlots,
        TaskCompletionSource<bool> transferGateEntered,
        TaskCompletionSource<bool> releaseTransferGate,
        TaskCompletionSource<bool> validationGateEntered,
        TaskCompletionSource<bool> scheduledValidationEntered,
        Func<Action?> finalDryRunAction) : ILftpSession
    {
        public int ProcessId { get; } = processId;
        public bool IsRunning { get; private set; } = true;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput;

        public Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning) return Task.FromResult(new LftpCommandResult([], Failure: "The LFTP session is not running."));
            commands.Add(command);
            taggedCommands.Add((role, command));
            if ((role == "transfer-queue" || role.StartsWith("transfer-policy-", StringComparison.Ordinal)) &&
                command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal))
            {
                var scheduledRetryAttempt = command.Contains("scheduled-retry-cancel.bin", StringComparison.Ordinal)
                    ? queueAttempts.AddOrUpdate("scheduled-retry-cancel.bin", 1, static (_, count) => count + 1)
                    : 0;
                if (command.Contains("slot-block-", StringComparison.Ordinal))
                {
                    if (queueAttempts.AddOrUpdate("slot-blockers", 1, static (_, count) => count + 1) == 2)
                        queueSlotsFilled.TrySetResult(true);
                    _ = EmitQueueMarkerAfterReleaseAsync(command, releaseReservedQueueSlots.Task, cancellationToken);
                }
                else if (!command.Contains("blocking.bin", StringComparison.Ordinal) && scheduledRetryAttempt != 2)
                {
                    var retryOnceFailed = command.Contains("retry-once.bin", StringComparison.Ordinal) &&
                        queueAttempts.AddOrUpdate("retry-once.bin", 1, static (_, count) => count + 1) == 1;
                    EmitQueueMarker(command,
                        command.Contains("failing-queue.bin", StringComparison.Ordinal) || retryOnceFailed || scheduledRetryAttempt == 1
                            ? "_FAILED"
                            : "_OK",
                        submission: false);
                }
                var submissionMarker = FindQueueMarker(command, "_SUBMIT_OK", submission: true)
                    ?? throw new InvalidOperationException("The test queue command did not contain a submission marker.");
                var submissionOutput = ImmutableArray.CreateBuilder<LftpOutputLine>();
                if (command.Contains("interleaved.bin", StringComparison.Ordinal))
                    submissionOutput.Add(new("stderr", "get: Access failed: Permission denied"));
                submissionOutput.Add(new("stdout", submissionMarker));
                return Task.FromResult(new LftpCommandResult(submissionOutput.ToImmutable()));
            }
            if (role == "remote-transfer" && command.Contains("blocking-r2r.bin", StringComparison.Ordinal))
                return WaitForCancellationAsync(cancellationToken);
            if (role == "transfer" && command.Contains("mirror", StringComparison.Ordinal) &&
                !command.Contains("--dry-run", StringComparison.Ordinal) &&
                command.Contains("/gate-blocker", StringComparison.Ordinal))
            {
                transferGateEntered.TrySetResult(true);
                return WaitForReleaseAsync(releaseTransferGate.Task, cancellationToken);
            }
            if (role == "remote-transfer" && command.Contains("failing-r2r.bin", StringComparison.Ordinal))
                return Task.FromResult(new LftpCommandResult([new("stderr", "get: Access failed: Permission denied")]));
            if (role == "transfer" && command.Contains("mirror", StringComparison.Ordinal) &&
                !command.Contains("--dry-run", StringComparison.Ordinal) &&
                command.Contains("retry-once.bin", StringComparison.Ordinal))
            {
                var failed = queueAttempts.AddOrUpdate("directory-retry-once.bin", 1, static (_, count) => count + 1) == 1;
                return Task.FromResult(failed
                    ? new LftpCommandResult([new("stderr", "mirror: Access failed: simulated directory transfer failure")])
                    : new LftpCommandResult([]));
            }
            if (role == "remote-edit-download" && command.StartsWith("get ", StringComparison.Ordinal))
            {
                WriteRemoteEditDownload(command);
                return Task.FromResult(new LftpCommandResult([]));
            }
            if (IsStatCommand(command) && command.Contains("/stat-drift-directory", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("stat-drift-directory", 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteDirectoryListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/cancel-validation.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("cancel-validation", 1, static (_, count) => count + 1);
                if (attempt >= 2) return WaitForCancellationAsync(cancellationToken);
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/validation-gate-one.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("validation-gate-one", 1, static (_, count) => count + 1);
                if (attempt >= 2)
                {
                    validationGateEntered.TrySetResult(true);
                    return WaitForCancellationAsync(cancellationToken);
                }
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/scheduled-validation-cancel.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("scheduled-validation-cancel", 1, static (_, count) => count + 1);
                if (attempt >= 2)
                {
                    scheduledValidationEntered.TrySetResult(true);
                    return WaitForCancellationAsync(cancellationToken);
                }
            }
            if (IsStatCommand(command) && command.Contains("\"/remote/file-slot-drift.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("file-slot-drift", 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteEditListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsStatCommand(command) &&
                (command.Contains("\"/remote/link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/directory-link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/mirror-link-ancestor\"", StringComparison.Ordinal)))
            {
                var key = command.Contains("directory-link-ancestor", StringComparison.Ordinal)
                    ? "directory-link-ancestor"
                    : command.Contains("mirror-link-ancestor", StringComparison.Ordinal)
                        ? "mirror-link-ancestor"
                        : "link-ancestor";
                var attempt = statAttempts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteDirectoryListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsStatCommand(command) &&
                (command.Contains("\"/remote/post-directory-link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/post-mirror-link-ancestor\"", StringComparison.Ordinal)))
            {
                var key = command.Contains("post-directory", StringComparison.Ordinal)
                    ? "post-directory-dry-run"
                    : "post-mirror-dry-run";
                var listing = statAttempts.GetOrAdd(key, 0) == 0
                    ? RemoteDirectoryListing(command)
                    : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsAutomaticDirectoryPreview(role, command) &&
                (command.Contains("/remote/retry-preview-target", StringComparison.Ordinal) ||
                 command.Contains("/remote/scheduled-preview-target", StringComparison.Ordinal)))
            {
                var key = command.Contains("retry-preview-target", StringComparison.Ordinal)
                    ? "retry-preview-target"
                    : "scheduled-preview-target";
                var attempt = statAttempts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                var destructiveAttempt = key == "retry-preview-target" ? 3 : 2;
                return Task.FromResult(attempt >= destructiveAttempt
                    ? new LftpCommandResult([new("stdout", "Removing old local file `collision'")])
                    : new LftpCommandResult([]));
            }
            if (IsAutomaticDirectoryPreview(role, command) && command.Contains("/guarded-drift-target", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("guarded-drift-target", 1, static (_, count) => count + 1);
                return Task.FromResult(attempt >= 2
                    ? new LftpCommandResult([new("stdout", "Removing old local file `collision'")])
                    : new LftpCommandResult([]));
            }
            if (IsAutomaticDirectoryPreview(role, command) &&
                command.Contains("/remote/post-directory-link-ancestor/source", StringComparison.Ordinal))
            {
                if (role == "transfer") statAttempts["post-directory-dry-run"] = 1;
                return Task.FromResult(new LftpCommandResult([]));
            }
            if (role == "transfer" && command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                command.Contains("/remote/post-mirror-link-ancestor/root", StringComparison.Ordinal))
            {
                statAttempts["post-mirror-dry-run"] = 1;
                return Task.FromResult(new LftpCommandResult([new("stdout", "Transferring file `new.txt'")]));
            }
            if (role == "transfer" && command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                command.Contains("/local-post-dryrun", StringComparison.Ordinal))
            {
                finalDryRunAction()?.Invoke();
                return Task.FromResult(new LftpCommandResult([new("stdout", "Transferring file `new.txt'")]));
            }
            ImmutableArray<LftpOutputLine> output = command switch
            {
                var value when value.Contains("mirror", StringComparison.Ordinal) &&
                    value.Contains("--dry-run", StringComparison.Ordinal) &&
                    value.Contains("dir-over-file-directory", StringComparison.Ordinal) =>
                    [new("stdout", "Removing old local file `collision'")],
                var value when value.Contains("mirror", StringComparison.Ordinal) &&
                    value.Contains("--dry-run", StringComparison.Ordinal) &&
                    value.Contains("unrecognized-destructive-directory", StringComparison.Ordinal) =>
                    [new("stdout", "Purging obsolete entry `collision'")],
                var value when IsAutomaticDirectoryPreview(role, value) => [],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
                    commands.Count(item => item.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) && item.Contains("/clean-collision-drift", StringComparison.Ordinal)) > 1 =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old local file `collision'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/clean-collision-drift", StringComparison.Ordinal) =>
                    [new("stdout", "Transferring file `new.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    (value.Contains("/gate-blocker", StringComparison.Ordinal) ||
                     value.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) ||
                     value.Contains("/remote/post-mirror-link-ancestor/root", StringComparison.Ordinal) ||
                     value.Contains("/local-post-dryrun", StringComparison.Ordinal) ||
                     value.Contains("/local-root-drift-", StringComparison.Ordinal)) =>
                    [new("stdout", "Transferring file `new.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/drift", StringComparison.Ordinal) &&
                    commands.Count(item => item.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) && item.Contains("/drift", StringComparison.Ordinal)) > 1 =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old file `different-old.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old file `old.txt'")],
                var value when value.StartsWith("cls -laB", StringComparison.Ordinal) =>
                    [new("stdout", "drwxr-xr-x 2 alice staff 0 2026-07-15 12:30 folder"), new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 曲.txt")],
                var value when value.StartsWith("cls -1 ", StringComparison.Ordinal) && value.Contains("missing.bin", StringComparison.Ordinal) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/missing-transfer-source", StringComparison.Ordinal) ||
                     value.Contains("/new-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/scheduled-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/retry-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/scheduled-preview-target", StringComparison.Ordinal) ||
                     value.Contains("/retry-preview-target", StringComparison.Ordinal) ||
                     value.Contains("/guarded-drift-target", StringComparison.Ordinal)) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/directory-transfer-source", StringComparison.Ordinal) ||
                     value.Contains("/directory-destination", StringComparison.Ordinal) ||
                     value.Contains("/dir-over-file-directory", StringComparison.Ordinal) ||
                     value.Contains("/unrecognized-destructive-directory", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/link-ancestor/file.bin\"", StringComparison.Ordinal) =>
                    [new("stdout", RemoteEditListing(value))],
                var value when IsStatCommand(value) &&
                    (value.Contains("\"/remote\"", StringComparison.Ordinal) ||
                     value.Contains("\"/collision-review\"", StringComparison.Ordinal) ||
                     value.Contains("\"/drift\"", StringComparison.Ordinal) ||
                     value.Contains("\"/clean-collision-drift\"", StringComparison.Ordinal) ||
                     value.Contains("\"/gate-blocker\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-root-drift-download\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-root-drift-upload\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-post-dryrun\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/directory-link-ancestor/source\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/mirror-link-ancestor/root\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/post-directory-link-ancestor/source\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/post-mirror-link-ancestor/root\"", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/transfer-link", StringComparison.Ordinal) =>
                    [new("stdout", RemoteSymbolicLinkListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/transfer-special", StringComparison.Ordinal) =>
                    [new("stdout", RemoteSpecialListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/mismatched-stat", StringComparison.Ordinal) =>
                    [new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 different-name.bin")],
                var value when IsStatCommand(value) &&
                    value.Contains("/zero-line-missing-target", StringComparison.Ordinal) => [],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/pathless-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/bound-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "recls: Access failed: 550 No such file or directory. (/remote/bound-missing-target)")],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/wrong-bound-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "recls: Access failed: 550 No such file or directory. (/remote/different-target)")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/created", StringComparison.Ordinal) || value.Contains("/renamed.txt", StringComparison.Ordinal) ||
                     value.Contains("/new-target.bin", StringComparison.Ordinal)) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/empty-dir", StringComparison.Ordinal) || value.Contains("/tree", StringComparison.Ordinal) ||
                     value.Contains("/source-folder", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) =>
                    [new("stdout", RemoteEditListing(value))],
                "pwd" => [new("stdout", "/remote/home")],
                _ => [],
            };
            return Task.FromResult(new LftpCommandResult(output));
        }

        private static void WriteRemoteEditDownload(string command)
        {
            const string marker = " -o \"";
            var start = command.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("The fake remote-edit download had no managed output path.");
            start += marker.Length;
            var end = command.IndexOf('"', start);
            if (end < 0) throw new InvalidOperationException("The fake remote-edit download output path was unterminated.");
            var msysPath = command[start..end];
            var localPath = msysPath.Length >= 3 && msysPath[0] == '/' && char.IsAsciiLetter(msysPath[1]) && msysPath[2] == '/'
                ? $"{char.ToUpperInvariant(msysPath[1])}:\\{msysPath[3..].Replace('/', '\\')}"
                : msysPath.Replace('/', Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllBytes(localPath, Encoding.UTF8.GetBytes("remote-bytes"));
        }

        private static bool IsStatCommand(string command) =>
            command.StartsWith("recls -ldB --time-style=long-iso", StringComparison.Ordinal) ||
            command.StartsWith("cls -ldB --time-style=long-iso", StringComparison.Ordinal);

        private static bool IsAutomaticDirectoryPreview(string role, string command) =>
            (role == "directory-transfer-preview" || role == "transfer") &&
            command.Contains("mirror", StringComparison.Ordinal) &&
            command.Contains("--dry-run", StringComparison.Ordinal) &&
            !command.Contains("--parallel=", StringComparison.Ordinal);

        private static string RemoteEditListing(string command) =>
            $"-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 {RemoteEntryName(command)}";

        private static string RemoteDirectoryListing(string command) =>
            $"drwxr-xr-x 2 alice staff 0 2026-07-15 12:30 {RemoteEntryName(command)}";

        private static string RemoteSymbolicLinkListing(string command) =>
            $"lrwxrwxrwx 1 alice staff 7 2026-07-15 12:30 {RemoteEntryName(command)} -> target";

        private static string RemoteSpecialListing(string command) =>
            $"prw-r--r-- 1 alice staff 0 2026-07-15 12:30 {RemoteEntryName(command)}";

        private static string RemoteEntryName(string command)
        {
            var end = command.LastIndexOf('"');
            var start = end > 0 ? command.LastIndexOf('"', end - 1) : -1;
            var remotePath = start >= 0 && end > start ? command[(start + 1)..end] : "/file.bin";
            var separator = remotePath.LastIndexOf('/');
            return separator >= 0 ? remotePath[(separator + 1)..] : remotePath;
        }

        private async Task<LftpCommandResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new([]);
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static async Task<LftpCommandResult> WaitForReleaseAsync(Task release, CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            return new([]);
        }

        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            stoppedRoles.Add(role);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            disposedRoles.Add(role);
            return ValueTask.CompletedTask;
        }

        public void Raise(LftpOutputLine line)
        {
            OutputReceived?.Invoke(this, line);
            UnsolicitedOutput?.Invoke(this, line);
        }

        private void EmitQueueMarker(string command, string suffix, bool submission)
        {
            var marker = FindQueueMarker(command, suffix, submission);
            if (marker is not null) OutputReceived?.Invoke(this, new("stdout", marker));
        }

        private async Task EmitQueueMarkerAfterReleaseAsync(string command, Task release, CancellationToken cancellationToken)
        {
            try
            {
                await release.WaitAsync(cancellationToken);
                EmitQueueMarker(command, "_OK", submission: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private static string? FindQueueMarker(string command, string suffix, bool submission)
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
            return null;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.WorkspaceTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true); }
    }
}
