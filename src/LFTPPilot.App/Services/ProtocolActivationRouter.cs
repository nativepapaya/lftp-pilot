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
            return null;
        }

        return ProtocolActivationParser.TryParse(protocol.Uri, out var request) ? request : null;
    }
}
