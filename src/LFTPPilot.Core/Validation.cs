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
        if (profile.Host is not null && (profile.Host.Contains("://", StringComparison.Ordinal) || profile.Host.Any(char.IsWhiteSpace) || profile.Host.Contains('/') || profile.Host.Contains('\\')))
            issues.Add(new("host", "format", "The host must not contain a scheme, path, or whitespace."));
        if (profile.Authentication == AuthenticationKind.SshKey && profile.Protocol != ConnectionProtocol.Sftp)
            issues.Add(new("authentication", "unsupported", "SSH key authentication is available only for SFTP."));
        if (profile.Authentication == AuthenticationKind.SshKey && string.IsNullOrWhiteSpace(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "required", "An SSH key path is required for key authentication."));
        if (profile.SshKeyPath is not null && ContainsProtocolControl(profile.SshKeyPath))
            issues.Add(new("sshKeyPath", "control-character", "The SSH key path contains a prohibited control character."));
        if (profile.InitialRemotePath is not null && !IsRemoteAbsolute(profile.InitialRemotePath))
            issues.Add(new("initialRemotePath", "absolute", "The initial remote path must begin with '/'."));
        if (profile.InitialLocalPath is not null && !Path.IsPathFullyQualified(profile.InitialLocalPath))
            issues.Add(new("initialLocalPath", "absolute", "The initial local path must be fully qualified."));
        foreach (var bookmark in profile.EffectiveBookmarks)
        {
            if (string.IsNullOrWhiteSpace(bookmark) || bookmark.Length > 4096 || !IsRemoteAbsolute(bookmark))
                issues.Add(new("bookmarks", "format", "Bookmarks must be non-empty absolute remote paths."));
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
        if (!Enum.IsDefined(plan.Mode)) issues.Add(new("mode", "unsupported", "The transfer mode is not supported."));
        if (plan.Segments is < 1 or > 64) issues.Add(new("segments", "range", "Segments must be between 1 and 64."));
        if (plan.RateLimitBytesPerSecond is <= 0) issues.Add(new("rateLimitBytesPerSecond", "range", "A rate limit must be positive."));
        if (issues.Count != 0) throw new ModelValidationException(issues);
    }

    public static void Validate(MirrorDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var issues = new List<ValidationIssue>();
        if (definition.Id == Guid.Empty) issues.Add(new("id", "required", "The mirror identifier cannot be empty."));
        if (definition.ProfileId == Guid.Empty) issues.Add(new("profileId", "required", "The profile identifier cannot be empty."));
        if (string.IsNullOrWhiteSpace(definition.LocalRoot) || !Path.IsPathFullyQualified(definition.LocalRoot)) issues.Add(new("localRoot", "absolute", "The local root must be fully qualified."));
        if (string.IsNullOrWhiteSpace(definition.RemoteRoot) || !ProfileValidator.IsRemoteAbsolute(definition.RemoteRoot)) issues.Add(new("remoteRoot", "absolute", "The remote root must begin with '/'."));
        ValidatePath(definition.Name, "name", issues);
        if (!Enum.IsDefined(definition.Direction)) issues.Add(new("direction", "unsupported", "The mirror direction is not supported."));
        if (definition.ParallelFiles is < 1 or > 16) issues.Add(new("parallelFiles", "range", "Parallel files must be between 1 and 16."));
        if (definition.SegmentsPerFile is < 1 or > 64) issues.Add(new("segmentsPerFile", "range", "Segments per file must be between 1 and 64."));
        if (definition.RateLimitBytesPerSecond is <= 0) issues.Add(new("rateLimitBytesPerSecond", "range", "A rate limit must be positive."));
        foreach (var pattern in definition.EffectiveIncludes) ValidatePath(pattern, "includes", issues);
        foreach (var pattern in definition.EffectiveExcludes) ValidatePath(pattern, "excludes", issues);
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

    private static void ValidatePath(string? value, string field, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) issues.Add(new(field, "required", $"{field} is required."));
        else if (ProfileValidator.ContainsProtocolControl(value)) issues.Add(new(field, "control-character", $"{field} contains a prohibited control character."));
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
