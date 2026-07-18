using System.Collections.Immutable;

namespace LFTPPilot.Core;

public sealed record ValidationIssue(string Field, string Code, string Message);

public sealed class ModelValidationException(IReadOnlyList<ValidationIssue> issues)
    : ArgumentException(string.Join(" ", issues.Select(static issue => issue.Message)))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

public static class ProfileValidator
{
    public static ImmutableArray<ValidationIssue> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();

        if (profile.Id == Guid.Empty)
            issues.Add(new("id", "required", "The profile identifier cannot be empty."));
        ValidateText(profile.Name, "name", 1, 120, issues);
        ValidateText(profile.Host, "host", 1, 253, issues);
        ValidateText(profile.UserName, "userName", profile.Authentication == AuthenticationKind.Anonymous ? 0 : 1, 256, issues);

        if (!Enum.IsDefined(profile.Protocol)) issues.Add(new("protocol", "unsupported", "The connection protocol is not supported."));
        if (!Enum.IsDefined(profile.Authentication)) issues.Add(new("authentication", "unsupported", "The authentication mode is not supported."));
        if (profile.Port is < 1 or > 65_535)
            issues.Add(new("port", "range", "The port must be between 1 and 65535."));
        if (profile.Host is not null && (profile.Host.Contains("://", StringComparison.Ordinal) ||
            profile.Host.Any(char.IsWhiteSpace) || profile.Host.Contains('/') || profile.Host.Contains('\\') ||
            !IsDnsOrIpHost(profile.Host)))
            issues.Add(new("host", "format", "The host must be a DNS name or IP address without a scheme, path, or whitespace."));
        if (profile.Authentication == AuthenticationKind.SshKey && profile.Protocol != ConnectionProtocol.Sftp)
            issues.Add(new("authentication", "unsupported", "SSH key authentication is available only for SFTP."));
        if (profile.Authentication == AuthenticationKind.SshKey && string.IsNullOrWhiteSpace(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "required", "An SSH key path is required for key authentication."));
        if (profile.SshKeyPath is { Length: > 32_767 })
            issues.Add(new("sshKeyPath", "length", "The SSH key path cannot exceed 32767 characters."));
        if (profile.Authentication == AuthenticationKind.SshKey &&
            !string.IsNullOrWhiteSpace(profile.SshKeyPath) &&
            !Path.IsPathFullyQualified(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "absolute", "The SSH key path must be fully qualified."));
        if (profile.Authentication == AuthenticationKind.SshKey &&
            !string.IsNullOrWhiteSpace(profile.SshKeyPath) &&
            IsWindowsDevicePath(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "device-path", "Windows device-namespace SSH key paths are not supported."));
        if (profile.SshKeyPath is not null && ContainsProtocolControl(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "control-character", "The SSH key path contains a prohibited control character."));
        if (profile.InitialRemotePath is not null && !IsCanonicalRemotePath(profile.InitialRemotePath))
            issues.Add(new("initialRemotePath", "absolute", "The initial remote path must be a bounded canonical path beginning with '/'."));
        if (profile.InitialLocalPath is not null && !Path.IsPathFullyQualified(profile.InitialLocalPath))
            issues.Add(new("initialLocalPath", "absolute", "The initial local path must be fully qualified."));
        foreach (var bookmark in profile.EffectiveBookmarks)
        {
            if (!IsCanonicalRemotePath(bookmark))
                issues.Add(new("bookmarks", "format", "Bookmarks must be bounded canonical remote paths."));
        }

