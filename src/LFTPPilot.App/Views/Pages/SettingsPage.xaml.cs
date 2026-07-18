using Microsoft.UI.Xaml.Controls;
using LFTPPilot.App.ViewModels;

namespace LFTPPilot.App.Views.Pages;

public sealed partial class SettingsPage : UserControl
{
    private readonly nint _ownerWindow;

    public SettingsPage() : this(0)
    {
    }

    public SettingsPage(nint ownerWindow)
    {
        _ownerWindow = ownerWindow;
        InitializeComponent();
    }

    private async void SupportBundle_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_ownerWindow == 0 || DataContext is not SettingsViewModel viewModel) return;
        var picker = new global::Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = $"LFTPPilot-Support-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("ZIP archive", [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindow);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try { await viewModel.CreateSupportBundleAsync(file.Path).ConfigureAwait(true); }
        catch { }
    }
}
