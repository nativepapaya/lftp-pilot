using Microsoft.Windows.AppLifecycle;
using LFTPPilot.Windows.Activation;
using Windows.ApplicationModel.Activation;

namespace LFTPPilot.App.Services;

public static class ProtocolActivationRouter
{
    public static ProtocolActivationRequest? ReadCurrentActivation()
    {
        var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activated.Kind != ExtendedActivationKind.Protocol || activated.Data is not ProtocolActivatedEventArgs protocol)
        {
            return TryParseCommandLine(Environment.GetCommandLineArgs().Skip(1), out var commandLineRequest)
                ? commandLineRequest
                : null;
        }

        return ProtocolActivationParser.TryParse(protocol.Uri, out var request) ? request : null;
    }

    internal static bool TryParseCommandLine(IEnumerable<string> arguments, out ProtocolActivationRequest? request)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        request = null;
        var values = arguments.ToArray();
        if (values.Length != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out var uri)) return false;
        return ProtocolActivationParser.TryParse(uri, out request);
    }
}
