using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class RemoteEditTests
{
    private static readonly Guid SessionId = Guid.Parse("4b35fd22-e7bb-4e21-94cc-c0a3bd687d0d");
    private static readonly DateTimeOffset BaselineTime = new(2026, 7, 15, 12, 34, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartUsesOpaqueAgentOwnedPathAndFailsClosedOnDownloadDrift()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "remote-edits");
        var transport = new FakeTransport();
        transport.Set("/docs/note.txt", "remote", BaselineTime);
        var watchers = new FakeWatcherFactory();
        await using var manager = new RemoteEditManager(root, transport, watchers);

        var edit = await manager.StartAsync(new(SessionId, "/docs/note.txt"), TestContext.Current.CancellationToken);

        Assert.Equal(48, edit.EditId.Length);
        Assert.All(edit.EditId, character => Assert.True(char.IsAsciiHexDigit(character)));
        Assert.DoesNotContain("docs", edit.EditId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("note.txt", edit.DisplayName);
        Assert.Equal("content.txt", Path.GetFileName(edit.LocalPath));
        Assert.True(File.Exists(edit.LocalPath));
        AssertOwnedBy(root, edit.LocalPath);
        Assert.Equal("remote", await File.ReadAllTextAsync(edit.LocalPath, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(
            new(SessionId, "/docs/note.txt"), TestContext.Current.CancellationToken));

        transport.Set("/docs/drift.txt", "before", BaselineTime);
        transport.ChangeAfterDownload("/docs/drift.txt", "after!", BaselineTime.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.StartAsync(
            new(SessionId, "/docs/drift.txt"), TestContext.Current.CancellationToken));
        Assert.Single(Directory.EnumerateDirectories(root));
    }

    [Fact]
    public async Task WatcherSaveNeverUploadsAndReviewedNormalSaveDoes()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/note.txt", "remote", BaselineTime);
        var watchers = new FakeWatcherFactory();
        var events = new ConcurrentQueue<(string Name, object? Payload)>();
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, watchers,
            (kind, name, payload, jobId, sessionId) => events.Enqueue((name, payload)));
        var edit = await manager.StartAsync(new(SessionId, "/note.txt"), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(edit.LocalPath, "local-save", TestContext.Current.CancellationToken);
        watchers.Single.Signal();

        Assert.Equal(0, transport.UploadCount);
        var changed = Assert.IsType<RemoteEditLocalChange>(events.Last(item => item.Name == "remoteEdit.localChanged").Payload);
        Assert.Equal(RemoteEditLocalChangeKind.Saved, changed.Kind);
        var review = await manager.ReviewAsync(new(edit.EditId), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteEditReviewState.ReadyToUpload, review.State);

        var result = await manager.ResolveAsync(new(edit.EditId, review.ReviewToken, RemoteEditResolution.Upload), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.Uploaded, result.Outcome);
        Assert.Equal(1, transport.UploadCount);
        Assert.Equal("local-save", transport.Text("/note.txt"));
        Assert.Equal(10, result.Session.Baseline.Size);
    }

    [Fact]
    public async Task SameLengthLocalChangeAfterReviewInvalidatesReviewToken()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/same.txt", "aaaaaa", BaselineTime);
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, new FakeWatcherFactory());
        var edit = await manager.StartAsync(new(SessionId, "/same.txt"), TestContext.Current.CancellationToken);
        var originalWriteTime = File.GetLastWriteTimeUtc(edit.LocalPath);
        var review = await manager.ReviewAsync(new(edit.EditId), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(edit.LocalPath, "bbbbbb", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(edit.LocalPath, originalWriteTime);
        var result = await manager.ResolveAsync(new(edit.EditId, review.ReviewToken, RemoteEditResolution.Upload), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.ReviewRequired, result.Outcome);
        Assert.NotNull(result.Review);
        Assert.Equal(0, transport.UploadCount);
        Assert.Equal("aaaaaa", transport.Text("/same.txt"));
    }

    [Fact]
    public async Task ChangedAndMissingRemoteFilesReturnStructuredConflicts()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/changed.txt", "original", BaselineTime);
        transport.Set("/missing.txt", "original", BaselineTime);
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, new FakeWatcherFactory());
        var changedEdit = await manager.StartAsync(new(SessionId, "/changed.txt"), TestContext.Current.CancellationToken);
        var missingEdit = await manager.StartAsync(new(SessionId, "/missing.txt"), TestContext.Current.CancellationToken);

        transport.Set("/changed.txt", "changed!", BaselineTime.AddMinutes(1));
        transport.Remove("/missing.txt");
        var changed = await manager.ReviewAsync(new(changedEdit.EditId), TestContext.Current.CancellationToken);
        var missing = await manager.ReviewAsync(new(missingEdit.EditId), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditConflictKind.RemoteChanged, changed.Conflict);
        Assert.Equal(RemoteEditConflictKind.RemoteMissingOrRenamed, missing.Conflict);
        Assert.NotNull(changed.Current);
        Assert.Null(missing.Current);
        Assert.Equal(0, transport.UploadCount);
    }

    [Fact]
    public async Task ReviewedRemoteConflictUploadsOnlyWithExplicitOverwrite()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/conflict.txt", "server-a", BaselineTime);
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, new FakeWatcherFactory());
        var edit = await manager.StartAsync(new(SessionId, "/conflict.txt"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(edit.LocalPath, "my-local", TestContext.Current.CancellationToken);
        transport.Set("/conflict.txt", "server-b", BaselineTime.AddMinutes(1));
        var review = await manager.ReviewAsync(new(edit.EditId), TestContext.Current.CancellationToken);

        var result = await manager.ResolveAsync(new(edit.EditId, review.ReviewToken, RemoteEditResolution.Overwrite), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.Uploaded, result.Outcome);
        Assert.Equal("my-local", transport.Text("/conflict.txt"));
        Assert.Equal(1, transport.UploadCount);
    }

    [Fact]
    public async Task RefreshReplacesManagedCopyAndRefreshesBaseline()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/refresh.txt", "server-a", BaselineTime);
        var watchers = new FakeWatcherFactory();
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, watchers);
        var edit = await manager.StartAsync(new(SessionId, "/refresh.txt"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(edit.LocalPath, "local", TestContext.Current.CancellationToken);
        transport.Set("/refresh.txt", "server-new", BaselineTime.AddMinutes(1));
        var review = await manager.ReviewAsync(new(edit.EditId), TestContext.Current.CancellationToken);

        var result = await manager.ResolveAsync(new(edit.EditId, review.ReviewToken, RemoteEditResolution.RefreshLocal), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditActionOutcome.Refreshed, result.Outcome);
        Assert.Equal("server-new", await File.ReadAllTextAsync(edit.LocalPath, TestContext.Current.CancellationToken));
        Assert.Equal(BaselineTime.AddMinutes(1), result.Session.Baseline.ModifiedAt);
        Assert.Equal(0, transport.UploadCount);
    }

    [Fact]
    public async Task CompletionAndShutdownDisposeWatchersAndCleanOwnedCache()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Path, "cache");
        var orphan = Path.Combine(root, "orphan");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "old.txt"), "old", TestContext.Current.CancellationToken);
        var transport = new FakeTransport();
        transport.Set("/one.txt", "one", BaselineTime);
        transport.Set("/two.txt", "two", BaselineTime);
        var watchers = new FakeWatcherFactory();
        var manager = new RemoteEditManager(root, transport, watchers);
        Assert.False(Directory.Exists(orphan));
        var one = await manager.StartAsync(new(SessionId, "/one.txt"), TestContext.Current.CancellationToken);
        var oneWatcher = watchers.Watchers[0];

        Assert.True(await manager.CompleteAsync(new(one.EditId), TestContext.Current.CancellationToken));
        Assert.True(oneWatcher.Disposed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(one.LocalPath)));

        var two = await manager.StartAsync(new(SessionId, "/two.txt"), TestContext.Current.CancellationToken);
        var twoWatcher = watchers.Watchers[1];
        await manager.DisposeAsync();
        Assert.True(twoWatcher.Disposed);
        Assert.False(Directory.Exists(Path.GetDirectoryName(two.LocalPath)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task AggregateManagedCacheBoundBlocksAdditionalEdit()
    {
        using var directory = new TestDirectory();
        var transport = new FakeTransport();
        transport.Set("/large-a.bin", [], BaselineTime, RemoteEditManager.MaximumRemoteEditBytes);
        transport.Set("/large-b.bin", [], BaselineTime, RemoteEditManager.MaximumRemoteEditBytes);
        transport.Set("/one-too-many.bin", [0x42], BaselineTime);
        await using var manager = new RemoteEditManager(Path.Combine(directory.Path, "cache"), transport, new FakeWatcherFactory());

        _ = await manager.StartAsync(new(SessionId, "/large-a.bin"), TestContext.Current.CancellationToken);
        _ = await manager.StartAsync(new(SessionId, "/large-b.bin"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<IOException>(() => manager.StartAsync(
            new(SessionId, "/one-too-many.bin"), TestContext.Current.CancellationToken));
        Assert.Contains("cache size limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, manager.ActiveCount);
    }

    [Fact]
    public void RemoteEditCommandsQuoteValidatedRemoteAndManagedPaths()
    {
        var download = LftpCommandBuilder.BuildRemoteEditDownload("/docs/a \"quote\".txt", @"C:\managed cache\content.txt");
        var upload = LftpCommandBuilder.BuildRemoteEditUpload(@"C:\managed cache\content.txt", "/docs/a \"quote\".txt");

        Assert.Equal("get \"/docs/a \\\"quote\\\".txt\" -o \"/c/managed cache/content.txt\"", download);
        Assert.Equal("put \"/c/managed cache/content.txt\" -o \"/docs/a \\\"quote\\\".txt\"", upload);
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildRemoteEditDownload("../relative", @"C:\cache\file"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildRemoteEditUpload("relative", "/file"));
    }

    private static void AssertOwnedBy(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        Assert.False(Path.IsPathRooted(relative));
        Assert.False(relative.StartsWith("..", StringComparison.Ordinal));
    }

    private sealed class FakeTransport : IRemoteEditTransport
    {
        private readonly ConcurrentDictionary<string, RemoteState> _states = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RemoteState> _afterDownload = new(StringComparer.Ordinal);
        private int _uploadCount;

        public int UploadCount => Volatile.Read(ref _uploadCount);

        public void Set(string path, string content, DateTimeOffset modifiedAt) => Set(path, Encoding.UTF8.GetBytes(content), modifiedAt);

        public void Set(string path, byte[] content, DateTimeOffset modifiedAt, long? declaredSize = null) =>
            _states[path] = RemoteState.Create(content, declaredSize ?? content.LongLength, modifiedAt);

        public void Remove(string path) => _states.TryRemove(path, out _);

        public string Text(string path) => Encoding.UTF8.GetString(_states[path].Content);

        public void ChangeAfterDownload(string path, string content, DateTimeOffset modifiedAt) =>
            _afterDownload[path] = RemoteState.Create(Encoding.UTF8.GetBytes(content), Encoding.UTF8.GetByteCount(content), modifiedAt);

        public Task<RemoteFileIdentity?> StatAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_states.TryGetValue(remotePath, out var state)
                ? new RemoteFileIdentity(remotePath, state.Size, state.ModifiedAt, state.Sha256)
                : null);
        }

        public async Task DownloadAsync(Guid sessionId, string remotePath, string managedLocalPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = _states.TryGetValue(remotePath, out var value) ? value : throw new FileNotFoundException();
            Directory.CreateDirectory(Path.GetDirectoryName(managedLocalPath)!);
            await using var stream = new FileStream(managedLocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(state.Content, cancellationToken);
            stream.SetLength(state.Size);
            await stream.FlushAsync(cancellationToken);
            if (_afterDownload.TryRemove(remotePath, out var replacement)) _states[remotePath] = replacement;
        }

        public async Task<RemoteEditCommitResult> CommitUploadAsync(
            Guid sessionId,
            string managedLocalPath,
            string remotePath,
            RemoteFileIdentity? reviewedIdentity,
            CancellationToken cancellationToken)
        {
            var current = await StatAsync(sessionId, remotePath, cancellationToken);
            if (current != reviewedIdentity) return new(false, current, "The simulated target changed after staging.");
            var content = await File.ReadAllBytesAsync(managedLocalPath, cancellationToken);
            var modifiedAt = _states.TryGetValue(remotePath, out var previous)
                ? previous.ModifiedAt.AddMinutes(1)
                : BaselineTime.AddMinutes(1);
            var state = RemoteState.Create(content, content.LongLength, modifiedAt);
            _states[remotePath] = state;
            Interlocked.Increment(ref _uploadCount);
            return new(true, new(remotePath, state.Size, state.ModifiedAt, state.Sha256), "Committed");
        }

        private sealed record RemoteState(byte[] Content, long Size, DateTimeOffset ModifiedAt, string Sha256)
        {
            public static RemoteState Create(byte[] content, long size, DateTimeOffset modifiedAt)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(content);
                var remaining = size - content.LongLength;
                if (remaining < 0) throw new ArgumentOutOfRangeException(nameof(size));
                var zeros = new byte[128 * 1024];
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeros.Length, remaining);
                    hash.AppendData(zeros, 0, count);
                    remaining -= count;
                }
                return new(content, size, modifiedAt, Convert.ToHexString(hash.GetHashAndReset()));
            }
        }
    }

    private sealed class FakeWatcherFactory : IRemoteEditWatcherFactory
    {
        public List<FakeWatcher> Watchers { get; } = [];
        public FakeWatcher Single => Assert.Single(Watchers);

        public IRemoteEditWatcher Create(string directoryPath, string fileName, Action signal, Action failure)
        {
            var watcher = new FakeWatcher(signal, failure);
            Watchers.Add(watcher);
            return watcher;
        }
    }

    private sealed class FakeWatcher(Action signal, Action failure) : IRemoteEditWatcher
    {
        public bool Disposed { get; private set; }
        public void Signal()
        {
            Assert.False(Disposed);
            signal();
        }
        public void Fail()
        {
            Assert.False(Disposed);
            failure();
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.RemoteEditTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
