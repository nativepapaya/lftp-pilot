using System.Collections.Immutable;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace LFTPPilot.App.Views.Controls;

public sealed class FilePaneDropEventArgs(IReadOnlyList<FilePaneTransferSource> sources) : EventArgs
{
    public IReadOnlyList<FilePaneTransferSource> Sources { get; } = sources;
}

public sealed class FilePaneDropRejectedEventArgs(FilePaneDropRejection rejection) : EventArgs
{
    public FilePaneDropRejection Rejection { get; } = rejection;
}

public sealed class FilePaneRemoteEditEventArgs(FileEntryViewModel entry) : EventArgs
{
    public FileEntryViewModel Entry { get; } = entry;
}

public readonly record struct FilePaneVirtualizationSnapshot(int TotalItems, int RealizedContainers);

public sealed partial class FilePaneView : UserControl
{
    private readonly HashSet<Grid> _realizedRows = [];
    private bool _columnLayoutSubscribed;
    private ExplorerExportDragState? _pendingExplorerExport;

    public FilePaneView()
    {
        InitializeComponent();
        Loaded += FilePaneView_Loaded;
        Unloaded += FilePaneView_Unloaded;
        ApplyColumnWidths();
    }

    public event EventHandler? TransferRequested;
    public event EventHandler? TransferOptionsRequested;
    public event EventHandler<FilePaneDropEventArgs>? FilesDropped;
    public event EventHandler<FilePaneDropRejectedEventArgs>? DropRejected;
    public event EventHandler<FilePaneRemoteEditEventArgs>? RemoteEditRequested;

    private FilePaneViewModel? ViewModel => DataContext as FilePaneViewModel;

    public FilePaneVirtualizationSnapshot GetVirtualizationSnapshot() => new(
        FilesList.Items.Count,
        FilesList.ItemsPanelRoot?.Children.Count ?? 0);

