using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;

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

    [Fact]
    public void MatchesUnicodeAndPatternCharactersAsLiteralDirectoryEntries()
    {
        const string path = "/remote/[EAC] 菊池桃子 - スペシャル・セレクションI";
        var result = new LftpCommandResult([
            new("stdout", LftpCommandBuilder.LiteralStatMarker + path),
            new("stdout", "drwxr-xr-x 2 alice staff 0 2026-07-19 12:30 [EAC] 菊池桃子 - スペシャル・セレクションI"),
            new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-19 12:31 another-file.bin"),
        ]);

        var entry = FreshRemoteStatParser.Parse(result, path, "Fresh remote path check");

        Assert.NotNull(entry);
        Assert.Equal(path, entry.FullPath);
        Assert.Equal(EntryKind.Directory, entry.Kind);
    }

    [Fact]
    public void TreatsCleanParentListingWithoutUnicodeUploadTargetAsAbsent()
    {
        const string path = "/remote/隣人が推し作家だった件 (とらの百合ノベルス).epub";
        var result = new LftpCommandResult([
            new("stdout", LftpCommandBuilder.LiteralStatMarker + path),
            new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-19 12:31 another-file.bin"),
        ]);

        Assert.Null(FreshRemoteStatParser.Parse(result, path, "Fresh remote path check"));
    }
}
