using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace LFTPPilot.Windows.Shell;

public sealed class NotificationService : IDisposable
{
    private bool _registered;

    public void Register()
    {
        if (_registered) return;
        AppNotificationManager.Default.Register();
        _registered = true;
    }

    public void Show(string title, string message, string? tag = null, string? group = null)
    {
        if (!_registered) throw new InvalidOperationException("Register notifications before showing one.");
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        AppNotification notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
        if (!string.IsNullOrWhiteSpace(tag)) notification.Tag = tag;
        if (!string.IsNullOrWhiteSpace(group)) notification.Group = group;
        AppNotificationManager.Default.Show(notification);
    }

    public void Dispose()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception exception) when (exception is not
            (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException)) { }
        finally { _registered = false; }
    }
}
