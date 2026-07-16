using System.Diagnostics;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using LFTPPilot.Windows.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LFTPPilot.App.Views.Controls;

public sealed partial class SessionWorkspaceView : UserControl
{
    private const string RunOnceExplanation =
        "The Agent must remain running until this time and completion. If Windows restarts or the Agent is explicitly exited, the job is marked missed and will not run late.";

    public SessionWorkspaceView() => InitializeComponent();

    private void LocalPane_TransferRequested(object? sender, EventArgs e)
    {
        if (DataContext is SessionViewModel viewModel && viewModel.UploadCommand.CanExecute(null))
        {
            viewModel.UploadCommand.Execute(null);
        }
    }

    private void RemotePane_TransferRequested(object? sender, EventArgs e)
    {
        if (DataContext is SessionViewModel viewModel && viewModel.DownloadCommand.CanExecute(null))
        {
            viewModel.DownloadCommand.Execute(null);
        }
    }

    private async void LocalPane_TransferOptionsRequested(object? sender, EventArgs e) =>
        await ShowTransferOptionsAsync(TransferDirection.Upload).ConfigureAwait(true);

    private async void RemotePane_TransferOptionsRequested(object? sender, EventArgs e) =>
        await ShowTransferOptionsAsync(TransferDirection.Download).ConfigureAwait(true);

    private async void UploadOptions_Click(object sender, RoutedEventArgs e) =>
        await ShowTransferOptionsAsync(TransferDirection.Upload).ConfigureAwait(true);

    private async void DownloadOptions_Click(object sender, RoutedEventArgs e) =>
        await ShowTransferOptionsAsync(TransferDirection.Download).ConfigureAwait(true);

    private async void LocalPane_FilesDropped(object? sender, FilePaneDropEventArgs e)
    {
        if (DataContext is SessionViewModel viewModel)
        {
            try
            {
                await viewModel.QueueSourcesAsync(TransferDirection.Download, e.Sources).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                await ShowTransferQueueErrorAsync(TransferDirection.Download, exception).ConfigureAwait(true);
            }
        }
    }

    private async void RemotePane_FilesDropped(object? sender, FilePaneDropEventArgs e)
    {
        if (DataContext is SessionViewModel viewModel)
        {
            try
            {
                await viewModel.QueueSourcesAsync(TransferDirection.Upload, e.Sources).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                await ShowTransferQueueErrorAsync(TransferDirection.Upload, exception).ConfigureAwait(true);
            }
        }
    }

    private async void Pane_DropRejected(object? sender, FilePaneDropRejectedEventArgs e) =>
        await ShowNoticeAsync("Explorer items were not queued", e.Rejection.Message).ConfigureAwait(true);

    private async void RemotePane_RemoteEditRequested(object? sender, FilePaneRemoteEditEventArgs e)
    {
        if (DataContext is not SessionViewModel viewModel) return;
        RemoteEditSession? edit = null;
        try
        {
            edit = await viewModel.StartRemoteEditAsync(e.Entry).ConfigureAwait(true);
            // Keep both the executable and argument boundary explicit. The Agent
            // supplies only the verified package-scoped managed path, which is
            // passed as one ArgumentList item to the trusted Windows Notepad.
            // Managed content is never handed to ShellExecute or a file association.
            var start = TrustedEditorLauncher.CreateStartInfo(edit.LocalPath);
            _ = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start Notepad for the managed copy.");
        }
        catch (Exception exception)
        {
            if (edit is not null)
            {
                try { await AppServices.Agent.CompleteRemoteEditAsync(edit.EditId).ConfigureAwait(true); }
                catch (Exception cleanupException) { System.Diagnostics.Debug.WriteLine(cleanupException); }
            }
            await ShowOperationErrorAsync("The remote file could not be opened for editing", exception).ConfigureAwait(true);
        }
    }

