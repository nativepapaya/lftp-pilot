using System.Collections.Immutable;
using System.Diagnostics;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using LFTPPilot.Windows.Shell;
using LFTPPilot.Windows.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LFTPPilot.App.Views.Controls;

public sealed partial class SessionWorkspaceView : UserControl
{
    private const string RunOnceExplanation =
        "The Agent must remain running until this time and completion. If Windows restarts or the Agent is explicitly exited, the job is marked missed and will not run late.";

    public SessionWorkspaceView() => InitializeComponent();

    private void RemoteSearch_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel viewModel) return;
        viewModel.Search.Open();
        DispatcherQueue.TryEnqueue(() => RemoteSearchQuery.Focus(FocusState.Keyboard));
    }

    private void RemoteSearchQuery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel viewModel) return;
        if (e.Key == VirtualKey.Enter && viewModel.Search.SearchCommand.CanExecute(null))
        {
            viewModel.Search.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (viewModel.Search.IsSearching && viewModel.Search.CancelCommand.CanExecute(null))
                viewModel.Search.CancelCommand.Execute(null);
            else if (viewModel.Search.CloseCommand.CanExecute(null))
                viewModel.Search.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void RemoteSearchResults_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel viewModel ||
            !viewModel.Search.OpenLocationCommand.CanExecute(null)) return;
        viewModel.Search.OpenLocationCommand.Execute(null);
        e.Handled = true;
    }

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
            var start = TrustedEditorLauncher.CreateStartInfo(
                edit.LocalPath,
                PackageDataPaths.CreateDefault().RemoteEdits);
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

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel viewModel || !viewModel.CanReconnect) return;

        PasswordBox? credentialInput = null;
        string? ephemeralCredential = null;
        if (viewModel.RequiresCredentialForReconnect)
        {
            credentialInput = new PasswordBox
            {
                Header = "Credential",
                PlaceholderText = "Enter the credential for this connection",
                PasswordRevealMode = PasswordRevealMode.Peek,
            };
            AutomationProperties.SetName(credentialInput, "Ask-on-connect credential");
            var validation = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "Credential required",
                Message = "Enter the credential before reconnecting this saved tab.",
            };
            var content = new StackPanel { Spacing = 12, MaxWidth = 520 };
            content.Children.Add(new TextBlock
            {
                Text = "This credential is sent only for this explicit reconnect. It is not saved by the restored tab.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(credentialInput);
            content.Children.Add(validation);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Reconnect {viewModel.DisplayName}",
                Content = content,
                PrimaryButtonText = "Reconnect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!string.IsNullOrEmpty(credentialInput.Password)) return;
                validation.IsOpen = true;
                args.Cancel = true;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                credentialInput.Password = string.Empty;
                return;
            }
            ephemeralCredential = credentialInput.Password;
        }

        try
        {
            await viewModel.ReconnectAsync(ephemeralCredential).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            await ShowOperationErrorAsync("The saved tab could not reconnect", exception).ConfigureAwait(true);
        }
        finally
        {
            if (credentialInput is not null) credentialInput.Password = string.Empty;
            ephemeralCredential = null;
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
            Value = viewModel.DefaultDownloadSegments,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Description = "1 uses a normal download; 2–16 use LFTP segmented downloads.",
        };
        AutomationProperties.SetName(segments, "Download segments per file, 1 to 16");

        var preset = new ComboBox
        {
            Header = "Saved folder preset",
            ItemsSource = viewModel.FolderTransferPresets,
            DisplayMemberPath = nameof(FolderTransferPreset.Name),
            PlaceholderText = "Custom folder options",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(preset, "Saved folder transfer preset");
        var presetName = new TextBox
        {
            Header = "Preset name",
            PlaceholderText = "Reusable folder filter name",
            MaxLength = FolderTransferPolicy.MaximumNameLength,
        };
        AutomationProperties.SetName(presetName, "Folder transfer preset name");
        var newPreset = new Button { Content = "New" };
        var savePreset = new Button { Content = "Save preset" };
        var deletePreset = new Button { Content = "Delete", IsEnabled = false };
        var presetButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        presetButtons.Children.Add(newPreset);
        presetButtons.Children.Add(savePreset);
        presetButtons.Children.Add(deletePreset);
        var includePatterns = new TextBox
        {
            Header = "Include globs (one per line)",
            PlaceholderText = "*.zip\r\ndocs/**",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
        };
        var excludePatterns = new TextBox
        {
            Header = "Exclude globs (one per line)",
            PlaceholderText = "cache/**\r\n*.tmp",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
        };
        AutomationProperties.SetName(includePatterns, "Folder include globs, one per line");
        AutomationProperties.SetName(excludePatterns, "Folder exclude globs, one per line");
        var parallelFiles = new NumberBox
        {
            Header = "Parallel files in each folder tree",
            Minimum = 1,
            Maximum = FolderTransferPolicy.MaximumParallelFiles,
            Value = viewModel.DefaultParallelFiles,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Description = "LFTP transfers this many files concurrently inside each selected folder.",
        };
        AutomationProperties.SetName(parallelFiles, "Parallel files per folder tree, 1 to 16");

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
        bandwidthUnit.SelectedIndex = 0;
        var defaultLimitKiB = direction == TransferDirection.Download
            ? viewModel.DownloadLimitKiB
            : viewModel.UploadLimitKiB;
        if (defaultLimitKiB > 0)
        {
            limitBandwidth.IsChecked = true;
            bandwidthValue.Value = defaultLimitKiB;
            bandwidthValue.IsEnabled = true;
            bandwidthUnit.IsEnabled = true;
        }
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
        if (containsDirectory)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Folder controls",
                Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
            });
            content.Children.Add(new TextBlock
            {
                Text = "Globs and parallelism apply only to selected folders. Every folder still receives a fresh non-destructive dry run before LFTP starts.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72,
            });
            content.Children.Add(preset);
            content.Children.Add(presetName);
            content.Children.Add(presetButtons);
            content.Children.Add(includePatterns);
            content.Children.Add(excludePatterns);
            content.Children.Add(parallelFiles);
        }
        content.Children.Add(limitBandwidth);
        content.Children.Add(bandwidthGrid);
        content.Children.Add(schedule);
        content.Children.Add(scheduleGrid);
        content.Children.Add(runOnceInfo);
        content.Children.Add(validation);

        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is not FolderTransferPreset selected) return;
            presetName.Text = selected.Name;
            includePatterns.Text = string.Join(Environment.NewLine, selected.EffectiveIncludes);
            excludePatterns.Text = string.Join(Environment.NewLine, selected.EffectiveExcludes);
            parallelFiles.Value = selected.ParallelFiles;
            segments.Value = selected.DownloadSegmentsPerFile;
            deletePreset.IsEnabled = true;
            validation.IsOpen = false;
        };
        newPreset.Click += (_, _) =>
        {
            preset.SelectedItem = null;
            presetName.Text = string.Empty;
            includePatterns.Text = string.Empty;
            excludePatterns.Text = string.Empty;
            parallelFiles.Value = viewModel.DefaultParallelFiles;
            segments.Value = viewModel.DefaultDownloadSegments;
            deletePreset.IsEnabled = false;
            validation.IsOpen = false;
        };
        savePreset.Click += async (_, _) =>
        {
            if (!TryReadFolderOptions(
                out var includes, out var excludes, out var parallel, out var presetSegments, out var error))
            {
                ShowValidation(error);
                return;
            }
            var id = preset.SelectedItem is FolderTransferPreset selected ? selected.Id : Guid.NewGuid();
            var candidate = new FolderTransferPreset(
                id, presetName.Text.Trim(), includes, excludes, parallel, presetSegments);
            try
            {
                PlanValidator.Validate(candidate);
                preset.SelectedItem = await viewModel.SaveFolderTransferPresetAsync(candidate).ConfigureAwait(true);
                validation.Title = "Folder preset saved";
                validation.Message = $"{candidate.Name} is available in every session.";
                validation.Severity = InfoBarSeverity.Success;
                validation.IsOpen = true;
            }
            catch (Exception exception)
            {
                ShowValidation(exception.Message);
            }
        };
        deletePreset.Click += async (_, _) =>
        {
            if (preset.SelectedItem is not FolderTransferPreset selected) return;
            try
            {
                _ = await viewModel.DeleteFolderTransferPresetAsync(selected.Id).ConfigureAwait(true);
                preset.SelectedItem = null;
                presetName.Text = string.Empty;
                deletePreset.IsEnabled = false;
                validation.IsOpen = false;
            }
            catch (Exception exception)
            {
                ShowValidation(exception.Message);
            }
        };

        TransferUiOptions? selectedOptions = null;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{direction} transfer options",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 680,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "Queue transfers",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (!TryCreateOptions(out selectedOptions, out var error))
            {
                ShowValidation(error);
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

        void ShowValidation(string message)
        {
            validation.Title = "Review these options";
            validation.Message = message;
            validation.Severity = InfoBarSeverity.Error;
            validation.IsOpen = true;
        }

        bool TryReadFolderOptions(
            out ImmutableArray<string> includes,
            out ImmutableArray<string> excludes,
            out int parallel,
            out int downloadSegments,
            out string error)
        {
            includes = SplitPatterns(includePatterns.Text);
            excludes = SplitPatterns(excludePatterns.Text);
            parallel = 1;
            downloadSegments = 1;
            error = string.Empty;
            if (!containsDirectory) return true;
            if (double.IsNaN(parallelFiles.Value) || parallelFiles.Value != Math.Truncate(parallelFiles.Value) ||
                parallelFiles.Value is < 1 or > FolderTransferPolicy.MaximumParallelFiles)
            {
                error = $"Parallel files must be a whole number from 1 through {FolderTransferPolicy.MaximumParallelFiles}.";
                return false;
            }
            parallel = (int)parallelFiles.Value;
            if (double.IsNaN(segments.Value) || segments.Value != Math.Truncate(segments.Value) ||
                segments.Value is < 1 or > FolderTransferPolicy.MaximumSegmentsPerFile)
            {
                error = $"Download segments must be a whole number from 1 through {FolderTransferPolicy.MaximumSegmentsPerFile}.";
                return false;
            }
            downloadSegments = (int)segments.Value;
            try
            {
                PlanValidator.Validate(new FolderTransferPreset(
                    Guid.NewGuid(), "Current folder options", includes, excludes, parallel, downloadSegments));
                return true;
            }
            catch (ModelValidationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        static ImmutableArray<string> SplitPatterns(string value) =>
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToImmutableArray();

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

            if (!TryReadFolderOptions(
                out var includes, out var excludes, out var parallel, out _, out error))
                return false;

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

            options = new TransferUiOptions(
                selectedMode, segmentCount, bytesPerSecond, runAt, includes, excludes, parallel);
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
        var title = exception switch
        {
            TransferQueueException { HasUnknownOutcome: true } => "Transfer acceptance is being checked",
            TransferQueueException { Result.IsPartialSuccess: true } => $"Only some {itemName} were queued",
            _ => $"No {itemName} were queued",
        };
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
