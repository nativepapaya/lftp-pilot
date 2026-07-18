using System.Diagnostics;
using LFTPPilot.Windows.Activation;

namespace LFTPPilot.Agent;

internal static class AgentNotificationActivation
{
    internal const string ArgumentPrefix = "----AppNotificationActivated:";

    internal static bool TryHandle(IEnumerable<string> arguments, Func<Uri, bool> launch)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(launch);
        if (!arguments.Any(static argument => argument.StartsWith(ArgumentPrefix, StringComparison.Ordinal))) return false;
        try
        {
            _ = launch(new Uri($"{ProtocolActivationParser.Scheme}://transfers"));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
    }

    internal static bool Launch(Uri uri)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
        return process is not null;
    }
}
