using System.Security.Cryptography;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

internal sealed class LftpRemoteEditTransport : IRemoteEditTransport
{
    private readonly SessionRegistry _sessions;
    private readonly AgentWorkspaceOptions _options;
    private readonly string _identityCacheRoot;

    public LftpRemoteEditTransport(SessionRegistry sessions, AgentWorkspaceOptions options)
    {
        _sessions = sessions;
        _options = options;
        _identityCacheRoot = Path.GetFullPath(Path.Combine(options.TemporaryRoot, "remote-edit-identities"));
        Directory.CreateDirectory(_identityCacheRoot);
        if ((File.GetAttributes(_identityCacheRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The remote-edit identity cache cannot be a reparse point.");
    }

    public async Task<RemoteFileIdentity?> StatAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken)
    {
        var metadata = await StatMetadataAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        if (metadata.Size > RemoteEditManager.MaximumRemoteEditBytes)
            throw new InvalidDataException("The remote file exceeds the managed-edit size limit.");

        var probePath = Path.Combine(_identityCacheRoot, $"identity-{NewToken()}.tmp");
        try
        {
            await DownloadCoreAsync(sessionId, remotePath, probePath, cancellationToken).ConfigureAwait(false);
            var local = await HashLocalFileAsync(probePath, cancellationToken).ConfigureAwait(false);
            if (local.Size != metadata.Size)
                throw new InvalidDataException("The downloaded identity probe did not match the remote file size.");

            var afterDownload = await StatMetadataAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The remote file disappeared while its strong identity was calculated.");
            if (metadata != afterDownload)
                throw new InvalidDataException("The remote file changed while its strong identity was calculated.");
            return new(remotePath, metadata.Size, metadata.ModifiedAt, local.Sha256);
        }
        finally
        {
            TryDeleteLocal(probePath);
        }
    }

    public Task DownloadAsync(Guid sessionId, string remotePath, string managedLocalPath, CancellationToken cancellationToken) =>
        DownloadCoreAsync(sessionId, remotePath, managedLocalPath, cancellationToken);

    public async Task<RemoteEditCommitResult> CommitUploadAsync(
        Guid sessionId,
        string managedLocalPath,
        string remotePath,
        RemoteFileIdentity? reviewedIdentity,
        CancellationToken cancellationToken)
    {
        var local = await HashLocalFileAsync(managedLocalPath, cancellationToken).ConfigureAwait(false);
        var remoteStagingPath = BuildAuxiliaryPath(remotePath, "upload");
        var remoteBackupPath = BuildAuxiliaryPath(remotePath, "backup");
        var remoteFailedPath = BuildAuxiliaryPath(remotePath, "failed");
        var session = _sessions.Get(sessionId);
        await using var process = await session.CreateEphemeralAsync("remote-edit-commit", cancellationToken).ConfigureAwait(false);
        var backupCreated = false;
        var stagingPromoted = false;

        try
        {
            if (await StatMetadataAsync(sessionId, remoteStagingPath, cancellationToken).ConfigureAwait(false) is not null ||
                await StatMetadataAsync(sessionId, remoteBackupPath, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("A generated remote-edit staging path unexpectedly already exists.");

            var upload = await process.ExecuteAsync(
                LftpCommandBuilder.BuildRemoteEditUpload(managedLocalPath, remoteStagingPath),
                _options.TransferTimeout,
                cancellationToken).ConfigureAwait(false);
            SessionRegistry.ThrowIfFailed(upload, "Remote edit staging upload");

            var stagedIdentity = await StatAsync(sessionId, remoteStagingPath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The staged remote upload could not be verified.");
            if (stagedIdentity.Size != local.Size || !string.Equals(stagedIdentity.ContentSha256, local.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged remote upload did not match the reviewed local content.");

            var current = await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
            if (current != reviewedIdentity)
            {
                await TryDeleteRemoteAsync(process, remoteStagingPath, cancellationToken).ConfigureAwait(false);
                return new(false, current, "The remote target changed after staging. No staged content was promoted.");
            }

            if (reviewedIdentity is not null)
            {
                await ExecuteRequiredAsync(process, LftpCommandBuilder.BuildMove(remotePath, remoteBackupPath),
                    "Remote edit backup creation", cancellationToken).ConfigureAwait(false);
                backupCreated = true;

                var backupIdentity = await StatAsync(sessionId, remoteBackupPath, cancellationToken).ConfigureAwait(false);
                if (!SameVersionAtDifferentPath(backupIdentity, reviewedIdentity))
                {
                    await RestoreBackupAsync(process, sessionId, remoteBackupPath, remotePath, remoteFailedPath, null, cancellationToken).ConfigureAwait(false);
                    backupCreated = false;
                    await TryDeleteRemoteAsync(process, remoteStagingPath, cancellationToken).ConfigureAwait(false);
                    var restored = await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
                    return new(false, restored, "The live target changed before backup. Its content was preserved and the staged upload was not promoted.");
                }

                if (await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false) is { } appeared)
                {
                    await RestoreBackupAsync(process, sessionId, remoteBackupPath, remotePath, remoteFailedPath, appeared, cancellationToken).ConfigureAwait(false);
                    backupCreated = false;
                    await TryDeleteRemoteAsync(process, remoteStagingPath, cancellationToken).ConfigureAwait(false);
                    return new(false, await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false),
                        "A new remote target appeared during promotion. No staged content was promoted.");
                }
            }
            else
            {
                // Missing-target overwrite is allowed only after one final fresh,
                // strong absence check. Any newly appearing target is preserved.
                var appeared = await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
                if (appeared is not null)
                {
                    await TryDeleteRemoteAsync(process, remoteStagingPath, cancellationToken).ConfigureAwait(false);
                    return new(false, appeared, "A remote target appeared after review. No staged content was promoted.");
                }
            }

            await ExecuteRequiredAsync(process, LftpCommandBuilder.BuildMove(remoteStagingPath, remotePath),
                "Remote edit staging promotion", cancellationToken).ConfigureAwait(false);
            stagingPromoted = true;
            var promoted = await StatAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The promoted remote edit could not be verified.");
            if (!SameVersionAtDifferentPath(promoted, stagedIdentity))
                throw new InvalidDataException("The promoted remote edit did not match the verified staging content.");

            if (backupCreated)
            {
                await ExecuteRequiredAsync(process, LftpCommandBuilder.BuildDelete(remoteBackupPath, isDirectory: false, recursive: false),
                    "Remote edit backup cleanup", cancellationToken).ConfigureAwait(false);
                backupCreated = false;
            }
            return new(true, promoted, "The verified staging upload was promoted and the prior remote version was safely retired.");
        }
        catch
        {
            if (backupCreated)
            {
                try
                {
                    var promotedIdentity = stagingPromoted
                        ? await StatAsync(sessionId, remotePath, CancellationToken.None).ConfigureAwait(false)
                        : null;
                    await RestoreBackupAsync(process, sessionId, remoteBackupPath, remotePath, remoteFailedPath, promotedIdentity, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException($"Remote edit promotion failed and the prior version could not be restored automatically. The preserved backup is '{remoteBackupPath}'.", rollbackError);
                }
            }
            await TryDeleteRemoteAsync(process, remoteStagingPath, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DownloadCoreAsync(Guid sessionId, string remotePath, string managedLocalPath, CancellationToken cancellationToken)
    {
        var session = _sessions.Get(sessionId);
        await using var process = await session.CreateEphemeralAsync("remote-edit-download", cancellationToken).ConfigureAwait(false);
        var result = await process.ExecuteAsync(
            LftpCommandBuilder.BuildRemoteEditDownload(remotePath, managedLocalPath),
            _options.TransferTimeout,
            cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, "Remote edit download");
    }

    private async Task<RemoteMetadata?> StatMetadataAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken)
    {
        remotePath = FreshRemoteStatParser.ValidateRemotePath(remotePath, nameof(remotePath));
        var session = _sessions.Get(sessionId);
        var result = await session.Browse.ExecuteAsync(
            LftpCommandBuilder.BuildStat(remotePath, fresh: true),
            _options.BrowseTimeout,
            cancellationToken).ConfigureAwait(false);
        var entry = FreshRemoteStatParser.Parse(result, remotePath, "The fresh remote edit identity check");
        if (entry is null) return null;
        if (entry.Kind != EntryKind.File) throw new NotSupportedException("Remote editing supports regular files only; links and special entries are not followed.");
        if (entry.Size is not { } size || entry.ModifiedAt is not { } modifiedAt)
            throw new InvalidDataException("The server did not provide the canonical path, size, and modification time required for remote editing.");
        return new(remotePath, size, modifiedAt);
    }

    private async Task RestoreBackupAsync(
        ILftpSession process,
        Guid sessionId,
        string backupPath,
        string targetPath,
        string failedPath,
        RemoteFileIdentity? currentTarget,
        CancellationToken cancellationToken)
    {
        if (currentTarget is not null)
        {
            if (await StatMetadataAsync(sessionId, failedPath, cancellationToken).ConfigureAwait(false) is not null)
                throw new IOException("The generated rollback quarantine path unexpectedly exists.");
            await ExecuteRequiredAsync(process, LftpCommandBuilder.BuildMove(targetPath, failedPath),
                "Remote edit rollback quarantine", cancellationToken).ConfigureAwait(false);
        }
        await ExecuteRequiredAsync(process, LftpCommandBuilder.BuildMove(backupPath, targetPath),
            "Remote edit backup restoration", cancellationToken).ConfigureAwait(false);
        // Never delete the quarantined target automatically. It may be a
        // concurrent writer's version rather than our promoted staging file.
        // Preserving it under the opaque failed path is safer than risking
        // another user's remote data during rollback.
    }

    private async Task TryDeleteRemoteAsync(ILftpSession process, string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await process.ExecuteAsync(
                LftpCommandBuilder.BuildDelete(remotePath, isDirectory: false, recursive: false),
                _options.BrowseTimeout,
                cancellationToken).ConfigureAwait(false);
            var error = LftpOutputParser.FirstError(result.Lines);
            if (error is not null && !error.Contains("no such", StringComparison.OrdinalIgnoreCase) && !error.Contains("not found", StringComparison.OrdinalIgnoreCase))
                SessionRegistry.ThrowIfFailed(result, "Remote edit staging cleanup");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
    }

    private async Task ExecuteRequiredAsync(ILftpSession process, string command, string operation, CancellationToken cancellationToken)
    {
        var result = await process.ExecuteAsync(command, _options.TransferTimeout, cancellationToken).ConfigureAwait(false);
        SessionRegistry.ThrowIfFailed(result, operation);
    }

    private static async Task<LocalDigest> HashLocalFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The remote-edit content identity requires a regular local file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 0 or > RemoteEditManager.MaximumRemoteEditBytes)
            throw new InvalidDataException("The remote-edit content identity exceeds the managed-edit size limit.");
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new(stream.Length, Convert.ToHexString(digest));
    }

    private static bool SameVersionAtDifferentPath(RemoteFileIdentity? left, RemoteFileIdentity right) =>
        left is not null && left.Size == right.Size && left.ModifiedAt == right.ModifiedAt &&
        string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.OrdinalIgnoreCase);

    private static string BuildAuxiliaryPath(string remotePath, string role)
    {
        var separator = remotePath.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : remotePath[..separator];
        var leaf = $".lftp-pilot-{NewToken()}.{role}";
        var path = parent == "/" ? "/" + leaf : parent + "/" + leaf;
        if (path.Length > 4096) throw new ArgumentException("The remote path is too deep for safe staging.", nameof(remotePath));
        return path;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();

    private static void TryDeleteLocal(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record RemoteMetadata(string CanonicalPath, long Size, DateTimeOffset ModifiedAt);
    private sealed record LocalDigest(long Size, string Sha256);
}
