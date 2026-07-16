using System.Collections.Immutable;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class CoreValidationTests
{
    [Fact]
    public void ValidSftpProfileHasNoIssues()
    {
        var profile = Profile(ConnectionProtocol.Sftp, AuthenticationKind.SshKey) with { SshKeyPath = @"C:\Keys\id_ed25519" };
        Assert.Empty(ProfileValidator.Validate(profile));
        Assert.Equal(22, ProfileValidator.DefaultPort(ConnectionProtocol.Sftp));
        Assert.Equal(990, ProfileValidator.DefaultPort(ConnectionProtocol.FtpsImplicit));
    }

    [Theory]
    [InlineData(@"\\?\C:\Keys\id_ed25519")]
    [InlineData(@"\\.\C:\Keys\id_ed25519")]
    [InlineData(@"\??\C:\Keys\id_ed25519")]
    [InlineData(@"\\??\C:\Keys\id_ed25519")]
    public void ProfileRejectsDeviceNamespaceSshKeyPaths(string sshKeyPath)
    {
        var issues = ProfileValidator.Validate(
            Profile(ConnectionProtocol.Sftp, AuthenticationKind.SshKey) with { SshKeyPath = sshKeyPath });

        Assert.Contains(issues, issue => issue.Field == "sshKeyPath" && issue.Code == "device-path");
    }

    [Fact]
    public void ConnectionIdentityPreservesCaseSensitiveSshKeyPathDistinctions()
    {
        var profile = Profile(ConnectionProtocol.Sftp, AuthenticationKind.SshKey) with
        {
            SshKeyPath = @"C:\Keys\CaseSensitive\id_ed25519",
        };

        var original = ConnectionIdentity.FromProfile(profile);
        var caseOnlyChange = ConnectionIdentity.FromProfile(profile with
        {
            SshKeyPath = @"C:\Keys\casesensitive\id_ed25519",
        });

        Assert.NotEqual(original, caseOnlyChange);
    }

    [Fact]
    public void ProfileRejectsOversizedSshKeyPathBeforeIdentityCanonicalization()
    {
        var oversized = @"C:\" + new string('a', 32_765);
        var profile = Profile(ConnectionProtocol.Sftp, AuthenticationKind.SshKey) with { SshKeyPath = oversized };

        var issues = ProfileValidator.Validate(profile);

        Assert.Contains(issues, issue => issue.Field == "sshKeyPath" && issue.Code == "length");
        Assert.Throws<ModelValidationException>(() => ConnectionIdentity.FromProfile(profile));
    }

    [Fact]
    public void ProfileRejectsSchemeControlCharactersAndInvalidKeyProtocol()
    {
        var profile = Profile(ConnectionProtocol.Ftp, AuthenticationKind.SshKey) with
        {
            Host = "ftp://example.test\nset ssl:verify-certificate no",
            SshKeyPath = @"C:\Keys\id",
        };
        var issues = ProfileValidator.Validate(profile);
        Assert.Contains(issues, issue => issue.Field == "host" && issue.Code == "control-character");
        Assert.Contains(issues, issue => issue.Field == "host" && issue.Code == "format");
        Assert.Contains(issues, issue => issue.Field == "authentication" && issue.Code == "unsupported");
    }

    [Theory]
    [InlineData("-oProxyCommand=calc.exe")]
    [InlineData("[example.test]")]
    [InlineData("bad;host")]
    [InlineData("broken[host")]
    public void ProfileRejectsHostsThatAreNotDnsNamesOrIpAddresses(string host)
    {
        var issues = ProfileValidator.Validate(Profile(ConnectionProtocol.Sftp, AuthenticationKind.AskOnConnect) with { Host = host });
        Assert.Contains(issues, issue => issue.Field == "host" && issue.Code == "format");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    [InlineData("[2001:db8::10]")]
    public void ProfileAcceptsDnsAndIpHostForms(string host)
    {
        Assert.Empty(ProfileValidator.Validate(Profile(ConnectionProtocol.Sftp, AuthenticationKind.AskOnConnect) with { Host = host }));
    }

    [Theory]
    [InlineData("/duplicate//separator")]
    [InlineData("/parent/../escape")]
    [InlineData("/trailing/")]
    public void ProfileRejectsNonCanonicalInitialPathsAndBookmarks(string remotePath)
    {
        var issues = ProfileValidator.Validate(Profile(ConnectionProtocol.Ftp, AuthenticationKind.Anonymous) with
        {
            InitialRemotePath = remotePath,
            Bookmarks = [remotePath],
        });

        Assert.Contains(issues, issue => issue.Field == "initialRemotePath");
        Assert.Contains(issues, issue => issue.Field == "bookmarks");
    }

    [Fact]
    public void TransferValidationRejectsInjectedPathAndUnboundedSegments()
    {
        var plan = new TransferPlan(Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote\nrm -rf", @"C:\Local", Segments: 65);
        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));
        Assert.Contains(exception.Issues, issue => issue.Field == "sourcePath");
        Assert.Contains(exception.Issues, issue => issue.Field == "segments");
    }

    [Theory]
    [InlineData(TransferMode.Overwrite)]
    [InlineData(TransferMode.Skip)]
    public void DirectoryTransferValidationRejectsFileOnlyModes(TransferMode mode)
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote/folder", @"C:\Local\folder",
            Mode: mode, SourceKind: TransferSourceKind.Directory);

        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));

        Assert.Contains(exception.Issues, issue => issue.Field == "mode" && issue.Code == "unsupported");
    }

    [Fact]
    public void TransferValidationRejectsUnsupportedSourceKind()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote/special", @"C:\Local\special",
            SourceKind: (TransferSourceKind)99);

        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));

        Assert.Contains(exception.Issues, issue => issue.Field == "sourceKind" && issue.Code == "unsupported");
    }

    [Theory]
    [InlineData(TransferSourceKind.File)]
    [InlineData(TransferSourceKind.Directory)]
    public void UploadTransferValidationRejectsIgnoredSegmentation(TransferSourceKind sourceKind)
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Upload, @"C:\Local\source", "/remote/target",
            Segments: 2, SourceKind: sourceKind);

        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));

        Assert.Contains(exception.Issues, issue =>
            issue.Field == "segments" && issue.Code == "unsupported");
    }

    [Fact]
    public void DirectoryTransferValidationUsesMirrorSegmentLimit()
    {
        var plan = new TransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), TransferDirection.Download, "/remote/folder", @"C:\Local\folder",
            Segments: 17, SourceKind: TransferSourceKind.Directory);

        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));

        Assert.Contains(exception.Issues, issue => issue.Field == "segments" && issue.Code == "range");
    }

    [Fact]
    public void MirrorValidationRequiresAbsoluteRootsAndBoundedParallelism()
    {
        var definition = Mirror() with { LocalRoot = "relative", RemoteRoot = "relative", ParallelFiles = 99 };
        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(definition));
        Assert.Equal(3, exception.Issues.Count);
    }

    [Theory]
    [InlineData("/safe/../outside")]
    [InlineData("/safe/./outside")]
    [InlineData("/safe//outside")]
    [InlineData("/safe/outside/")]
    public void MirrorValidationRejectsNonCanonicalRemoteRoots(string remoteRoot)
    {
        var exception = Assert.Throws<ModelValidationException>(() =>
            PlanValidator.Validate(Mirror() with { RemoteRoot = remoteRoot }));

        Assert.Contains(exception.Issues, issue => issue.Field == "remoteRoot" && issue.Code == "ambiguous");
    }

    [Fact]
    public void MirrorValidationRejectsOverlongRemoteRoot()
    {
        var exception = Assert.Throws<ModelValidationException>(() =>
            PlanValidator.Validate(Mirror() with { RemoteRoot = "/" + new string('a', 4096) }));

        Assert.Contains(exception.Issues, issue => issue.Field == "remoteRoot");
    }

    [Theory]
    [InlineData("control\tname", "control-character")]
    public void MirrorValidationRejectsNamesThatCannotBePersistedAsJobText(string name, string expectedCode)
    {
        var exception = Assert.Throws<ModelValidationException>(() =>
            PlanValidator.Validate(Mirror() with { Name = name }));

        Assert.Contains(exception.Issues, issue => issue.Field == "name" && issue.Code == expectedCode);
    }

    [Fact]
    public void MirrorValidationRejectsNameBeyondDurableJobDisplayLimit()
    {
        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(
            Mirror() with { Name = new string('m', JobSnapshotPolicy.MaximumDisplayNameLength + 1) }));

        Assert.Contains(exception.Issues, issue => issue.Field == "name" && issue.Code == "length");
    }

    [Theory]
    [InlineData(@"C:\safe\..\outside")]
    [InlineData("C:/safe/./outside")]
    [InlineData(@"\\?\C:\safe")]
    [InlineData(@"\\.\C:\safe")]
    [InlineData(@"C:\safe\")]
    [InlineData("C:/safe/")]
    [InlineData(@"C:\\")]
    public void MirrorValidationRejectsAmbiguousOrDeviceLocalRoots(string localRoot)
    {
        var exception = Assert.Throws<ModelValidationException>(() =>
            PlanValidator.Validate(Mirror() with { LocalRoot = localRoot }));

        Assert.Contains(exception.Issues, issue => issue.Field == "localRoot" && issue.Code is "ambiguous" or "device-path");
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    public void MirrorValidationAllowsTrailingSeparatorOnlyForFileSystemRoots(string localRoot)
    {
        PlanValidator.Validate(Mirror() with { LocalRoot = localRoot });
    }

    [Fact]
    public void RemoteTransferValidationRequiresDistinctProfilesAndUnambiguousFilePaths()
    {
        var profile = Guid.NewGuid();
        var plan = new RemoteTransferPlan(
            Guid.Empty,
            profile,
            profile,
            "/folder/../secret.txt",
            "/destination/",
            (RemoteTransferMode)99);

        var exception = Assert.Throws<ModelValidationException>(() => PlanValidator.Validate(plan));
        Assert.Contains(exception.Issues, issue => issue.Field == "id" && issue.Code == "required");
        Assert.Contains(exception.Issues, issue => issue.Field == "destinationProfileId" && issue.Code == "distinct");
        Assert.Contains(exception.Issues, issue => issue.Field == "sourcePath" && issue.Code == "ambiguous");
        Assert.Contains(exception.Issues, issue => issue.Field == "destinationPath" && issue.Code == "file-path");
        Assert.Contains(exception.Issues, issue => issue.Field == "mode" && issue.Code == "unsupported");
    }

    [Theory]
    [InlineData("! calc")]
    [InlineData("cat x | sh")]
    [InlineData("cat x > local.txt")]
    [InlineData("source startup.lftp")]
    [InlineData("set sftp:connect-program ssh")]
    [InlineData("local ls")]
    [InlineData("glob shell *")]
    [InlineData("queue ! calc")]
    [InlineData("edit remote.txt")]
    [InlineData("cat $HOME")]
    [InlineData("cat \"unterminated")]
    [InlineData("wait")]
    public void SafeConsoleBlocksLocalAndIndirectExecution(string command)
    {
        var decision = SafeConsolePolicy.Evaluate(command);
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
    }

    [Fact]
    public void SafeConsoleAllowsQuotedMetacharactersAndExplicitUnsafeOptIn()
    {
        Assert.True(SafeConsolePolicy.Evaluate("cat \"name|still-remote\"").Allowed);
        Assert.True(SafeConsolePolicy.Evaluate("! calc", localShellEnabled: true).Allowed);
    }

    [Theory]
    [InlineData("mirror --delete /source /target")]
    [InlineData("mir --delete /source /target")]
    [InlineData("rm old.txt")]
    [InlineData("rmdir old")]
    [InlineData("mrm *.tmp")]
    [InlineData("mv old new")]
    [InlineData("rename old new")]
    [InlineData("put local.txt")]
    [InlineData("get remote.txt")]
    [InlineData("mkdir remote")]
    [InlineData("chmod 777 remote.txt")]
    [InlineData("pwd; mirror --delete /source /target")]
    [InlineData("pwd;rm old.txt")]
    [InlineData("alias wipe mirror --delete")]
    [InlineData("history")]
    [InlineData("history -w C:\\temp\\commands.txt")]
    [InlineData("history -r C:\\temp\\commands.txt")]
    [InlineData("history -c")]
    [InlineData("pwd -p")]
    public void SafeConsoleBlocksAllMutatingCommandsAndAbbreviations(string command)
    {
        Assert.False(SafeConsolePolicy.Evaluate(command).Allowed);
    }

    private static ConnectionProfile Profile(ConnectionProtocol protocol, AuthenticationKind authentication) => new(
        Guid.NewGuid(), "Test", protocol, "example.test", ProfileValidator.DefaultPort(protocol), "alice", authentication);

    internal static MirrorDefinition Mirror(MirrorDirection direction = MirrorDirection.Download, bool delete = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Mirror", direction, @"C:\Data", "/srv/data",
        ImmutableArray.Create("*.txt"), ImmutableArray.Create("*.tmp"), delete, 4, 3, 1024);
}
