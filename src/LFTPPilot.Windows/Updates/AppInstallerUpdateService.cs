using LFTPPilot.Core;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace LFTPPilot.Windows.Updates;

public sealed class AppInstallerUpdateService : IAppUpdateService
{
    public static readonly Uri DefaultInstallerUri = new(
        "https://github.com/nativepapaya/lftp-pilot/releases/latest/download/LFTPPilot.appinstaller");
    private readonly Uri _installerUri;

    public AppInstallerUpdateService(Uri? installerUri = null) => _installerUri = installerUri ?? DefaultInstallerUri;

    public async Task<AppUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        try
        {
            Package package = Package.Current;
            Version installed = ToVersion(package.Id.Version);
            if (package.GetAppInstallerInfo() is null)
                return new(installed, UpdateAvailability.Unassociated, InstallerUri: _installerUri, CheckedAt: checkedAt);

            PackageUpdateAvailabilityResult result = await package.CheckUpdateAvailabilityAsync().AsTask(cancellationToken);
            UpdateAvailability availability = result.Availability switch
            {
                PackageUpdateAvailability.NoUpdates => UpdateAvailability.Current,
                PackageUpdateAvailability.Available => UpdateAvailability.Available,
                PackageUpdateAvailability.Required => UpdateAvailability.Required,
                PackageUpdateAvailability.Error => UpdateAvailability.Error,
                _ => UpdateAvailability.Unknown,
            };
            string? error = result.ExtendedError is null ? null : result.ExtendedError.Message;
            return new(installed, availability, InstallerUri: _installerUri, ErrorMessage: error, CheckedAt: checkedAt);
        }
        catch (InvalidOperationException)
        {
            return new(GetAssemblyVersion(), UpdateAvailability.Unassociated, InstallerUri: _installerUri,
                ErrorMessage: "This installation is not associated with Windows App Installer. Use Get update installer to connect automatic updates.",
                CheckedAt: checkedAt);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(GetAssemblyVersion(), UpdateAvailability.Error, InstallerUri: _installerUri,
                ErrorMessage: error.Message, CheckedAt: checkedAt);
        }
    }

    public async Task OpenInstallerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Launcher.LaunchUriAsync(_installerUri))
            throw new InvalidOperationException("Windows could not open the LFTP Pilot update installer.");
    }

    private static Version ToVersion(PackageVersion value) => new(value.Major, value.Minor, value.Build, value.Revision);
    private static Version GetAssemblyVersion() => typeof(AppInstallerUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
}
