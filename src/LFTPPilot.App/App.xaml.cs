using LFTPPilot.App.Diagnostics;
using LFTPPilot.App.Services;
using Microsoft.UI.Xaml;

namespace LFTPPilot.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine(args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var performanceDiagnostic = FilePanePerformanceHarness.IsRequested(args.Arguments);
        var commandLineArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var demoDiagnostic = commandLineArguments.Any(argument =>
            argument.Equals("--diagnostic-demo", StringComparison.OrdinalIgnoreCase));
        var liveAgentDiagnostic = commandLineArguments.Any(argument =>
            argument.Equals("--diagnostic-live-agent", StringComparison.OrdinalIgnoreCase));
        AppServices.Initialize(performanceDiagnostic || demoDiagnostic);
        if (liveAgentDiagnostic)
        {
            _ = RunLiveAgentDiagnosticAsync();
            return;
        }
        if (performanceDiagnostic)
        {
            _window = FilePanePerformanceHarness.Create(AppServices.Agent);
            _window.Activate();
            return;
        }

        var activation = ProtocolActivationRouter.ReadCurrentActivation();
        _window = new MainWindow(activation);
        _window.Activate();
    }

    private static async Task RunLiveAgentDiagnosticAsync()
    {
        var exitCode = 1;
        try
        {
            var workspace = await AppServices.Agent.LoadAsync().ConfigureAwait(true);
            exitCode = AppServices.Agent.IsConnected && !workspace.IsDemoMode ? 0 : 2;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
        finally
        {
            try { await AppServices.ShutdownAsync(stopAgent: true).ConfigureAwait(true); }
            catch
            {
                await AppServices.ForceStopOwnedAgentAsync().ConfigureAwait(true);
                await AppServices.ShutdownAsync(stopAgent: false).ConfigureAwait(true);
            }
            Environment.Exit(exitCode);
        }
    }
}
