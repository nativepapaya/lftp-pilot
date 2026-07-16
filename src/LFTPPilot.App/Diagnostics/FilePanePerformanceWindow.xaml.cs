using System.Diagnostics;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace LFTPPilot.App.Diagnostics;

public sealed partial class FilePanePerformanceWindow : Window
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly Stopwatch _initializationTimer = Stopwatch.StartNew();
    private int _layoutPassCount;

    public FilePanePerformanceWindow(IAgentWorkspaceClient agent)
    {
        InitializeComponent();
        ViewModel = new FilePaneViewModel(agent, Guid.Empty, PaneKind.Local, @"C:\LFTPPilot\PaneDiagnostic", CreateEntries());
        PaneView.DataContext = ViewModel;
        Root.LayoutUpdated += Root_LayoutUpdated;
        ConfigureWindow();
    }

    public FilePaneViewModel ViewModel { get; }

    private static IReadOnlyList<FileEntry> CreateEntries()
    {
        var entries = new FileEntry[10_000];
        for (var index = 0; index < entries.Length; index++)
        {
            var isDirectory = index % 40 == 0;
            var name = isDirectory ? $"folder-{index:D5}" : $"artifact-{index:D5}.bin";
            entries[index] = new FileEntry(
                name,
                $@"C:\LFTPPilot\PaneDiagnostic\{name}",
                isDirectory ? EntryKind.Directory : EntryKind.File,
                isDirectory ? null : (index + 1L) * 4_096L,
                FixedTimestamp.AddMinutes(-(index % 1_440)));
        }

        return entries;
    }

    private void Root_LayoutUpdated(object? sender, object e)
    {
        if (++_layoutPassCount < 2)
        {
            return;
        }

        Root.LayoutUpdated -= Root_LayoutUpdated;
        _initializationTimer.Stop();
        var snapshot = PaneView.GetVirtualizationSnapshot();
        var isVirtualized = snapshot.RealizedContainers > 0 && snapshot.RealizedContainers < snapshot.TotalItems;
        ResultBar.Severity = isVirtualized ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title = isVirtualized ? "Virtualization active" : "Virtualization needs inspection";
        ResultBar.Message = $"{snapshot.TotalItems:N0} items · {snapshot.RealizedContainers:N0} realized containers · {_initializationTimer.ElapsedMilliseconds:N0} ms through initial layout.";
    }

    private void SortSize_Click(object sender, RoutedEventArgs e) => ViewModel.Sort("Size");

    private void Filter_Click(object sender, RoutedEventArgs e) => ViewModel.FilterText = "025";

    private void ClearFilter_Click(object sender, RoutedEventArgs e) => ViewModel.FilterText = string.Empty;

    private void ConfigureWindow()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(windowId).Resize(new global::Windows.Graphics.SizeInt32(1_100, 780));
    }
}
