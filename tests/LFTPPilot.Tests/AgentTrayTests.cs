using LFTPPilot.Agent;
using LFTPPilot.Windows.Activation;
using LFTPPilot.Windows.Shell;

namespace LFTPPilot.Tests;

public sealed class AgentTrayTests
{
    [Fact]
    public void NotificationActivationCanOnlyOpenTheFixedTransfersRoute()
    {
        Uri? launched = null;

        Assert.True(AgentNotificationActivation.TryHandle(
            [AgentNotificationActivation.ArgumentPrefix + "untrusted=ignored"],
            uri => { launched = uri; return true; }));
        Assert.Equal(AgentTrayActions.TransfersUri, launched);
        Assert.False(AgentNotificationActivation.TryHandle(["--help"], _ => true));
    }

    [Fact]
    public void NotificationActivationIsConsumedEvenWhenWindowsDoesNotReturnAProcess()
    {
        Assert.True(AgentNotificationActivation.TryHandle(
            [AgentNotificationActivation.ArgumentPrefix + "untrusted-payload"],
            static _ => false));
    }

    [Fact]
    public void OpenCommandUsesTheAllowlistedTransfersActivation()
    {
        Uri? launched = null;
        var actions = new AgentTrayActions(
            uri =>
            {
                launched = uri;
                return true;
            },
            () => throw new Xunit.Sdk.XunitException("Open must not stop the Agent."));

        Assert.True(actions.TryExecute(AgentTrayActions.OpenCommand));
        Assert.Equal(AgentTrayActions.TransfersUri, launched);
        Assert.True(ProtocolActivationParser.TryParse(launched!, out var request));
        Assert.Equal(new ProtocolActivationRequest(ProtocolActivationAction.ShowTransfers), request);
    }

    [Fact]
    public void StopCommandRequestsGracefulAgentShutdown()
    {
        var stopRequested = false;
        var actions = new AgentTrayActions(
            _ => throw new Xunit.Sdk.XunitException("Stop must not activate the App."),
            () => stopRequested = true);

        Assert.True(actions.TryExecute(AgentTrayActions.StopCommand));
        Assert.True(stopRequested);
    }

    [Fact]
    public void UnknownAndFailingCommandsCannotEscapeTheTrayCallback()
    {
        var actions = new AgentTrayActions(_ => throw new InvalidOperationException("activation failed"), () => { });

        Assert.False(actions.TryExecute(42));
        Assert.False(actions.TryExecute(AgentTrayActions.OpenCommand));
    }
}
