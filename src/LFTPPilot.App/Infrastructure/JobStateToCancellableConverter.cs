using LFTPPilot.Core;
using Microsoft.UI.Xaml.Data;

namespace LFTPPilot.App.Infrastructure;

public sealed class JobStateToCancellableConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is JobState.Queued or JobState.Running or JobState.Paused or JobState.Scheduled;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