        return issues.ToImmutable();
    }

    public static void ThrowIfInvalid(ConnectionProfile profile)
    {
        var issues = Validate(profile);
        if (issues.Length != 0)
            throw new ModelValidationException(issues);
    }

    public static int DefaultPort(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Sftp => 22,
        ConnectionProtocol.FtpsImplicit => 990,
        _ => 21,
    };

    internal static bool ContainsProtocolControl(string value) => value.IndexOfAny(['\0', '\r', '\n']) >= 0;

    internal static bool IsRemoteAbsolute(string value) => value.StartsWith("/", StringComparison.Ordinal) && !ContainsProtocolControl(value);

    public static bool IsCanonicalRemotePath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && IsRemoteAbsolute(value) &&
        !value.Contains("//", StringComparison.Ordinal) &&
        !value.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or "..") &&
        value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length <= 128 &&
        (value.Length == 1 || !value.EndsWith("/", StringComparison.Ordinal));

    private static bool IsWindowsDevicePath(string value)
    {
        var normalized = value.Replace('/', '\\');
        return normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\??\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\??\", StringComparison.Ordinal);
    }

    private static bool IsDnsOrIpHost(string value)
    {
        if (value.Length == 0 || value[0] == '-') return false;
        var bracketed = value.Length >= 2 && value[0] == '[' && value[^1] == ']';
        var host = bracketed ? value[1..^1] : value;
        if (host.Length == 0 || host.Contains('[') || host.Contains(']')) return false;
        var hostNameType = Uri.CheckHostName(host);
        if (bracketed) return hostNameType == UriHostNameType.IPv6;
        return hostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    private static void ValidateText(string? value, string field, int minimum, int maximum, ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (value is null)
        {
            issues.Add(new(field, "required", $"{field} is required."));
            return;
        }
        if (value.Length < minimum || value.Length > maximum)
            issues.Add(new(field, "length", $"{field} must contain between {minimum} and {maximum} characters."));
        if (ContainsProtocolControl(value))
            issues.Add(new(field, "control-character", $"{field} contains a prohibited control character."));
    }
}

