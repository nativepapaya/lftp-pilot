using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

internal interface IRemoteEditTransport
{
    Task<RemoteFileIdentity?> StatAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken);
    Task DownloadAsync(Guid sessionId, string remotePath, string managedLocalPath, CancellationToken cancellationToken);
    Task<RemoteEditCommitResult> CommitUploadAsync(
        Guid sessionId,
        string managedLocalPath,
        string remotePath,
        RemoteFileIdentity? reviewedIdentity,
        CancellationToken cancellationToken);
}

internal sealed record RemoteEditCommitResult(bool Committed, RemoteFileIdentity? Current, string Message);

internal interface IRemoteEditWatcher : IDisposable;

internal interface IRemoteEditWatcherFactory
{
    IRemoteEditWatcher Create(string directoryPath, string fileName, Action signal, Action failure);
}

internal sealed class FileSystemRemoteEditWatcherFactory : IRemoteEditWatcherFactory
{
    public IRemoteEditWatcher Create(string directoryPath, string fileName, Action signal, Action failure) =>
        new FileSystemRemoteEditWatcher(directoryPath, fileName, signal, failure);
}

internal sealed class FileSystemRemoteEditWatcher : IRemoteEditWatcher
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(650);
    private readonly string _fileName;
    private readonly Action _signal;
    private readonly Action _failure;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private int _pending;
    private int _disposed;

    public FileSystemRemoteEditWatcher(string directoryPath, string fileName, Action signal, Action failure)
    {
        _fileName = fileName;
        _signal = signal;
        _failure = failure;
        _timer = new(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new(directoryPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 8 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (string.Equals(args.Name, _fileName, StringComparison.OrdinalIgnoreCase)) QueueSignal();
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (string.Equals(args.Name, _fileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args.OldName, _fileName, StringComparison.OrdinalIgnoreCase)) QueueSignal();
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0) _failure();
    }

    private void QueueSignal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _pending, 1);
        try { _timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pending, 0) == 0) return;
        try { _signal(); }
        catch { _failure(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}

internal sealed class RemoteEditManager : IAsyncDisposable
{
    internal const long MaximumRemoteEditBytes = 256L * 1024 * 1024;
    internal const long MaximumManagedCacheBytes = 512L * 1024 * 1024;
    internal const int MaximumActiveEdits = 16;
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(2);
    private readonly string _cacheRoot;
    private readonly IRemoteEditTransport _transport;
    private readonly IRemoteEditWatcherFactory _watcherFactory;
    private readonly Action<EngineEventKind, string, object?, Guid?, Guid?>? _publish;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Registration> _edits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _disposed;

    public RemoteEditManager(
        string cacheRoot,
        IRemoteEditTransport transport,
        IRemoteEditWatcherFactory? watcherFactory = null,
        Action<EngineEventKind, string, object?, Guid?, Guid?>? publish = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("The managed remote-edit cache root must be fully qualified.", nameof(cacheRoot));
        _cacheRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _transport = transport;
        _watcherFactory = watcherFactory ?? new FileSystemRemoteEditWatcherFactory();
        _publish = publish;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_cacheRoot);
        if ((File.GetAttributes(_cacheRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The managed remote-edit cache root cannot be a reparse point.");
        PurgeOrphanedCache();
    }

    internal int ActiveCount => _edits.Count;
    internal IReadOnlyList<RemoteEditSession> GetSnapshots() => _edits.Values.Select(Snapshot).ToArray();
    internal bool HasActiveSession(Guid sessionId) => _edits.Values.Any(edit => edit.SessionId == sessionId);

    public async Task<RemoteEditSession> StartAsync(RemoteEditStartRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty) throw new ArgumentException("A connected session is required.", nameof(request));
        var remotePath = CanonicalRemotePath(request.RemotePath);

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_edits.Count >= MaximumActiveEdits)
                throw new InvalidOperationException($"At most {MaximumActiveEdits} remote files can be edited at once.");
            if (_edits.Values.Any(edit => edit.SessionId == request.SessionId && string.Equals(edit.RemotePath, remotePath, StringComparison.Ordinal)))
                throw new InvalidOperationException("This remote file already has an active managed edit.");

            var baseline = await _transport.StatAsync(request.SessionId, remotePath, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The remote file no longer exists.");
            EnsureIdentity(baseline, remotePath);
            if (ComputeManagedCacheBytes() + baseline.Size > MaximumManagedCacheBytes)
                throw new IOException("The managed remote-edit cache size limit would be exceeded.");

            var editId = NewOpaqueToken(24);
            var directory = OwnedPath(editId);
            Directory.CreateDirectory(directory);
            var displayName = RemoteName(remotePath);
            var localPath = Path.Combine(directory, "content" + SafeExtension(displayName));
            var stagingPath = Path.Combine(directory, ".download");
            Registration? registration = null;
            try
            {
                await _transport.DownloadAsync(request.SessionId, remotePath, stagingPath, cancellationToken).ConfigureAwait(false);
                EnsureManagedFile(stagingPath, baseline.Size, baseline.ContentSha256);
                if (ComputeManagedCacheBytes() > MaximumManagedCacheBytes)
                    throw new IOException("The managed remote-edit cache size limit was exceeded while the file was downloaded.");
                var afterDownload = await _transport.StatAsync(request.SessionId, remotePath, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The remote file disappeared while its managed copy was downloaded.");
                EnsureIdentity(afterDownload, remotePath);
                if (afterDownload != baseline)
                    throw new InvalidDataException("The remote file changed while its managed copy was downloaded. Open it again to use a fresh baseline.");
                File.Move(stagingPath, localPath, overwrite: false);

                registration = new(editId, request.SessionId, baseline, displayName, remotePath, directory, localPath);
                AttachWatcher(registration);
                if (!_edits.TryAdd(editId, registration)) throw new InvalidOperationException("A remote-edit identifier collision occurred.");
                var snapshot = Snapshot(registration);
                _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.started", snapshot, null, request.SessionId);
                return snapshot;
            }
            catch
            {
                registration?.Watcher?.Dispose();
                DeleteOwnedDirectory(directory);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<RemoteEditReview> ReviewAsync(RemoteEditReviewRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var registration = Get(request?.EditId);
        await registration.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false); }
        finally { registration.Gate.Release(); }
    }

    public async Task<RemoteEditActionResult> ResolveAsync(RemoteEditResolveRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Resolution)) throw new ArgumentOutOfRangeException(nameof(request));
        var registration = Get(request.EditId);
        await registration.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = registration.PendingReview;
            if (pending is null || pending.Review.ExpiresAt <= _timeProvider.GetUtcNow() || !TokensEqual(pending.Review.ReviewToken, request.ReviewToken))
                return ReviewRequired(registration, await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false), "The review expired or did not match this edit. Review the current state again.");

            var observed = await ObserveAsync(registration, cancellationToken).ConfigureAwait(false);
            if (!SameObservation(pending.Observation, observed))
                return ReviewRequired(registration, StoreReview(registration, observed), "The local or remote file changed after review. Review the new state before continuing.");

            if (request.Resolution == RemoteEditResolution.Upload && observed.State != RemoteEditReviewState.ReadyToUpload)
                return ReviewRequired(registration, StoreReview(registration, observed), "A normal upload is allowed only while the remote baseline is unchanged.");
            if (request.Resolution == RemoteEditResolution.Overwrite && observed.Conflict is not (RemoteEditConflictKind.RemoteChanged or RemoteEditConflictKind.RemoteMissingOrRenamed))
                return ReviewRequired(registration, StoreReview(registration, observed), "Explicit overwrite is available only for a reviewed remote change, deletion, or rename.");
            if (request.Resolution == RemoteEditResolution.RefreshLocal &&
                (observed.Current is null || observed.Conflict != RemoteEditConflictKind.RemoteChanged))
                return ReviewRequired(registration, StoreReview(registration, observed), "The managed copy can be refreshed only from an existing changed remote file.");

            return request.Resolution == RemoteEditResolution.RefreshLocal
                ? await RefreshLocalAsync(registration, observed.Current!, observed.Local!, cancellationToken).ConfigureAwait(false)
                : await UploadAsync(registration, observed.Local!, observed.Current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    public Task<bool> CompleteAsync(RemoteEditCompleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateOpaqueId(request.EditId);
        return CompleteCoreAsync(request.EditId, cancellationToken);
    }

    public async Task CompleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        foreach (var editId in _edits.Values.Where(edit => edit.SessionId == sessionId).Select(static edit => edit.EditId).ToArray())
            await CompleteCoreAsync(editId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var editId in _edits.Keys.ToArray())
        {
            try { await CompleteCoreAsync(editId, CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _startGate.Dispose();
    }

    private async Task<RemoteEditActionResult> UploadAsync(
        Registration registration,
        LocalFileIdentity reviewedLocal,
        RemoteFileIdentity? reviewedRemote,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(registration.DirectoryPath, ".upload-" + NewOpaqueToken(8));
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ComputeManagedCacheBytes() + reviewedLocal.Size > MaximumManagedCacheBytes)
                return ReviewRequired(registration, StoreReview(registration, new(RemoteEditReviewState.Conflict,
                    RemoteEditConflictKind.ManagedCacheLimitExceeded, registration.Baseline, reviewedLocal,
                    "Staging this upload would exceed the managed remote-edit cache limit.")),
                    "Complete another managed edit before staging this upload.");
            var copied = await CopyStableLocalAsync(registration.LocalPath, stagingPath, cancellationToken).ConfigureAwait(false);
            if (copied != reviewedLocal)
                return ReviewRequired(registration, await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false),
                    "The managed local copy changed after review. Review it again before uploading.");

            var immediatelyBeforeCommit = await _transport.StatAsync(registration.SessionId, registration.RemotePath, cancellationToken).ConfigureAwait(false);
            if (immediatelyBeforeCommit != reviewedRemote)
                return ReviewRequired(registration, await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false),
                    "The remote target changed while the reviewed local copy was staged. No upload was promoted.");

            var commit = await _transport.CommitUploadAsync(
                registration.SessionId,
                stagingPath,
                registration.RemotePath,
                reviewedRemote,
                cancellationToken).ConfigureAwait(false);
            if (!commit.Committed)
                return ReviewRequired(registration, await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false), commit.Message);
            var current = commit.Current ?? throw new InvalidDataException("The promoted remote file could not be verified.");
            EnsureIdentity(current, registration.RemotePath);
            if (current.Size != copied.Size || !string.Equals(current.ContentSha256, copied.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The promoted remote file did not match the reviewed local copy.");
            registration.Baseline = current;
            registration.PendingReview = null;
            registration.Dirty = false;
            var snapshot = Snapshot(registration);
            _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.uploaded", snapshot, null, registration.SessionId);
            return new(RemoteEditActionOutcome.Uploaded, snapshot, "The reviewed local copy was uploaded and its remote baseline was refreshed.");
        }
        finally
        {
            TryDeleteFile(stagingPath);
            _startGate.Release();
        }
    }

    private async Task<RemoteEditActionResult> RefreshLocalAsync(
        Registration registration,
        RemoteFileIdentity reviewedIdentity,
        LocalFileIdentity reviewedLocal,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(registration.DirectoryPath, ".refresh-" + NewOpaqueToken(8));
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ComputeManagedCacheBytes() + reviewedIdentity.Size > MaximumManagedCacheBytes)
                return ReviewRequired(registration, StoreReview(registration, new(RemoteEditReviewState.Conflict,
                    RemoteEditConflictKind.ManagedCacheLimitExceeded, reviewedIdentity, reviewedLocal,
                    "Staging this refresh would exceed the managed remote-edit cache limit.")),
                    "Complete another managed edit before refreshing this managed copy.");
            await _transport.DownloadAsync(registration.SessionId, registration.RemotePath, stagingPath, cancellationToken).ConfigureAwait(false);
            EnsureManagedFile(stagingPath, reviewedIdentity.Size, reviewedIdentity.ContentSha256);
            var current = await _transport.StatAsync(registration.SessionId, registration.RemotePath, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return ReviewRequired(registration, await CreateReviewAsync(registration, cancellationToken).ConfigureAwait(false), "The remote file disappeared while the managed copy was refreshed.");
            EnsureIdentity(current, registration.RemotePath);
            if (current != reviewedIdentity)
                return ReviewRequired(registration, StoreReview(registration, new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.RemoteChanged, current, reviewedLocal,
                    "The remote file changed again while it was being refreshed.")), "The remote file changed again. Review the new state.");

            var refreshedLocal = await ComputeLocalIdentityAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref registration.AgentWriteIdentity, refreshedLocal);
            try
            {
                File.Move(stagingPath, registration.LocalPath, overwrite: true);
            }
            catch
            {
                Volatile.Write(ref registration.AgentWriteIdentity, null);
                throw;
            }
            registration.Baseline = current;
            registration.PendingReview = null;
            registration.Dirty = false;
            var snapshot = Snapshot(registration);
            _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.refreshed", snapshot, null, registration.SessionId);
            return new(RemoteEditActionOutcome.Refreshed, snapshot, "The managed local copy was replaced with the reviewed remote version.");
        }
        finally
        {
            TryDeleteFile(stagingPath);
            _startGate.Release();
        }
    }

    private async Task<RemoteEditReview> CreateReviewAsync(Registration registration, CancellationToken cancellationToken) =>
        StoreReview(registration, await ObserveAsync(registration, cancellationToken).ConfigureAwait(false));

    private RemoteEditReview StoreReview(Registration registration, Observation observation)
    {
        var review = new RemoteEditReview(
            registration.EditId,
            observation.State,
            observation.Conflict,
            registration.Baseline,
            observation.Current,
            NewOpaqueToken(24),
            _timeProvider.GetUtcNow() + ReviewLifetime,
            observation.Message);
        registration.PendingReview = new(observation, review);
        return review;
    }

    private async Task<Observation> ObserveAsync(Registration registration, CancellationToken cancellationToken)
    {
        if (!TryGetManagedFileLength(registration.LocalPath, out var localLength, out var localConflict))
            return localConflict == RemoteEditConflictKind.LocalTooLarge
                ? new(RemoteEditReviewState.Conflict, localConflict, null, null, "The managed local copy exceeds the remote-edit size limit.")
                : new(RemoteEditReviewState.Conflict, localConflict, null, null, "The managed local copy is missing, renamed, or no longer a regular file.");
        if (localLength > MaximumRemoteEditBytes)
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.LocalTooLarge, null, null, "The managed local copy exceeds the remote-edit size limit.");
        if (ComputeManagedCacheBytes() > MaximumManagedCacheBytes)
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.ManagedCacheLimitExceeded, null, null,
                "The aggregate managed remote-edit cache limit was exceeded. Upload is blocked.");

        LocalFileIdentity local;
        try
        {
            local = await ComputeLocalIdentityAsync(registration.LocalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.LocalChanged, null, null,
                "The managed local copy changed while it was being reviewed. Save it, then review again.");
        }

        RemoteFileIdentity? current;
        try
        {
            current = await _transport.StatAsync(registration.SessionId, registration.RemotePath, cancellationToken).ConfigureAwait(false);
            if (current is not null) EnsureIdentity(current, registration.RemotePath);
        }
        catch (InvalidDataException)
        {
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.RemoteIdentityUnavailable, null, local,
                "The server did not provide a trustworthy path, size, and modification time. Upload is blocked.");
        }

        if (current is null)
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.RemoteMissingOrRenamed, null, local,
                "The remote path is missing or was renamed after the managed copy was opened.");
        if (current != registration.Baseline)
            return new(RemoteEditReviewState.Conflict, RemoteEditConflictKind.RemoteChanged, current, local,
                "The remote file changed after the managed copy was opened.");
        return new(RemoteEditReviewState.ReadyToUpload, RemoteEditConflictKind.None, current, local,
            "The remote file still matches the reviewed baseline.");
    }

    private async Task<bool> CompleteCoreAsync(string editId, CancellationToken cancellationToken)
    {
        ValidateOpaqueId(editId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_edits.TryGetValue(editId, out var registration)) return false;
        await registration.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completed = false;
        try
        {
            if (!_edits.TryGetValue(editId, out var current) || !ReferenceEquals(current, registration)) return false;
            registration.IsDisposed = true;
            registration.Watcher?.Dispose();
            registration.Watcher = null;
            try
            {
                DeleteOwnedDirectoryStrict(registration.DirectoryPath);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                registration.IsDisposed = false;
                try
                {
                    AttachWatcher(registration);
                }
                catch (Exception watcherError) when (watcherError is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    MarkWatcherFailed(registration);
                    throw new IOException(
                        "The managed edit cache could not be deleted and its watcher could not be restarted. The edit remains registered for recovery.",
                        new AggregateException(cleanupError, watcherError));
                }
                throw;
            }
            if (!_edits.TryRemove(editId, out var removed) || !ReferenceEquals(removed, registration))
                throw new InvalidOperationException("The managed edit registration changed during completion.");
            completed = true;
        }
        finally
        {
            registration.Gate.Release();
        }
        if (!completed) return false;
        _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.completed",
            new RemoteEditCompleted(registration.EditId, registration.DisplayName), null, registration.SessionId);
        return true;
    }

    private void OnWatcherSignal(Registration registration)
    {
        if (registration.IsDisposed || IsExactAgentWriteNotification(registration)) return;
        var aggregateExceeded = ComputeManagedCacheBytes() > MaximumManagedCacheBytes;
        var kind = TryGetManagedFileLength(registration.LocalPath, out _, out var conflict)
            ? aggregateExceeded ? RemoteEditLocalChangeKind.TooLarge : RemoteEditLocalChangeKind.Saved
            : conflict == RemoteEditConflictKind.LocalTooLarge ? RemoteEditLocalChangeKind.TooLarge : RemoteEditLocalChangeKind.MissingOrRenamed;
        var detectedAt = _timeProvider.GetUtcNow();
        registration.Dirty = true;
        registration.LastLocalChangeAt = detectedAt;
        registration.PendingReview = null;
        var message = kind switch
        {
            RemoteEditLocalChangeKind.Saved => "A save was detected in the managed local copy. No upload has occurred.",
            RemoteEditLocalChangeKind.TooLarge => aggregateExceeded
                ? "The aggregate managed remote-edit cache limit was exceeded. Upload is blocked."
                : "The managed local copy exceeds the remote-edit size limit. Upload is blocked.",
            _ => "The managed local copy is missing, renamed, or no longer a regular file. Upload is blocked.",
        };
        _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.localChanged",
            new RemoteEditLocalChange(registration.EditId, registration.DisplayName, kind, detectedAt, message), null, registration.SessionId);
    }

    private void OnWatcherFailure(Registration registration)
    {
        if (registration.IsDisposed) return;
        MarkWatcherFailed(registration);
    }

    private void MarkWatcherFailed(Registration registration)
    {
        var detectedAt = _timeProvider.GetUtcNow();
        Volatile.Write(ref registration.AgentWriteIdentity, null);
        registration.Dirty = true;
        registration.WatcherFailed = true;
        registration.LastLocalChangeAt = detectedAt;
        registration.PendingReview = null;
        _publish?.Invoke(EngineEventKind.RemoteEdit, "remoteEdit.localChanged",
            new RemoteEditLocalChange(registration.EditId, registration.DisplayName, RemoteEditLocalChangeKind.WatcherError,
                detectedAt, "The managed cache watcher failed. No upload has occurred; finish this managed edit before reopening the remote file."), null, registration.SessionId);
    }

    private void AttachWatcher(Registration registration)
    {
        registration.Watcher = _watcherFactory.Create(
            registration.DirectoryPath,
            Path.GetFileName(registration.LocalPath),
            () => OnWatcherSignal(registration),
            () => OnWatcherFailure(registration));
        registration.WatcherFailed = false;
    }

    private static bool IsExactAgentWriteNotification(Registration registration)
    {
        var expected = Volatile.Read(ref registration.AgentWriteIdentity);
        if (expected is null) return false;
        try
        {
            if (ComputeLocalIdentity(registration.LocalPath) == expected) return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        Interlocked.CompareExchange(ref registration.AgentWriteIdentity, null, expected);
        return false;
    }

    private Registration Get(string? editId)
    {
        ValidateOpaqueId(editId);
        return _edits.TryGetValue(editId!, out var registration)
            ? registration
            : throw new KeyNotFoundException("The remote-edit registration was not found or has expired.");
    }

    private static void ValidateOpaqueId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 32 or > 128 || value.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("A valid opaque remote-edit identifier is required.");
    }

    private static string CanonicalRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) ||
            path.IndexOfAny(['\0', '\r', '\n']) >= 0 || path.Contains("//", StringComparison.Ordinal) ||
            path.Split("/", StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            throw new ArgumentException("A canonical bounded remote file path is required.", nameof(path));
        var canonical = path.TrimEnd('/');
        if (canonical.Length == 0) throw new ArgumentException("The remote root is not a regular file.", nameof(path));
        return canonical;
    }

    private static void EnsureIdentity(RemoteFileIdentity identity, string expectedPath)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(CanonicalRemotePath(identity.CanonicalPath), expectedPath, StringComparison.Ordinal) ||
            identity.Size is < 0 or > MaximumRemoteEditBytes || identity.ModifiedAt == default ||
            identity.ContentSha256 is not { Length: 64 } hash || hash.Any(static character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("The server did not provide a trustworthy bounded remote file identity.");
    }

    private static void EnsureManagedFile(string path, long expectedLength, string expectedSha256)
    {
        if (!TryGetManagedFileLength(path, out var length, out _))
            throw new InvalidDataException("LFTP did not produce a regular managed-cache file.");
        if (length != expectedLength) throw new InvalidDataException("The managed copy size did not match the remote baseline.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var digest = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(digest, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The managed copy content did not match the strong remote baseline.");
    }

    private static bool TryGetManagedFileLength(string path, out long length, out RemoteEditConflictKind conflict)
    {
        length = 0;
        conflict = RemoteEditConflictKind.LocalMissingOrRenamed;
        try
        {
            if (!File.Exists(path)) return false;
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            length = new FileInfo(path).Length;
            if (length > MaximumRemoteEditBytes)
            {
                conflict = RemoteEditConflictKind.LocalTooLarge;
                return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<LocalFileIdentity> ComputeLocalIdentityAsync(string path, CancellationToken cancellationToken)
    {
        if (!TryGetManagedFileLength(path, out var length, out var conflict))
            throw new IOException(conflict == RemoteEditConflictKind.LocalTooLarge
                ? "The managed local copy exceeds the remote-edit size limit."
                : "The managed local copy is missing, renamed, or no longer a regular file.");
        var modifiedAt = File.GetLastWriteTimeUtc(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (stream.Length != length || File.GetLastWriteTimeUtc(path) != modifiedAt)
            throw new IOException("The managed local copy changed while its review identity was calculated.");
        return new(length, modifiedAt, Convert.ToHexString(digest));
    }

    private static LocalFileIdentity ComputeLocalIdentity(string path)
    {
        if (!TryGetManagedFileLength(path, out var length, out var conflict))
            throw new IOException(conflict == RemoteEditConflictKind.LocalTooLarge
                ? "The managed local copy exceeds the remote-edit size limit."
                : "The managed local copy is missing, renamed, or no longer a regular file.");
        var modifiedAt = File.GetLastWriteTimeUtc(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var digest = SHA256.HashData(stream);
        if (stream.Length != length || File.GetLastWriteTimeUtc(path) != modifiedAt)
            throw new IOException("The managed local copy changed while its identity was calculated.");
        return new(length, modifiedAt, Convert.ToHexString(digest));
    }

    private static async Task<LocalFileIdentity> CopyStableLocalAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (!TryGetManagedFileLength(sourcePath, out var length, out var conflict))
            throw new IOException(conflict == RemoteEditConflictKind.LocalTooLarge
                ? "The managed local copy exceeds the remote-edit size limit."
                : "The managed local copy is missing, renamed, or no longer a regular file.");
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var modifiedAt = File.GetLastWriteTimeUtc(sourcePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (destination.Length != length || source.Length != length || File.GetLastWriteTimeUtc(sourcePath) != modifiedAt)
            throw new IOException("The managed local copy changed while it was staged for upload.");
        return new(length, modifiedAt, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private RemoteEditActionResult ReviewRequired(Registration registration, RemoteEditReview review, string message) =>
        new(RemoteEditActionOutcome.ReviewRequired, Snapshot(registration), message, review);

    private static bool SameObservation(Observation left, Observation right) =>
        left.State == right.State && left.Conflict == right.Conflict && left.Current == right.Current && left.Local == right.Local;

    private static bool TokensEqual(string expected, string supplied)
    {
        if (expected.Length != supplied?.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(supplied));
    }

    private static RemoteEditSession Snapshot(Registration registration) => new(
        registration.EditId,
        registration.SessionId,
        registration.DisplayName,
        registration.RemotePath,
        registration.LocalPath,
        registration.Baseline,
        registration.Dirty,
        registration.WatcherFailed,
        registration.LastLocalChangeAt);

    private string OwnedPath(string leaf)
    {
        var path = Path.GetFullPath(Path.Combine(_cacheRoot, leaf));
        if (!IsOwnedPath(path)) throw new IOException("The managed cache path escaped its package-scoped root.");
        return path;
    }

    private bool IsOwnedPath(string path)
    {
        var relative = Path.GetRelativePath(_cacheRoot, Path.GetFullPath(path));
        return relative.Length > 0 && !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private long ComputeManagedCacheBytes()
    {
        long total = 0;
        var filesSeen = 0;
        if (Directory.EnumerateFiles(_cacheRoot).Any())
            throw new IOException("The managed remote-edit cache contains an unexpected root file.");
        foreach (var directory in Directory.EnumerateDirectories(_cacheRoot))
        {
            if (!IsOwnedPath(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The managed remote-edit cache contains an unsafe directory.");
            if (Directory.EnumerateDirectories(directory).Any())
                throw new IOException("The managed remote-edit cache contains an unexpected nested directory.");
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (++filesSeen > MaximumActiveEdits * 8)
                    throw new IOException("The managed remote-edit cache contains too many files.");
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The managed remote-edit cache contains an unsafe file.");
                total = checked(total + new FileInfo(file).Length);
                if (total > MaximumManagedCacheBytes) return total;
            }
        }
        return total;
    }

    private void PurgeOrphanedCache()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_cacheRoot))
        {
            if (!IsOwnedPath(path)) continue;
            if (Directory.Exists(path)) DeleteOwnedDirectory(path);
            else TryDeleteFile(path);
        }
    }

    private void DeleteOwnedDirectory(string path)
    {
        if (!IsOwnedPath(path) || !Directory.Exists(path)) return;
        DeleteTreeNoFollow(path);
    }

    private void DeleteOwnedDirectoryStrict(string path)
    {
        if (!IsOwnedPath(path)) throw new IOException("The managed cache directory escaped its package-scoped root.");
        if (!Directory.Exists(path)) return;
        DeleteTreeNoFollowStrict(path);
        if (Directory.Exists(path)) throw new IOException("The managed cache directory still exists after cleanup.");
    }

    private static void DeleteTreeNoFollow(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path)) TryDeleteFile(file);
        foreach (var directory in Directory.EnumerateDirectories(path)) DeleteTreeNoFollow(directory);
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteTreeNoFollowStrict(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(path)) DeleteTreeNoFollowStrict(directory);
        Directory.Delete(path, recursive: false);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string SafeExtension(string displayName)
    {
        var extension = Path.GetExtension(displayName);
        return extension.Length is >= 2 and <= 12 && extension.Skip(1).All(static character => char.IsAsciiLetterOrDigit(character))
            ? extension.ToLowerInvariant()
            : ".bin";
    }

    private static string RemoteName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string NewOpaqueToken(int byteCount) => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    private sealed class Registration(
        string editId,
        Guid sessionId,
        RemoteFileIdentity baseline,
        string displayName,
        string remotePath,
        string directoryPath,
        string localPath)
    {
        public string EditId { get; } = editId;
        public Guid SessionId { get; } = sessionId;
        public string DisplayName { get; } = displayName;
        public string RemotePath { get; } = remotePath;
        public string DirectoryPath { get; } = directoryPath;
        public string LocalPath { get; } = localPath;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IRemoteEditWatcher? Watcher { get; set; }
        public RemoteFileIdentity Baseline { get; set; } = baseline;
        public PendingReview? PendingReview { get; set; }
        public LocalFileIdentity? AgentWriteIdentity;
        public DateTimeOffset? LastLocalChangeAt { get; set; }
        public bool Dirty { get; set; }
        public bool WatcherFailed { get; set; }
        public volatile bool IsDisposed;
    }

    private sealed record Observation(
        RemoteEditReviewState State,
        RemoteEditConflictKind Conflict,
        RemoteFileIdentity? Current,
        LocalFileIdentity? Local,
        string Message);

    private sealed record LocalFileIdentity(long Size, DateTime LastWriteUtc, string Sha256);

    private sealed record PendingReview(Observation Observation, RemoteEditReview Review);
}
