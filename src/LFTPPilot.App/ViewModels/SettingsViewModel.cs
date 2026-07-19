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
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly SemaphoreSlim _preferencesLoadGate = new(1, 1);
    private readonly SemaphoreSlim _preferencesSaveGate = new(1, 1);
    private AppPreferences _preferences = new();
    private bool _preferencesLoaded;
    private string _installedVersion = "Detecting…";
    private string _updateStatus = "Updates are checked quietly through Windows App Installer.";
    private bool _canOpenInstaller;
    private string _supportBundleStatus = "Create a sanitized ZIP when diagnostics are needed.";
    private string _preferencesStatus = "Interface and transfer defaults are saved for this Windows account.";

    public SettingsViewModel(IAgentWorkspaceClient agent, IAppPreferencesStore? preferencesStore = null)
    {
        _agent = agent;
        _preferencesStore = preferencesStore ?? new MemoryAppPreferencesStore();
        CheckForUpdatesCommand = new AsyncRelayCommand(_ => RefreshAsync(), null, ReportError);
        OpenInstallerCommand = new AsyncRelayCommand(_ => OpenInstallerAsync(), _ => CanOpenInstaller, ReportError);
    }

    public event EventHandler<AppPreferences>? PreferencesChanged;
    public AsyncRelayCommand CheckForUpdatesCommand { get; }
    public AsyncRelayCommand OpenInstallerCommand { get; }
    public IReadOnlyList<string> ThemeOptions { get; } = ["Use Windows setting", "Light", "Dark"];
    public IReadOnlyList<string> FileDensityOptions { get; } = ["Compact", "Comfortable"];
    public string InstalledVersion { get => _installedVersion; private set => SetProperty(ref _installedVersion, value); }
    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public bool CanOpenInstaller { get => _canOpenInstaller; private set { if (SetProperty(ref _canOpenInstaller, value)) OpenInstallerCommand.NotifyCanExecuteChanged(); } }
    public string SupportBundleStatus { get => _supportBundleStatus; private set => SetProperty(ref _supportBundleStatus, value); }
    public string PreferencesStatus { get => _preferencesStatus; private set => SetProperty(ref _preferencesStatus, value); }
    public AppPreferences Preferences => _preferences;
    public string RuntimeStatus => _agent.IsConnected
        ? "Background Agent connected · packaged LFTP runtime authenticated"
        : "The background Agent is not currently connected.";
    public string DataFolder => PackageDataPaths.CreateDefault().LocalState;
    public string CacheFolder => PackageDataPaths.CreateDefault().LocalCache;

    public int ThemeSelectionIndex
    {
        get => (int)_preferences.Theme;
        set
        {
            if (!Enum.IsDefined((AppThemePreference)value)) return;
            UpdatePreferences(_preferences with { Theme = (AppThemePreference)value }, nameof(ThemeSelectionIndex));
        }
    }

    public int FileDensitySelectionIndex
    {
        get => (int)_preferences.FileListDensity;
        set
        {
            if (!Enum.IsDefined((FileListDensity)value)) return;
            UpdatePreferences(_preferences with { FileListDensity = (FileListDensity)value }, nameof(FileDensitySelectionIndex));
        }
    }

    public bool ShowHiddenLocal
    {
        get => _preferences.ShowHiddenLocal;
        set => UpdatePreferences(_preferences with { ShowHiddenLocal = value }, nameof(ShowHiddenLocal));
    }

    public bool ShowHiddenRemote
    {
        get => _preferences.ShowHiddenRemote;
        set => UpdatePreferences(_preferences with { ShowHiddenRemote = value }, nameof(ShowHiddenRemote));
    }

    public bool ExpandActivityCenter
    {
        get => _preferences.ExpandActivityCenter;
        set => UpdatePreferences(_preferences with { ExpandActivityCenter = value }, nameof(ExpandActivityCenter));
    }

    public double DefaultParallelFiles
    {
        get => _preferences.DefaultParallelFiles;
        set
        {
            if (double.IsNaN(value) || value != Math.Truncate(value) ||
                value is < 1 or > AppPreferencesPolicy.MaximumParallelFiles) return;
            UpdatePreferences(_preferences with { DefaultParallelFiles = (int)value }, nameof(DefaultParallelFiles));
        }
    }

    public double DefaultDownloadSegments
    {
        get => _preferences.DefaultDownloadSegments;
        set
        {
            if (double.IsNaN(value) || value != Math.Truncate(value) ||
                value is < 1 or > AppPreferencesPolicy.MaximumDownloadSegments) return;
            UpdatePreferences(_preferences with { DefaultDownloadSegments = (int)value }, nameof(DefaultDownloadSegments));
        }
    }

    public double DownloadLimitKiB
    {
        get => _preferences.DownloadLimitKiB;
        set
        {
            if (double.IsNaN(value) || value != Math.Truncate(value) ||
                value is < 0 or > AppPreferencesPolicy.MaximumRateLimitKiB) return;
            UpdatePreferences(_preferences with { DownloadLimitKiB = (long)value }, nameof(DownloadLimitKiB));
        }
    }

    public double UploadLimitKiB
    {
        get => _preferences.UploadLimitKiB;
        set
        {
            if (double.IsNaN(value) || value != Math.Truncate(value) ||
                value is < 0 or > AppPreferencesPolicy.MaximumRateLimitKiB) return;
            UpdatePreferences(_preferences with { UploadLimitKiB = (long)value }, nameof(UploadLimitKiB));
        }
    }

    public async Task LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (_preferencesLoaded) return;
        await _preferencesLoadGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_preferencesLoaded) return;
            _preferences = await _preferencesStore.GetAsync(cancellationToken).ConfigureAwait(true);
            AppPreferencesPolicy.Validate(_preferences);
            _preferencesLoaded = true;
            NotifyPreferenceProperties();
            PreferencesStatus = "Interface and transfer defaults are saved for this Windows account.";
            PreferencesChanged?.Invoke(this, _preferences);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _preferences = new();
            _preferencesLoaded = true;
            NotifyPreferenceProperties();
            PreferencesStatus = $"Saved preferences could not be loaded; safe defaults are active. {exception.Message}";
            PreferencesChanged?.Invoke(this, _preferences);
        }
        finally { _preferencesLoadGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await LoadPreferencesAsync(cancellationToken).ConfigureAwait(true);
            OnPropertyChanged(nameof(RuntimeStatus));
            await CheckAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError(exception);
        }
    }

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
                ["appVersion"] = InstalledVersion,
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

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        UpdateStatus = "Checking with Windows App Installer…";
        var result = await _agent.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(true);
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

    private void UpdatePreferences(AppPreferences next, string propertyName)
    {
        try { AppPreferencesPolicy.Validate(next); }
        catch (ArgumentException exception)
        {
            PreferencesStatus = exception.Message;
            return;
        }
        if (next == _preferences) return;
        _preferences = next;
        _preferencesLoaded = true;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(Preferences));
        PreferencesChanged?.Invoke(this, next);
        _ = PersistPreferencesAsync(next);
    }

    private async Task PersistPreferencesAsync(AppPreferences snapshot)
    {
        await _preferencesSaveGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await _preferencesStore.SaveAsync(snapshot).ConfigureAwait(true);
            PreferencesStatus = "Changes saved.";
        }
        catch (Exception exception)
        {
            PreferencesStatus = $"Preferences could not be saved. {exception.Message}";
        }
        finally { _preferencesSaveGate.Release(); }
    }

    private void NotifyPreferenceProperties()
    {
        OnPropertyChanged(nameof(Preferences));
        OnPropertyChanged(nameof(ThemeSelectionIndex));
        OnPropertyChanged(nameof(FileDensitySelectionIndex));
        OnPropertyChanged(nameof(ShowHiddenLocal));
        OnPropertyChanged(nameof(ShowHiddenRemote));
        OnPropertyChanged(nameof(ExpandActivityCenter));
        OnPropertyChanged(nameof(DefaultParallelFiles));
        OnPropertyChanged(nameof(DefaultDownloadSegments));
        OnPropertyChanged(nameof(DownloadLimitKiB));
        OnPropertyChanged(nameof(UploadLimitKiB));
    }

    private sealed class MemoryAppPreferencesStore : IAppPreferencesStore
    {
        private AppPreferences _value = new();
        public Task<AppPreferences> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_value);
        }

        public Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppPreferencesPolicy.Validate(preferences);
            _value = preferences;
            return Task.CompletedTask;
        }
    }
}