public static class PlanValidator
{
    public static void Validate(TransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<ValidationIssue>();
        if (plan.Id == Guid.Empty) issues.Add(new("id", "required", "The transfer identifier cannot be empty."));
        if (plan.ProfileId == Guid.Empty) issues.Add(new("profileId", "required", "The profile identifier cannot be empty."));
        ValidatePath(plan.SourcePath, "sourcePath", issues);
        ValidatePath(plan.DestinationPath, "destinationPath", issues);
        if (!Enum.IsDefined(plan.Direction)) issues.Add(new("direction", "unsupported", "The transfer direction is not supported."));
        if (!Enum.IsDefined(plan.SourceKind)) issues.Add(new("sourceKind", "unsupported", "The transfer source kind is not supported."));
        if (!Enum.IsDefined(plan.Mode)) issues.Add(new("mode", "unsupported", "The transfer mode is not supported."));
        if (plan.SourceKind == TransferSourceKind.Directory && plan.Mode is not TransferMode.Auto and not TransferMode.Resume)
            issues.Add(new("mode", "unsupported", "Directory transfers support only automatic and resume modes."));
        var maximumSegments = plan.SourceKind == TransferSourceKind.Directory ? 16 : 64;
        if (plan.Segments < 1 || plan.Segments > maximumSegments)
            issues.Add(new("segments", "range", $"Segments must be between 1 and {maximumSegments}."));
        if (plan.Direction == TransferDirection.Upload && plan.Segments != 1)
            issues.Add(new("segments", "unsupported", "Upload transfers do not support segmented transfer."));
        if (plan.SourceKind == TransferSourceKind.File &&
            (plan.EffectiveIncludes.Length != 0 || plan.EffectiveExcludes.Length != 0 || plan.ParallelFiles != 1))
        {
            issues.Add(new("folderOptions", "unsupported", "Folder filters and parallelism apply only to directory transfers."));
        }
        if (plan.SourceKind == TransferSourceKind.Directory)
        {
            if (plan.ParallelFiles is < 1 or > FolderTransferPolicy.MaximumParallelFiles)
                issues.Add(new("parallelFiles", "range", $"Parallel files must be between 1 and {FolderTransferPolicy.MaximumParallelFiles}."));
            ValidatePatterns(plan.EffectiveIncludes, "includes", issues);
            ValidatePatterns(plan.EffectiveExcludes, "excludes", issues);
            ValidatePatternTotal(plan.EffectiveIncludes, plan.EffectiveExcludes, issues);
        }
        if (plan.RateLimitBytesPerSecond is <= 0) issues.Add(new("rateLimitBytesPerSecond", "range", "A rate limit must be positive."));
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    public static void Validate(FolderTransferPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var issues = new List<ValidationIssue>();
        if (preset.Id == Guid.Empty)
            issues.Add(new("id", "required", "The folder-transfer preset identifier cannot be empty."));
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > FolderTransferPolicy.MaximumNameLength)
            issues.Add(new("name", "length", $"Preset name must contain between 1 and {FolderTransferPolicy.MaximumNameLength} characters."));
        if (preset.Name is not null && ProfileValidator.ContainsProtocolControl(preset.Name))
            issues.Add(new("name", "control-character", "Preset name contains a prohibited control character."));
        if (preset.ParallelFiles is < 1 or > FolderTransferPolicy.MaximumParallelFiles)
            issues.Add(new("parallelFiles", "range", $"Parallel files must be between 1 and {FolderTransferPolicy.MaximumParallelFiles}."));
        if (preset.DownloadSegmentsPerFile is < 1 or > FolderTransferPolicy.MaximumSegmentsPerFile)
            issues.Add(new("downloadSegmentsPerFile", "range", $"Download segments per file must be between 1 and {FolderTransferPolicy.MaximumSegmentsPerFile}."));
        ValidatePatterns(preset.EffectiveIncludes, "includes", issues);
        ValidatePatterns(preset.EffectiveExcludes, "excludes", issues);
        ValidatePatternTotal(preset.EffectiveIncludes, preset.EffectiveExcludes, issues);
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    public static void Validate(MirrorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = new List<ValidationIssue>();
        if (definition.Id == Guid.Empty) issues.Add(new("id", "required", "The mirror identifier cannot be empty."));
        if (definition.ProfileId == Guid.Empty) issues.Add(new("profileId", "required", "The profile identifier cannot be empty."));
        ValidateLocalMirrorRoot(definition.LocalRoot, issues);
        ValidateRemoteMirrorRoot(definition.RemoteRoot, issues);
        ValidateMirrorName(definition.Name, issues);
        if (!Enum.IsDefined(definition.Direction)) issues.Add(new("direction", "unsupported", "The mirror direction is not supported."));
        if (definition.ParallelFiles is < 1 or > MirrorDefinitionPolicy.MaximumParallelFiles)
            issues.Add(new("parallelFiles", "range", $"Parallel files must be between 1 and {MirrorDefinitionPolicy.MaximumParallelFiles}."));
        if (definition.SegmentsPerFile is < 1 or > MirrorDefinitionPolicy.MaximumSegmentsPerFile)
            issues.Add(new("segmentsPerFile", "range", $"Segments per file must be between 1 and {MirrorDefinitionPolicy.MaximumSegmentsPerFile}."));
        if (definition.RateLimitBytesPerSecond is <= 0 or > MirrorDefinitionPolicy.MaximumRateLimitBytesPerSecond)
            issues.Add(new("rateLimitBytesPerSecond", "range", $"A rate limit must be between 1 and {MirrorDefinitionPolicy.MaximumRateLimitBytesPerSecond} bytes per second."));
        ValidatePatterns(definition.EffectiveIncludes, "includes", issues);
        ValidatePatterns(definition.EffectiveExcludes, "excludes", issues);
        ValidatePatternTotal(definition.EffectiveIncludes, definition.EffectiveExcludes, issues);
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    public static void Validate(RemoteTransferPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var issues = new List<ValidationIssue>();
        if (plan.Id == Guid.Empty) issues.Add(new("id", "required", "The remote transfer identifier cannot be empty."));
        if (plan.SourceProfileId == Guid.Empty) issues.Add(new("sourceProfileId", "required", "The source profile identifier cannot be empty."));
        if (plan.DestinationProfileId == Guid.Empty) issues.Add(new("destinationProfileId", "required", "The destination profile identifier cannot be empty."));
        if (plan.SourceProfileId == plan.DestinationProfileId) issues.Add(new("destinationProfileId", "distinct", "Remote transfer profiles must be distinct."));
        ValidateRemoteFilePath(plan.SourcePath, "sourcePath", issues);
        ValidateRemoteFilePath(plan.DestinationPath, "destinationPath", issues);
        if (!Enum.IsDefined(plan.Mode)) issues.Add(new("mode", "unsupported", "The remote transfer routing mode is unsupported."));
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    private static void ValidateRemoteFilePath(string? value, string field, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096 || !ProfileValidator.IsRemoteAbsolute(value))
        {
            issues.Add(new(field, "absolute", $"{field} must be a bounded absolute remote file path beginning with '/'."));
            return;
        }

        if (value == "/" || value.EndsWith("/", StringComparison.Ordinal))
            issues.Add(new(field, "file-path", $"{field} must identify a remote file, not a directory."));
        if (value.Contains("//", StringComparison.Ordinal) ||
            value.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            issues.Add(new(field, "ambiguous", $"{field} cannot contain empty, current-directory, or parent-directory segments."));
    }

    private static void ValidateLocalMirrorRoot(string? value, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32_767 ||
            !Path.IsPathFullyQualified(value) || ProfileValidator.ContainsProtocolControl(value))
        {
            issues.Add(new("localRoot", "absolute", "The local root must be a bounded fully qualified path."));
            return;
        }

        if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("//?/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("//./", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("localRoot", "device-path", "The local root cannot use a Windows device namespace."));
        }
        if (value.Split(['\\', '/'], StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            issues.Add(new("localRoot", "ambiguous", "The local root cannot contain current-directory or parent-directory segments."));
        var trailingSeparatorCount = value.Reverse().TakeWhile(static character => character is '\\' or '/').Count();
        var normalizedValue = value.Replace('/', '\\');
        var normalizedPathRoot = Path.GetPathRoot(value)?.Replace('/', '\\');
        if (trailingSeparatorCount > 0 &&
            (trailingSeparatorCount != 1 || !string.Equals(
                normalizedValue.TrimEnd('\\'),
                normalizedPathRoot?.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)))
            issues.Add(new("localRoot", "ambiguous", "A non-root local mirror path cannot end with a directory separator."));
    }

    private static void ValidateRemoteMirrorRoot(string? value, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !ProfileValidator.IsRemoteAbsolute(value))
        {
            issues.Add(new("remoteRoot", "absolute", "The remote root must be a bounded absolute path beginning with '/'."));
            return;
        }

        if (value.Contains("//", StringComparison.Ordinal) ||
            value.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or "..") ||
            value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 128 ||
            value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
        {
            issues.Add(new("remoteRoot", "ambiguous", "The remote root must be canonical and cannot contain duplicate separators, dot segments, or a trailing separator."));
        }
    }

    private static void ValidatePath(string? value, string field, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) issues.Add(new(field, "required", $"{field} is required."));
        else if (ProfileValidator.ContainsProtocolControl(value)) issues.Add(new(field, "control-character", $"{field} contains a prohibited control character."));
    }

    private static void ValidatePatterns(
        IReadOnlyCollection<string> patterns,
        string field,
        ICollection<ValidationIssue> issues)
    {
        if (patterns.Count > MirrorDefinitionPolicy.MaximumPatternsPerList)
        {
            issues.Add(new(
                field,
                "count",
                $"{field} cannot contain more than {MirrorDefinitionPolicy.MaximumPatternsPerList} patterns."));
        }

        foreach (var pattern in patterns)
        {
            ValidatePath(pattern, field, issues);
            if (pattern is { Length: > MirrorDefinitionPolicy.MaximumPatternLength })
            {
                issues.Add(new(
                    field,
                    "length",
                    $"Each {field} pattern cannot exceed {MirrorDefinitionPolicy.MaximumPatternLength} characters."));
            }
            if (pattern is not null && pattern.Any(char.IsControl) && !ProfileValidator.ContainsProtocolControl(pattern))
                issues.Add(new(field, "control-character", $"{field} contains a prohibited control character."));
        }
    }

    private static void ValidatePatternTotal(
        IEnumerable<string> includes,
        IEnumerable<string> excludes,
        ICollection<ValidationIssue> issues)
    {
        var totalPatternCharacters = includes.Sum(static pattern => (long)(pattern?.Length ?? 0)) +
            excludes.Sum(static pattern => (long)(pattern?.Length ?? 0));
        if (totalPatternCharacters > MirrorDefinitionPolicy.MaximumPatternCharactersPerDefinition)
        {
            issues.Add(new(
                "patterns",
                "length",
                $"Include and exclude patterns cannot exceed {MirrorDefinitionPolicy.MaximumPatternCharactersPerDefinition} total characters."));
        }
    }

    private static void ValidateMirrorName(string? value, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new("name", "required", "name is required."));
        else
        {
            if (value.Length > JobSnapshotPolicy.MaximumDisplayNameLength)
                issues.Add(new("name", "length", $"name cannot exceed {JobSnapshotPolicy.MaximumDisplayNameLength} characters."));
            if (JobSnapshotPolicy.ContainsControlCharacter(value))
                issues.Add(new("name", "control-character", "name contains a prohibited control character."));
        }
    }
}

public static class MirrorDefinitionPolicy
{
    public const int MaximumDefinitions = 512;
    public const int MaximumPatternsPerList = 256;
    public const int MaximumPatternLength = 4096;
    public const int MaximumPatternCharactersPerDefinition = 65_536;
    public const int MaximumAggregatePatternCharacters = 131_072;
    public const int MaximumSerializedStoreBytes = 256 * 1024;
    public const int MaximumParallelFiles = 16;
    public const int MaximumSegmentsPerFile = 64;
    public const long MaximumRateLimitBytesPerSecond = 1_000_000_000_000;
}

public static class FolderTransferPolicy
{
    public const int MaximumPresets = 64;
    public const int MaximumNameLength = 80;
    public const int MaximumParallelFiles = 16;
    public const int MaximumSegmentsPerFile = 16;
    public const int MaximumAggregatePatternCharacters = 65_536;
    public const int MaximumSerializedStoreBytes = 128 * 1024;
}

public static class RemoteSearchPolicy
{
    public const int DefaultMaxDepth = 32;
    public const int MaximumMaxDepth = 128;
    public const int MaximumQueryCharacters = 256;
    public const int MaximumPathCharacters = 4096;
    public const int MaximumOutputLineCharacters = MaximumPathCharacters + 1;
    public const int MaximumOutputLines = 100_000;
    public const int MaximumOutputBytes = 16 * 1024 * 1024;
    public const int MaximumMatches = 5_000;
    public const int MaximumStoredMatchBytes = 2 * 1024 * 1024;
    public const int DefaultPageSize = 256;
    public const int MaximumPageSize = 512;
    public const int MaximumPageBytes = 512 * 1024;
    public const int MaximumContinuationTokenCharacters = 96;