    private async Task ShowTransferOptionsAsync(TransferDirection direction)
    {
        if (DataContext is not SessionViewModel viewModel) return;
        var sources = viewModel.GetSelectedSources(direction);
        if (sources.Count == 0)
        {
            var paneName = direction == TransferDirection.Upload ? "local" : "remote";
            await ShowNoticeAsync(
                $"Select files to {direction.ToString().ToLowerInvariant()}",
                $"Select one or more {paneName} files or folders, then open Transfer options again.").ConfigureAwait(true);
            return;
        }

        var isDownload = direction == TransferDirection.Download;
        var containsDirectory = sources.Any(static source => source.Kind == TransferSourceKind.Directory);
        var destinationRoot = isDownload ? viewModel.LocalPane.Path : viewModel.RemotePane.Path;
        var mode = new ComboBox
        {
            Header = "Transfer mode",
            // Directory mirror transfers support only Auto and Resume. LFTP's
            // no-clobber control applies to downloads; upload Skip cannot be
            // made race-free if the remote target appears after Agent preflight.
            ItemsSource = Enum.GetValues<TransferMode>()
                .Where(value => (!containsDirectory || value is TransferMode.Auto or TransferMode.Resume) &&
                    (isDownload || value != TransferMode.Skip))
                .ToArray(),
            SelectedItem = TransferMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(mode, "Transfer mode");

        var segments = new NumberBox
        {
            Header = "Download segments per file",
            Minimum = 1,
            Maximum = 16,
            Value = 4,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Description = "1 uses a normal download; 2–16 use LFTP segmented downloads.",
        };
        AutomationProperties.SetName(segments, "Download segments per file, 1 to 16");

        var limitBandwidth = new CheckBox { Content = "Limit bandwidth per transfer" };
        AutomationProperties.SetName(limitBandwidth, "Limit bandwidth per transfer");
        var bandwidthValue = new NumberBox
        {
            Header = "Bandwidth limit",
            Minimum = 1,
            Maximum = 1_048_576,
            Value = 10,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            IsEnabled = false,
        };
        AutomationProperties.SetName(bandwidthValue, "Bandwidth limit value");
        var bandwidthUnit = new ComboBox
        {
            Header = "Units",
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        bandwidthUnit.Items.Add(new ComboBoxItem { Content = "KiB/s (1,024 bytes/s)", Tag = 1_024L });
        bandwidthUnit.Items.Add(new ComboBoxItem { Content = "MiB/s (1,048,576 bytes/s)", Tag = 1_048_576L });
        bandwidthUnit.SelectedIndex = 1;
        AutomationProperties.SetName(bandwidthUnit, "Bandwidth units");
        limitBandwidth.Checked += (_, _) => SetBandwidthEnabled(true);
        limitBandwidth.Unchecked += (_, _) => SetBandwidthEnabled(false);

        var bandwidthGrid = new Grid { ColumnSpacing = 12 };
        bandwidthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bandwidthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bandwidthGrid.Children.Add(bandwidthValue);
        Grid.SetColumn(bandwidthUnit, 1);
        bandwidthGrid.Children.Add(bandwidthUnit);

        var schedule = new CheckBox { Content = "Run once at a future time" };
        AutomationProperties.SetName(schedule, "Run once at a future time");
        var suggestedRunAt = DateTimeOffset.Now.AddHours(1);
        var runDate = new DatePicker
        {
            Header = "Run date",
            Date = suggestedRunAt,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(runDate, "Run date");
        var runTime = new TimePicker
        {
            Header = "Run time",
            Time = suggestedRunAt.TimeOfDay,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(runTime, "Run time");
        var scheduleGrid = new Grid { ColumnSpacing = 12 };
        scheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scheduleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scheduleGrid.Children.Add(runDate);
        Grid.SetColumn(runTime, 1);
        scheduleGrid.Children.Add(runTime);

        var runOnceInfo = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Run-once scheduling",
            Message = RunOnceExplanation,
        };
        AutomationProperties.SetName(runOnceInfo, "Run-once scheduling requirements");
        schedule.Checked += (_, _) => SetScheduleEnabled(true);
        schedule.Unchecked += (_, _) => SetScheduleEnabled(false);

        var validation = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = "Review these options",
        };
        AutomationProperties.SetName(validation, "Transfer option validation error");

        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        content.Children.Add(new TextBlock
        {
            Text = $"Review {sources.Count} selected item{(sources.Count == 1 ? string.Empty : "s")} before queueing.",
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
        });
        content.Children.Add(new ListView
        {
            ItemsSource = sources.Select(static source => source.Path),
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 124,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"Destination: {destinationRoot}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        content.Children.Add(mode);
        if (isDownload) content.Children.Add(segments);
        content.Children.Add(limitBandwidth);
        content.Children.Add(bandwidthGrid);
        content.Children.Add(schedule);
        content.Children.Add(scheduleGrid);
        content.Children.Add(runOnceInfo);
        content.Children.Add(validation);

        TransferUiOptions? selectedOptions = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{direction} transfer options",
            Content = content,
            PrimaryButtonText = "Queue transfers",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!TryCreateOptions(out selectedOptions, out var error))
            {
                validation.Message = error;
                validation.IsOpen = true;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selectedOptions is null) return;
        try
        {
            await viewModel.QueueSourcesAsync(direction, sources, selectedOptions).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await ShowTransferQueueErrorAsync(direction, exception).ConfigureAwait(true);
        }

        void SetBandwidthEnabled(bool enabled)
        {
            bandwidthValue.IsEnabled = enabled;
            bandwidthUnit.IsEnabled = enabled;
        }

        void SetScheduleEnabled(bool enabled)
        {
            runDate.IsEnabled = enabled;
            runTime.IsEnabled = enabled;
            runOnceInfo.IsOpen = enabled;
        }

        bool TryCreateOptions(out TransferUiOptions? options, out string error)
        {
            options = null;
            error = string.Empty;
            if (mode.SelectedItem is not TransferMode selectedMode)
            {
                error = "Choose a transfer mode.";
                return false;
            }

            var segmentCount = 1;
            if (isDownload)
            {
                if (double.IsNaN(segments.Value) || segments.Value != Math.Truncate(segments.Value) || segments.Value is < 1 or > 16)
                {
                    error = "Download segments must be a whole number from 1 through 16.";
                    return false;
                }

                segmentCount = (int)segments.Value;
            }

            long? bytesPerSecond = null;
            if (limitBandwidth.IsChecked == true)
            {
                if (double.IsNaN(bandwidthValue.Value) || bandwidthValue.Value <= 0)
                {
                    error = "Enter a positive bandwidth limit.";
                    return false;
                }

                if (bandwidthUnit.SelectedItem is not ComboBoxItem { Tag: long multiplier })
                {
                    error = "Choose bandwidth units.";
                    return false;
                }

                var converted = bandwidthValue.Value * multiplier;
                if (double.IsInfinity(converted) || converted > long.MaxValue)
                {
                    error = "The bandwidth limit is too large.";
                    return false;
                }

                bytesPerSecond = checked((long)Math.Round(converted, MidpointRounding.AwayFromZero));
            }

            DateTimeOffset? runAt = null;
            if (schedule.IsChecked == true)
            {
                var date = runDate.Date;
                var time = runTime.Time;
                var localTime = new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, 0, DateTimeKind.Unspecified);
                if (TimeZoneInfo.Local.IsInvalidTime(localTime))
                {
                    error = "That local time does not exist because of the daylight-saving transition. Choose another time.";
                    return false;
                }

                runAt = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
                if (runAt <= DateTimeOffset.Now.AddMinutes(1))
                {
                    error = "Choose a run-once time at least one minute in the future.";
                    return false;
                }
            }

            options = new TransferUiOptions(selectedMode, segmentCount, bytesPerSecond, runAt);
            return true;
        }
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    private Task ShowTransferQueueErrorAsync(TransferDirection direction, Exception exception)
    {
        var itemName = direction == TransferDirection.Upload ? "uploads" : "downloads";
        var title = exception is TransferQueueException { Result.IsPartialSuccess: true }
            ? $"Only some {itemName} were queued"
            : $"No {itemName} were queued";
        return ShowOperationErrorAsync(title, exception);
    }

    private async Task ShowOperationErrorAsync(string title, Exception exception)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        }.ShowAsync();
    }
}
