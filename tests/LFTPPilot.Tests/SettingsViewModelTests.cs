using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task RefreshUsesTheInstalledPackageVersion()
    {
        var service = new StubUpdateService(new(
            new Version(1, 0, 0, 16),
            UpdateAvailability.Current,
            CheckedAt: DateTimeOffset.UtcNow));
        var viewModel = new SettingsViewModel(new DemoAgentWorkspaceClient(service));

        Assert.Equal("Detecting…", viewModel.InstalledVersion);

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.0.0.16", viewModel.InstalledVersion);
        Assert.Equal("LFTP Pilot is up to date.", viewModel.UpdateStatus);
        Assert.False(viewModel.CanOpenInstaller);
        Assert.Equal(1, service.CheckCount);
    }

    [Fact]
    public async Task RefreshReportsUpdateServiceFailuresWithoutShowingAFakeVersion()
    {
        var service = new StubUpdateService(error: new InvalidOperationException("Update query failed."));
        var viewModel = new SettingsViewModel(new DemoAgentWorkspaceClient(service));

        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Detecting…", viewModel.InstalledVersion);
        Assert.Equal("Update query failed.", viewModel.UpdateStatus);
        Assert.Equal(1, service.CheckCount);
    }

    private sealed class StubUpdateService(AppUpdateStatus? status = null, Exception? error = null) : IAppUpdateService
    {
        public int CheckCount { get; private set; }

        public Task<AppUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return error is null
                ? Task.FromResult(status ?? throw new InvalidOperationException("A status is required."))
                : Task.FromException<AppUpdateStatus>(error);
        }

        public Task OpenInstallerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
