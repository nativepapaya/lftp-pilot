namespace LFTPPilot.Tests;

public sealed class NativeUiContractTests
{
    [Fact]
    public async Task FirstLaunchRetainsVisibleDualPaneAndConnectionEntryPoint()
    {
        var root = FindRepositoryRoot();
        var mainWindow = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "MainWindow.xaml"),
            TestContext.Current.CancellationToken);

        Assert.Contains("Disconnected dual pane workspace", mainWindow, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding OfflineLocalPane}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding HasNoSessions", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Click=\"Connect_Click\"", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionActionsArePinnedAndWideDialogLimitIsOverridden()
    {
        var root = FindRepositoryRoot();
        var connections = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Pages", "ConnectionProfilesPage.xaml"),
            TestContext.Current.CancellationToken);
        var shell = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "MainWindow.xaml.cs"),
            TestContext.Current.CancellationToken);

        var scrollViewerEnd = connections.LastIndexOf("</ScrollViewer>", StringComparison.Ordinal);
        var pinnedActions = connections.IndexOf("Grid.Row=\"1\" Grid.ColumnSpan=\"2\"", StringComparison.Ordinal);
        Assert.True(scrollViewerEnd >= 0 && pinnedActions > scrollViewerEnd,
            "Connection actions must remain outside the scrolling form so they cannot be clipped below it.");
        Assert.Contains("Content=\"Create and connect\"", connections, StringComparison.Ordinal);
        Assert.Contains("ContentDialogMaxWidth", shell, StringComparison.Ordinal);
        Assert.Contains("ConfigureDialogSize(dialog, 900", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectedSessionLeavesLocalPaneUsable()
    {
        var root = FindRepositoryRoot();
        var workspace = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Controls", "SessionWorkspaceView.xaml"),
            TestContext.Current.CancellationToken);

        var localPane = workspace.Split('\n').Single(line => line.Contains("x:Name=\"LocalPane\"", StringComparison.Ordinal));
        Assert.DoesNotContain("IsEnabled=\"{Binding IsConnected}\"", localPane, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Remote session disconnected", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShellKeepsCommanderWorkflowHierarchyWithoutFlatAdvancedToolbar()
    {
        var root = FindRepositoryRoot();
        var mainWindow = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "MainWindow.xaml"),
            TestContext.Current.CancellationToken);
        var filePane = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Controls", "FilePaneView.xaml"),
            TestContext.Current.CancellationToken);
        var activity = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Controls", "ActivityCenterView.xaml"),
            TestContext.Current.CancellationToken);

        Assert.Contains("Text=\"Connections\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Tools\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"Synchronize folders…\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("<CommandBar", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"GET\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"PUT\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding SelectedSession}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ToggleFilterCommand}\"", filePane, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding HiddenFilesLabel}\"", filePane, StringComparison.Ordinal);
        Assert.DoesNotContain("<TabView", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("PilotCardStyle", activity, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TransfersDockTab\"", activity, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HistoryDockTab\"", activity, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LogDockTab\"", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeShellUsesOneIntegratedSessionStripAndFlatActivityDock()
    {
        var root = FindRepositoryRoot();
        var mainWindow = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "MainWindow.xaml"),
            TestContext.Current.CancellationToken);
        var activity = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Controls", "ActivityCenterView.xaml"),
            TestContext.Current.CancellationToken);
        var workspace = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Controls", "SessionWorkspaceView.xaml"),
            TestContext.Current.CancellationToken);

        Assert.Contains("x:Name=\"AppTitleBar\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SessionTabs\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ConnectionStateOpacity", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStateLabel", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStateLabel", workspace, StringComparison.Ordinal);
        Assert.Equal(1, mainWindow.Split("IsClosable=\"True\"").Length - 1);
        Assert.DoesNotContain("<RowDefinition Height=\"44\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0,1,0,0\"", activity, StringComparison.Ordinal);
        Assert.Contains("<Grid Height=\"136\" Background=\"Transparent\">", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsExposeOnlyPersistedNativeSectionsAndTransferDefaults()
    {
        var root = FindRepositoryRoot();
        var settings = await File.ReadAllTextAsync(
            Path.Combine(root, "src", "LFTPPilot.App", "Views", "Pages", "SettingsPage.xaml"),
            TestContext.Current.CancellationToken);

        Assert.Contains("Content=\"Interface\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content=\"Transfers\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content=\"LFTP engine\"", settings, StringComparison.Ordinal);
        Assert.Contains("Content=\"Storage &amp; updates\"", settings, StringComparison.Ordinal);
        Assert.Contains("DefaultParallelFiles", settings, StringComparison.Ordinal);
        Assert.Contains("DefaultDownloadSegments", settings, StringComparison.Ordinal);
        Assert.Contains("DownloadLimitKiB", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("custom font", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OLED", settings, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LFTPPilot.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The LFTP Pilot repository root was not found from the test output directory.");
    }
}
