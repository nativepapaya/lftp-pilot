using Microsoft.UI.Xaml.Controls;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.App.Views.Controls;

public sealed partial class ActivityCenterView : UserControl
{
    public ActivityCenterView() => InitializeComponent();

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
