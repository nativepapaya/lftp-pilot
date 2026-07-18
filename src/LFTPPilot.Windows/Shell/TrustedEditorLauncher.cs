using System.Diagnostics;

namespace LFTPPilot.Windows.Shell;

public static class TrustedEditorLauncher
{
    public static ProcessStartInfo CreateStartInfo(string managedPath, string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        if (!Path.IsPathFullyQualified(managedPath))
            throw new ArgumentException("The managed editor path must be fully qualified.", nameof(managedPath));
        if (!Path.IsPathFullyQualified(managedRoot))
            throw new ArgumentException("The managed editor root must be fully qualified.", nameof(managedRoot));

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        var fullPath = Path.GetFullPath(managedPath);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathFullyQualified(relative) || relative is "." or ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException("The editor target is outside the managed remote-edit cache.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The managed remote-edit target does not exist.", fullPath);

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("The editor target must be a regular non-reparse file.");
        for (var current = Path.GetDirectoryName(fullPath); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The managed editor path cannot contain a reparse point.");
            if (string.Equals(Path.TrimEndingDirectorySeparator(current), fullRoot, StringComparison.OrdinalIgnoreCase))
                break;
        }

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            UseShellExecute = false,
        };
        start.ArgumentList.Add(fullPath);
        return start;
    }
}
