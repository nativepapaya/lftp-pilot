using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using LFTPPilot.App.ViewModels;

namespace LFTPPilot.App.Views.Pages;

public sealed partial class ConnectionProfilesPage : UserControl
{
    private readonly nint _ownerWindow;
    private ConnectionProfilesViewModel? _viewModel;
    private bool _synchronizingCredential;

    public ConnectionProfilesPage() : this(0)
    {
    }

    public ConnectionProfilesPage(nint ownerWindow)
    {
        _ownerWindow = ownerWindow;
        InitializeComponent();
        DataContextChanged += ConnectionProfilesPage_DataContextChanged;
        Unloaded += ConnectionProfilesPage_Unloaded;
    }

    private async void SshKeyBrowse_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_ownerWindow == 0 || DataContext is not ConnectionProfilesViewModel viewModel) return;
        var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) viewModel.SshKeyPath = file.Path;
    }

    private void Credential_PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_synchronizingCredential &&
            DataContext is ConnectionProfilesViewModel viewModel &&
            sender is PasswordBox passwordBox &&
            !string.Equals(viewModel.Credential, passwordBox.Password, StringComparison.Ordinal))
        {
            viewModel.Credential = passwordBox.Password;
        }
    }

    private void ConnectionProfilesPage_DataContextChanged(Microsoft.UI.Xaml.FrameworkElement sender, Microsoft.UI.Xaml.DataContextChangedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = args.NewValue as ConnectionProfilesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SynchronizeCredentialFromViewModel();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionProfilesViewModel.Credential))
        {
            SynchronizeCredentialFromViewModel();
        }
        else if (e.PropertyName == nameof(ConnectionProfilesViewModel.HasPendingHostKeyReview) && _viewModel?.HasPendingHostKeyReview == true)
        {
            // Trust is never the default. Focus Cancel after the inline review
            // is rendered so Enter or an accidental primary action cannot
            // enroll or replace a host key.
            DispatcherQueue.TryEnqueue(() => CancelHostKeyReviewButton.Focus(FocusState.Programmatic));
        }
    }

    private void SynchronizeCredentialFromViewModel()
    {
        var credential = _viewModel?.Credential ?? string.Empty;
        if (string.Equals(CredentialBox.Password, credential, StringComparison.Ordinal)) return;
        try
        {
            _synchronizingCredential = true;
            CredentialBox.Password = credential;
        }
        finally
        {
            _synchronizingCredential = false;
        }
    }

    private void ConnectionProfilesPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _viewModel?.CancelPendingHostKeyReview(clearCredential: true);
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }
}
