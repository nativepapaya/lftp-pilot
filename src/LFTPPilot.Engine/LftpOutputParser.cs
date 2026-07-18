using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed record RemoteSearchParseResult(
    ImmutableArray<RemoteSearchMatch> Matches,
    int ScannedEntries,
    bool WasLimited);

public static partial class LftpOutputParser
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

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

    public static RemoteSearchParseResult ParseRemoteFindOutput(
        IEnumerable<string> lines,
        RemoteSearchSpec search)
    {
        ArgumentNullException.ThrowIfNull(lines);
        RemoteSearchPolicy.Validate(search);

        var matches = ImmutableArray.CreateBuilder<RemoteSearchMatch>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var outputLines = 0;
        var scannedEntries = 0;
        var outputBytes = 0L;
        var storedMatchBytes = 0L;
        var rootSeen = false;
        var wasLimited = false;

        foreach (var raw in lines)
        {
            if (raw is null) throw new InvalidDataException("Remote-search output contained a null line.");
            outputLines++;
            if (outputLines > RemoteSearchPolicy.MaximumOutputLines)
                throw new InvalidDataException("Remote search produced too many paths to process safely.");
            if (raw.Length is 0 or > RemoteSearchPolicy.MaximumOutputLineCharacters ||
                raw.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                throw new InvalidDataException("Remote-search output contained an invalid or oversized path line.");
            }

            try
            {
                outputBytes = checked(outputBytes + StrictUtf8.GetByteCount(raw) + 1L);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException("Remote-search output was not valid Unicode.", exception);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException("Remote-search output exceeded its byte accounting limit.", exception);
            }
            if (outputBytes > RemoteSearchPolicy.MaximumOutputBytes)
                throw new InvalidDataException("Remote search produced more output than can be processed safely.");

            var isDirectory = raw.Length > 1 && raw.EndsWith("/", StringComparison.Ordinal);
            var fullPath = isDirectory ? raw[..^1] : raw;
            if (string.Equals(fullPath, search.Root, StringComparison.Ordinal))
            {
                if (rootSeen) throw new InvalidDataException("Remote-search output repeated its root path.");
                rootSeen = true;
                continue;
            }
            if (!rootSeen)
                throw new InvalidDataException("Remote-search output did not begin with the requested root.");
            if (!ProfileValidator.IsCanonicalRemotePath(fullPath) ||
                !RemoteSearchPolicy.IsWithinRoot(search.Root, fullPath))
            {
                throw new InvalidDataException("Remote-search output escaped its requested root or contained a non-canonical path.");
            }
            var relativeDepth = CountRemoteSegments(fullPath) - CountRemoteSegments(search.Root);
            if (relativeDepth <= 0 || relativeDepth >= search.MaxDepth)
                throw new InvalidDataException("Remote-search output exceeded the requested maximum depth.");
            if (!paths.Add(fullPath))
                throw new InvalidDataException("Remote-search output contained a duplicate path.");

            scannedEntries++;
            var separator = fullPath.LastIndexOf('/');
            var name = fullPath[(separator + 1)..];
            if (!RemoteSearchPolicy.MatchesName(name, search.Query, search.MatchCase)) continue;

            var match = new RemoteSearchMatch(
                name,
                fullPath,
                isDirectory ? RemoteSearchEntryKind.Directory : RemoteSearchEntryKind.Other);
            var matchBytes = JsonSerializer.SerializeToUtf8Bytes(match, FramedJsonStream.SerializerOptions).Length + 1L;
            if (matches.Count >= RemoteSearchPolicy.MaximumMatches ||
                storedMatchBytes + matchBytes > RemoteSearchPolicy.MaximumStoredMatchBytes)
            {
                wasLimited = true;
                continue;
            }
            storedMatchBytes += matchBytes;
            matches.Add(match);
        }

        if (!rootSeen) throw new InvalidDataException("Remote-search output did not identify the requested root.");
        return new(matches.ToImmutable(), scannedEntries, wasLimited);
    }

    private static int CountRemoteSegments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

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

    [GeneratedRegex("^(?:open|cd|ls|cls|recls|find|get|put|pget|mirror|rm|rmdir|mkdir|mv|chmod|rename|lcd|login|pwd)[^:]*:\\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandErrorRegex();

    [GeneratedRegex("fail|denied|no such|not found|refused|timed? ?out|cannot|unable|invalid|error|fatal|reset by peer|unreachable|not allowed|530|550|553|421|Login incorrect", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorWordRegex();

    [GeneratedRegex("\\x1b\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();
}
