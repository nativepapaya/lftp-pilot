using LFTPPilot.App.Services;

namespace LFTPPilot.App.Diagnostics;

/// <summary>
/// Explicit, deterministic launch gate for the real file-pane performance surface.
/// The diagnostic is never selected by protocol activation or a normal app launch.
/// </summary>
public static class FilePanePerformanceHarness
{
    public const string LaunchArgument = "--diagnostic-file-pane";

    public static bool IsRequested(string? arguments)
    {
        var activationArguments = arguments?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return activationArguments.Any(IsDiagnosticArgument) || Environment.GetCommandLineArgs().Skip(1).Any(IsDiagnosticArgument);
    }

    private static bool IsDiagnosticArgument(string argument) =>
        argument.Equals(LaunchArgument, StringComparison.OrdinalIgnoreCase);

    public static FilePanePerformanceWindow Create(IAgentWorkspaceClient agent) => new(agent);
}
