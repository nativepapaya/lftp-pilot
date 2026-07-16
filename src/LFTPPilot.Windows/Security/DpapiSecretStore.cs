using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Core;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Windows.Security;

/// <summary>DPAPI CurrentUser store whose entropy binds a credential to its complete profile identity.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private const int MaximumSecretBytes = 64 * 1024;
    private static readonly byte[] Magic = "LFTPPILOT-SECRET\0"u8.ToArray();
    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (string.IsNullOrEmpty(secret.Value)) throw new ArgumentException("A secret cannot be empty.", nameof(secret));
        byte[] entropy = GetEntropy(secret.Binding);
        byte[] plaintext = Encoding.UTF8.GetBytes(secret.Value);
        if (plaintext.Length > MaximumSecretBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            throw new ArgumentException("A secret exceeds the bounded credential size.", nameof(secret));
        }
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
            byte[] envelope = new byte[Magic.Length + entropy.Length + sizeof(int) + encrypted.Length];
            Magic.CopyTo(envelope, 0);
            entropy.CopyTo(envelope, Magic.Length);
            BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(Magic.Length + entropy.Length, sizeof(int)), encrypted.Length);
            encrypted.CopyTo(envelope, Magic.Length + entropy.Length + sizeof(int));
            await AtomicFile.WriteBytesAsync(GetPath(secret.Binding), envelope, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(envelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(entropy);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        string path = GetPath(binding);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumSecretBytes * 2L)
            throw new CryptographicException("The credential envelope exceeds its bounded size.");
        byte[] envelope = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] entropy = GetEntropy(binding);
        byte[]? plaintext = null;
        try
        {
            int headerLength = Magic.Length + entropy.Length + sizeof(int);
            if (envelope.Length < headerLength || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                !CryptographicOperations.FixedTimeEquals(envelope.AsSpan(Magic.Length, entropy.Length), entropy))
                throw new CryptographicException("The credential is invalid or belongs to another profile identity.");

            int length = BinaryPrimitives.ReadInt32LittleEndian(envelope.AsSpan(Magic.Length + entropy.Length, sizeof(int)));
            if (length <= 0 || length != envelope.Length - headerLength)
                throw new CryptographicException("The credential envelope length is invalid.");
            plaintext = ProtectedData.Unprotect(envelope.AsSpan(headerLength, length).ToArray(), entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(entropy);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty || !Directory.Exists(_directory)) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string path in Directory.EnumerateFiles(_directory, $"{profileId:N}.*.secret", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string GetPath(SecretBinding binding)
    {
        Directory.CreateDirectory(_directory);
        byte[] entropy = GetEntropy(binding);
        try
        {
            string key = Convert.ToHexString(entropy).ToLowerInvariant();
            return Path.Combine(_directory, $"{binding.ProfileId:N}.{key}.secret");
        }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    private static byte[] GetEntropy(SecretBinding binding)
    {
        if (binding.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(binding.Endpoint) ||
            string.IsNullOrWhiteSpace(binding.UserName) || string.IsNullOrWhiteSpace(binding.Purpose))
            throw new ArgumentException("A complete secret binding is required.", nameof(binding));
        return SHA256.HashData(Encoding.UTF8.GetBytes("lftp-pilot-secret:v1\0" + binding.CanonicalIdentity));
    }
}