    public static void Validate(RemoteSearchSpec search)
    {
        ArgumentNullException.ThrowIfNull(search);
        var issues = new List<ValidationIssue>();
        if (search.SearchId == Guid.Empty)
            issues.Add(new("searchId", "required", "The remote-search identifier cannot be empty."));
        if (search.SessionId == Guid.Empty)
            issues.Add(new("sessionId", "required", "The remote-search session identifier cannot be empty."));
        if (!ProfileValidator.IsCanonicalRemotePath(search.Root))
            issues.Add(new("root", "absolute", "The remote-search root must be a bounded canonical absolute path."));
        if (string.IsNullOrWhiteSpace(search.Query))
            issues.Add(new("query", "required", "Enter a remote file or folder name to search for."));
        else
        {
            if (search.Query.Length > MaximumQueryCharacters)
                issues.Add(new("query", "length", $"The remote-search query cannot exceed {MaximumQueryCharacters} characters."));
            if (ProfileValidator.ContainsProtocolControl(search.Query))
                issues.Add(new("query", "control-character", "The remote-search query contains a prohibited control character."));
        }
        if (search.MaxDepth is < 1 or > MaximumMaxDepth)
            issues.Add(new("maxDepth", "range", $"Remote-search depth must be between 1 and {MaximumMaxDepth}."));
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    public static void ValidatePageRequest(RemoteSearchGetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifiers(request.SearchId, request.SessionId);
        if (request.PageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(request), $"Remote-search pages must contain between 1 and {MaximumPageSize} matches.");
        if (request.ContinuationToken is { Length: > MaximumContinuationTokenCharacters } ||
            request.ContinuationToken is not null && ProfileValidator.ContainsProtocolControl(request.ContinuationToken))
        {
            throw new ArgumentException("The remote-search continuation token is invalid.", nameof(request));
        }
    }

    public static void ValidateCancelRequest(RemoteSearchCancelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifiers(request.SearchId, request.SessionId);
    }

    public static bool IsWithinRoot(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(root, path, StringComparison.Ordinal) ||
            (root == "/"
                ? path.StartsWith("/", StringComparison.Ordinal)
                : path.StartsWith(root + "/", StringComparison.Ordinal));
    }

    public static bool MatchesName(string name, string query, bool matchCase)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(query);
        return name.Contains(query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateIdentifiers(Guid searchId, Guid sessionId)
    {
        if (searchId == Guid.Empty) throw new ArgumentException("A remote-search identifier is required.", nameof(searchId));
        if (sessionId == Guid.Empty) throw new ArgumentException("A remote-search session identifier is required.", nameof(sessionId));
    }
}

public sealed record ConsolePolicyDecision(bool Allowed, string? Reason = null);

public static class SafeConsolePolicy
{
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "cd", "cls", "du", "find", "help", "jobs", "lcd", "lpwd",
        "ls", "nlist", "pwd", "recls", "rels", "status", "version", "zcat",
    };

    public static ConsolePolicyDecision Evaluate(string command, bool localShellEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(command)) return new(false, "Enter an LFTP command.");
        if (ProfileValidator.ContainsProtocolControl(command)) return new(false, "Only one command line can be submitted at a time.");
        foreach (var segment in SplitCommands(command))
        {
            if (HasUnbalancedQuotes(segment)) return new(false, "The command contains an unbalanced quote.");
            var token = FirstToken(segment);
            if (token.Length == 0) continue;
            if (token[0] == '!')
            {
                if (localShellEnabled) continue;
                return new(false, "Local execution syntax ('!') is disabled. Enable local shell access explicitly to use it.");
            }
            if (TryFindUnquotedMetacharacter(segment, out var metacharacter))
                return new(false, $"Local execution syntax ('{metacharacter}') is disabled. Enable local shell access explicitly to use it.");
            if (localShellEnabled && token.Equals("shell", StringComparison.OrdinalIgnoreCase)) continue;
            if (token.Equals("pwd", StringComparison.OrdinalIgnoreCase) && HasArguments(segment, token))
                return new(false, "The read-only console permits 'pwd' without options; 'pwd -p' can expose credentials.");
            if (!ReadOnlyCommands.Contains(token))
                return new(false, $"'{token}' is not available in the read-only console. Use the structured transfer, mirror, or file-operation UI instead.");
        }

        return new(true);
    }

