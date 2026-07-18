using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class CommandBuilderTests
{
    private const string KnownHostsPath = @"C:\LFTP Pilot\known_hosts";
    private const string HostKeyAlias = "lftp-pilot-test-alias";

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
        var command = LftpCommandBuilder.BuildOpen(
            Profile(ConnectionProtocol.Sftp, 22), "p,a\\\"ss", KnownHostsPath, HostKeyAlias);
        Assert.Contains("open --user \"alice\" --password \"p,a\\\\\\\"ss\"", command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(
            Profile(ConnectionProtocol.Sftp, 22), "password\n! calc", KnownHostsPath, HostKeyAlias));
    }

    [Fact]
    public void UnencryptedSshKeyInitializesEmptyLftpCredentialWithoutInventingASecret()
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Key", ConnectionProtocol.Sftp, "example.test", 22, "alice", AuthenticationKind.SshKey,
            SshKeyPath: @"C:\Keys\id_ed25519");

        var command = LftpCommandBuilder.BuildOpen(profile, trustedKnownHostsPath: KnownHostsPath, hostKeyAlias: HostKeyAlias);
        Assert.Contains("open --user \"alice\" --password \"\" \"sftp://example.test:22\"", command, StringComparison.Ordinal);
        Assert.Contains("-o IdentitiesOnly=yes -i '/c/Keys/id_ed25519'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptedSshKeyPassphraseUsesLftpCredentialChannel()
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Encrypted key", ConnectionProtocol.Sftp, "example.test", 22, "alice", AuthenticationKind.SshKey,
            SshKeyPath: @"C:\Keys\id_ed25519_encrypted");

        var command = LftpCommandBuilder.BuildOpen(
            profile, "key-passphrase", KnownHostsPath, HostKeyAlias);

        Assert.Contains("open --user \"alice\" --password \"key-passphrase\"", command, StringComparison.Ordinal);
        Assert.Contains("-i '/c/Keys/id_ed25519_encrypted'", command, StringComparison.Ordinal);
    }

    [Fact]
    public void SftpOpenRequiresPinnedHostKeyInputsAndForcesStrictOpenSshOptions()
    {
        var profile = Profile(ConnectionProtocol.Sftp, 22);

        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(profile, "secret"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(profile, "secret", KnownHostsPath));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(profile, "secret", KnownHostsPath, "bad;alias"));

        var command = LftpCommandBuilder.BuildOpen(profile, "secret", KnownHostsPath, HostKeyAlias);

        Assert.Contains("ssh -F none -a -x", command, StringComparison.Ordinal);
        Assert.Contains("-o StrictHostKeyChecking=yes", command, StringComparison.Ordinal);
        Assert.Contains("-o 'UserKnownHostsFile=\\\"/c/LFTP Pilot/known_hosts\\\"'", command, StringComparison.Ordinal);
        Assert.Contains("-o GlobalKnownHostsFile=none", command, StringComparison.Ordinal);
        Assert.Contains("-o UpdateHostKeys=no", command, StringComparison.Ordinal);
        Assert.Contains("-o VerifyHostKeyDNS=no", command, StringComparison.Ordinal);
        Assert.Contains("-o CheckHostIP=no", command, StringComparison.Ordinal);
        Assert.Contains("-o IdentityAgent=none", command, StringComparison.Ordinal);
        Assert.Contains($"-o 'HostKeyAlias={HostKeyAlias}'", command, StringComparison.Ordinal);
        Assert.DoesNotContain("StrictHostKeyChecking=no", command, StringComparison.Ordinal);
        Assert.DoesNotContain("StrictHostKeyChecking=accept-new", command, StringComparison.Ordinal);
        Assert.DoesNotContain("BatchMode=yes", command, StringComparison.Ordinal);
    }

    [Fact]
    public void FtpOpenPreservesExistingBehaviorAndRejectsSshTrustInputs()
    {
        var profile = Profile(ConnectionProtocol.Ftp, 21);

        var command = LftpCommandBuilder.BuildOpen(profile, "secret");

        Assert.Contains("set ftp:ssl-allow false", command, StringComparison.Ordinal);
        Assert.DoesNotContain("sftp:connect-program", command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildOpen(
            profile, "secret", KnownHostsPath, HostKeyAlias));
    }

    [Fact]
    public void DownloadMirrorUsesRemoteThenLocalAndSegments()
    {
        var command = LftpCommandBuilder.BuildMirror(CoreValidationTests.Mirror(), dryRun: true);
        Assert.Contains("--dry-run", command, StringComparison.Ordinal);
        Assert.Contains("--no-symlinks --overwrite", command, StringComparison.Ordinal);
        Assert.Contains("--use-pget-n=3", command, StringComparison.Ordinal);
        Assert.Contains("\"/srv/data\" \"/c/Data\"", command, StringComparison.Ordinal);
        Assert.EndsWith("set net:limit-rate 0:0", command, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadMirrorUsesLocalThenRemoteAndDoesNotUsePget()
    {
        var command = LftpCommandBuilder.BuildMirror(CoreValidationTests.Mirror(MirrorDirection.Upload), dryRun: false);
        Assert.Contains("--reverse", command, StringComparison.Ordinal);
        Assert.Contains("--no-symlinks --overwrite", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--use-pget-n", command, StringComparison.Ordinal);
        Assert.Contains("\"/c/Data\" \"/srv/data\"", command, StringComparison.Ordinal);
        Assert.EndsWith("set net:limit-rate 0:0", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\", "/c")]
    [InlineData(@"\\server\share\", "//server/share")]
    public void MirrorWindowsRootsNeverEmitATrailingSlashThatAppendsTheSourceName(string localRoot, string expected)
    {
        var definition = CoreValidationTests.Mirror() with { LocalRoot = localRoot };

        var download = LftpCommandBuilder.BuildMirror(definition, dryRun: true);
        var upload = LftpCommandBuilder.BuildMirror(
            definition with { Direction = MirrorDirection.Upload }, dryRun: true);

        Assert.Contains($"\"/srv/data\" \"{expected}\"", download, StringComparison.Ordinal);
        Assert.Contains($"\"{expected}\" \"/srv/data\"", upload, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{expected}/\"", download, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{expected}/\"", upload, StringComparison.Ordinal);
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
        Assert.Equal("set net:limit-rate 4096:4096; set xfer:use-temp-file no; set xfer:clobber no; get \"/remote.bin\" -o \"/c/Out/remote.bin\"; set xfer:clobber yes; set xfer:use-temp-file yes; set net:limit-rate 0:0", command);
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

        Assert.StartsWith("alias QUEUE_ALIAS \"( ( set net:limit-rate 4096:4096 && set xfer:use-temp-file no && set xfer:clobber no && get", command, StringComparison.Ordinal);
        Assert.Contains("; ( queue QUEUE_ALIAS && echo \"SUBMIT_OK\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("queue ( (", command, StringComparison.Ordinal);
        Assert.Equal(3, command.Split("set net:limit-rate 0:0", StringSplitOptions.None).Length);
        Assert.Equal(3, command.Split("set xfer:clobber yes", StringSplitOptions.None).Length);
        Assert.Equal(3, command.Split("set xfer:use-temp-file yes", StringSplitOptions.None).Length);
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
    public void DirectoryDownloadUsesGuardedMirrorWithoutExtraneousDeleteFlags()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote folder", @"C:\Out\local folder",
            TransferMode.Resume, Segments: 8, RateLimitBytesPerSecond: 4096, SourceKind: TransferSourceKind.Directory);

        var command = LftpCommandBuilder.BuildTransfer(plan, background: false);

        Assert.Equal(
            "set net:limit-rate 4096:4096; mirror --continue --no-symlinks --overwrite --use-pget-n=8 \"/remote folder\" \"/c/Out/local folder\"; set net:limit-rate 0:0",
            command);
        AssertGuardedDirectoryMirrorCommand(command);
    }

    [Fact]
    public void DirectoryUploadUsesGuardedReverseMirrorWithoutExtraneousDeleteFlags()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Upload, @"C:\Source folder", "/remote folder",
            SourceKind: TransferSourceKind.Directory);

        var command = LftpCommandBuilder.BuildTransfer(plan, background: false);

        Assert.Equal("mirror --reverse --no-symlinks --overwrite \"/c/Source folder\" \"/remote folder\"", command);
        AssertGuardedDirectoryMirrorCommand(command);
    }

    [Fact]
    public void DirectoryTransferPreviewMatchesProductionCoreWithoutRateMutation()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Upload, @"C:\Source folder\", "/remote folder",
            TransferMode.Resume, RateLimitBytesPerSecond: 4096, SourceKind: TransferSourceKind.Directory);

        var command = LftpCommandBuilder.BuildDirectoryTransferPreview(plan);

        Assert.Equal(
            "mirror --verbose=1 --dry-run --reverse --continue --no-symlinks --overwrite \"/c/Source folder\" \"/remote folder\"",
            command);
        Assert.DoesNotContain("net:limit-rate", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--script", command, StringComparison.Ordinal);
        AssertGuardedDirectoryMirrorCommand(command);

        var download = plan with
        {
            Direction = TransferDirection.Download,
            SourcePath = "/remote folder",
            DestinationPath = @"C:\Destination folder\",
            Segments = 8,
        };
        var downloadCommand = LftpCommandBuilder.BuildDirectoryTransferPreview(download);
        Assert.Contains("--use-pget-n=8", downloadCommand, StringComparison.Ordinal);
        Assert.Contains("\"/c/Destination folder\"", downloadCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/c/Destination folder/\"", downloadCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryTransferPreviewRejectsFilePlans()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote.bin", @"C:\Out\remote.bin");

        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildDirectoryTransferPreview(plan));
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

    [Fact]
    public void RemoteSearchUsesOnlyTheLockedBoundedFindCommand()
    {
        var command = LftpCommandBuilder.BuildRemoteFind("/srv/remote \"folder\"; ! literal", 32);

        Assert.Equal("command find -d 32 \"/srv/remote \\\"folder\\\"; ! literal\"", command);
        Assert.DoesNotContain("--script", command, StringComparison.Ordinal);
        Assert.DoesNotContain("redirect", command, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/safe/")]
    [InlineData("/safe/../escape")]
    [InlineData("/safe//ambiguous")]
    [InlineData("/safe\nquit")]
    public void RemoteSearchRejectsNonCanonicalRoots(string root)
    {
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.BuildRemoteFind(root, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void RemoteSearchRejectsDepthOutsideTheSharedPolicy(int maxDepth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LftpCommandBuilder.BuildRemoteFind("/safe", maxDepth));
    }

    private static void AssertGuardedDirectoryMirrorCommand(string command)
    {
        Assert.Contains("--no-symlinks", command, StringComparison.Ordinal);
        Assert.Contains("--overwrite", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--Remove-source", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--Move", command, StringComparison.Ordinal);
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
    public void RemoteTransferUsesCredentialFreeSlotUrlsAndKeepsClientRelayOutOfTheSharedProcess()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var fxp = new RemoteTransferPlan(Guid.NewGuid(), source, destination, "/source file.bin", "/target.bin", RemoteTransferMode.Fxp);
        var relay = fxp with { Id = Guid.NewGuid(), Mode = RemoteTransferMode.ClientRelay, Overwrite = true };

        Assert.Equal(
            "set ftp:use-fxp true; set xfer:use-temp-file no; set xfer:clobber no; get \"slot:source/source file.bin\" -o \"slot:destination/target.bin\"; set xfer:clobber yes; set xfer:use-temp-file yes",
            LftpCommandBuilder.BuildRemoteTransfer(fxp));
        Assert.Throws<InvalidOperationException>(() => LftpCommandBuilder.BuildRemoteTransfer(relay));
    }

    [Fact]
    public void RemoteRelayUsesManagedLocalPathAndScopesNoClobberWithoutDeleteBeforeUpload()
    {
        const string managed = @"C:\Agent State\relay\payload.bin";

        Assert.Equal(
            "set xfer:use-temp-file no; get \"/source file.bin\" -o \"/c/Agent State/relay/payload.bin\"; set xfer:use-temp-file yes",
            LftpCommandBuilder.BuildRemoteRelayDownload("/source file.bin", managed));
        Assert.Equal(
            "set xfer:use-temp-file no; set xfer:clobber no; put \"/c/Agent State/relay/payload.bin\" -o \"/target.bin\"; set xfer:clobber yes; set xfer:use-temp-file yes",
            LftpCommandBuilder.BuildRemoteRelayUpload(managed, "/target.bin", overwrite: false));
        var overwrite = LftpCommandBuilder.BuildRemoteRelayUpload(managed, "/target.bin", overwrite: true);
        Assert.Equal("put \"/c/Agent State/relay/payload.bin\" -o \"/target.bin\"", overwrite);
        Assert.DoesNotContain("put -e", overwrite, StringComparison.Ordinal);
    }

    private static ConnectionProfile Profile(ConnectionProtocol protocol, int port) =>
        new(Guid.NewGuid(), "Site", protocol, "example.test", port, "alice", AuthenticationKind.Password);
}
