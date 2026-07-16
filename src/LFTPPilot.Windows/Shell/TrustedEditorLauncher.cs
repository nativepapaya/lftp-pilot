using System.Diagnostics;

namespace LFTPPilot.Windows.Shell;

public static class TrustedEditorLauncher
{
    public static ProcessStartInfo CreateStartInfo(string managedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        if (!Path.IsPathFullyQualified(managedPath))
            throw new ArgumentException("The managed editor path must be fully qualified.", nameof(managedPath));

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            UseShellExecute = false,
        };
        start.ArgumentList.Add(managedPath);
        return start;
    }
}
