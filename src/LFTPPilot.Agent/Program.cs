using System.Diagnostics;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Security;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Agent;

public static class Program
{
    private const string MutexName = "Local\\LFTPPilot.Agent.v1";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("LFTP Pilot background agent");
            return 0;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew) return 0;
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };

        var paths = PackageDataPaths.CreateDefault();
        paths.EnsureCreated();
        var statePath = Path.Combine(paths.History, "agent-state.json");
        var runtimeProvider = new PackagedLftpRuntimeProvider();
        var hostKeyManager = new SftpHostKeyManager(
            new JsonHostKeyStore(Path.Combine(paths.HostKeys, "trusted-sftp-host-keys.json")),
            new OpenSshHostKeyProbe(runtimeProvider, Path.Combine(paths.Temporary, "host-key-probes")));
        var rootJob = new WindowsJobObject();
        rootJob.Assign(Process.GetCurrentProcess());
        await using (var host = new AgentHost(
            statePath,
            profileStore: new JsonProfileStore(Path.Combine(paths.Profiles, "profiles.json")),
            secretStore: new DpapiSecretStore(paths.Secrets),
            hostKeyManager: hostKeyManager,
            processHost: new LftpProcessHost(),
            runtimeProvider: runtimeProvider,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: AgentWorkspaceOptions.CreateDefault(paths.RuntimeHome, paths.LocalCache, paths.Temporary),
            clientAuthorizer: AgentClientAuthorization.Create(paths),
            mirrorDefinitionStore: new JsonMirrorDefinitionStore(
                Path.Combine(paths.MirrorDefinitions, JsonMirrorDefinitionStore.FileName)),
            historyStore: new JsonHistoryStore(Path.Combine(paths.History, "history.json"))))
        {
            try { await host.RunAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        }

        // Do not dispose this kill-on-close handle while the assigned agent is
        // still running. The OS closes it as this process exits and atomically
        // terminates any LFTP/SSH descendants that survived normal cleanup.
        GC.KeepAlive(rootJob);
        singleInstance.ReleaseMutex();
        return 0;
    }
}
