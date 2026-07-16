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
        var pathCharacters = 0;
        foreach (var line in dryRunOutput)
        {
            var action = ParseAction(line, definition.Direction);
            if (action is null)
            {
                if (DestructiveDryRunLineRegex().IsMatch(line ?? string.Empty))
                    throw new InvalidDataException($"The LFTP dry-run contained an unrecognized deletion action: {BoundedDiagnostic(line)}");
                continue;
            }
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

        if (definition.DeleteExtraneous)
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
        var canonical = string.Join('\u001f',
            definition.Id.ToString("N"), definition.ProfileId.ToString("N"), definition.Name,
            definition.Direction.ToString(), definition.LocalRoot, definition.RemoteRoot,
            definition.DeleteExtraneous.ToString(), definition.ParallelFiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
            definition.SegmentsPerFile.ToString(System.Globalization.CultureInfo.InvariantCulture),
            definition.RateLimitBytesPerSecond?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join('\u001e', definition.EffectiveIncludes), string.Join('\u001e', definition.EffectiveExcludes));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_approvalKey);
        _disposed = true;
    }

    private static MirrorAction? ParseAction(string line, MirrorDirection direction)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        Match match;
        if ((match = TransferRegex().Match(line)).Success)
            return new(direction == MirrorDirection.Download ? MirrorActionKind.Download : MirrorActionKind.Upload, match.Groups[1].Value);
        if ((match = MakeDirectoryRegex().Match(line)).Success)
            return new(MirrorActionKind.CreateDirectory, match.Groups[1].Value);
        if ((match = RemoveFileRegex().Match(line)).Success)
            return new(MirrorActionKind.DeleteFile, match.Groups[1].Value);
        if ((match = RemoveDirectoryRegex().Match(line)).Success)
            return new(MirrorActionKind.DeleteDirectory, match.Groups[1].Value);
        return null;
    }

    private static string BoundedDiagnostic(string? value) => string.IsNullOrEmpty(value)
        ? "<empty>"
        : value.Length <= 256 ? value : value[..256] + "...";

    private string CreateToken(MirrorPreview preview)
    {
        var actions = string.Join('\u001e', preview.Actions.Select(static action => $"{action.Kind}:{action.Path}:{action.Detail}"));
        var input = Encoding.UTF8.GetBytes($"{preview.Id:N}|{preview.DefinitionId:N}|{preview.GeneratedAt.UtcTicks}|{preview.ExpiresAt.UtcTicks}|{preview.DefinitionFingerprint}|{actions}");
        return Convert.ToBase64String(HMACSHA256.HashData(_approvalKey, input));
    }

    [GeneratedRegex("Transferring file [`'](.+?)'", RegexOptions.CultureInvariant)]
    private static partial Regex TransferRegex();

    [GeneratedRegex("Making directory [`'](.+?)'", RegexOptions.CultureInvariant)]
    private static partial Regex MakeDirectoryRegex();

    [GeneratedRegex("Removing old file [`'](.+?)'", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveFileRegex();

    [GeneratedRegex("Removing old directory [`'](.+?)'", RegexOptions.CultureInvariant)]
    private static partial Regex RemoveDirectoryRegex();

    [GeneratedRegex(@"\b(?:remov(?:e|ed|es|ing)|delet(?:e|ed|es|ing)|rm|rmdir|unlink(?:ed|s|ing)?|purg(?:e|ed|es|ing))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DestructiveDryRunLineRegex();
}
