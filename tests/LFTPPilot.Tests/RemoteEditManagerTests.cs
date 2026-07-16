using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class RemoteEditManagerTests
{
    private static readonly DateTimeOffset InitialModifiedAt = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartCreatesOpaqueAgentOwnedCopyFromStableBaseline()
    {
        await using var harness = new Harness();
        var content = Encoding.UTF8.GetBytes("native remote edit\n");
        harness.Transport.SetRemote("/reports/quarterly.txt", content, InitialModifiedAt);

        var session = await harness.StartAsync("/reports/quarterly.txt");

        Assert.Matches("^[0-9a-f]{48}$", session.EditId);
        Assert.Equal("quarterly.txt", session.DisplayName);
        Assert.Equal("/reports/quarterly.txt", session.RemotePath);
        Assert.Equal(CreateRemoteIdentity("/reports/quarterly.txt", content, InitialModifiedAt), session.Baseline);
        Assert.Equal(content, await File.ReadAllBytesAsync(session.LocalPath, TestContext.Current.CancellationToken));
        Assert.Equal(
            Path.Combine(session.EditId, "content.txt"),
            Path.GetRelativePath(harness.CacheRoot, session.LocalPath));
        Assert.StartsWith(harness.CacheRoot + Path.DirectorySeparatorChar, session.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, harness.Transport.StatCalls);
        var download = Assert.Single(harness.Transport.Downloads);
        Assert.Equal(session.SessionId, download.SessionId);
        Assert.Equal(session.RemotePath, download.RemotePath);
        Assert.Equal(Path.Combine(harness.CacheRoot, session.EditId, ".download"), download.LocalPath);
        Assert.Single(harness.Watchers.Watchers);
        Assert.Equal(1, harness.Manager.ActiveCount);
        Assert.Equal(session, Assert.IsType<RemoteEditSession>(Assert.Single(harness.Events, item => item.Name == "remoteEdit.started").Payload));
    }

    [Fact]
    public async Task StartRejectsRemoteDriftAndCleansPartialCache()
    {
        await using var harness = new Harness();
        var content = Encoding.UTF8.GetBytes("same size");
        var baseline = CreateRemoteIdentity("/drift.txt", content, InitialModifiedAt);
        var drifted = baseline with { ModifiedAt = InitialModifiedAt.AddSeconds(1) };
        harness.Transport.SetRemote("/drift.txt", content, InitialModifiedAt);
        harness.Transport.QueueStats(baseline, drifted);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => harness.StartAsync("/drift.txt"));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Manager.ActiveCount);
        Assert.Empty(harness.Watchers.Watchers);
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.CacheRoot));
    }

    [Fact]
    public async Task CoalescedSaveSignalRequiresReviewBeforeNormalUpload()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/draft.txt", Encoding.UTF8.GetBytes("draft one"), InitialModifiedAt);
        var session = await harness.StartAsync("/draft.txt");
        var edited = Encoding.UTF8.GetBytes("draft two");
        await File.WriteAllBytesAsync(session.LocalPath, edited, TestContext.Current.CancellationToken);

        var watcher = Assert.Single(harness.Watchers.Watchers);
        watcher.SignalCoalescedBatch(rawNotificationCount: 4);

        Assert.Equal(4, watcher.RawNotificationCount);
        var localEvent = Assert.IsType<RemoteEditLocalChange>(
            Assert.Single(harness.Events, item => item.Name == "remoteEdit.localChanged").Payload);
        Assert.Equal(RemoteEditLocalChangeKind.Saved, localEvent.Kind);
        Assert.Contains("No upload", localEvent.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.Uploads);
        var dirtySnapshot = Assert.Single(harness.Manager.GetSnapshots());
        Assert.True(dirtySnapshot.Dirty);
        Assert.False(dirtySnapshot.WatcherFailed);
        Assert.Equal(localEvent.DetectedAt, dirtySnapshot.LastLocalChangeAt);

        var review = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditReviewState.ReadyToUpload, review.State);
        Assert.Equal(RemoteEditConflictKind.None, review.Conflict);

        var result = await harness.Manager.ResolveAsync(
            new(session.EditId, review.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.Uploaded, result.Outcome);
        Assert.Equal(edited, harness.Transport.Content);
        var upload = Assert.Single(harness.Transport.Uploads);
        Assert.Equal("/draft.txt", upload.RemotePath);
        Assert.StartsWith(Path.GetDirectoryName(session.LocalPath)!, upload.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".upload-", Path.GetFileName(upload.LocalPath), StringComparison.Ordinal);
        Assert.False(File.Exists(upload.LocalPath));
        Assert.Equal(edited.LongLength, result.Session.Baseline.Size);
        Assert.False(result.Session.Dirty);
        Assert.Equal(localEvent.DetectedAt, result.Session.LastLocalChangeAt);
    }

    [Fact]
    public async Task SameLengthSameTimestampMutationAfterReviewForcesFreshReview()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/hash.txt", Encoding.UTF8.GetBytes("AAAA"), InitialModifiedAt);
        var session = await harness.StartAsync("/hash.txt");
        var firstReview = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        var originalWriteTime = File.GetLastWriteTimeUtc(session.LocalPath);

        await File.WriteAllBytesAsync(session.LocalPath, Encoding.UTF8.GetBytes("BBBB"), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(session.LocalPath, originalWriteTime);

        var staleResolution = await harness.Manager.ResolveAsync(
            new(session.EditId, firstReview.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.ReviewRequired, staleResolution.Outcome);
        Assert.NotNull(staleResolution.Review);
        Assert.NotEqual(firstReview.ReviewToken, staleResolution.Review.ReviewToken);
        Assert.Contains("changed after review", staleResolution.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Transport.Uploads);

        var approvedResolution = await harness.Manager.ResolveAsync(
            new(session.EditId, staleResolution.Review.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.Uploaded, approvedResolution.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes("BBBB"), harness.Transport.Content);
    }

    [Fact]
    public async Task RemoteChangeBlocksNormalUploadButReviewedOverwriteSucceeds()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/shared.txt", Encoding.UTF8.GetBytes("base"), InitialModifiedAt);
        var session = await harness.StartAsync("/shared.txt");
        await File.WriteAllTextAsync(session.LocalPath, "mine", TestContext.Current.CancellationToken);
        harness.Transport.SetRemote("/shared.txt", Encoding.UTF8.GetBytes("theirs"), InitialModifiedAt.AddMinutes(1));

        var conflict = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditReviewState.Conflict, conflict.State);
        Assert.Equal(RemoteEditConflictKind.RemoteChanged, conflict.Conflict);

        var normalUpload = await harness.Manager.ResolveAsync(
            new(session.EditId, conflict.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.ReviewRequired, normalUpload.Outcome);
        Assert.Empty(harness.Transport.Uploads);

        var overwrite = await harness.Manager.ResolveAsync(
            new(session.EditId, normalUpload.Review!.ReviewToken, RemoteEditResolution.Overwrite),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.Uploaded, overwrite.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes("mine"), harness.Transport.Content);
        Assert.Single(harness.Transport.Uploads);
    }

    [Fact]
    public async Task MissingRemoteBlocksNormalUploadButReviewedOverwriteRecreatesIt()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/missing.txt", Encoding.UTF8.GetBytes("base"), InitialModifiedAt);
        var session = await harness.StartAsync("/missing.txt");
        await File.WriteAllTextAsync(session.LocalPath, "replacement", TestContext.Current.CancellationToken);
        harness.Transport.RemoveRemote();

        var conflict = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditConflictKind.RemoteMissingOrRenamed, conflict.Conflict);
        Assert.Null(conflict.Current);

        var normalUpload = await harness.Manager.ResolveAsync(
            new(session.EditId, conflict.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.ReviewRequired, normalUpload.Outcome);
        Assert.Empty(harness.Transport.Uploads);

        var overwrite = await harness.Manager.ResolveAsync(
            new(session.EditId, normalUpload.Review!.ReviewToken, RemoteEditResolution.Overwrite),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.Uploaded, overwrite.Outcome);
        Assert.Equal(Encoding.UTF8.GetBytes("replacement"), harness.Transport.Content);
        Assert.NotNull(harness.Transport.Identity);
    }

    [Fact]
    public async Task RefreshLocalReplacesManagedCopyAndAdvancesBaselineWithoutUpload()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/refresh.json", Encoding.UTF8.GetBytes("{\"v\":1}"), InitialModifiedAt);
        var session = await harness.StartAsync("/refresh.json");
        await File.WriteAllTextAsync(session.LocalPath, "local edits", TestContext.Current.CancellationToken);
        var remoteContent = Encoding.UTF8.GetBytes("{\"v\":2}");
        var changedAt = InitialModifiedAt.AddMinutes(2);
        harness.Transport.SetRemote("/refresh.json", remoteContent, changedAt);

        var review = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditConflictKind.RemoteChanged, review.Conflict);

        var result = await harness.Manager.ResolveAsync(
            new(session.EditId, review.ReviewToken, RemoteEditResolution.RefreshLocal),
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.Refreshed, result.Outcome);
        Assert.Equal(remoteContent, await File.ReadAllBytesAsync(session.LocalPath, TestContext.Current.CancellationToken));
        Assert.Equal(CreateRemoteIdentity("/refresh.json", remoteContent, changedAt), result.Session.Baseline);
        Assert.Equal(2, harness.Transport.Downloads.Count);
        Assert.Empty(harness.Transport.Uploads);

        var watcher = Assert.Single(harness.Watchers.Watchers);
        watcher.SignalCoalescedBatch(rawNotificationCount: 2);
        Assert.DoesNotContain(harness.Events, item => item.Name == "remoteEdit.localChanged");
        Assert.False(Assert.Single(harness.Manager.GetSnapshots()).Dirty);

        await File.WriteAllTextAsync(session.LocalPath, "{\"v\":3}", TestContext.Current.CancellationToken);
        watcher.SignalCoalescedBatch(rawNotificationCount: 1);
        var changed = Assert.IsType<RemoteEditLocalChange>(
            Assert.Single(harness.Events, item => item.Name == "remoteEdit.localChanged").Payload);
        Assert.Equal(RemoteEditLocalChangeKind.Saved, changed.Kind);
        Assert.True(Assert.Single(harness.Manager.GetSnapshots()).Dirty);
    }

    [Fact]
    public async Task UntrustworthyRemoteIdentityBlocksEveryUploadResolution()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/identity.txt", Encoding.UTF8.GetBytes("base"), InitialModifiedAt);
        var session = await harness.StartAsync("/identity.txt");
        await File.WriteAllTextAsync(session.LocalPath, "mine", TestContext.Current.CancellationToken);
        harness.Transport.Identity = CreateRemoteIdentity(
            "/different.txt", Encoding.UTF8.GetBytes("mine"), InitialModifiedAt.AddMinutes(1));

        var review = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditReviewState.Conflict, review.State);
        Assert.Equal(RemoteEditConflictKind.RemoteIdentityUnavailable, review.Conflict);

        foreach (var resolution in new[] { RemoteEditResolution.Upload, RemoteEditResolution.Overwrite, RemoteEditResolution.RefreshLocal })
        {
            var result = await harness.Manager.ResolveAsync(
                new(session.EditId, review.ReviewToken, resolution),
                TestContext.Current.CancellationToken);
            Assert.Equal(RemoteEditActionOutcome.ReviewRequired, result.Outcome);
            Assert.Equal(RemoteEditConflictKind.RemoteIdentityUnavailable, result.Review!.Conflict);
            review = result.Review;
        }
        Assert.Empty(harness.Transport.Uploads);
    }

    [Fact]
    public async Task AggregateCacheLimitBlocksReviewAndWatcherReportsTooLarge()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/aggregate.txt", Encoding.UTF8.GetBytes("tiny"), InitialModifiedAt);
        var session = await harness.StartAsync("/aggregate.txt");
        var ballastPath = Path.Combine(Path.GetDirectoryName(session.LocalPath)!, "aggregate-limit.ballast");
        await using (var ballast = new FileStream(ballastPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            ballast.SetLength(RemoteEditManager.MaximumManagedCacheBytes);
        }

        Assert.Single(harness.Watchers.Watchers).SignalCoalescedBatch(rawNotificationCount: 2);
        var localEvent = Assert.IsType<RemoteEditLocalChange>(
            Assert.Single(harness.Events, item => item.Name == "remoteEdit.localChanged").Payload);
        Assert.Equal(RemoteEditLocalChangeKind.TooLarge, localEvent.Kind);

        var review = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditReviewState.Conflict, review.State);
        Assert.Equal(RemoteEditConflictKind.ManagedCacheLimitExceeded, review.Conflict);
        Assert.Empty(harness.Transport.Uploads);
    }

    [Fact]
    public async Task WatcherFailurePublishesStructuredErrorAndInvalidatesPendingReview()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/watched.txt", Encoding.UTF8.GetBytes("server"), InitialModifiedAt);
        var session = await harness.StartAsync("/watched.txt");
        await File.WriteAllTextAsync(session.LocalPath, "editor", TestContext.Current.CancellationToken);
        var review = await harness.Manager.ReviewAsync(
            new(session.EditId), TestContext.Current.CancellationToken);

        Assert.Single(harness.Watchers.Watchers).Fail();

        var localEvent = Assert.IsType<RemoteEditLocalChange>(
            Assert.Single(harness.Events, item => item.Name == "remoteEdit.localChanged").Payload);
        Assert.Equal(RemoteEditLocalChangeKind.WatcherError, localEvent.Kind);
        Assert.Contains("failed", localEvent.Message, StringComparison.OrdinalIgnoreCase);
        var failedSnapshot = Assert.Single(harness.Manager.GetSnapshots());
        Assert.True(failedSnapshot.Dirty);
        Assert.True(failedSnapshot.WatcherFailed);
        Assert.Equal(localEvent.DetectedAt, failedSnapshot.LastLocalChangeAt);
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new(true, true, "test"),
            [],
            [],
            [],
            [failedSnapshot]);
        var restored = JsonSerializer.Deserialize<WorkspaceBootstrap>(
            JsonSerializer.Serialize(bootstrap, FramedJsonStream.SerializerOptions),
            FramedJsonStream.SerializerOptions)!;
        var restoredEdit = Assert.Single(restored.RemoteEdits);
        Assert.True(restoredEdit.Dirty);
        Assert.True(restoredEdit.WatcherFailed);
        Assert.Equal(localEvent.DetectedAt, restoredEdit.LastLocalChangeAt);

        var staleResolution = await harness.Manager.ResolveAsync(
            new(session.EditId, review.ReviewToken, RemoteEditResolution.Upload),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditActionOutcome.ReviewRequired, staleResolution.Outcome);
        Assert.NotNull(staleResolution.Review);
        Assert.NotEqual(review.ReviewToken, staleResolution.Review.ReviewToken);
        Assert.Empty(harness.Transport.Uploads);
    }

    [Fact]
    public async Task FailedCacheDeletionRetainsRegistrationAndRecreatesWatcherForRetry()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/locked.txt", Encoding.UTF8.GetBytes("locked"), InitialModifiedAt);
        var session = await harness.StartAsync("/locked.txt");
        var originalWatcher = Assert.Single(harness.Watchers.Watchers);
        Assert.True(harness.Manager.HasActiveSession(session.SessionId));

        await using (var locked = new FileStream(
            session.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => harness.Manager.CompleteAsync(
                new(session.EditId), TestContext.Current.CancellationToken));

            Assert.True(originalWatcher.IsDisposed);
            Assert.Equal(2, harness.Watchers.Watchers.Count);
            Assert.False(harness.Watchers.Watchers[1].IsDisposed);
            Assert.Equal(1, harness.Manager.ActiveCount);
            Assert.True(harness.Manager.HasActiveSession(session.SessionId));
            Assert.Equal(session.EditId, Assert.Single(harness.Manager.GetSnapshots()).EditId);
            Assert.True(File.Exists(session.LocalPath));
        }

        Assert.True(await harness.Manager.CompleteAsync(
            new(session.EditId), TestContext.Current.CancellationToken));
        Assert.True(harness.Watchers.Watchers[1].IsDisposed);
        Assert.Equal(0, harness.Manager.ActiveCount);
        Assert.False(harness.Manager.HasActiveSession(session.SessionId));
        Assert.False(Directory.Exists(Path.GetDirectoryName(session.LocalPath)));
    }

    [Fact]
    public async Task CompleteAndDisposeRemoveWatchersRegistrationsAndOwnedCache()
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/one.txt", Encoding.UTF8.GetBytes("one"), InitialModifiedAt);
        var first = await harness.StartAsync("/one.txt");
        var firstWatcher = Assert.Single(harness.Watchers.Watchers);

        Assert.True(await harness.Manager.CompleteAsync(
            new(first.EditId), TestContext.Current.CancellationToken));
        Assert.True(firstWatcher.IsDisposed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(first.LocalPath)));
        Assert.Equal(0, harness.Manager.ActiveCount);
        Assert.False(await harness.Manager.CompleteAsync(
            new(first.EditId), TestContext.Current.CancellationToken));

        harness.Transport.SetRemote("/two.txt", Encoding.UTF8.GetBytes("two"), InitialModifiedAt.AddMinutes(1));
        var second = await harness.StartAsync("/two.txt");
        var secondWatcher = harness.Watchers.Watchers[1];
        await harness.Manager.DisposeAsync();

        Assert.True(secondWatcher.IsDisposed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(second.LocalPath)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.CacheRoot));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.StartAsync("/two.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("relative.txt")]
    [InlineData("/folder//file.txt")]
    [InlineData("/folder/../file.txt")]
    [InlineData("/folder/./file.txt")]
    [InlineData("/folder/file.txt\rcommand")]
    public async Task StartRejectsNonCanonicalOrCommandBearingRemotePaths(string remotePath)
    {
        await using var harness = new Harness();
        harness.Transport.SetRemote("/valid.txt", Encoding.UTF8.GetBytes("valid"), InitialModifiedAt);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.StartAsync(remotePath));

        Assert.Empty(harness.Transport.Downloads);
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.CacheRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("0123456789abcdef")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    [InlineData("0000000000000000000000000000000/0")]
    public async Task ReviewAndCompleteRejectUnsafeOpaqueIdentifiers(string editId)
    {
        await using var harness = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Manager.ReviewAsync(
            new(editId), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await harness.Manager.CompleteAsync(
            new(editId), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(harness.CacheRoot));
    }

    [Fact]
    public async Task ReparsePointCacheRootAndChildAreRejectedWithoutFollowingTargets()
    {
        var outer = Path.Combine(Path.GetTempPath(), "LFTPPilot.RemoteEdit.Reparse", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(outer, "target");
        var linkedRoot = Path.Combine(outer, "linked-root");
        Directory.CreateDirectory(target);
        CreateDirectoryJunction(linkedRoot, target);
        try
        {
            Assert.Throws<IOException>(() => new RemoteEditManager(linkedRoot, new FakeTransport()));
        }
        finally
        {
            Directory.Delete(linkedRoot, recursive: false);
            Directory.Delete(target, recursive: false);
            Directory.Delete(outer, recursive: false);
        }

        await using var harness = new Harness();
        harness.Transport.SetRemote("/safe.txt", Encoding.UTF8.GetBytes("safe"), InitialModifiedAt);
        var session = await harness.StartAsync("/safe.txt");
        var externalTarget = Path.Combine(Path.GetTempPath(), "LFTPPilot.RemoteEdit.External", Guid.NewGuid().ToString("N"));
        var unsafeChild = Path.Combine(harness.CacheRoot, "unsafe-link");
        Directory.CreateDirectory(externalTarget);
        await File.WriteAllTextAsync(Path.Combine(externalTarget, "sentinel.txt"), "must survive", TestContext.Current.CancellationToken);
        CreateDirectoryJunction(unsafeChild, externalTarget);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => harness.Manager.ReviewAsync(
                new(session.EditId), TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(externalTarget, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(unsafeChild, recursive: false);
            Directory.Delete(externalTarget, recursive: true);
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

    private static RemoteFileIdentity CreateRemoteIdentity(string path, byte[] content, DateTimeOffset modifiedAt) =>
        new(path, content.LongLength, modifiedAt, Convert.ToHexString(SHA256.HashData(content)));

    private sealed class Harness : IAsyncDisposable
    {
        private static readonly Guid SessionId = new("a583e7e6-9b9d-4e4e-bd57-5a72b403f28a");

        public Harness()
        {
            CacheRoot = Path.Combine(Path.GetTempPath(), "LFTPPilot.RemoteEdit.Tests", Guid.NewGuid().ToString("N"));
            Transport = new();
            Watchers = new();
            Manager = new(CacheRoot, Transport, Watchers, Publish);
        }

        public string CacheRoot { get; }
        public FakeTransport Transport { get; }
        public FakeWatcherFactory Watchers { get; }
        public RemoteEditManager Manager { get; }
        public List<PublishedEvent> Events { get; } = [];

        public Task<RemoteEditSession> StartAsync(string remotePath) => Manager.StartAsync(
            new(SessionId, remotePath), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            if (Directory.Exists(CacheRoot)) Directory.Delete(CacheRoot, recursive: true);
        }

        private void Publish(EngineEventKind kind, string name, object? payload, Guid? jobId, Guid? sessionId) =>
            Events.Add(new(kind, name, payload, jobId, sessionId));
    }

    private sealed class FakeTransport : IRemoteEditTransport
    {
        private readonly Queue<RemoteFileIdentity?> _statResults = new();

        public RemoteFileIdentity? Identity { get; set; }
        public byte[] Content { get; private set; } = [];
        public int StatCalls { get; private set; }
        public List<TransferCall> Downloads { get; } = [];
        public List<TransferCall> Uploads { get; } = [];

        public void SetRemote(string remotePath, byte[] content, DateTimeOffset modifiedAt)
        {
            Content = [.. content];
            Identity = CreateRemoteIdentity(remotePath, content, modifiedAt);
        }

        public void RemoveRemote() => Identity = null;

        public void QueueStats(params RemoteFileIdentity?[] identities)
        {
            foreach (var identity in identities) _statResults.Enqueue(identity);
        }

        public Task<RemoteFileIdentity?> StatAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatCalls++;
            return Task.FromResult(_statResults.Count > 0 ? _statResults.Dequeue() : Identity);
        }

        public async Task DownloadAsync(Guid sessionId, string remotePath, string managedLocalPath, CancellationToken cancellationToken)
        {
            Downloads.Add(new(sessionId, remotePath, managedLocalPath));
            await File.WriteAllBytesAsync(managedLocalPath, Content, cancellationToken);
        }

        public async Task<RemoteEditCommitResult> CommitUploadAsync(
            Guid sessionId,
            string managedLocalPath,
            string remotePath,
            RemoteFileIdentity? reviewedIdentity,
            CancellationToken cancellationToken)
        {
            Uploads.Add(new(sessionId, remotePath, managedLocalPath));
            Content = await File.ReadAllBytesAsync(managedLocalPath, cancellationToken);
            Identity = CreateRemoteIdentity(
                remotePath,
                Content,
                (Identity?.ModifiedAt ?? InitialModifiedAt).AddMinutes(1));
            return new(true, Identity, "Committed by the deterministic fake transport.");
        }
    }

    private sealed class FakeWatcherFactory : IRemoteEditWatcherFactory
    {
        public List<FakeWatcher> Watchers { get; } = [];

        public IRemoteEditWatcher Create(string directoryPath, string fileName, Action signal, Action failure)
        {
            var watcher = new FakeWatcher(directoryPath, fileName, signal, failure);
            Watchers.Add(watcher);
            return watcher;
        }
    }

    private sealed class FakeWatcher(
        string directoryPath,
        string fileName,
        Action signal,
        Action failure) : IRemoteEditWatcher
    {
        public string DirectoryPath { get; } = directoryPath;
        public string FileName { get; } = fileName;
        public bool IsDisposed { get; private set; }
        public int RawNotificationCount { get; private set; }

        public void SignalCoalescedBatch(int rawNotificationCount)
        {
            Assert.False(IsDisposed);
            RawNotificationCount += rawNotificationCount;
            signal();
        }

        public void Fail()
        {
            Assert.False(IsDisposed);
            failure();
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed record PublishedEvent(
        EngineEventKind Kind,
        string Name,
        object? Payload,
        Guid? JobId,
        Guid? SessionId);

    private sealed record TransferCall(Guid SessionId, string RemotePath, string LocalPath);
}
