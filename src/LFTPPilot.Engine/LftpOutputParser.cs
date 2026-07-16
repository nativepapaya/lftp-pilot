using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public static partial class LftpOutputParser
{
    public static ImmutableArray<FileEntry> ParseLongListing(IEnumerable<string> lines, string remoteRoot)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrWhiteSpace(remoteRoot) || !remoteRoot.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A remote absolute root is required.", nameof(remoteRoot));
        var entries = ImmutableArray.CreateBuilder<FileEntry>();
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
            var match = LongIsoRegex().Match(raw);
            FileEntry? entry = null;
            if (match.Success)
                entry = CreateEntry(match.Groups[1].Value, match.Groups[5].Value, match.Groups[6].Value, match.Groups[7].Value,
                    match.Groups[3].Value, match.Groups[4].Value, remoteRoot);
            else if ((match = LongIsoUserGroupRegex().Match(raw)).Success)
                entry = CreateEntry(match.Groups[1].Value, match.Groups[4].Value, match.Groups[5].Value, match.Groups[6].Value,
                    match.Groups[2].Value, match.Groups[3].Value, remoteRoot);
            else if ((match = LongIsoMinimalRegex().Match(raw)).Success)
                entry = CreateEntry(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value,
                    null, null, remoteRoot);
            if (entry is not null) entries.Add(entry);
        }
        return entries.ToImmutable();
    }

    public static ImmutableArray<FileEntry> ParseClassifiedNames(IEnumerable<string> lines, string remoteRoot)
    {
        var entries = ImmutableArray.CreateBuilder<FileEntry>();
        foreach (var raw in lines)
        {
            var name = AnsiRegex().Replace(raw ?? string.Empty, string.Empty);
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;
            var suffix = name[^1];
            var kind = suffix switch { '/' => EntryKind.Directory, '@' => EntryKind.SymbolicLink, _ => EntryKind.File };
            if (suffix is '/' or '@') name = name[..^1];
            if (name is "" or "." or "..") continue;
            entries.Add(new(name, JoinRemote(remoteRoot, name), kind, null, null));
        }
        return entries.ToImmutable();
    }

    public static string? FirstError(IEnumerable<LftpOutputLine> lines)
    {
        foreach (var output in lines)
        {
            if (!string.Equals(output.Stream, "stderr", StringComparison.OrdinalIgnoreCase)) continue;
            var line = output.Line;
            var command = CommandErrorRegex().Match(line);
            if (command.Success && ErrorWordRegex().IsMatch(line)) return command.Groups[1].Value.Trim();
            if (line.StartsWith("Fatal error:", StringComparison.OrdinalIgnoreCase)) return line[12..].Trim();
            if (line.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Login failed", StringComparison.OrdinalIgnoreCase)) return line.Trim();
        }
        return null;
    }

    private static string JoinRemote(string root, string name) => root == "/" ? $"/{name}" : $"{root.TrimEnd('/')}/{name}";

    private static FileEntry? CreateEntry(string permissions, string sizeText, string dateText, string rawName, string? owner, string? group, string remoteRoot)
    {
        var name = rawName;
        string? linkTarget = null;
        if (permissions[0] == 'l')
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) { linkTarget = name[(arrow + 4)..]; name = name[..arrow]; }
            name = name.TrimEnd('@');
        }
        name = name.TrimEnd('/');
        if (name is "" or "." or "..") return null;
        var kind = permissions[0] switch { 'd' => EntryKind.Directory, 'l' => EntryKind.SymbolicLink, '-' => EntryKind.File, _ => EntryKind.Other };
        _ = long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size);
        _ = DateTimeOffset.TryParseExact(dateText, ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var modified);
        return new(name, JoinRemote(remoteRoot, name), kind, kind == EntryKind.Directory ? null : size,
            modified == default ? null : modified, permissions, owner, group, linkTarget);
    }

    [GeneratedRegex("^([-dlcbpsD][rwxsStT+@.-]{9,11})\\s+(\\d+)\\s+(\\S+)\\s+(\\S+)\\s+(\\d+)\\s+(\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}(?::\\d{2})?)\\s(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LongIsoRegex();

    [GeneratedRegex("^([-dlcbpsD][rwxsStT+@.-]{9,11})\\s+(\\S+)\\s+(\\S+)\\s+(\\d+)\\s+(\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}(?::\\d{2})?)\\s(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LongIsoUserGroupRegex();

    [GeneratedRegex("^([-dlcbpsD][rwxsStT+@.-]{9,11})\\s+(\\d+)\\s+(\\d{4}-\\d{2}-\\d{2}[ T]\\d{2}:\\d{2}(?::\\d{2})?)\\s(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LongIsoMinimalRegex();

    [GeneratedRegex("^(?:open|cd|ls|cls|recls|get|put|pget|mirror|rm|rmdir|mkdir|mv|chmod|rename|lcd|login|pwd)[^:]*:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandErrorRegex();

    [GeneratedRegex("fail|denied|no such|not found|refused|timed? ?out|cannot|unable|invalid|error|fatal|reset by peer|unreachable|not allowed|530|550|553|421|Login incorrect", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWordRegex();

    [GeneratedRegex("\\x1b\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();
}
