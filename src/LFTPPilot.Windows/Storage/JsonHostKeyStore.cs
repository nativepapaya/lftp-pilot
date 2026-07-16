using System.Security.Cryptography;
using System.Text.Json;
using LFTPPilot.Core;

namespace LFTPPilot.Windows.Storage;

public sealed class JsonHostKeyStore : IHostKeyStore
{
    private const int MaximumRecords = 512;
    private const int MaximumJsonBytes = 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonHostKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        if (_path.Length > 32_767 || _path.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            _path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            _path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The trusted SFTP host-key store requires a bounded non-device path.", nameof(path));
        }
    }

    public async Task<TrustedSftpHostKey?> GetAsync(
        HostKeyBinding binding,
        CancellationToken cancellationToken = default)
    {
        ValidateBinding(binding);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return records.FirstOrDefault(record => record.Binding == binding);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var index = records.FindIndex(record => record.Binding == key.Binding);
            if (index >= 0) records[index] = key;
            else
            {
                if (records.Count >= MaximumRecords)
                    throw new InvalidDataException("The trusted SFTP host-key store has reached its record limit.");
                records.Add(key);
            }

            await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (records.RemoveAll(record => record.Binding.ProfileId == profileId) > 0)
                await WriteAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<TrustedSftpHostKey>> ReadAsync(CancellationToken cancellationToken)
    {
        ValidateSafeStorePath();
        if (!File.Exists(_path)) return [];
        var info = new FileInfo(_path);
        if (info.Length > MaximumJsonBytes)
            throw new InvalidDataException("The trusted SFTP host-key store exceeds its size limit.");

        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var records = await JsonSerializer.DeserializeAsync<List<TrustedSftpHostKey>>(
                stream,
                _options,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The trusted SFTP host-key store is empty.");
            if (records.Count > MaximumRecords)
                throw new InvalidDataException("The trusted SFTP host-key store contains too many records.");

            var bindings = new HashSet<HostKeyBinding>();
            foreach (var record in records)
            {
                ValidateKey(record);
                if (!bindings.Add(record.Binding))
                    throw new InvalidDataException("The trusted SFTP host-key store contains a duplicate binding.");
            }

            return records;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The trusted SFTP host-key store contains invalid JSON.", exception);
        }
    }

    private async Task WriteAsync(List<TrustedSftpHostKey> records, CancellationToken cancellationToken)
    {
        records.Sort(static (left, right) =>
        {
            var profile = left.Binding.ProfileId.CompareTo(right.Binding.ProfileId);
            return profile != 0 ? profile : string.CompareOrdinal(left.Binding.Endpoint, right.Binding.Endpoint);
        });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, _options);
        if (bytes.Length > MaximumJsonBytes)
            throw new InvalidDataException("The trusted SFTP host-key store exceeds its size limit.");
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The trusted SFTP host-key store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        ValidateSafeStorePath();
        await AtomicFile.WriteBytesAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
        ValidateSafeStorePath(requireExistingFile: true);
    }

    private void ValidateSafeStorePath(bool requireExistingFile = false)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The trusted SFTP host-key store requires a parent directory.");
        ValidateNoReparseAncestors(directory);
        if (!File.Exists(_path))
        {
            if (Directory.Exists(_path))
                throw new InvalidDataException("The trusted SFTP host-key store path cannot be a directory.");
            if (requireExistingFile)
                throw new InvalidDataException("The trusted SFTP host-key store write did not create a regular file.");
            return;
        }

        if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("The trusted SFTP host-key store must be a regular non-reparse file.");
    }

    private static void ValidateNoReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The trusted SFTP host-key store path cannot contain a reparse point.");
            }
        }
    }

    private static void ValidateKey(TrustedSftpHostKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateBinding(key.Binding);
        if (string.IsNullOrEmpty(key.Algorithm) || key.Algorithm.Length > 128 ||
            key.Algorithm.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not '+' and not '@'))
        {
            throw new InvalidDataException("The trusted SFTP host-key algorithm is invalid.");
        }

        if (string.IsNullOrEmpty(key.PublicKeyBase64) || key.PublicKeyBase64.Length > 16 * 1024 ||
            key.PublicKeyBase64.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("The trusted SFTP public-key blob is invalid.");
        }

        byte[] publicKey;
        try { publicKey = Convert.FromBase64String(key.PublicKeyBase64); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The trusted SFTP public-key blob is not valid Base64.", exception);
        }
        if (publicKey.Length == 0 || !string.Equals(Convert.ToBase64String(publicKey), key.PublicKeyBase64, StringComparison.Ordinal))
            throw new InvalidDataException("The trusted SFTP public-key blob is not canonical Base64.");

        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');
        if (!string.Equals(fingerprint, key.FingerprintSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The trusted SFTP host-key fingerprint does not match its public key.");
    }

    private static void ValidateBinding(HostKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.ProfileId == Guid.Empty || string.IsNullOrEmpty(binding.Endpoint) || binding.Endpoint.Length > 2048 ||
            binding.Endpoint.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            !binding.Endpoint.StartsWith("sftp://", StringComparison.Ordinal) ||
            binding.Endpoint.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("The trusted SFTP host-key binding is invalid.");
        }

        var authority = binding.Endpoint["sftp://".Length..];
        if (authority.IndexOfAny(['/', '?', '#', '@']) >= 0)
            throw new InvalidDataException("The trusted SFTP host-key endpoint must contain only a host and explicit port.");
        var portSeparator = authority.LastIndexOf(':');
        if (portSeparator <= 0 || portSeparator == authority.Length - 1 ||
            !int.TryParse(authority[(portSeparator + 1)..], out var port) || port is < 1 or > 65_535 ||
            !string.Equals(port.ToString(System.Globalization.CultureInfo.InvariantCulture), authority[(portSeparator + 1)..], StringComparison.Ordinal))
        {
            throw new InvalidDataException("The trusted SFTP host-key endpoint must contain a canonical explicit port.");
        }

        var host = authority[..portSeparator];
        var bracketed = host.Length >= 2 && host[0] == '[' && host[^1] == ']';
        if (host.Length == 0 || host.Any(char.IsUpper) ||
            bracketed && (host.Length == 2 || !host[1..^1].Contains(':')) ||
            !bracketed && (host.Contains(':') || host.Contains('[') || host.Contains(']')))
        {
            throw new InvalidDataException("The trusted SFTP host-key endpoint host is not canonical.");
        }
    }
}
