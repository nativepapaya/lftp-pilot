using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class CommandBuilderTests
{
    [Fact]
    public void QuoteEscapesLftpSyntaxAndRejectsNewlines()
    {
        Assert.Equal("\"a\\\\b\\\"c\"", LftpCommandBuilder.Quote("a\\b\"c"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.Quote("safe\nset ssl:verify-certificate no"));
        Assert.Equal("'a'\"'\"'b'", LftpCommandBuilder.ShellQuote("a'b"));
    }

    [Fact]
    public void ExplicitAndImplicitFtpsUseCorrectSchemes()
    {
        var explicitProfile = Profile(ConnectionProtocol.FtpsExplicit, 21);
        var implicitProfile = Profile(ConnectionProtocol.FtpsImplicit, 990);
        var explicitCommand = LftpCommandBuilder.BuildOpen(explicitProfile, "secret");
        var implicitCommand = LftpCommandBuilder.BuildOpen(implicitProfile, "secret");
        Assert.Contains("ftp://example.test:21", explicitCommand, StringComparison.Ordinal);
        Assert.Contains("set ftp:ssl-force true", explicitCommand, StringComparison.Ordinal);
        Assert.Contains("ftps://example.test:990", implicitCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenQuotesCredentialsAndRejectsSecretInjection()
    {
        var command = LftpCommandBuilder.BuildOpen(Profile(ConnectionProtocol.Sftp, 22), "p,a\\\"ss");
        Assert.Contains("open --user \"alice\" --password \"p,a\\\\\\\"ss\"", command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(Profile(ConnectionProtocol.Sftp, 22), "password\n! calc"));
    }

    [Fact]
    public void SshKeyOpenUsesUserWithoutAnEmptyPasswordOption()
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Key", ConnectionProtocol.Sftp, "example.test", 22, "alice", AuthenticationKind.SshKey,
            SshKeyPath: @"C:\Keys\id_ed25519");

        var command = LftpCommandBuilder.BuildOpen(profile);
        Assert.Contains("open --user \"alice\" \"sftp://example.test:22\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--password", command, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadMirrorUsesRemoteThenLocalAndSegments()
    {
        var command = LftpCommandBuilder.BuildMirror(CoreValidationTests.Mirror(), dryRun: true);
        Assert.Contains("--dry-run", command, StringComparison.Ordinal);
        Assert.Contains("--use-pget-n=3", command, StringComparison.Ordinal);
        Assert.Contains("\"/srv/data\" \"/c/Data\"", command, StringComparison.Ordinal);
        Assert.EndsWith("set net:limit-rate 0:0", command, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadMirrorUsesLocalThenRemoteAndDoesNotUsePget()
    {
        var command = LftpCommandBuilder.BuildMirror(CoreValidationTests.Mirror(MirrorDirection.Upload), dryRun: false);
        Assert.Contains("--reverse", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--use-pget-n", command, StringComparison.Ordinal);
        Assert.Contains("\"/c/Data\" \"/srv/data\"", command, StringComparison.Ordinal);
        Assert.EndsWith("set net:limit-rate 0:0", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentedDownloadUsesPgetResumeRateAndBackgroundJob()
    {
        var plan = new TransferPlan(Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "-remote.bin", @"C:\Out\remote.bin", TransferMode.Resume, 8, 4096);
        var command = LftpCommandBuilder.BuildTransfer(plan);
        Assert.StartsWith("set net:limit-rate 4096:4096; pget -n 8 -c \"./-remote.bin\"", command, StringComparison.Ordinal);
        Assert.EndsWith(" &", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundSkipDownloadDisablesClobberAndRestoresScopedSettings()
    {
        var plan = new TransferPlan(Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote.bin", @"C:\Out\remote.bin", TransferMode.Skip, 1, 4096);
        var command = LftpCommandBuilder.BuildTransfer(plan, background: false);
        Assert.Equal("set net:limit-rate 4096:4096; set xfer:clobber no; get \"/remote.bin\" -o \"/c/Out/remote.bin\"; set xfer:clobber yes; set net:limit-rate 0:0", command);
        Assert.Throws<InvalidOperationException>(() => LftpCommandBuilder.BuildTransfer(plan, background: true));
    }

    [Fact]
    public void QueuedTransferUsesValidatedCompletionMarkersAndRestoresSettingsOnBothOutcomes()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote.bin", @"C:\Out\remote.bin",
            TransferMode.Skip, RateLimitBytesPerSecond: 4096);
        var command = LftpCommandBuilder.BuildQueuedTransfer(
            plan, "QUEUE_ALIAS", "QUEUE_OK", "QUEUE_FAILED", "SUBMIT_OK", "SUBMIT_FAILED");

        Assert.StartsWith("alias QUEUE_ALIAS \"( ( set net:limit-rate 4096:4096 && set xfer:clobber no && get", command, StringComparison.Ordinal);
        Assert.Contains("; ( queue QUEUE_ALIAS && echo \"SUBMIT_OK\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("queue ( (", command, StringComparison.Ordinal);
        Assert.Equal(3, command.Split("set net:limit-rate 0:0", StringSplitOptions.None).Length);
        Assert.Equal(3, command.Split("set xfer:clobber yes", StringSplitOptions.None).Length);
        Assert.Contains("echo \\\"QUEUE_OK\\\"", command, StringComparison.Ordinal);
        Assert.Contains("echo \\\"QUEUE_FAILED\\\"", command, StringComparison.Ordinal);
        Assert.Equal(4, command.Split("alias QUEUE_ALIAS", StringSplitOptions.None).Length);
        Assert.EndsWith("&& echo \"SUBMIT_OK\" || echo \"SUBMIT_FAILED\" )", command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildQueuedTransfer(
            plan, "QUEUE_ALIAS", "OK; ! calc", "FAILED", "SUBMIT_OK", "SUBMIT_FAILED"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildQueuedTransfer(
            plan, "QUEUE_ALIAS", "SAME", "SAME", "SUBMIT_OK", "SUBMIT_FAILED"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildQueuedTransfer(
            plan, "QUEUE; quit", "OK", "FAILED", "SUBMIT_OK", "SUBMIT_FAILED"));
    }

    [Fact]
    public void ExplicitOverwriteUsesDocumentedDeleteTargetOptionWhileAutoDoesNot()
    {
        var download = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote.bin", @"C:\Out\remote.bin",
            TransferMode.Overwrite, Segments: 8);
        var upload = download with
        {
            Id = Guid.NewGuid(),
            Direction = TransferDirection.Upload,
            SourcePath = @"C:\Out\remote.bin",
            DestinationPath = "/remote.bin",
            Segments = 1,
        };

        Assert.StartsWith("get -e ", LftpCommandBuilder.BuildTransfer(download, background: false), StringComparison.Ordinal);
        Assert.DoesNotContain("pget", LftpCommandBuilder.BuildTransfer(download, background: false), StringComparison.Ordinal);
        Assert.StartsWith("put -e ", LftpCommandBuilder.BuildTransfer(upload, background: false), StringComparison.Ordinal);
        Assert.DoesNotContain(" -e ", LftpCommandBuilder.BuildTransfer(download with { Mode = TransferMode.Auto }, background: false),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredFileMutationsQuotePathsAndSelectNonRecursiveDeletionByDefault()
    {
        Assert.Equal("mkdir -p \"/new directory\"", LftpCommandBuilder.BuildCreateDirectory("/new directory"));
        Assert.Equal("mv \"/old; quit\" \"/renamed\\\".txt\"", LftpCommandBuilder.BuildMove("/old; quit", "/renamed\".txt"));
        Assert.Equal("rm \"/file.txt\"", LftpCommandBuilder.BuildDelete("/file.txt", isDirectory: false, recursive: true));
        Assert.Equal("rmdir \"/empty\"", LftpCommandBuilder.BuildDelete("/empty", isDirectory: true, recursive: false));
        Assert.Equal("rm -r \"/tree\"", LftpCommandBuilder.BuildDelete("/tree", isDirectory: true, recursive: true));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/safe/../escape")]
    [InlineData("/safe//ambiguous")]
    [InlineData("/safe\nquit")]
    public void StructuredFileMutationsRejectRootAmbiguityAndCommandControls(string path)
    {
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildCreateDirectory(path));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildDelete(path, isDirectory: false, recursive: false));
    }

    [Fact]
    public void RemoteTransferUsesCredentialFreeSlotUrlsAndScopedFxpRouting()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var fxp = new RemoteTransferPlan(Guid.NewGuid(), source, destination, "/source file.bin", "/target.bin", RemoteTransferMode.Fxp);
        var relay = fxp with { Id = Guid.NewGuid(), Mode = RemoteTransferMode.ClientRelay, Overwrite = true };

        Assert.Equal(
            "set ftp:use-fxp true; set xfer:clobber no; get \"slot:source/source file.bin\" -o \"slot:destination/target.bin\"",
            LftpCommandBuilder.BuildRemoteTransfer(fxp));
        Assert.Equal(
            "set ftp:use-fxp false; get -e \"slot:source/source file.bin\" -o \"slot:destination/target.bin\"",
            LftpCommandBuilder.BuildRemoteTransfer(relay));
    }

    private static ConnectionProfile Profile(ConnectionProtocol protocol, int port) =>
        new(Guid.NewGuid(), "Site", protocol, "example.test", port, "alice", AuthenticationKind.Password);
}
