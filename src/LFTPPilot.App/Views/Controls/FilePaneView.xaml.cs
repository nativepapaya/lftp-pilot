using LFTPPilot.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace LFTPPilot.App.Views.Controls;

public sealed class FilePaneDropEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
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

    private async void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (FilesList.SelectedItem is FileEntryViewModel item && ViewModel is not null)
        {
            await ViewModel.OpenAsync(item).ConfigureAwait(true);
        }
    }

    private void FilesList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var paths = e.Items.OfType<FileEntryViewModel>().Select(static item => item.FullPath).ToArray();
        e.Data.SetText(string.Join(Environment.NewLine, paths));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void FilesList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Queue transfer";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void FilesList_Drop(object sender, DragEventArgs e)
    {
        var paths = new List<string>();
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths.AddRange(items.OfType<IStorageItem>().Select(static item => item.Path).Where(static path => !string.IsNullOrWhiteSpace(path)));
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();
            paths.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (paths.Count > 0)
        {
            FilesDropped?.Invoke(this, new FilePaneDropEventArgs(paths));
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
