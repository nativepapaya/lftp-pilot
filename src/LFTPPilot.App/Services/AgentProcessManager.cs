using System.Diagnostics;

namespace LFTPPilot.App.Services;

public sealed class AgentProcessManager : IDisposable
{
    private readonly object _gate = new();
    private Process? _ownedProcess;
    private int? _connectedProcessId;

    public bool OwnsRunningAgent
    {
        get
        {
            lock (_gate)
            {
                return _ownedProcess is { HasExited: false } && _connectedProcessId == _ownedProcess.Id;
            }
        }
    }

    public IReadOnlyList<int> FindTrustedRunningAgentProcessIds()
    {
        var expectedPath = ResolveExecutablePath();
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var processName = Path.GetFileNameWithoutExtension(expectedPath);
        var trusted = new List<int>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited || process.SessionId != currentSessionId) continue;
                    var actualPath = process.MainModule?.FileName is { Length: > 0 } value ? Path.GetFullPath(value) : null;
                    if (actualPath is not null && string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        trusted.Add(process.Id);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A process may exit or become inaccessible while the snapshot is inspected.
                }
            }
        }

        return trusted;
    }

    public void RecordConnectedProcess(int processId)
    {
        if (processId <= 0) throw new UnauthorizedAccessException("The Agent returned an invalid process identifier.");
        using var process = Process.GetProcessById(processId);
        using var currentProcess = Process.GetCurrentProcess();
        if (process.SessionId != currentProcess.SessionId)
            throw new UnauthorizedAccessException("The named-pipe server is not in the current Windows session.");
        var actualPath = process.MainModule?.FileName is { Length: > 0 } value ? Path.GetFullPath(value) : null;
        var expectedPath = ResolveExecutablePath();
        if (actualPath is null || !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The named-pipe server is not the packaged LFTP Pilot Agent.");
        lock (_gate) _connectedProcessId = processId;
    }

    public int Launch()
    {
        lock (_gate)
        {
            if (_ownedProcess is { HasExited: false }) return _ownedProcess.Id;
            _ownedProcess?.Dispose();
            _ownedProcess = null;
            var executable = ResolveExecutablePath();
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            _ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the LFTP Pilot Agent.");
            return _ownedProcess.Id;
        }
    }

    public Task StopOwnedAgentAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = OwnsRunningAgent ? _ownedProcess : null;
        }

        if (process is null) return Task.CompletedTask;
        try
        {
            process.Kill(entireProcessTree: true);
            return process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ownedProcess?.Dispose();
            _ownedProcess = null;
        }
    }

    private static string ResolveExecutablePath()
    {
        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "agent", "LFTPPilot.Agent.exe"));
        if (File.Exists(packaged)) return packaged;
#if DEBUG
        var overridePath = Environment.GetEnvironmentVariable("LFTP_PILOT_AGENT_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullPath = Path.GetFullPath(overridePath);
            if (File.Exists(fullPath) && string.Equals(Path.GetFileName(fullPath), "LFTPPilot.Agent.exe", StringComparison.OrdinalIgnoreCase)) return fullPath;
        }
#endif
        throw new FileNotFoundException("The packaged LFTP Pilot Agent was not found.", packaged);
    }
}
