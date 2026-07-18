using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Services;
using LFTPPilot.Core;
using LFTPPilot.Windows.Diagnostics;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private string _installedVersion = "1.0.0.0";
    private string _updateStatus = "Updates are checked quietly through Windows App Installer.";
    private bool _canOpenInstaller;
    private string _supportBundleStatus = "Create a sanitized ZIP when diagnostics are needed.";

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
    public string SupportBundleStatus { get => _supportBundleStatus; private set => SetProperty(ref _supportBundleStatus, value); }

    public async Task CreateSupportBundleAsync(string destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        SupportBundleStatus = "Collecting and sanitizing diagnostics…";
        try
        {
            var workspace = await _agent.LoadAsync(cancellationToken).ConfigureAwait(true);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            };
            var paths = PackageDataPaths.CreateDefault();
            var metadata = new Dictionary<string, object?>
            {
                ["generatedAt"] = DateTimeOffset.UtcNow,
                ["appVersion"] = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(),
                ["os"] = Environment.OSVersion.VersionString,
                ["framework"] = RuntimeInformation.FrameworkDescription,
                ["packaged"] = paths.IsPackaged,
                ["demoMode"] = workspace.IsDemoMode,
                ["agentStatus"] = workspace.AgentStatus,
            };
            var profiles = workspace.Profiles.Select(static profile => new
            {
                profile.Id,
                profile.Name,
                profile.Protocol,
                profile.Host,
                profile.Port,
                profile.UserName,
                profile.Authentication,
                HasSshKey = !string.IsNullOrWhiteSpace(profile.SshKeyPath),
            });
            var entries = new[]
            {
                new SupportBundleText("workspace/profiles.json", JsonSerializer.Serialize(profiles, options)),
                new SupportBundleText("workspace/sessions.json", JsonSerializer.Serialize(workspace.Sessions.Select(static session => session.Snapshot), options)),
                new SupportBundleText("activity/jobs.json", JsonSerializer.Serialize(workspace.Jobs, options)),
                new SupportBundleText("activity/history.json", JsonSerializer.Serialize(workspace.History, options)),
                new SupportBundleText("activity/log.json", JsonSerializer.Serialize(workspace.Log, options)),
                new SupportBundleText("workspace/mirror-definitions.json", JsonSerializer.Serialize(workspace.MirrorDefinitions, options)),
                new SupportBundleText("workspace/remote-edits.json", JsonSerializer.Serialize(workspace.RemoteEdits, options)),
            };
            await new SupportBundleBuilder().CreateAsync(destination, metadata, entries, cancellationToken).ConfigureAwait(true);
            SupportBundleStatus = $"Support bundle saved as {Path.GetFileName(destination)}.";
        }
        catch (Exception exception)
        {
            SupportBundleStatus = $"Support bundle failed: {exception.Message}";
            throw;
        }
    }

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
