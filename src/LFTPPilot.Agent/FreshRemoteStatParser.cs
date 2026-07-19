using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Agent;

internal static class FreshRemoteStatParser
{
    public static FileEntry? Parse(LftpCommandResult result, string remotePath, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateRemotePath(remotePath, nameof(remotePath));
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("An operation name is required.", nameof(operation));
        if (result.TimedOut) throw new TimeoutException($"{operation} timed out.");
        if (result.Failure is not null) throw new IOException($"{operation} failed: {result.Failure}");
        if (result.Truncated) throw new InvalidDataException($"{operation} produced too much output.");
        var separator = remotePath.LastIndexOf('/');
        var parent = separator <= 0 ? "/" : remotePath[..separator];
        var error = LftpOutputParser.FirstError(result.Lines);
        if (error is not null)
        {
            if (error.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                if (result.Lines.Length == 1 &&
                    string.Equals(result.Lines[0].Stream, "stderr", StringComparison.OrdinalIgnoreCase) &&
                    (MissingDiagnosticMatchesPath(result.Lines[0].Line, remotePath) ||
                     MissingDiagnosticMatchesPath(result.Lines[0].Line, parent)))
                {
                    return null;
                }

                throw new InvalidDataException($"The server returned ambiguous output for {operation.ToLowerInvariant()}.");
            }
            throw new IOException($"{operation} failed closed: {error}");
        }

        if (IsBoundMissingPathDiagnostic(result, remotePath) ||
            IsBoundMissingPathDiagnostic(result, parent)) return null;

        var marker = LftpCommandBuilder.LiteralStatMarker + remotePath;
        var listingLines = new List<string>();
        foreach (var line in result.Lines)
        {
            if (!string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(line.Line))
                throw new InvalidDataException($"The server returned ambiguous output for {operation.ToLowerInvariant()}.");
            if (string.Equals(line.Line, marker, StringComparison.Ordinal) ||
                line.Line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                continue;
            listingLines.Add(line.Line);
        }
        if (remotePath == "/") return new("/", "/", EntryKind.Directory, null, null);
        if (listingLines.Count == 0) return null;

        var parsed = new List<FileEntry>();
        foreach (var line in listingLines)
        {
            var entries = LftpOutputParser.ParseLongListing([line], parent);
            if (entries.Length == 0)
            {
                if (IsDotDirectoryListing(line)) continue;
                throw new InvalidDataException($"The server returned an unparseable entry for {operation.ToLowerInvariant()}.");
            }
            parsed.AddRange(entries);
        }
        var matches = parsed.Where(entry => string.Equals(entry.FullPath, remotePath, StringComparison.Ordinal)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"The server repeated the requested path for {operation.ToLowerInvariant()}.")
        };
    }

    public static string ValidateRemotePath(string remotePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(remotePath) || remotePath.Length > 4096 ||
            !remotePath.StartsWith("/", StringComparison.Ordinal) ||
            remotePath.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            remotePath.Contains("//", StringComparison.Ordinal) ||
            remotePath.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or "..") ||
            remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 128 ||
            remotePath.Length > 1 && remotePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A bounded canonical remote absolute path is required.", parameterName);
        }
        return remotePath;
    }

    private static bool MissingDiagnosticMatchesPath(string diagnostic, string remotePath)
    {
        var searchAt = 0;
        var foundPath = false;
        while (true)
        {
            var open = diagnostic.IndexOf("(/", searchAt, StringComparison.Ordinal);
            if (open < 0) break;
            var close = diagnostic.IndexOf(')', open + 2);
            if (close < 0) return false;
            foundPath = true;
            if (!string.Equals(diagnostic[(open + 1)..close], remotePath, StringComparison.Ordinal)) return false;
            searchAt = close + 1;
        }

        // A sole command-prefixed diagnostic without a named path is the
        // documented LFTP shape for a missing target. Any other slash-bearing
        // diagnostic names an unbound path and must fail closed.
        return foundPath || !diagnostic.Contains('/', StringComparison.Ordinal);
    }

    private static bool IsBoundMissingPathDiagnostic(LftpCommandResult result, string remotePath)
    {
        if (result.Lines.Length != 1 ||
            !string.Equals(result.Lines[0].Stream, "stderr", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var diagnostic = result.Lines[0].Line;
        return diagnostic.StartsWith("Access failed:", StringComparison.OrdinalIgnoreCase) &&
            (diagnostic.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
             diagnostic.Contains("not found", StringComparison.OrdinalIgnoreCase)) &&
            diagnostic.Contains("(/", StringComparison.Ordinal) &&
            MissingDiagnosticMatchesPath(diagnostic, remotePath);
    }

    private static bool IsDotDirectoryListing(string line) =>
        line.Length > 10 && line[0] == 'd' &&
        (line.EndsWith(" .", StringComparison.Ordinal) || line.EndsWith(" ..", StringComparison.Ordinal));
}
