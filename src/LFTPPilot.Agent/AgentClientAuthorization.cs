using System.Diagnostics;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Agent;

public static class AgentClientAuthorization
{
    public static Func<int, bool> Create(PackageDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var agentDirectory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        var packageRoot = string.Equals(agentDirectory.Name, "agent", StringComparison.OrdinalIgnoreCase)
            ? agentDirectory.Parent?.FullName ?? throw new InvalidOperationException("The packaged Agent directory has no package root.")
            : agentDirectory.FullName;
        var expectedApp = Path.GetFullPath(Path.Combine(packageRoot, "LFTPPilot.exe"));

#if DEBUG
        if (!paths.IsPackaged)
        {
            var developmentOverride = Environment.GetEnvironmentVariable("LFTP_PILOT_APP_PATH");
            if (!string.IsNullOrWhiteSpace(developmentOverride)) expectedApp = Path.GetFullPath(developmentOverride);
        }
#endif

        return processId => IsExpectedAppProcess(processId, expectedApp);
    }

    private static bool IsExpectedAppProcess(int processId, string expectedApp)
    {
        if (processId <= 0 || !File.Exists(expectedApp)) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            var actual = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(actual) &&
                string.Equals(Path.GetFullPath(actual), expectedApp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
