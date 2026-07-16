using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public static class SftpHostKeyIdentity
{
    public static HostKeyBinding CreateBinding(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileValidator.ThrowIfInvalid(profile);
        if (profile.Protocol != ConnectionProtocol.Sftp)
            throw new ArgumentException("SSH host-key identity is available only for SFTP profiles.", nameof(profile));

        var host = profile.Host.Trim();
        var bracketed = host.Length >= 2 && host[0] == '[' && host[^1] == ']';
        if (bracketed) host = host[1..^1];
        if (host.Length == 0 || host[0] == '-' || host.Contains('[') || host.Contains(']'))
            throw new ArgumentException("The SFTP host must be a DNS name or IP address, not an OpenSSH option or bracket fragment.", nameof(profile));
        var hostNameType = Uri.CheckHostName(host);
        if (hostNameType is not UriHostNameType.Dns and not UriHostNameType.IPv4 and not UriHostNameType.IPv6 ||
            bracketed && hostNameType != UriHostNameType.IPv6)
        {
            throw new ArgumentException("The SFTP host must be a valid DNS name, IPv4 address, or IPv6 address.", nameof(profile));
        }
        host = host.ToLowerInvariant();
        if (host.Contains(':')) host = $"[{host}]";
        return new(profile.Id, $"sftp://{host}:{profile.Port}");
    }

    public static string CreateHostKeyAlias(HostKeyBinding binding)
    {
        ValidateBinding(binding);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Endpoint)));
        return $"lftp-pilot-{binding.ProfileId:N}-{hash[..32]}";
    }

    public static void ValidateAlias(string? alias, string parameterName)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 128 || alias.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "An SSH host-key alias may contain only 1-128 ASCII letters, digits, hyphens, underscores, or periods.",
                parameterName);
        }
    }

    public static string CreateUserKnownHostsOption(string posixPath)
    {
        if (string.IsNullOrWhiteSpace(posixPath) || posixPath.Length > 32_767 ||
            posixPath[0] != '/' ||
            posixPath.IndexOfAny(['\0', '\r', '\n', '"', '\\']) >= 0 ||
            posixPath.Contains("${", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The SSH known-hosts path cannot be represented safely as an OpenSSH configuration value.",
                nameof(posixPath));
        }

        // OpenSSH expands percent tokens even inside quotes. Doubling each
        // percent preserves it as path text; ${...} is rejected above because
        // environment expansion has no equivalent literal escape.
        var escaped = posixPath.Replace("%", "%%", StringComparison.Ordinal);
        return $"UserKnownHostsFile=\"{escaped}\"";
    }

    internal static void ValidateBinding(HostKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(binding.Endpoint) || binding.Endpoint.Length > 512 ||
            !binding.Endpoint.StartsWith("sftp://", StringComparison.Ordinal) ||
            binding.Endpoint.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("A bounded SFTP host-key binding is required.", nameof(binding));
        }
    }
}

public static class SshKnownHostsParser
{
    public const int MaximumDocumentCharacters = 64 * 1024;
    public const int MaximumKeyBlobBytes = 16 * 1024;

    public static TrustedSftpHostKey Parse(string text, HostKeyBinding binding, string expectedAlias)
    {
        ArgumentNullException.ThrowIfNull(text);
        SftpHostKeyIdentity.ValidateBinding(binding);
        SftpHostKeyIdentity.ValidateAlias(expectedAlias, nameof(expectedAlias));
        if (text.Length is 0 or > MaximumDocumentCharacters || text.IndexOf('\0') >= 0)
            throw new InvalidDataException("The SSH host-key proposal has an invalid size or contains a control character.");

        var line = RemoveOneTrailingLineEnding(text);
        if (line.Length == 0 || line.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The SSH host-key proposal must contain exactly one entry.");

        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !string.Equals(fields[0], expectedAlias, StringComparison.Ordinal))
            throw new InvalidDataException("The SSH host-key proposal is not bound to the expected endpoint alias.");

        var algorithm = fields[1];
        if (!IsAlgorithmName(algorithm) || algorithm.EndsWith("-cert-v01@openssh.com", StringComparison.Ordinal))
            throw new InvalidDataException("The SSH host-key proposal uses an unsupported key algorithm.");

        var encodedKey = fields[2];
        if (encodedKey.Length == 0 || encodedKey.Length > ((MaximumKeyBlobBytes + 2) / 3) * 4)
            throw new InvalidDataException("The SSH host-key proposal contains an invalid public-key blob.");

        byte[] keyBlob;
        try
        {
            keyBlob = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The SSH host-key proposal contains invalid Base64.", exception);
        }

        if (keyBlob.Length is < 8 or > MaximumKeyBlobBytes ||
            !string.Equals(Convert.ToBase64String(keyBlob), encodedKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The SSH host-key proposal contains a non-canonical public-key blob.");
        }

        var embeddedAlgorithm = ReadLeadingSshString(keyBlob);
        if (!string.Equals(embeddedAlgorithm, algorithm, StringComparison.Ordinal))
            throw new InvalidDataException("The SSH host-key algorithm does not match its public-key blob.");

        var fingerprint = Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');
        return new(binding, algorithm, encodedKey, $"SHA256:{fingerprint}");
    }

    public static string Format(string alias, TrustedSftpHostKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var line = $"{alias} {key.Algorithm} {key.PublicKeyBase64}\n";
        var parsed = Parse(line, key.Binding, alias);
        if (!string.Equals(parsed.FingerprintSha256, key.FingerprintSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The stored SSH host-key fingerprint does not match its public key.");
        return line;
    }

    private static string RemoveOneTrailingLineEnding(string value)
    {
        if (value.EndsWith("\r\n", StringComparison.Ordinal)) return value[..^2];
        if (value.EndsWith('\n') || value.EndsWith('\r')) return value[..^1];
        return value;
    }

    private static bool IsAlgorithmName(string value) => value.Length is >= 1 and <= 128 && value.All(static character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '+' or '_' or '.' or '@');

    private static string ReadLeadingSshString(ReadOnlySpan<byte> blob)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length is 0 or > 128 || length > blob.Length - sizeof(uint))
            throw new InvalidDataException("The SSH host-key blob has an invalid algorithm field.");
        var algorithmBytes = blob.Slice(sizeof(uint), checked((int)length));
        foreach (var value in algorithmBytes)
            if (value > 0x7f)
                throw new InvalidDataException("The SSH host-key blob has a non-ASCII algorithm field.");
        return Encoding.ASCII.GetString(algorithmBytes);
    }
}