    private void FilePaneView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_columnLayoutSubscribed) return;
        _columnLayoutSubscribed = true;
        FilePaneColumnLayout.Changed += FilePaneColumnLayout_Changed;
        ApplyColumnWidths();
    }

    private void FilePaneView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_columnLayoutSubscribed) return;
        _columnLayoutSubscribed = false;
        FilePaneColumnLayout.Changed -= FilePaneColumnLayout_Changed;
        _realizedRows.Clear();
        ReleasePendingExplorerExport();
    }

    private void FilePaneColumnLayout_Changed(object? sender, EventArgs e) => ApplyColumnWidths();

    private void ApplyColumnWidths()
    {
        HeaderSizeColumn.Width = new GridLength(FilePaneColumnLayout.SizeWidth);
        HeaderModifiedColumn.Width = new GridLength(FilePaneColumnLayout.ModifiedWidth);
        HeaderTypeColumn.Width = new GridLength(FilePaneColumnLayout.TypeWidth);
        foreach (var row in _realizedRows.ToArray()) ApplyColumnWidths(row);
    }

    private static void ApplyColumnWidths(Grid row)
    {
        if (row.ColumnDefinitions.Count < 8) return;
        row.ColumnDefinitions[3].Width = new GridLength(FilePaneColumnLayout.SizeWidth);
        row.ColumnDefinitions[5].Width = new GridLength(FilePaneColumnLayout.ModifiedWidth);
        row.ColumnDefinitions[7].Width = new GridLength(FilePaneColumnLayout.TypeWidth);
    }

    private void FileRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        _realizedRows.Add(row);
        ApplyColumnWidths(row);
    }

    private void FileRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid row) _realizedRows.Remove(row);
    }

    private void NameSizeSplitter_DragDelta(object sender, DragDeltaEventArgs e) => FilePaneColumnLayout.ResizeNameAndSize(e.HorizontalChange);

    private void SizeModifiedSplitter_DragDelta(object sender, DragDeltaEventArgs e) => FilePaneColumnLayout.ResizeSizeAndModified(e.HorizontalChange);

    private void ModifiedTypeSplitter_DragDelta(object sender, DragDeltaEventArgs e) => FilePaneColumnLayout.ResizeModifiedAndType(e.HorizontalChange);

    private void ColumnSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => FilePaneColumnLayout.Persist();

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ViewModel?.UpdateSelection(FilesList.SelectedItems);

    private void FilterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel?.ToggleFilterCommand.CanExecute(null) != true) return;
        ViewModel.ToggleFilterCommand.Execute(null);
        if (ViewModel.IsFilterVisible) DispatcherQueue.TryEnqueue(() => FilterBox.Focus(FocusState.Keyboard));
        args.Handled = true;
    }

    private void FilePane_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox or PasswordBox or NumberBox or ComboBox) return;
        switch (e.Key)
        {
            case VirtualKey.Back:
                if (ViewModel?.NavigateUpCommand.CanExecute(null) == true) ViewModel.NavigateUpCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.F3:
                if (ViewModel?.RefreshCommand.CanExecute(null) == true) ViewModel.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.F7:
                CreateDirectory_Click(this, e);
                e.Handled = true;
                break;
            case VirtualKey.F2:
                RenameOrMove_Click(this, e);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                DeleteEntries_Click(this, e);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                OpenMenuItem_Click(this, e);
                e.Handled = true;
                break;
        }
    }

    private async void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FilesList.SelectedItem is FileEntryViewModel item && ViewModel is not null)
        {
            await ViewModel.OpenAsync(item).ConfigureAwait(true);
        }
    }

    private void FilesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (ViewModel is not { } viewModel || !TryCreateTransferSources(e.Items.OfType<FileEntryViewModel>(), out var sources) ||
            !FilePaneDragDropRegistry.Shared.TryIssue(viewModel.SessionId, viewModel.Kind, sources, out var token))
        {
            e.Data.RequestedOperation = DataPackageOperation.None;
            return;
        }

        e.Data.SetData(FilePaneDragDropRegistry.DataFormat, token);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        if (viewModel.Kind == PaneKind.Remote && sources.All(static source => source.Kind == TransferSourceKind.File))
        {
            ReleasePendingExplorerExport();
            var state = new ExplorerExportDragState(Guid.NewGuid(), viewModel, sources);
            _pendingExplorerExport = state;
            e.Data.Properties.Title = sources.Length == 1 ? "LFTP Pilot remote file" : $"{sources.Length:N0} LFTP Pilot remote files";
            e.Data.Properties.Description = "LFTP Pilot will prepare managed local copies if Explorer accepts this drop.";
            e.Data.Properties.FileTypes.Add(StandardDataFormats.StorageItems);
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => ProvideExplorerExport(request, state));
        }
    }

    private void FilesList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => ReleasePendingExplorerExport();

    private static async void ProvideExplorerExport(DataProviderRequest request, ExplorerExportDragState state)
    {
        var deferral = request.GetDeferral();
        try
        {
            var snapshot = await state.GetOrStartAsync(request.Deadline).ConfigureAwait(true);
            var files = new List<IStorageItem>(snapshot.LocalPaths.Length);
            foreach (var path in snapshot.LocalPaths)
                files.Add(await StorageFile.GetFileFromPathAsync(path));
            request.SetData(files.ToArray());
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            await state.ReleaseAsync().ConfigureAwait(true);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ReleasePendingExplorerExport()
    {
        var state = Interlocked.Exchange(ref _pendingExplorerExport, null);
        if (state is not null) _ = ReleaseExplorerExportSafelyAsync(state);
    }

    private static async Task ReleaseExplorerExportSafelyAsync(ExplorerExportDragState state)
    {
        try { await state.ReleaseAsync().ConfigureAwait(false); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private async void FilesList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        if (ViewModel is not { } viewModel) return;

        if (ContainsFormat(e.DataView, FilePaneDragDropRegistry.DataFormat))
        {
            try
            {
                var deferral = e.GetDeferral();
                try
                {
                    var token = await e.DataView.GetDataAsync(FilePaneDragDropRegistry.DataFormat) as string;
                    if (FilePaneDragDropRegistry.Shared.CanAccept(token, viewModel.SessionId, viewModel.Kind))
                    {
                        AcceptDrop(e);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            return;
        }

        if (ContainsFormat(e.DataView, StandardDataFormats.StorageItems) &&
            FilePaneDragDropRegistry.CanAcceptExplorerStorageItems(viewModel.Kind))
        {
            AcceptDrop(e);
        }
    }

    private async void FilesList_Drop(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        if (ViewModel is not { } viewModel) return;

        if (ContainsFormat(e.DataView, FilePaneDragDropRegistry.DataFormat))
        {
            try
            {
                var deferral = e.GetDeferral();
                try
                {
                    var token = await e.DataView.GetDataAsync(FilePaneDragDropRegistry.DataFormat) as string;
                    if (FilePaneDragDropRegistry.Shared.TryConsume(token, viewModel.SessionId, viewModel.Kind, out var payload))
                    {
                        e.AcceptedOperation = DataPackageOperation.Copy;
                        FilesDropped?.Invoke(this, new FilePaneDropEventArgs(payload!.Sources));
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }

            return;
        }

        if (!ContainsFormat(e.DataView, StandardDataFormats.StorageItems) ||
            !FilePaneDragDropRegistry.CanAcceptExplorerStorageItems(viewModel.Kind))
        {
            return;
        }

        var outcomeReported = false;
        try
        {
            var deferral = e.GetDeferral();
            try
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                if (storageItems.Count == 0)
                {
                    outcomeReported = true;
                    ReportDropRejection(FilePaneDropRejectionKind.Empty);
                    return;
                }

                if (storageItems.Count > FilePaneDragDropRegistry.MaximumSources)
                {
                    outcomeReported = true;
                    ReportDropRejection(FilePaneDropRejectionKind.TooManyItems);
                    return;
                }

                var sources = ImmutableArray.CreateBuilder<FilePaneTransferSource>(storageItems.Count);
                foreach (var item in storageItems)
                {
                    var source = item switch
                    {
                        StorageFile file => new FilePaneTransferSource(file.Path, TransferSourceKind.File),
                        StorageFolder folder => new FilePaneTransferSource(folder.Path, TransferSourceKind.Directory),
                        _ => null,
                    };
                    if (source is null)
                    {
                        outcomeReported = true;
                        ReportDropRejection(FilePaneDropRejectionKind.UnsupportedItem);
                        return;
                    }

                    sources.Add(source);
                }

                var typedSources = sources.MoveToImmutable();
                if (!FilePaneDragDropRegistry.AreValidExplorerSources(typedSources))
                {
                    outcomeReported = true;
                    ReportDropRejection(FilePaneDropRejectionKind.InvalidLocalPath);
                    return;
                }

                e.AcceptedOperation = DataPackageOperation.Copy;
                outcomeReported = true;
                FilesDropped?.Invoke(this, new FilePaneDropEventArgs(typedSources));
            }
            finally
            {
                deferral.Complete();
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            if (!outcomeReported) ReportDropRejection(FilePaneDropRejectionKind.DataUnavailable);
        }
    }

    private void ReportDropRejection(FilePaneDropRejectionKind kind)
    {
        try
        {
            DropRejected?.Invoke(this, new FilePaneDropRejectedEventArgs(FilePaneDropRejection.Create(kind)));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static void AcceptDrop(DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Queue transfer";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private static bool TryCreateTransferSources(
        IEnumerable<FileEntryViewModel> entries,
        out ImmutableArray<FilePaneTransferSource> sources)
    {
        var builder = ImmutableArray.CreateBuilder<FilePaneTransferSource>();
        foreach (var entry in entries)
        {
            if (builder.Count >= FilePaneDragDropRegistry.MaximumSources)
            {
                sources = [];
                return false;
            }

            var kind = entry.Entry.Kind switch
            {
                EntryKind.File => TransferSourceKind.File,
                EntryKind.Directory => TransferSourceKind.Directory,
                _ => (TransferSourceKind?)null,
            };
            if (kind is null)
            {
                sources = [];
                return false;
            }

            builder.Add(new(entry.FullPath, kind.Value));
        }

        return FilePaneDragDropRegistry.TryCopyValidSources(builder, out sources);
    }

    private static bool ContainsFormat(DataPackageView data, string format)
    {
        try
        {
            return data.Contains(format);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return false;
        }
    }

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel is not null)
        {
            e.Handled = true;
            await ViewModel.NavigateAsync(PathBox.Text).ConfigureAwait(true);
        }
    }

    private sealed class ExplorerExportDragState(
        Guid exportId,
        FilePaneViewModel viewModel,
        IReadOnlyList<FilePaneTransferSource> sources)
    {
        private readonly object _gate = new();
        private Task<ExplorerExportSnapshot>? _preparation;
        private bool _released;

        public Task<ExplorerExportSnapshot> GetOrStartAsync(DateTimeOffset deadline)
        {
            lock (_gate)
            {
                if (_released) throw new InvalidOperationException("The Explorer export was already released.");
                return _preparation ??= viewModel.PrepareExplorerExportAsync(exportId, sources, deadline);
            }
        }

        public async Task ReleaseAsync()
        {
            bool wasStarted;
            lock (_gate)
            {
                if (_released) return;
                _released = true;
                wasStarted = _preparation is not null;
            }
            if (wasStarted) _ = await viewModel.ReleaseExplorerExportAsync(exportId).ConfigureAwait(false);
        }
    }

    private void QuickAccessBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not ComboBox { SelectedItem: string bookmark }) return;
        if (!string.Equals(bookmark, viewModel.Path, StringComparison.Ordinal) && viewModel.NavigateBookmarkCommand.CanExecute(bookmark))
            viewModel.NavigateBookmarkCommand.Execute(bookmark);
    }

    private async void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is FileEntryViewModel item && ViewModel is not null)
        {
            await ViewModel.OpenAsync(item).ConfigureAwait(true);
        }
    }

    private void TransferMenuItem_Click(object sender, RoutedEventArgs e) => TransferRequested?.Invoke(this, EventArgs.Empty);

    private void RemoteEditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanRemoteEdit: true } viewModel || viewModel.SelectedEntries.Count != 1) return;
        RemoteEditRequested?.Invoke(this, new(viewModel.SelectedEntries[0]));
    }

    private void TransferOptionsMenuItem_Click(object sender, RoutedEventArgs e) => TransferOptionsRequested?.Invoke(this, EventArgs.Empty);

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.OfType<FileEntryViewModel>().Select(static item => item.FullPath).ToList();
        if (selected.Count == 0) return;
        var package = new DataPackage();
        // Explicit Copy path remains text for the clipboard. Drag transfers never
        // publish paths and accept only the opaque in-process token above.
        package.SetText(string.Join(Environment.NewLine, selected));
        Clipboard.SetContent(package);
    }

    private async void CreateDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        var input = new TextBox
        {
            Header = "Folder name or full destination path",
            Text = "New folder",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Create a folder in {viewModel.PaneTitle}",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await viewModel.CreateDirectoryAsync(input.Text).ConfigureAwait(true); }
        catch (Exception exception) { await ShowOperationErrorAsync("The folder could not be created", exception).ConfigureAwait(true); }
    }

    private async void RenameOrMove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        if (viewModel.SelectedEntries.Count != 1)
        {
            await ShowNoticeAsync("Select one item", "Rename or move works with one selected file or folder at a time.").ConfigureAwait(true);
            return;
        }

        var entry = viewModel.SelectedEntries[0];
        var input = new TextBox
        {
            Header = "New name or full destination path",
            Text = entry.Name,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Rename or move {entry.Name}",
            Content = input,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await viewModel.RenameOrMoveAsync(entry, input.Text).ConfigureAwait(true); }
        catch (Exception exception) { await ShowOperationErrorAsync("The item could not be moved", exception).ConfigureAwait(true); }
    }

    private async void DeleteEntries_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        var entries = viewModel.SelectedEntries.ToArray();
        if (entries.Length is < 1 or > 100)
        {
            await ShowNoticeAsync("Select items to delete", "Select between 1 and 100 files or folders before deleting.").ConfigureAwait(true);
            return;
        }

        var directoryCount = entries.Count(static entry => entry.IsDirectory);
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"Permanently delete {entries.Length} selected item{(entries.Length == 1 ? string.Empty : "s")} from {viewModel.PaneTitle}? This cannot be undone.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, entries.Take(8).Select(static entry => entry.Name)) + (entries.Length > 8 ? $"{Environment.NewLine}…and {entries.Length - 8} more" : string.Empty),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 150,
        });
        var recursive = new CheckBox
        {
            Content = $"Recursively delete the contents of the selected director{(directoryCount == 1 ? "y" : "ies")}",
            IsChecked = false,
        };
        if (directoryCount > 0)
        {
            content.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Directory deletion",
                Message = "Without recursive deletion, only empty directories are removed. Enable it only after reviewing the selected directories and their contents.",
            });
            content.Children.Add(recursive);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = directoryCount > 0 ? "Confirm file and directory deletion" : "Confirm file deletion",
            Content = content,
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            // This is the only path that supplies Confirmed=true to the Agent.
            await viewModel.DeleteEntriesAsync(entries, recursive.IsChecked == true, confirmed: true).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await ShowOperationErrorAsync("The selected items could not all be deleted", exception).ConfigureAwait(true);
        }
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "Close" };
        await dialog.ShowAsync();
    }

    private Task ShowOperationErrorAsync(string title, Exception exception) => ShowNoticeAsync(title, exception.Message);

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RefreshCommand.CanExecute(null) == true) ViewModel.RefreshCommand.Execute(null);
    }

    private static class FilePaneColumnLayout
    {
        private const string SizeKey = "FilePane.SizeColumnWidth";
        private const string ModifiedKey = "FilePane.ModifiedColumnWidth";
        private const string TypeKey = "FilePane.TypeColumnWidth";
        private const double MinimumSizeWidth = 64;
        private const double MinimumModifiedWidth = 105;
        private const double MinimumTypeWidth = 72;

        static FilePaneColumnLayout()
        {
            SizeWidth = Read(SizeKey, 92, MinimumSizeWidth, 260);
            ModifiedWidth = Read(ModifiedKey, 142, MinimumModifiedWidth, 320);
            TypeWidth = Read(TypeKey, 94, MinimumTypeWidth, 220);
        }

        public static event EventHandler? Changed;
        public static double SizeWidth { get; private set; }
        public static double ModifiedWidth { get; private set; }
        public static double TypeWidth { get; private set; }

        public static void ResizeNameAndSize(double horizontalChange)
        {
            SizeWidth = Math.Clamp(SizeWidth - horizontalChange, MinimumSizeWidth, 260);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void ResizeSizeAndModified(double horizontalChange)
        {
            var total = SizeWidth + ModifiedWidth;
            SizeWidth = Math.Clamp(SizeWidth + horizontalChange, MinimumSizeWidth, total - MinimumModifiedWidth);
            ModifiedWidth = total - SizeWidth;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void ResizeModifiedAndType(double horizontalChange)
        {
            var total = ModifiedWidth + TypeWidth;
            ModifiedWidth = Math.Clamp(ModifiedWidth + horizontalChange, MinimumModifiedWidth, total - MinimumTypeWidth);
            TypeWidth = total - ModifiedWidth;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Persist()
        {
            try
            {
                var values = global::Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                values[SizeKey] = SizeWidth;
                values[ModifiedKey] = ModifiedWidth;
                values[TypeKey] = TypeWidth;
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
            }
        }

        private static double Read(string key, double fallback, double minimum, double maximum)
        {
            try
            {
                var value = global::Windows.Storage.ApplicationData.Current.LocalSettings.Values[key];
                return value is double width ? Math.Clamp(width, minimum, maximum) : fallback;
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                return fallback;
            }
        }
    }
}
