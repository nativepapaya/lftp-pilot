using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed partial class MirrorPlanner : IMirrorPlanner, IDisposable
{
    private const int MaximumPreviewActions = 10_000;
    private const int MaximumPreviewPathCharacters = 512 * 1024;
    private readonly byte[] _approvalKey;
    private readonly TimeSpan _previewLifetime;
    private bool _disposed;

    public MirrorPlanner(TimeSpan? previewLifetime = null, byte[]? approvalKey = null)
    {
        _previewLifetime = previewLifetime ?? TimeSpan.FromMinutes(5);
        if (_previewLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(previewLifetime));
        _approvalKey = approvalKey?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
        if (_approvalKey.Length < 32) throw new ArgumentException("The approval key must be at least 256 bits.", nameof(approvalKey));
    }

    public MirrorPreview CreatePreview(MirrorDefinition definition, IEnumerable<string> dryRunOutput, DateTimeOffset? now = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PlanValidator.Validate(definition);
        ArgumentNullException.ThrowIfNull(dryRunOutput);
        var generatedAt = now ?? DateTimeOffset.UtcNow;
        var actions = ImmutableArray.CreateBuilder<MirrorAction>();
        // NTFS directories can opt into case-sensitive names. Treat every
        // preview path as exact-case so distinct A.txt/a.txt removals can
        // never collapse into one reviewed action.
        var deletionKeys = new HashSet<string>(StringComparer.Ordinal);
        var pathCharacters = 0;
        foreach (var line in dryRunOutput)
        {
            var action = ParseAction(line, definition);
            if (action is null)
            {
                if (DestructiveDryRunLineRegex().IsMatch(line ?? string.Empty))
                    throw new InvalidDataException("The LFTP dry-run contained an unrecognized deletion action.");
                continue;
            }
            if (action.IsDeletion && !deletionKeys.Add($"{action.Kind}\u001f{action.Path}")) continue;
            if (action.Path.Length > 4096 || (pathCharacters += action.Path.Length) > MaximumPreviewPathCharacters || actions.Count >= MaximumPreviewActions)
                throw new InvalidDataException("The mirror preview is too large to review safely as one operation. Narrow the mirror with include or exclude rules.");
            actions.Add(action);
        }
        var previewId = Guid.NewGuid();
        var expiresAt = generatedAt + _previewLifetime;
        var fingerprint = Fingerprint(definition);
        var unsigned = new MirrorPreview(previewId, definition.Id, generatedAt, expiresAt, actions.ToImmutable(), fingerprint, string.Empty);
        return unsigned with { ApprovalToken = CreateToken(unsigned) };
    }

    public string BuildExecutionCommand(MirrorDefinition definition, MirrorPreview preview, string? approvalToken, DateTimeOffset? now = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PlanValidator.Validate(definition);
        ArgumentNullException.ThrowIfNull(preview);
        var current = now ?? DateTimeOffset.UtcNow;
        if (preview.DefinitionId != definition.Id || !string.Equals(preview.DefinitionFingerprint, Fingerprint(definition), StringComparison.Ordinal))
            throw new InvalidOperationException("The mirror definition changed after the preview was generated. Generate a new preview.");
        if (current < preview.GeneratedAt - TimeSpan.FromSeconds(5) || current > preview.ExpiresAt)
            throw new InvalidOperationException("The mirror preview is stale. Generate a new preview.");

        if (definition.DeleteExtraneous || preview.ContainsDeletions)
        {
            if (string.IsNullOrWhiteSpace(approvalToken))
                throw new InvalidOperationException("Deletion-capable mirrors require explicit approval of a fresh preview.");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(CreateToken(preview)), Encoding.UTF8.GetBytes(approvalToken)))
                throw new InvalidOperationException("The mirror preview approval token is invalid.");
        }

        return LftpCommandBuilder.BuildMirror(definition, dryRun: false);
    }

    public static string Fingerprint(MirrorDefinition definition)
    {
        PlanValidator.Validate(definition);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "LFTPPilot.MirrorDefinition.v1");
        AppendGuid(hash, definition.Id);
        AppendGuid(hash, definition.ProfileId);
        AppendString(hash, definition.Name);
        AppendInt32(hash, (int)definition.Direction);
        AppendString(hash, definition.LocalRoot);
        AppendString(hash, definition.RemoteRoot);
        AppendInt32(hash, definition.EffectiveIncludes.Length);
        foreach (var include in definition.EffectiveIncludes) AppendString(hash, include);
        AppendInt32(hash, definition.EffectiveExcludes.Length);
        foreach (var exclude in definition.EffectiveExcludes) AppendString(hash, exclude);
        AppendInt32(hash, definition.DeleteExtraneous ? 1 : 0);
        AppendInt32(hash, definition.ParallelFiles);
        AppendInt32(hash, definition.SegmentsPerFile);
        if (definition.RateLimitBytesPerSecond is { } rateLimit)
        {
            AppendInt32(hash, 1);
            AppendInt64(hash, rateLimit);
        }
        else
        {
            AppendInt32(hash, 0);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string ReviewFingerprint(MirrorPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendGuid(hash, preview.Id);
        AppendGuid(hash, preview.DefinitionId);
        AppendInt64(hash, preview.GeneratedAt.UtcTicks);
        AppendInt64(hash, preview.GeneratedAt.Offset.Ticks);
        AppendInt64(hash, preview.ExpiresAt.UtcTicks);
        AppendInt64(hash, preview.ExpiresAt.Offset.Ticks);
        AppendString(hash, preview.DefinitionFingerprint);
        AppendInt32(hash, preview.Actions.IsDefault ? -1 : preview.Actions.Length);
        if (!preview.Actions.IsDefault)
        {
            foreach (var action in preview.Actions)
            {
                if (action is null)
                {
                    AppendInt32(hash, -1);
                    continue;
                }
                AppendInt32(hash, (int)action.Kind);
                AppendString(hash, action.Path);
                if (action.Detail is null)
                {
                    AppendInt32(hash, -1);
                }
                else
                {
                    AppendString(hash, action.Detail);
                }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_approvalKey);
        _disposed = true;
    }

    private static MirrorAction? ParseAction(string line, MirrorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        Match match;
        if ((match = TransferRegex().Match(line)).Success)
            return new(definition.Direction == MirrorDirection.Download ? MirrorActionKind.Download : MirrorActionKind.Upload, match.Groups[1].Value);
        if ((match = MakeDirectoryRegex().Match(line)).Success)
            return new(MirrorActionKind.CreateDirectory, match.Groups[1].Value);
        if ((match = RemoveFileRegex().Match(line)).Success)
            return new(MirrorActionKind.DeleteFile, NormalizeDescriptiveDeletionPath(match.Groups[1].Value, definition));
        if ((match = RemoveDirectoryRegex().Match(line)).Success)
            return new(MirrorActionKind.DeleteDirectory, NormalizeDescriptiveDeletionPath(match.Groups[1].Value, definition));
        if ((match = RawRemoveRegex().Match(line)).Success)
        {
            var kind = match.Groups["recursive"].Success ? MirrorActionKind.DeleteDirectory : MirrorActionKind.DeleteFile;
            return new(kind, NormalizeGeneratedDeletionTarget(match.Groups["target"].Value, definition));
        }
        if ((match = RawRemoveDirectoryRegex().Match(line)).Success)
            return new(MirrorActionKind.DeleteDirectory, NormalizeGeneratedDeletionTarget(match.Groups["target"].Value, definition));
        return null;
    }

    private static string NormalizeDescriptiveDeletionPath(string value, MirrorDefinition definition)
    {
        var candidate = value.Trim();
        RejectPathControls(candidate);
        if (definition.Direction == MirrorDirection.Download)
        {
            var root = NormalizeAbsolutePath(LftpCommandBuilder.ToMsysPath(definition.LocalRoot));
            if (Path.IsPathFullyQualified(candidate)) candidate = LftpCommandBuilder.ToMsysPath(candidate);
            return candidate.StartsWith("/", StringComparison.Ordinal)
                ? RelativeToReviewedRoot(NormalizeAbsolutePath(candidate), root, StringComparison.Ordinal)
                : NormalizeRelativePath(candidate);
        }

        var remoteRoot = NormalizeAbsolutePath(definition.RemoteRoot);
        return candidate.StartsWith("/", StringComparison.Ordinal)
            ? RelativeToReviewedRoot(NormalizeAbsolutePath(candidate), remoteRoot, StringComparison.Ordinal)
            : NormalizeRelativePath(candidate);
    }

    private static string NormalizeGeneratedDeletionTarget(string value, MirrorDefinition definition)
    {
        ValidateGeneratedTargetToken(value);
        if (definition.Direction == MirrorDirection.Download)
        {
            if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A generated local deletion must use a file URL.");
            var encodedPath = value["file:".Length..];
            var decodedPath = DecodeUrlPath(encodedPath);
            if (decodedPath.StartsWith("///", StringComparison.Ordinal)) decodedPath = decodedPath[2..];
            RejectPathControls(decodedPath);
            var root = NormalizeAbsolutePath(LftpCommandBuilder.ToMsysPath(definition.LocalRoot));
            return RelativeToReviewedRoot(NormalizeAbsolutePath(decodedPath), root, StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ftp" or "ftps" or "sftp") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("A generated remote deletion must use a supported endpoint URL without query or fragment syntax.");
        var decodedRemotePath = DecodeUrlPath(uri.AbsolutePath);
        RejectPathControls(decodedRemotePath);
        return RelativeToReviewedRoot(
            NormalizeAbsolutePath(decodedRemotePath),
            NormalizeAbsolutePath(definition.RemoteRoot),
            StringComparison.Ordinal);
    }

    private static void ValidateGeneratedTargetToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(
                ['\0', '\r', '\n', ';', '&', '|', '`', '$', '(', ')', '<', '>', '"', '\'', '\\', '*', '[', ']', '{', '}']) >= 0)
            throw new InvalidDataException("The generated deletion target contains command or glob syntax.");
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !Uri.IsHexDigit(value[index + 1]) || !Uri.IsHexDigit(value[index + 2]))
                throw new InvalidDataException("The generated deletion target contains invalid URL escaping.");
            index += 2;
        }
    }

    private static string DecodeUrlPath(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            throw new InvalidDataException("The generated deletion target contains invalid URL escaping.", exception);
        }
    }

    private static string NormalizeAbsolutePath(string value)
    {
        RejectPathControls(value);
        if (!value.StartsWith("/", StringComparison.Ordinal) || value.Contains('\\'))
            throw new InvalidDataException("A generated deletion path is not an absolute slash-delimited path.");
        var isUnc = value.StartsWith("//", StringComparison.Ordinal);
        var minimumLength = isUnc ? 2 : 1;
        if (value.Length > minimumLength) value = value.TrimEnd('/');
        var segments = value.TrimStart('/').Split('/', StringSplitOptions.None);
        if (segments.Length == 1 && segments[0].Length == 0)
        {
            if (isUnc) throw new InvalidDataException("A UNC deletion path requires a server and share root.");
            return "/";
        }
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("A generated deletion path contains an empty, current-directory, or parent-directory segment.");
        return (isUnc ? "//" : "/") + string.Join('/', segments);
    }

    private static string NormalizeRelativePath(string value)
    {
        RejectPathControls(value);
        if (value.Contains('\\'))
            throw new InvalidDataException("A deletion preview path must use slash separators.");
        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("A deletion preview path contains an empty, current-directory, or parent-directory segment.");
        return string.Join('/', segments);
    }

    private static string RelativeToReviewedRoot(string path, string root, StringComparison comparison)
    {
        if (string.Equals(path, root, comparison))
            throw new InvalidDataException("A generated deletion cannot remove the reviewed mirror root itself.");
        var prefix = root == "/" ? "/" : root + "/";
        if (!path.StartsWith(prefix, comparison))
            throw new InvalidDataException("A generated deletion falls outside the reviewed mirror destination root.");
        return NormalizeRelativePath(path[prefix.Length..]);
    }

    private static void RejectPathControls(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException("A deletion preview path is empty or contains a protocol control character.");
    }

    private string CreateToken(MirrorPreview preview)
    {
        var input = Encoding.UTF8.GetBytes(ReviewFingerprint(preview));
        return Convert.ToBase64String(HMACSHA256.HashData(_approvalKey, input));
    }

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendInt32(hash, -1);
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    [GeneratedRegex("^Transferring file [`'](.+?)'$", RegexOptions.CultureInvariant)]
    private static partial Regex TransferRegex();

    [GeneratedRegex("^Making directory [`'](.+?)'$", RegexOptions.CultureInvariant)]
    private static partial Regex MakeDirectoryRegex();

    [GeneratedRegex("^Removing old (?:(?:local|remote) )?file [`'](.+?)'$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveFileRegex();

    [GeneratedRegex("^Removing old (?:(?:local|remote) )?directory [`'](.+?)'$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveDirectoryRegex();

    [GeneratedRegex(@"^rm(?<recursive> -r)? (?<target>\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RawRemoveRegex();

    [GeneratedRegex(@"^rmdir (?<target>\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RawRemoveDirectoryRegex();

    [GeneratedRegex(@"\b(?:remov(?:e|ed|es|ing)|delet(?:e|ed|es|ing)|rm|rmdir|unlink(?:ed|s|ing)?|purg(?:e|ed|es|ing))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveDryRunLineRegex();
}
