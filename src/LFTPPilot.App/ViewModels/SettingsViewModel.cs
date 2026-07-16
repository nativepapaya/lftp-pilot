using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private string _installedVersion = "1.0.0.0";
    private string _updateStatus = "Updates are checked quietly through Windows App Installer.";
    private bool _canOpenInstaller;

    public SettingsViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        CheckForUpdatesCommand = new AsyncRelayCommand(_ => CheckAsync(), null, ReportError);
        OpenInstallerCommand = new AsyncRelayCommand(_ => OpenInstallerAsync(), _ => CanOpenInstaller, ReportError);
    }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand OpenInstallerCommand { get; }
    public string InstalledVersion { get => _installedVersion; private set => SetProperty(ref _installedVersion, value); }
    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public bool CanOpenInstaller { get => _canOpenInstaller; private set { if (SetProperty(ref _canOpenInstaller, value)) OpenInstallerCommand.NotifyCanExecuteChanged(); } }

    private async Task CheckAsync()
    {
        UpdateStatus = "Checking with Windows App Installer…";
        var result = await _agent.CheckForUpdatesAsync().ConfigureAwait(true);
        InstalledVersion = result.InstalledVersion.ToString();
        UpdateStatus = result.Availability switch
        {
            UpdateAvailability.Current => "LFTP Pilot is up to date.",
            UpdateAvailability.Available => result.AvailableVersion is { } available
                ? $"Version {available} is ready through App Installer."
                : "An update is ready through App Installer.",
            UpdateAvailability.Required => result.AvailableVersion is { } required
                ? $"Version {required} is required and ready to install."
                : "A required update is ready through App Installer.",
            UpdateAvailability.Unassociated => result.ErrorMessage ?? "Open LFTPPilot.appinstaller once to reconnect automatic updates.",
            UpdateAvailability.Error => result.ErrorMessage ?? "Windows could not check for updates.",
            _ => "Update status is not available.",
        };
        CanOpenInstaller = result.Availability is UpdateAvailability.Available or UpdateAvailability.Required or UpdateAvailability.Unassociated;
    }

    private async Task OpenInstallerAsync()
    {
        await _agent.OpenUpdateInstallerAsync().ConfigureAwait(true);
        UpdateStatus = "The update installer was opened. Active transfers were not interrupted.";
    }

    private void ReportError(Exception exception) => UpdateStatus = exception.Message;
}
