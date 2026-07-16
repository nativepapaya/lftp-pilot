using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Core;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Agent;

public sealed record SftpKnownHostsMaterialization(string HostKeyAlias, string KnownHostsPath);

public sealed class SftpHostKeyManager
{
    private const int MaximumPendingReviews = 128;
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(5);
    private readonly IHostKeyStore _store;
    private readonly ISshHostKeyProbe _probe;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, PendingReview> _pending = [];

    public SftpHostKeyManager(IHostKeyStore store, ISshHostKeyProbe probe, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SftpHostKeyInspection> InspectAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        var binding = CreateBinding(profile);
        var alias = SftpHostKeyIdentity.CreateHostKeyAlias(binding);
        var proposed = await _probe.ProbeAsync(profile, alias, cancellationToken).ConfigureAwait(false);
        ValidateKey(proposed, binding, alias);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            PurgeExpired(now);
            var trusted = await _store.GetAsync(binding, cancellationToken).ConfigureAwait(false);
            if (KeysEqual(trusted, proposed))
            {
                _pending.Remove(profile.Id);
                return new(SftpHostKeyState.Trusted);
            }

            if (!_pending.ContainsKey(profile.Id) && _pending.Count >= MaximumPendingReviews)
                throw new InvalidOperationException("Too many SFTP host-key reviews are pending. Finish or let an existing review expire before inspecting another server.");

            var state = trusted is null ? SftpHostKeyState.EnrollmentRequired : SftpHostKeyState.Changed;
            var review = new SftpHostKeyReview(
                Guid.NewGuid(),
                profile.Id,
                binding.Endpoint,
                state,
                proposed.Algorithm,
                proposed.FingerprintSha256,
                trusted?.Algorithm,
                trusted?.FingerprintSha256,
                now + ReviewLifetime,
                CreateApprovalToken());
            _pending[profile.Id] = new(review, proposed, trusted);
            return new(state, review);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SftpHostKeyApproveResult> ApproveAsync(
        ConnectionProfile profile,
        SftpHostKeyApproveRequest request,
        bool replacementAllowed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var binding = CreateBinding(profile);
        if (request.ProfileId == Guid.Empty || request.ProfileId != profile.Id || request.ReviewId == Guid.Empty ||
            !IsApprovalToken(request.ApprovalToken))
        {
            throw new ArgumentException("A valid SFTP host-key approval request is required.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PurgeExpired(_timeProvider.GetUtcNow());
            if (!_pending.TryGetValue(profile.Id, out var pending) || pending.Review.ReviewId != request.ReviewId ||
                !FixedTimeTokenEquals(pending.Review.ApprovalToken, request.ApprovalToken))
            {
                throw new InvalidOperationException("The SFTP host-key review is missing, expired, or no longer current.");
            }

            if (pending.Proposed.Binding != binding || !string.Equals(pending.Review.Endpoint, binding.Endpoint, StringComparison.Ordinal))
            {
                _pending.Remove(profile.Id);
                throw new InvalidOperationException("The connection profile changed after the SFTP host key was reviewed. Inspect it again.");
            }

            if (pending.Review.State == SftpHostKeyState.EnrollmentRequired && request.ReplaceExisting)
                throw new InvalidOperationException("A first-time SFTP host-key enrollment cannot be approved as a replacement.");
            if (pending.Review.State == SftpHostKeyState.Changed && (!request.ReplaceExisting || !replacementAllowed))
                throw new InvalidOperationException("A changed SFTP host key requires explicit replacement approval and must not be in use by active work.");
            if (pending.Review.State is not SftpHostKeyState.EnrollmentRequired and not SftpHostKeyState.Changed)
                throw new InvalidOperationException("Only a pending enrollment or changed host key can be approved.");

            var current = await _store.GetAsync(binding, cancellationToken).ConfigureAwait(false);
            if (!KeysEqual(current, pending.TrustedBeforeReview))
            {
                _pending.Remove(profile.Id);
                throw new InvalidOperationException("The trusted SFTP host key changed after review. Inspect the server again.");
            }

            await _store.SaveAsync(pending.Proposed, cancellationToken).ConfigureAwait(false);
            _pending.Remove(profile.Id);
            return new(profile.Id, binding.Endpoint, pending.Proposed.FingerprintSha256);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrustedSftpHostKey> RequireTrustedAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        var binding = CreateBinding(profile);
        var trusted = await _store.GetAsync(binding, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This SFTP endpoint does not have an approved host key. Inspect and review it before connecting.");
        ValidateKey(trusted, binding, SftpHostKeyIdentity.CreateHostKeyAlias(binding));
        return trusted;
    }

    public async Task<SftpKnownHostsMaterialization> MaterializeAsync(
        ConnectionProfile profile,
        string directory,
        string? leafName = null,
        CancellationToken cancellationToken = default)
    {
        var trusted = await RequireTrustedAsync(profile, cancellationToken).ConfigureAwait(false);
        var targetDirectory = ValidateTargetDirectory(directory);
        Directory.CreateDirectory(targetDirectory);
        ThrowIfPathContainsReparsePoint(targetDirectory);
        var leaf = ValidateLeafName(leafName ?? "known_hosts");
        var destination = Path.Combine(targetDirectory, leaf);
        ThrowIfDestinationIsNotRegular(destination);

        var alias = SftpHostKeyIdentity.CreateHostKeyAlias(trusted.Binding);
        var line = SshKnownHostsParser.Format(alias, trusted);
        await AtomicFile.WriteBytesAsync(destination, Encoding.ASCII.GetBytes(line), cancellationToken).ConfigureAwait(false);
        ThrowIfDestinationIsNotRegular(destination, requireExisting: true);
        return new(alias, destination);
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PurgeExpired(_timeProvider.GetUtcNow());
            _pending.Remove(profileId);
            await _store.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static HostKeyBinding CreateBinding(ConnectionProfile profile) => SftpHostKeyIdentity.CreateBinding(profile);

    private static void ValidateKey(TrustedSftpHostKey key, HostKeyBinding expectedBinding, string alias)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Binding != expectedBinding)
            throw new InvalidDataException("The SFTP host-key probe returned a key for a different profile or endpoint.");
        _ = SshKnownHostsParser.Format(alias, key);
    }

    private static bool KeysEqual(TrustedSftpHostKey? left, TrustedSftpHostKey? right) => left == right;

    private static string CreateApprovalToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsApprovalToken(string? token) => token is { Length: 64 } && token.All(char.IsAsciiHexDigit);

    private static bool FixedTimeTokenEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var profileId in _pending.Where(pair => pair.Value.Review.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
            _pending.Remove(profileId);
    }

    private static string ValidateTargetDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 32_767 || !Path.IsPathFullyQualified(directory) ||
            directory.IndexOfAny(['\0', '\r', '\n']) >= 0 || directory.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The SFTP known-hosts directory must be a bounded fully qualified non-device path.", nameof(directory));
        }
        var fullPath = Path.GetFullPath(directory);
        ThrowIfPathContainsReparsePoint(fullPath);
        return fullPath;
    }

    private static void ThrowIfPathContainsReparsePoint(string fullPath)
    {
        for (var current = fullPath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The SFTP known-hosts directory cannot contain a reparse point in its existing path.");
            }
        }
    }

    private static void ThrowIfDestinationIsNotRegular(string destination, bool requireExisting = false)
    {
        if (Directory.Exists(destination))
            throw new IOException("The SFTP known-hosts destination must be a regular file.");
        if (!File.Exists(destination))
        {
            if (requireExisting) throw new IOException("The SFTP known-hosts file was not materialized.");
            return;
        }

        var attributes = File.GetAttributes(destination);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new IOException("The SFTP known-hosts destination must be a regular file without reparse or device attributes.");
    }

    private static string ValidateLeafName(string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName) || leafName.Length > 128 || leafName is "." or ".." ||
            leafName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || leafName.EndsWith(' ') || leafName.EndsWith('.') ||
            !string.Equals(Path.GetFileName(leafName), leafName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The SFTP known-hosts leaf name is invalid.", nameof(leafName));
        }
        return leafName;
    }

    private sealed record PendingReview(
        SftpHostKeyReview Review,
        TrustedSftpHostKey Proposed,
        TrustedSftpHostKey? TrustedBeforeReview);
}