    private static IEnumerable<string> SplitCommands(string command)
    {
        var start = 0;
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < command.Length; index++)
        {
            var current = command[index];
            if (escaped) { escaped = false; continue; }
            if (current == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote != '\0') { if (current == quote) quote = '\0'; continue; }
            if (current is '\'' or '"') { quote = current; continue; }
            if (current == ';') { yield return command[start..index]; start = index + 1; }
        }
        yield return command[start..];
    }

    private static string FirstToken(string command)
    {
        var trimmed = command.TrimStart();
        var length = 0;
        while (length < trimmed.Length && !char.IsWhiteSpace(trimmed[length])) length++;
        return trimmed[..length];
    }

    private static bool HasArguments(string command, string token)
    {
        var trimmed = command.TrimStart();
        return trimmed.Length > token.Length && !string.IsNullOrWhiteSpace(trimmed[token.Length..]);
    }

    private static bool TryFindUnquotedMetacharacter(string command, out char metacharacter)
    {
        var quote = '\0';
        var escaped = false;
        foreach (var current in command)
        {
            if (escaped) { escaped = false; continue; }
            if (current == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote != '\0') { if (current == quote) quote = '\0'; continue; }
            if (current is '\'' or '"') quote = current;
            else if (current is '!' or '|' or '>' or '<' or '&' or '`' or '$' or '(' or ')' or '{' or '}')
            {
                metacharacter = current;
                return true;
            }
        }
        metacharacter = '\0';
        return false;
    }

    private static bool HasUnbalancedQuotes(string command)
    {
        var quote = '\0';
        var escaped = false;
        foreach (var current in command)
        {
            if (escaped) { escaped = false; continue; }
            if (current == '\\' && quote != '\0') { escaped = true; continue; }
            if (quote == '\0' && current is '\'' or '"') quote = current;
            else if (current == quote) quote = '\0';
        }
        return quote != '\0' || escaped;
    }
}
