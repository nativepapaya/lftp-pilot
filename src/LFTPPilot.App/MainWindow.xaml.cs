using System.Collections.Specialized;
using System.Runtime.InteropServices;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.App.Views.Pages;
using LFTPPilot.Core;
using LFTPPilot.Windows.Activation;
using LFTPPilot.Windows.Shell;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace LFTPPilot.App;

public sealed partial class MainWindow : Window
{
    private readonly ProtocolActivationRequest? _activation;
    private bool _initialized;
    private bool _allowClose;
    private bool _closePromptOpen;
    private AppWindow? _appWindow;
    private TaskbarProgressService? _taskbarProgress;
    private readonly JumpListService _jumpLists = new();
    private readonly SemaphoreSlim _jumpListGate = new(1, 1);
    private readonly SemaphoreSlim _remoteEditDialogGate = new(1, 1);
    private readonly HashSet<string> _pendingRemoteEditPrompts = new(StringComparer.Ordinal);
    private bool _activeEditsSurfacePending;

    public MainWindow(ProtocolActivationRequest? activation)
    {
        InitializeComponent();
        _activation = activation;
        ViewModel = new MainWindowViewModel(AppServices.Agent, AppServices.Preferences);
        ViewModel.RemoteEditLocalChanged += ViewModel_RemoteEditLocalChanged;
        ViewModel.Settings.PreferencesChanged += Settings_PreferencesChanged;
        RootGrid.DataContext = ViewModel;
        ViewModel.Activity.Jobs.CollectionChanged += Jobs_CollectionChanged;
        ViewModel.Connections.Profiles.CollectionChanged += Profiles_CollectionChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        ConfigureWindow();
        RootGrid.Loaded += RootGrid_Loaded;
        ApplyTheme(ViewModel.Settings.Preferences.Theme);
    }

    public MainWindowViewModel ViewModel { get; }

    private void Settings_PreferencesChanged(object? sender, AppPreferences preferences) => ApplyTheme(preferences.Theme);

    private void ApplyTheme(AppThemePreference preference) => RootGrid.RequestedTheme = preference switch
    {
        AppThemePreference.Light => ElementTheme.Light,
        AppThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        RootGrid.Loaded -= RootGrid_Loaded;
        var requestedProfile = _activation?.Action == ProtocolActivationAction.OpenProfile ? _activation.ProfileId : null;
        await ViewModel.InitializeAsync(requestedProfile).ConfigureAwait(true);
        UpdateTaskbarProgress();
        await RefreshJumpListAsync().ConfigureAwait(true);
        if (_activation?.Action == ProtocolActivationAction.OpenSettings)
        {
            await ShowToolAsync("Settings", new SettingsPage(WindowNative.GetWindowHandle(this)) { DataContext = ViewModel.Settings }, 860).ConfigureAwait(true);
        }
        else if (_activation?.Action == ProtocolActivationAction.ShowTransfers)
        {
            ViewModel.Activity.IsExpanded = true;
        }
    }

    private async void SessionTabs_AddTabButtonClick(TabView sender, object args) => await ShowConnectionsAsync().ConfigureAwait(true);

