using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LFTPPilot.App.Views.Controls;

public sealed partial class ActivityCenterView : UserControl
{
    public ActivityCenterView() => InitializeComponent();

    private void ActivityTab_Click(object sender, RoutedEventArgs e)
    {
        var selected = (sender as ToggleButton)?.Tag?.ToString() ?? "Transfers";
        TransfersDockTab.IsChecked = selected == "Transfers";
        HistoryDockTab.IsChecked = selected == "History";
        LogDockTab.IsChecked = selected == "Log";
        TransfersDockList.Visibility = selected == "Transfers" ? Visibility.Visible : Visibility.Collapsed;
        HistoryDockList.Visibility = selected == "History" ? Visibility.Visible : Visibility.Collapsed;
        LogDockList.Visibility = selected == "Log" ? Visibility.Visible : Visibility.Collapsed;
        if (DataContext is ActivityCenterViewModel viewModel) viewModel.IsExpanded = true;
    }

    private void CancelJob_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is ActivityCenterViewModel viewModel && sender is Button { DataContext: JobSnapshot job } && viewModel.CancelJobCommand.CanExecute(job))
            viewModel.CancelJobCommand.Execute(job);
    }

    private void RetryJob_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is ActivityCenterViewModel viewModel && sender is Button { DataContext: JobSnapshot job } && viewModel.RetryJobCommand.CanExecute(job))
            viewModel.RetryJobCommand.Execute(job);
    }
}
