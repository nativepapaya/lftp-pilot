namespace LFTPPilot.App.Services;

public static class AppServices
{
    private static readonly SemaphoreSlim ShutdownGate = new(1, 1);
    private static bool _disposed;

    public static IAgentWorkspaceClient Agent { get; private set; } = null!;
    public static AgentProcessManager ProcessManager { get; private set; } = null!;
    public static LFTPPilot.Core.IAppPreferencesStore Preferences { get; private set; } = null!;

    public static void Initialize(bool useDemoTransport)
    {
        _disposed = false;
        var updates = new LFTPPilot.Windows.Updates.AppInstallerUpdateService();
        var dataPaths = LFTPPilot.Windows.Storage.PackageDataPaths.CreateDefault();
        dataPaths.EnsureCreated();
        Preferences = new LFTPPilot.Windows.Storage.JsonAppPreferencesStore(dataPaths.UiPreferences);
        ProcessManager = new AgentProcessManager();
        Agent = useDemoTransport
            ? new DemoAgentWorkspaceClient(updates)
            : new LiveAgentWorkspaceClient(ProcessManager, updates);
    }

    public static async Task ShutdownAsync(bool stopAgent, CancellationToken cancellationToken = default)
    {
        await ShutdownGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (stopAgent && Agent.IsConnected) await Agent.StopAgentAsync(cancellationToken).ConfigureAwait(false);
            await Agent.DisposeAsync().ConfigureAwait(false);
            ProcessManager.Dispose();
            _disposed = true;
        }
        finally
        {
            ShutdownGate.Release();
        }
    }

    public static Task ForceStopOwnedAgentAsync() => ProcessManager.StopOwnedAgentAsync();
}