    private async void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is SessionViewModel session)
        {
            try
            {
                await ViewModel.CloseSessionAsync(session).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ViewModel.RequestWorkspaceRefresh();
                var content = new StackPanel { Spacing = 8, MaxWidth = 560 };
                content.Children.Add(new TextBlock
                {
                    Text = "The disconnect outcome could not be confirmed. The tab will remain visible until workspace state refreshes.",
                    TextWrapping = TextWrapping.Wrap,
                });
                content.Children.Add(new TextBlock
                {
                    Text = exception.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.78,
                });
                if (ViewModel.ActiveRemoteEditCount > 0)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "Review or finish the managed edits that belong to this connection, then try again.",
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                var blocked = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "Disconnect outcome unconfirmed",
                    Content = content,
                    PrimaryButtonText = ViewModel.ActiveRemoteEditCount > 0 ? "Open active edits" : null,
                    CloseButtonText = "OK",
                    DefaultButton = ViewModel.ActiveRemoteEditCount > 0 ? ContentDialogButton.Primary : ContentDialogButton.Close,
                };
                if (await blocked.ShowAsync() == ContentDialogResult.Primary)
                    await ShowActiveEditsAsync().ConfigureAwait(true);
            }
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await ShowConnectionsAsync().ConfigureAwait(true);

    private async Task ShowConnectionsAsync()
    {
        var page = new ConnectionProfilesPage(WindowNative.GetWindowHandle(this)) { DataContext = ViewModel.Connections };
        var dialog = new ContentDialog
        {
            Title = "Connections",
            Content = page,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = RootGrid.XamlRoot,
            MinWidth = 860,
        };
        ConfigureDialogSize(dialog, 900, 780);
        EventHandler<Models.WorkspaceSessionSeed> connected = (_, _) => dialog.Hide();
        ViewModel.Connections.SessionConnected += connected;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            ViewModel.Connections.SessionConnected -= connected;
        }
    }

    private async void Mirror_Click(object sender, RoutedEventArgs e) =>
        await ShowToolAsync("Mirror preview", new MirrorPage { DataContext = ViewModel.Mirror }, 900).ConfigureAwait(true);

    private async void RemoteTransfer_Click(object sender, RoutedEventArgs e) =>
        await ShowToolAsync("Remote-to-remote transfer", new RemoteTransferPage { DataContext = ViewModel.RemoteTransfer }, 760).ConfigureAwait(true);

    private async void ActiveEdits_Click(object sender, RoutedEventArgs e) =>
        await ShowActiveEditsAsync().ConfigureAwait(true);

    private async void Console_Click(object sender, RoutedEventArgs e) =>
        await ShowToolAsync("Isolated LFTP console", new ConsolePage { DataContext = ViewModel.Console }, 860).ConfigureAwait(true);

    private async void Settings_Click(object sender, RoutedEventArgs e) =>
        await ShowToolAsync("Settings", new SettingsPage(WindowNative.GetWindowHandle(this)) { DataContext = ViewModel.Settings }, 860).ConfigureAwait(true);

    private async void Help_Click(object sender, RoutedEventArgs e) =>
        await ShowToolAsync("Keyboard & mouse", new ShortcutsPage(), 620).ConfigureAwait(true);

    private async Task ShowToolAsync(string title, FrameworkElement content, double width)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
            MinWidth = width,
        };
        ConfigureDialogSize(dialog, width, 780);
        await dialog.ShowAsync();
    }

    private static void ConfigureDialogSize(ContentDialog dialog, double width, double maximumHeight)
    {
        // WinUI's default ContentDialog template caps every dialog at 548 px,
        // even when MinWidth is larger. Override the template resources on the
        // individual tool surface so wide forms are never silently clipped.
        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMaxHeight"] = maximumHeight;
    }

    private async Task ShowActiveEditsAsync()
    {
        if (_activeEditsSurfacePending) return;
        _activeEditsSurfacePending = true;
        await _remoteEditDialogGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await ViewModel.EnsureStateCurrentAsync().ConfigureAwait(true);
            while (ViewModel.ActiveRemoteEditCount > 0)
            {
                ViewModel.SelectedActiveRemoteEdit ??= ViewModel.ActiveRemoteEdits.FirstOrDefault();
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = $"Active edits ({ViewModel.ActiveRemoteEditCount})",
                    Content = new ActiveEditsPage { DataContext = ViewModel },
                    PrimaryButtonText = "Review / sync",
                    SecondaryButtonText = "Finish edit",
                    CloseButtonText = "Later",
                    DefaultButton = ContentDialogButton.Primary,
                    IsPrimaryButtonEnabled = ViewModel.SelectedActiveRemoteEdit is not null,
                    IsSecondaryButtonEnabled = ViewModel.SelectedActiveRemoteEdit is not null,
                };
                var result = await dialog.ShowAsync();
                var selected = ViewModel.SelectedActiveRemoteEdit;
                if (result == ContentDialogResult.None) return;
                if (selected is null) continue;
                if (result == ContentDialogResult.Primary)
                {
                    await ReviewAndResolveRemoteEditAsync(selected.EditId, selected.DisplayName).ConfigureAwait(true);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    await ConfirmFinishRemoteEditAsync(selected).ConfigureAwait(true);
                }
            }

            await new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "No active edits",
                Content = new TextBlock
                {
                    Text = "There are no Agent-managed remote files waiting for review or save monitoring.",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "OK",
            }.ShowAsync();
        }
        catch (Exception exception)
        {
            ViewModel.RequestWorkspaceRefresh();
            await new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Active edit state could not be confirmed",
                Content = new TextBlock
                {
                    Text = $"An edit action may have completed before the error was reported. Workspace state is refreshing.\n\n{exception.Message}",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
            }.ShowAsync();
        }
        finally
        {
            _remoteEditDialogGate.Release();
            _activeEditsSurfacePending = false;
        }
    }

    private async Task ConfirmFinishRemoteEditAsync(RemoteEditItemViewModel edit)
    {
        var warning = edit.Dirty
            ? "This managed copy has local changes that have not been synced. Finishing removes the copy without uploading them."
            : edit.WatcherFailed
                ? "Save monitoring failed for this managed copy. Finishing removes it without uploading anything."
                : "Finishing stops save monitoring and removes the managed copy. No upload occurs.";
        var confirm = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"Finish editing · {edit.DisplayName}?",
            Content = new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Finish and remove copy",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.CompleteRemoteEditAsync(edit.EditId).ConfigureAwait(true);
    }

    private void ViewModel_RemoteEditLocalChanged(RemoteEditLocalChange change)
    {
        if (!_pendingRemoteEditPrompts.Add(change.EditId)) return;
        _ = HandleRemoteEditChangeAsync(change);
    }

    private async Task HandleRemoteEditChangeAsync(RemoteEditLocalChange change)
    {
        await _remoteEditDialogGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var activeEdit = ViewModel.ActiveRemoteEdits.FirstOrDefault(candidate =>
                string.Equals(candidate.EditId, change.EditId, StringComparison.Ordinal));
            if (activeEdit is null || change.Kind == RemoteEditLocalChangeKind.Saved && !activeEdit.Dirty) return;

            if (change.Kind != RemoteEditLocalChangeKind.Saved)
            {
                var attention = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = $"Managed edit needs attention · {change.DisplayName}",
                    Content = new TextBlock { Text = change.Message, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "Finish editing",
                    CloseButtonText = "Keep managed copy",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await attention.ShowAsync() == ContentDialogResult.Primary)
                    await ConfirmFinishRemoteEditAsync(activeEdit).ConfigureAwait(true);
                return;
            }

            var detected = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = $"Local save detected · {change.DisplayName}",
                Content = new TextBlock
                {
                    Text = "LFTP Pilot has not uploaded anything. Review the current remote identity before choosing whether to sync this managed copy.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Review sync",
                SecondaryButtonText = "Finish editing",
                CloseButtonText = "Later",
                DefaultButton = ContentDialogButton.Primary,
            };
            var detectedResult = await detected.ShowAsync();
            if (detectedResult == ContentDialogResult.Secondary)
            {
                await ConfirmFinishRemoteEditAsync(activeEdit).ConfigureAwait(true);
                return;
            }
            if (detectedResult != ContentDialogResult.Primary) return;
            await ReviewAndResolveRemoteEditAsync(change.EditId, change.DisplayName).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ViewModel.RequestWorkspaceRefresh();
            await new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Remote edit outcome unconfirmed",
                Content = new TextBlock
                {
                    Text = $"A review, upload, overwrite, refresh, or finish action may have completed before the error was reported. Workspace state is refreshing.\n\n{exception.Message}",
                    TextWrapping = TextWrapping.Wrap,
                },
                CloseButtonText = "Close",
            }.ShowAsync();
        }
        finally
        {
            _pendingRemoteEditPrompts.Remove(change.EditId);
            _remoteEditDialogGate.Release();
        }
    }

    private async Task ReviewAndResolveRemoteEditAsync(string editId, string displayName)
    {
        var review = await ViewModel.ReviewRemoteEditAsync(editId).ConfigureAwait(true);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var resolution = await ShowRemoteEditReviewAsync(displayName, review).ConfigureAwait(true);
            if (resolution is null) return;
            var action = await ViewModel.ResolveRemoteEditAsync(editId, review.ReviewToken, resolution.Value).ConfigureAwait(true);
            if (action.Outcome == RemoteEditActionOutcome.ReviewRequired && action.Review is { } updatedReview)
            {
                review = updatedReview;
                continue;
            }

            var completed = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = action.Outcome == RemoteEditActionOutcome.Uploaded ? "Remote file updated" : "Managed copy refreshed",
                Content = new TextBlock { Text = action.Message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Finish editing",
                CloseButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await completed.ShowAsync() == ContentDialogResult.Primary &&
                ViewModel.ActiveRemoteEdits.FirstOrDefault(candidate => string.Equals(candidate.EditId, editId, StringComparison.Ordinal)) is { } activeEdit)
            {
                await ConfirmFinishRemoteEditAsync(activeEdit).ConfigureAwait(true);
            }
            return;
        }

        await new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "The file kept changing",
            Content = new TextBlock
            {
                Text = "No upload occurred. Wait for local and remote changes to settle, then use Active edits to start a fresh review.",
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "OK",
        }.ShowAsync();
    }

    private async Task<RemoteEditResolution?> ShowRemoteEditReviewAsync(string displayName, RemoteEditReview review)
    {
        var identityDetails = review.Current is null
            ? "Current remote identity: unavailable"
            : $"Current remote identity: {review.Current.Size:N0} bytes · {review.Current.ModifiedAt.ToLocalTime():g}";
        var content = new StackPanel { Spacing = 8, MaxWidth = 560 };
        content.Children.Add(new TextBlock { Text = review.Message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = $"Reviewed baseline: {review.Baseline.Size:N0} bytes · {review.Baseline.ModifiedAt.ToLocalTime():g}\n{identityDetails}",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = review.State == RemoteEditReviewState.ReadyToUpload
                ? $"Upload reviewed copy · {displayName}"
                : $"Remote conflict · {displayName}",
            Content = content,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (review.State == RemoteEditReviewState.ReadyToUpload)
        {
            dialog.PrimaryButtonText = "Upload reviewed copy";
            dialog.DefaultButton = ContentDialogButton.Primary;
        }
        else if (review.Conflict == RemoteEditConflictKind.RemoteChanged)
        {
            dialog.PrimaryButtonText = "Overwrite remote";
            dialog.SecondaryButtonText = "Refresh local copy";
        }
        else if (review.Conflict == RemoteEditConflictKind.RemoteMissingOrRenamed)
        {
            dialog.PrimaryButtonText = "Overwrite original path";
        }

        var result = await dialog.ShowAsync();
        if (review.State == RemoteEditReviewState.ReadyToUpload && result == ContentDialogResult.Primary)
            return RemoteEditResolution.Upload;
        if (review.Conflict is RemoteEditConflictKind.RemoteChanged or RemoteEditConflictKind.RemoteMissingOrRenamed && result == ContentDialogResult.Primary)
            return RemoteEditResolution.Overwrite;
        if (review.Conflict == RemoteEditConflictKind.RemoteChanged && result == ContentDialogResult.Secondary)
            return RemoteEditResolution.RefreshLocal;
        return null;
    }

    private void ConfigureWindow()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new global::Windows.Graphics.SizeInt32(1440, 920));
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.Closing += AppWindow_Closing;
        try { _taskbarProgress = new(); }
        catch (Exception exception) when (exception is COMException or InvalidCastException) { }
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateTaskbarProgress();

    private void Profiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _ = RefreshJumpListAsync();

    private void UpdateTaskbarProgress()
    {
        if (_taskbarProgress is null) return;
        try
        {
            var summary = TaskbarProgressPolicy.Summarize(ViewModel.Activity.Jobs);
            var handle = WindowNative.GetWindowHandle(this);
            if (summary.Completed is { } completed && summary.Total is { } total)
                _taskbarProgress.SetValue(handle, completed, total);
            _taskbarProgress.SetState(handle, summary.State);
        }
        catch (Exception exception) when (exception is COMException or ArgumentException or InvalidOperationException) { }
    }

    private async Task RefreshJumpListAsync()
    {
        await _jumpListGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var entries = new List<JumpListEntry>
            {
                new("Transfers", "lftp-pilot://transfers"),
                new("Settings", "lftp-pilot://settings"),
            };
            entries.AddRange(ViewModel.Connections.Profiles
                .OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(10)
                .Select(static profile => new JumpListEntry(
                    profile.Name,
                    $"lftp-pilot://open-profile?id={profile.Id:D}",
                    "Connections")));
            await _jumpLists.ReplaceAsync(entries).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is ArgumentException or COMException or InvalidOperationException or UnauthorizedAccessException) { }
        finally
        {
            _jumpListGate.Release();
        }
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        await ViewModel.EnsureStateCurrentAsync().ConfigureAwait(true);
        var hasActiveWork = ViewModel.Activity.ActiveCount > 0 || ViewModel.ActiveRemoteEditCount > 0;
        if (!hasActiveWork)
        {
            try { await CompleteCloseAsync(stopAgent: true).ConfigureAwait(true); }
            finally { _closePromptOpen = false; }
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = ViewModel.ActiveRemoteEditCount > 0 ? "Background work is still active" : "Transfers are still active",
                Content = ViewModel.ActiveRemoteEditCount > 0
                    ? "Managed remote edits or transfers are still active. Keep the Agent running to preserve managed copies and save monitoring. Stopping it cleans the managed cache and terminates its LFTP/SSH process trees."
                    : "Keep the Agent running to finish queued, active, or scheduled work. Stopping it terminates its LFTP and SSH process trees.",
                PrimaryButtonText = "Keep running",
                SecondaryButtonText = "Stop Agent",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsSecondaryButtonEnabled = AppServices.Agent.IsConnected,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                await CompleteCloseAsync(stopAgent: true).ConfigureAwait(true);
            }
            else if (result == ContentDialogResult.Primary)
            {
                await CompleteCloseAsync(stopAgent: false).ConfigureAwait(true);
            }
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private async Task CompleteCloseAsync(bool stopAgent)
    {
        try
        {
            // Remote searches are transient App-owned work, unlike durable transfers and
            // schedules. Cancel them before disconnecting the App so an isolated LFTP find
            // process cannot continue invisibly when the user keeps the Agent running.
            await ViewModel.CancelTransientOperationsAsync().ConfigureAwait(true);
            if (stopAgent && !AppServices.Agent.IsConnected && AppServices.ProcessManager.OwnsRunningAgent)
                await AppServices.ForceStopOwnedAgentAsync().ConfigureAwait(true);
            await AppServices.ShutdownAsync(stopAgent).ConfigureAwait(true);
            DetachShellHandlers();
            _allowClose = true;
            Close();
        }
        catch (Exception exception) when (stopAgent && AppServices.ProcessManager.OwnsRunningAgent)
        {
            await AppServices.ForceStopOwnedAgentAsync().ConfigureAwait(true);
            await AppServices.ShutdownAsync(stopAgent: false).ConfigureAwait(true);
            DetachShellHandlers();
            _allowClose = true;
            Close();
            System.Diagnostics.Debug.WriteLine(exception);
        }
        catch (Exception exception)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "The Agent could not be stopped",
                Content = exception.Message,
                CloseButtonText = "Keep LFTP Pilot open",
            };
            await dialog.ShowAsync();
        }
    }

    private void DetachShellHandlers()
    {
        ViewModel.Activity.Jobs.CollectionChanged -= Jobs_CollectionChanged;
        ViewModel.Connections.Profiles.CollectionChanged -= Profiles_CollectionChanged;
    }
}
