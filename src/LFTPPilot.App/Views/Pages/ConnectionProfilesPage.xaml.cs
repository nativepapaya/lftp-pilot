using Microsoft.UI.Xaml.Controls;
using LFTPPilot.App.ViewModels;

namespace LFTPPilot.App.Views.Pages;

public sealed partial class ConnectionProfilesPage : UserControl
{
    private readonly nint _ownerWindow;
    private ConnectionProfilesViewModel? _viewModel;

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
        if (DataContext is ConnectionProfilesViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.Credential = passwordBox.Password;
        }
    }

    private void ConnectionProfilesPage_DataContextChanged(Microsoft.UI.Xaml.FrameworkElement sender, Microsoft.UI.Xaml.DataContextChangedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = args.NewValue as ConnectionProfilesViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionProfilesViewModel.Credential) && string.IsNullOrEmpty(_viewModel?.Credential) && CredentialBox.Password.Length > 0)
        {
            CredentialBox.Password = string.Empty;
        }
    }

    private void ConnectionProfilesPage_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = null;
    }
}
