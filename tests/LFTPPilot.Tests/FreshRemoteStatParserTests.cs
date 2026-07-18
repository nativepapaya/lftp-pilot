using LFTPPilot.Agent;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class FreshRemoteStatParserTests
{
    [Fact]
    public void AcceptsSingleSftpMissingDiagnosticBoundToExactRequestedPath()
    {
        var result = new LftpCommandResult([
            new("stderr", "Access failed: No such file (/remote/new-雪)")
        ]);

        Assert.Null(FreshRemoteStatParser.Parse(result, "/remote/new-雪", "Fresh remote path check"));
    }

    [Theory]
    [InlineData("Access failed: No such file")]
    [InlineData("Access failed: No such file (/remote/different)")]
    [InlineData("Access failed: No such file (/remote/new-雪) (/remote/different)")]
    public void RejectsUnboundOrDifferentlyBoundSftpMissingDiagnostic(string diagnostic)
    {
        var result = new LftpCommandResult([new("stderr", diagnostic)]);

        var exception = Assert.Throws<InvalidDataException>(() =>
            FreshRemoteStatParser.Parse(result, "/remote/new-雪", "Fresh remote path check"));
        Assert.Contains("ambiguous output", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
