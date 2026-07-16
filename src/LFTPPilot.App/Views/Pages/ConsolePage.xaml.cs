using LFTPPilot.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LFTPPilot.App.Views.Pages;

public sealed partial class ConsolePage : UserControl
{
    public ConsolePage() => InitializeComponent();

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && DataContext is ConsoleViewModel viewModel && viewModel.ExecuteCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.ExecuteCommand.Execute(null);
        }
    }
}
