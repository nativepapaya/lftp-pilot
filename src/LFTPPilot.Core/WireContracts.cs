using System.Text.Json;

namespace LFTPPilot.Core;

public static class AgentProtocol
{
    public const int CurrentVersion = 5;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const string ControlPipeName = "LFTPPilot.Agent.Control.v1";
    public const string EventPipeName = "LFTPPilot.Agent.Events.v1";
    public const string StopMethod = "agent.stop";
}

public sealed record ProtocolEnvelope(
    int Version,
    string Kind,
    Guid CorrelationId,
    JsonElement Payload);

public sealed record ProtocolError(string Code, string Message);

public sealed record AgentRequest(string Method, JsonElement Arguments);

public sealed record AgentResponse(bool Success, JsonElement? Result = null, ProtocolError? Error = null);
