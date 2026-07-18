using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Shell;

namespace LFTPPilot.Tests;

public sealed class ProtocolIntegrationTests
{
    private const string ConfigVariable = "LFTP_PILOT_PROTOCOL_LAB_CONFIG";
    private const string RuntimeVariable = "LFTP_PILOT_PROTOCOL_LAB_LFTP";

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task PasswordEndpointsBrowseMutateAndRoundTripUnicodeTransfers()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;

        var config = await LoadConfigAsync(configPath);
        Assert.True(Path.IsPathFullyQualified(executablePath));
        Assert.True(File.Exists(executablePath));

        var endpoints = new[]
        {
            (ConnectionProtocol.Ftp, config.Endpoints.Ftp),
            (ConnectionProtocol.FtpOpportunisticTls, config.Endpoints.FtpOpportunisticTls),
            (ConnectionProtocol.FtpsExplicit, config.Endpoints.FtpsExplicit),
            (ConnectionProtocol.FtpsImplicit, config.Endpoints.FtpsImplicit),
            (ConnectionProtocol.Sftp, config.Endpoints.Sftp),
        };
        foreach (var endpoint in endpoints)
        {
            try
            {
                await ExerciseEndpointAsync(config, executablePath, endpoint.Item1, endpoint.Item2);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"The controlled {endpoint.Item1} endpoint failed.", exception);
            }
        }
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task SftpUnencryptedAndEncryptedKeysRoundTripThroughRealLftpSessions()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);

        await ExerciseEndpointAsync(
            config, executablePath, ConnectionProtocol.Sftp, config.Endpoints.Sftp,
            AuthenticationKind.SshKey, config.SftpClientKeyPath);
        await ExerciseEndpointAsync(
            config, executablePath, ConnectionProtocol.Sftp, config.Endpoints.Sftp,
            AuthenticationKind.SshKey, config.SftpEncryptedClientKeyPath, config.KeyPassphrase);
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task SftpHostKeyEnrollmentUnchangedCheckAndRotationUseRealOpenSshProbe()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-host-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profile = new ConnectionProfile(
                Guid.NewGuid(), "Rotating loopback host", ConnectionProtocol.Sftp,
                config.Host, config.Endpoints.Sftp, config.Username, AuthenticationKind.SshKey,
                SshKeyPath: config.SftpClientKeyPath);
            var store = new MemoryHostKeyStore();
            var manager = new SftpHostKeyManager(
                store,
                new OpenSshHostKeyProbe(PackagedLftpRuntimeProvider.CreateTestOverride(executablePath), root));

            var first = await manager.InspectAsync(profile, TestContext.Current.CancellationToken);
            Assert.Equal(SftpHostKeyState.EnrollmentRequired, first.State);
            var enrollment = Assert.IsType<SftpHostKeyReview>(first.Review);
            await manager.ApproveAsync(
                profile,
                new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken),
                replacementAllowed: true,
                cancellationToken: TestContext.Current.CancellationToken);
            var unchanged = await manager.InspectAsync(profile, TestContext.Current.CancellationToken);
            Assert.Equal(SftpHostKeyState.Trusted, unchanged.State);
            Assert.Null(unchanged.Review);

            await File.WriteAllTextAsync(
                config.SftpRotateHostKeyPath, "rotate", TestContext.Current.CancellationToken);
            ProtocolLabConfig rotated = config;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (rotated.SftpHostKeyGeneration <= config.SftpHostKeyGeneration && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                rotated = await LoadConfigAsync(configPath);
            }
            Assert.True(rotated.SftpHostKeyGeneration > config.SftpHostKeyGeneration);

            var changed = await manager.InspectAsync(profile, TestContext.Current.CancellationToken);
            Assert.Equal(SftpHostKeyState.Changed, changed.State);
            var replacement = Assert.IsType<SftpHostKeyReview>(changed.Review);
            Assert.Equal(enrollment.PresentedFingerprintSha256, replacement.TrustedFingerprintSha256);
            Assert.Equal(rotated.SftpHostKeyFingerprint, replacement.PresentedFingerprintSha256);
            Assert.NotEqual(replacement.TrustedFingerprintSha256, replacement.PresentedFingerprintSha256);
            await manager.ApproveAsync(
                profile,
                new(profile.Id, replacement.ReviewId, replacement.ApprovalToken, ReplaceExisting: true),
                replacementAllowed: true,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(
                SftpHostKeyState.Trusted,
                (await manager.InspectAsync(profile, TestContext.Current.CancellationToken)).State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task SftpAndFtpRemoteTransfersUseManagedTwoProcessRelayInBothDirections()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new MemoryProfileStore();
            var secrets = new MemorySecretStore();
            var hostKeys = new MemoryHostKeyStore();
            var sftp = new ConnectionProfile(
                Guid.NewGuid(), "Relay SFTP", ConnectionProtocol.Sftp, config.Host,
                config.Endpoints.Sftp, config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            var ftp = new ConnectionProfile(
                Guid.NewGuid(), "Relay FTP", ConnectionProtocol.Ftp, config.Host,
                config.Endpoints.Ftp, config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            var sftpPeer = new ConnectionProfile(
                Guid.NewGuid(), "Relay SFTP peer", ConnectionProtocol.Sftp, config.Host,
                config.Endpoints.Sftp, config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            await hostKeys.SaveAsync(new(
                SftpHostKeyManager.CreateBinding(sftp),
                config.SftpHostKeyAlgorithm,
                config.SftpHostKeyBase64,
                config.SftpHostKeyFingerprint),
                TestContext.Current.CancellationToken);
            await hostKeys.SaveAsync(new(
                SftpHostKeyManager.CreateBinding(sftpPeer),
                config.SftpHostKeyAlgorithm,
                config.SftpHostKeyBase64,
                config.SftpHostKeyFingerprint),
                TestContext.Current.CancellationToken);
            var jobs = new JobCoordinator();
            var progressUpdates = new ConcurrentBag<JobSnapshot>();
            jobs.JobChanged += (_, job) =>
            {
                if (job.Progress is > 0 and < 1) progressUpdates.Add(job);
            };
            var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(root, "agent")) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                BrowseTimeout = TimeSpan.FromSeconds(20),
                TransferTimeout = TimeSpan.FromMinutes(2),
            };
            await using var service = new AgentWorkspaceService(
                profiles,
                secrets,
                new SftpHostKeyManager(hostKeys, new UnexpectedHostKeyProbe()),
                new TlsCaProcessHost(new LftpProcessHost(), config.TlsCaPath),
                PackagedLftpRuntimeProvider.CreateTestOverride(executablePath),
                jobs,
                new MirrorPlanner(),
                options);

            await service.SaveProfileAsync(new(sftp), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(sftp, config.Password), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(sftpPeer), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(sftpPeer, config.Password), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(ftp, config.Password), TestContext.Current.CancellationToken);
            var sftpSession = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(sftp)), TestContext.Current.CancellationToken);
            var ftpSession = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(ftp)), TestContext.Current.CancellationToken);
            var sftpPeerSession = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(sftpPeer)), TestContext.Current.CancellationToken);

            const string ftpTarget = "/relay-from-sftp-雪.txt";
            var outbound = await service.PlanRemoteTransferAsync(
                new(sftp.Id, ftp.Id, "/seed-雪.txt", ftpTarget), TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.ClientRelay, outbound.Mode);
            var outboundJob = await service.EnqueueRemoteTransferAsync(
                new(outbound), TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, outboundJob.Job.Id, ConnectionProtocol.Sftp);

            const string sftpTarget = "/relay-roundtrip-雪.txt";
            var inbound = await service.PlanRemoteTransferAsync(
                new(ftp.Id, sftp.Id, ftpTarget, sftpTarget), TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.ClientRelay, inbound.Mode);
            var inboundJob = await service.EnqueueRemoteTransferAsync(
                new(inbound), TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, inboundJob.Job.Id, ConnectionProtocol.Sftp);
            Assert.Contains(progressUpdates, job => job.Id == outboundJob.Job.Id && job.Progress == 0.65);
            Assert.Contains(progressUpdates, job => job.Id == outboundJob.Job.Id && job.Progress == 0.98);
            Assert.Contains(progressUpdates, job => job.Id == inboundJob.Job.Id && job.Progress == 0.65);
            Assert.Contains(progressUpdates, job => job.Id == inboundJob.Job.Id && job.Progress == 0.98);

            const string sftpPeerTarget = "/relay-sftp-peer-雪.txt";
            var sftpPeerPlan = await service.PlanRemoteTransferAsync(
                new(sftp.Id, sftpPeer.Id, "/seed-雪.txt", sftpPeerTarget), TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.ClientRelay, sftpPeerPlan.Mode);
            var sftpPeerJob = await service.EnqueueRemoteTransferAsync(
                new(sftpPeerPlan), TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, sftpPeerJob.Job.Id, ConnectionProtocol.Sftp);

            var verificationPath = Path.Combine(root, "relay-verification.txt");
            var verification = await service.EnqueueTransferAsync(new(
                sftpSession.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), sftp.Id, TransferDirection.Download,
                    sftpTarget, verificationPath, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, verification.Job.Id, ConnectionProtocol.Sftp);
            await using (var verificationStream = File.OpenRead(verificationPath))
            {
                Assert.Equal(
                    config.SeedSha256,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(
                        verificationStream, TestContext.Current.CancellationToken)));
            }
            var peerVerificationPath = Path.Combine(root, "relay-peer-verification.txt");
            var peerVerification = await service.EnqueueTransferAsync(new(
                sftpPeerSession.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), sftpPeer.Id, TransferDirection.Download,
                    sftpPeerTarget, peerVerificationPath, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, peerVerification.Job.Id, ConnectionProtocol.Sftp);
            await using (var verificationStream = File.OpenRead(peerVerificationPath))
            {
                Assert.Equal(
                    config.SeedSha256,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(
                        verificationStream, TestContext.Current.CancellationToken)));
            }
            Assert.False(Directory.Exists(Path.Combine(options.TemporaryRoot, "remote-relays", outbound.Id.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(options.TemporaryRoot, "remote-relays", inbound.Id.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(options.TemporaryRoot, "remote-relays", sftpPeerPlan.Id.ToString("N"))));

            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [ftpTarget], ftpSession.SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [sftpTarget], sftpSession.SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [sftpPeerTarget], sftpPeerSession.SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            await service.DisconnectAsync(new(sftpPeerSession.SessionId), TestContext.Current.CancellationToken);
            await service.DisconnectAsync(new(ftpSession.SessionId), TestContext.Current.CancellationToken);
            await service.DisconnectAsync(new(sftpSession.SessionId), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task FtpFamilyRemoteTransfersCompleteThroughFxpAndLftpClientFallback()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-fxp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new MemoryProfileStore();
            var secrets = new MemorySecretStore();
            var hostKeys = new MemoryHostKeyStore();
            ConnectionProfile Profile(string name, int port) => new(
                Guid.NewGuid(), name, ConnectionProtocol.Ftp, config.Host, port,
                config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            var fxpSource = Profile("FXP source", config.Endpoints.Ftp);
            var fxpDestination = Profile("FXP destination", config.Endpoints.FtpPeer);
            var fallbackSource = Profile("FXP-disabled source", config.Endpoints.FtpNoFxpSource);
            var fallbackDestination = Profile("FXP-disabled destination", config.Endpoints.FtpNoFxpDestination);
            var tlsSource = new ConnectionProfile(
                Guid.NewGuid(), "FTPES source", ConnectionProtocol.FtpsExplicit, config.Host,
                config.Endpoints.FtpsExplicit, config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            var tlsDestination = new ConnectionProfile(
                Guid.NewGuid(), "Implicit FTPS destination", ConnectionProtocol.FtpsImplicit, config.Host,
                config.Endpoints.FtpsImplicit, config.Username, AuthenticationKind.Password,
                InitialRemotePath: "/", InitialLocalPath: root);
            var allProfiles = new[]
            {
                fxpSource,
                fxpDestination,
                fallbackSource,
                fallbackDestination,
                tlsSource,
                tlsDestination,
            };
            var jobs = new JobCoordinator();
            var processHost = new TrackingProcessHost(
                new TlsCaProcessHost(new LftpProcessHost(), config.TlsCaPath));
            var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(root, "agent")) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                BrowseTimeout = TimeSpan.FromSeconds(20),
                TransferTimeout = TimeSpan.FromMinutes(2),
            };
            await using var service = new AgentWorkspaceService(
                profiles,
                secrets,
                new SftpHostKeyManager(hostKeys, new UnexpectedHostKeyProbe()),
                processHost,
                PackagedLftpRuntimeProvider.CreateTestOverride(executablePath),
                jobs,
                new MirrorPlanner(),
                options);

            foreach (var profile in allProfiles)
                await service.SaveProfileAsync(new(profile, config.Password), TestContext.Current.CancellationToken);
            var sessions = new Dictionary<Guid, SessionSnapshot>();
            foreach (var profile in allProfiles)
            {
                sessions[profile.Id] = await service.ConnectAsync(
                    new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
            }

            const string fxpTarget = "/fxp-direct-雪.txt";
            var fxpPlan = await service.PlanRemoteTransferAsync(
                new(fxpSource.Id, fxpDestination.Id, "/seed-雪.txt", fxpTarget),
                TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.Fxp, fxpPlan.Mode);
            var fxpJob = await service.EnqueueRemoteTransferAsync(
                new(fxpPlan), TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, fxpJob.Job.Id, ConnectionProtocol.Ftp);

            const string fallbackTarget = "/fxp-fallback-雪.txt";
            var fallbackPlan = await service.PlanRemoteTransferAsync(
                new(fallbackSource.Id, fallbackDestination.Id, "/seed-雪.txt", fallbackTarget),
                TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.Fxp, fallbackPlan.Mode);
            var fallbackJob = await service.EnqueueRemoteTransferAsync(
                new(fallbackPlan), TestContext.Current.CancellationToken);
            Assert.Contains("relay through this client", fallbackJob.RoutingNote, StringComparison.OrdinalIgnoreCase);
            await WaitForCompletedAsync(jobs, fallbackJob.Job.Id, ConnectionProtocol.Ftp);

            const string encryptedTarget = "/encrypted-ftp-family-雪.txt";
            var encryptedPlan = await service.PlanRemoteTransferAsync(
                new(tlsSource.Id, tlsDestination.Id, "/seed-雪.txt", encryptedTarget),
                TestContext.Current.CancellationToken);
            Assert.Equal(RemoteTransferMode.ClientRelay, encryptedPlan.Mode);
            var encryptedJob = await service.EnqueueRemoteTransferAsync(
                new(encryptedPlan), TestContext.Current.CancellationToken);
            Assert.Contains("does not use direct FXP", encryptedJob.RoutingNote, StringComparison.OrdinalIgnoreCase);
            await WaitForCompletedAsync(jobs, encryptedJob.Job.Id, ConnectionProtocol.FtpsExplicit);

            Assert.True(File.Exists(config.FxpRejectionPaths.Source),
                "The controlled source server did not reject either FXP active-mode disposition.");
            Assert.True(File.Exists(config.FxpRejectionPaths.Destination),
                "The controlled destination server did not reject either FXP active-mode disposition.");
            Assert.True(processHost.Commands.Count(item =>
                item.Role == "remote-transfer" &&
                item.Command.Contains("set ftp:use-fxp true", StringComparison.Ordinal)) >= 2);

            async Task VerifyAsync(ConnectionProfile profile, string remotePath, string name)
            {
                var localPath = Path.Combine(root, name);
                var transfer = await service.EnqueueTransferAsync(new(
                    sessions[profile.Id].SessionId,
                    new TransferPlan(
                        Guid.NewGuid(), profile.Id, TransferDirection.Download,
                        remotePath, localPath, TransferMode.Overwrite)),
                    TestContext.Current.CancellationToken);
                await WaitForCompletedAsync(jobs, transfer.Job.Id, ConnectionProtocol.Ftp);
                await using var stream = File.OpenRead(localPath);
                Assert.Equal(
                    config.SeedSha256,
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(
                        stream, TestContext.Current.CancellationToken)));
            }

            await VerifyAsync(fxpDestination, fxpTarget, "verified-fxp.txt");
            await VerifyAsync(fallbackDestination, fallbackTarget, "verified-fallback.txt");
            await VerifyAsync(tlsDestination, encryptedTarget, "verified-encrypted-route.txt");
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [fxpTarget], sessions[fxpDestination.Id].SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [fallbackTarget], sessions[fallbackDestination.Id].SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [encryptedTarget], sessions[tlsDestination.Id].SessionId, Confirmed: true),
                TestContext.Current.CancellationToken);
            foreach (var session in sessions.Values.Reverse())
                await service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task ManagedRemoteEditUsesReviewedCacheAndTransactionalPromotionAcrossEveryProtocol()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);

        var endpoints = new[]
        {
            (ConnectionProtocol.Ftp, config.Endpoints.Ftp),
            (ConnectionProtocol.FtpOpportunisticTls, config.Endpoints.FtpOpportunisticTls),
            (ConnectionProtocol.FtpsExplicit, config.Endpoints.FtpsExplicit),
            (ConnectionProtocol.FtpsImplicit, config.Endpoints.FtpsImplicit),
            (ConnectionProtocol.Sftp, config.Endpoints.Sftp),
        };
        foreach (var (protocol, port) in endpoints)
        {
            try
            {
                await ExerciseManagedRemoteEditAsync(config, executablePath, protocol, port);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"The controlled {protocol} managed-edit matrix failed.", exception);
            }
        }
    }

    [Fact]
    [Trait("Category", "ProtocolIntegration")]
    public async Task ManagedRemoteEditRestoresBackupAfterRealFtpAndSftpPromotionFailures()
    {
        var configPath = Environment.GetEnvironmentVariable(ConfigVariable);
        var executablePath = Environment.GetEnvironmentVariable(RuntimeVariable);
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(executablePath)) return;
        var config = await LoadConfigAsync(configPath);

        await ExerciseRemoteEditRollbackAsync(
            config, executablePath, ConnectionProtocol.Ftp, config.Endpoints.Ftp,
            config.RemoteEditPromotionFailurePaths.Ftp);
        await ExerciseRemoteEditRollbackAsync(
            config, executablePath, ConnectionProtocol.Sftp, config.Endpoints.Sftp,
            config.RemoteEditPromotionFailurePaths.Sftp);
    }

    private static async Task ExerciseManagedRemoteEditAsync(
        ProtocolLabConfig config,
        string executablePath,
        ConnectionProtocol protocol,
        int port)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-edit-{protocol}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (service, jobs, profile, session, options) = await CreateConnectedServiceAsync(
                config, executablePath, protocol, port, root);
            await using (service)
            {
                var remotePath = $"/managed-edit-{profile.Id:N}.txt";
                var original = $"original {protocol} {Guid.NewGuid():N}\n";
                await UploadTextAsync(service, jobs, session, profile, root, remotePath, original, protocol, "original");

                var edit = await service.StartRemoteEditAsync(
                    new(session.SessionId, remotePath), TestContext.Current.CancellationToken);
                var managedRoot = Path.Combine(options.CacheRoot, "remote-edits");
                Assert.Equal(original, await File.ReadAllTextAsync(edit.LocalPath, TestContext.Current.CancellationToken));
                var editor = TrustedEditorLauncher.CreateStartInfo(edit.LocalPath, managedRoot);
                Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"), editor.FileName);
                Assert.Collection(editor.ArgumentList, value => Assert.Equal(edit.LocalPath, value));

                var mine = $"my first edit {protocol} {Guid.NewGuid():N}\n";
                await File.WriteAllTextAsync(edit.LocalPath, mine, TestContext.Current.CancellationToken);
                var ready = await service.ReviewRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditReviewState.ReadyToUpload, ready.State);
                var uploaded = await service.ResolveRemoteEditAsync(
                    new(edit.EditId, ready.ReviewToken, RemoteEditResolution.Upload),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditActionOutcome.Uploaded, uploaded.Outcome);
                Assert.False(uploaded.Session.Dirty);
                Assert.Equal(mine, await DownloadTextAsync(
                    service, jobs, session, profile, root, remotePath, protocol, "uploaded"));
                await AssertNoRemoteEditArtifactsAsync(service, session);

                var mineAfterConflict = $"my conflicting edit {protocol} {Guid.NewGuid():N}\n";
                await File.WriteAllTextAsync(edit.LocalPath, mineAfterConflict, TestContext.Current.CancellationToken);
                var beforeConflict = await service.ReviewRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditReviewState.ReadyToUpload, beforeConflict.State);
                var otherWriter = $"other writer {protocol} {Guid.NewGuid():N}\n";
                await service.DeleteEntriesAsync(
                    new(PaneKind.Remote, [remotePath], session.SessionId, Confirmed: true),
                    TestContext.Current.CancellationToken);
                await UploadTextAsync(service, jobs, session, profile, root, remotePath, otherWriter, protocol, "other-writer");

                var conflict = await service.ResolveRemoteEditAsync(
                    new(edit.EditId, beforeConflict.ReviewToken, RemoteEditResolution.Upload),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditActionOutcome.ReviewRequired, conflict.Outcome);
                var conflictReview = Assert.IsType<RemoteEditReview>(conflict.Review);
                Assert.Equal(RemoteEditConflictKind.RemoteChanged, conflictReview.Conflict);
                Assert.Equal(otherWriter, await DownloadTextAsync(
                    service, jobs, session, profile, root, remotePath, protocol, "conflict-preserved"));

                var refreshed = await service.ResolveRemoteEditAsync(
                    new(edit.EditId, conflictReview.ReviewToken, RemoteEditResolution.RefreshLocal),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditActionOutcome.Refreshed, refreshed.Outcome);
                Assert.Equal(otherWriter, await File.ReadAllTextAsync(edit.LocalPath, TestContext.Current.CancellationToken));

                var afterDelete = $"explicit overwrite {protocol} {Guid.NewGuid():N}\n";
                await File.WriteAllTextAsync(edit.LocalPath, afterDelete, TestContext.Current.CancellationToken);
                await service.DeleteEntriesAsync(
                    new(PaneKind.Remote, [remotePath], session.SessionId, Confirmed: true),
                    TestContext.Current.CancellationToken);
                var missing = await service.ReviewRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditReviewState.Conflict, missing.State);
                Assert.Equal(RemoteEditConflictKind.RemoteMissingOrRenamed, missing.Conflict);
                var overwritten = await service.ResolveRemoteEditAsync(
                    new(edit.EditId, missing.ReviewToken, RemoteEditResolution.Overwrite),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditActionOutcome.Uploaded, overwritten.Outcome);
                Assert.Equal(afterDelete, await DownloadTextAsync(
                    service, jobs, session, profile, root, remotePath, protocol, "overwrite"));
                await AssertNoRemoteEditArtifactsAsync(service, session);

                var editDirectory = Path.GetDirectoryName(edit.LocalPath)!;
                Assert.True(await service.CompleteRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken));
                Assert.False(Directory.Exists(editDirectory));
                await service.DeleteEntriesAsync(
                    new(PaneKind.Remote, [remotePath], session.SessionId, Confirmed: true),
                    TestContext.Current.CancellationToken);
                await service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ExerciseRemoteEditRollbackAsync(
        ProtocolLabConfig config,
        string executablePath,
        ConnectionProtocol protocol,
        int port,
        string promotionFailurePath)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-edit-rollback-{protocol}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (service, jobs, profile, session, _) = await CreateConnectedServiceAsync(
                config, executablePath, protocol, port, root);
            await using (service)
            {
                var remotePath = $"/rollback-edit-{profile.Id:N}.txt";
                var original = $"rollback original {protocol} {Guid.NewGuid():N}\n";
                await UploadTextAsync(service, jobs, session, profile, root, remotePath, original, protocol, "rollback-original");
                var edit = await service.StartRemoteEditAsync(
                    new(session.SessionId, remotePath), TestContext.Current.CancellationToken);
                var replacement = $"rollback replacement {protocol} {Guid.NewGuid():N}\n";
                await File.WriteAllTextAsync(edit.LocalPath, replacement, TestContext.Current.CancellationToken);
                var review = await service.ReviewRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditReviewState.ReadyToUpload, review.State);

                await File.WriteAllTextAsync(promotionFailurePath, "fail once", TestContext.Current.CancellationToken);
                _ = await Assert.ThrowsAnyAsync<Exception>(() => service.ResolveRemoteEditAsync(
                    new(edit.EditId, review.ReviewToken, RemoteEditResolution.Upload),
                    TestContext.Current.CancellationToken));
                Assert.False(File.Exists(promotionFailurePath), "The controlled server did not consume the promotion fault.");
                Assert.Equal(original, await DownloadTextAsync(
                    service, jobs, session, profile, root, remotePath, protocol, "rollback-preserved"));
                await AssertNoRemoteEditArtifactsAsync(service, session);

                var retryReview = await service.ReviewRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditReviewState.ReadyToUpload, retryReview.State);
                var retry = await service.ResolveRemoteEditAsync(
                    new(edit.EditId, retryReview.ReviewToken, RemoteEditResolution.Upload),
                    TestContext.Current.CancellationToken);
                Assert.Equal(RemoteEditActionOutcome.Uploaded, retry.Outcome);
                Assert.Equal(replacement, await DownloadTextAsync(
                    service, jobs, session, profile, root, remotePath, protocol, "rollback-retry"));

                Assert.True(await service.CompleteRemoteEditAsync(
                    new(edit.EditId), TestContext.Current.CancellationToken));
                await service.DeleteEntriesAsync(
                    new(PaneKind.Remote, [remotePath], session.SessionId, Confirmed: true),
                    TestContext.Current.CancellationToken);
                await service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            if (File.Exists(promotionFailurePath)) File.Delete(promotionFailurePath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(AgentWorkspaceService Service, JobCoordinator Jobs, ConnectionProfile Profile, SessionSnapshot Session, AgentWorkspaceOptions Options)>
        CreateConnectedServiceAsync(
            ProtocolLabConfig config,
            string executablePath,
            ConnectionProtocol protocol,
            int port,
            string root)
    {
        var profiles = new MemoryProfileStore();
        var secrets = new MemorySecretStore();
        var hostKeys = new MemoryHostKeyStore();
        var profile = new ConnectionProfile(
            Guid.NewGuid(), $"Managed edit {protocol}", protocol, config.Host, port,
            config.Username, AuthenticationKind.Password,
            InitialRemotePath: "/", InitialLocalPath: root);
        if (protocol == ConnectionProtocol.Sftp)
        {
            await hostKeys.SaveAsync(new(
                SftpHostKeyManager.CreateBinding(profile),
                config.SftpHostKeyAlgorithm,
                config.SftpHostKeyBase64,
                config.SftpHostKeyFingerprint),
                TestContext.Current.CancellationToken);
        }
        var jobs = new JobCoordinator();
        var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(root, "agent")) with
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            BrowseTimeout = TimeSpan.FromSeconds(20),
            TransferTimeout = TimeSpan.FromMinutes(2),
        };
        var service = new AgentWorkspaceService(
            profiles,
            secrets,
            new SftpHostKeyManager(hostKeys, new UnexpectedHostKeyProbe()),
            new TlsCaProcessHost(new LftpProcessHost(), config.TlsCaPath),
            PackagedLftpRuntimeProvider.CreateTestOverride(executablePath),
            jobs,
            new MirrorPlanner(),
            options);
        try
        {
            if (protocol == ConnectionProtocol.Sftp)
                await service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
            await service.SaveProfileAsync(new(profile, config.Password), TestContext.Current.CancellationToken);
            var session = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
            return (service, jobs, profile, session, options);
        }
        catch
        {
            await service.DisposeAsync();
            throw;
        }
    }

    private static async Task UploadTextAsync(
        AgentWorkspaceService service,
        JobCoordinator jobs,
        SessionSnapshot session,
        ConnectionProfile profile,
        string root,
        string remotePath,
        string content,
        ConnectionProtocol protocol,
        string suffix)
    {
        var localPath = Path.Combine(root, $"{suffix}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localPath, content, TestContext.Current.CancellationToken);
        var queued = await service.EnqueueTransferAsync(new(
            session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload,
                localPath, remotePath, TransferMode.Overwrite)),
            TestContext.Current.CancellationToken);
        await WaitForCompletedAsync(jobs, queued.Job.Id, protocol);
    }

    private static async Task<string> DownloadTextAsync(
        AgentWorkspaceService service,
        JobCoordinator jobs,
        SessionSnapshot session,
        ConnectionProfile profile,
        string root,
        string remotePath,
        ConnectionProtocol protocol,
        string suffix)
    {
        var localPath = Path.Combine(root, $"{suffix}-{Guid.NewGuid():N}.txt");
        var queued = await service.EnqueueTransferAsync(new(
            session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download,
                remotePath, localPath, TransferMode.Overwrite)),
            TestContext.Current.CancellationToken);
        await WaitForCompletedAsync(jobs, queued.Job.Id, protocol);
        return await File.ReadAllTextAsync(localPath, TestContext.Current.CancellationToken);
    }

    private static async Task AssertNoRemoteEditArtifactsAsync(
        AgentWorkspaceService service,
        SessionSnapshot session)
    {
        var listing = await service.BrowseRemoteAsync(
            new(session.SessionId, "/", Fresh: true), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(listing.Entries, entry =>
            entry.Name.StartsWith(".lftp-pilot-", StringComparison.Ordinal) &&
            (entry.Name.EndsWith(".upload", StringComparison.Ordinal) ||
             entry.Name.EndsWith(".backup", StringComparison.Ordinal) ||
             entry.Name.EndsWith(".failed", StringComparison.Ordinal)));
    }

    private static async Task ExerciseEndpointAsync(
        ProtocolLabConfig config,
        string executablePath,
        ConnectionProtocol protocol,
        int port,
        AuthenticationKind authentication = AuthenticationKind.Password,
        string? sshKeyPath = null,
        string? credential = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lftp-pilot-protocol-{protocol}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new MemoryProfileStore();
            var secrets = new MemorySecretStore();
            var hostKeys = new MemoryHostKeyStore();
            var profile = new ConnectionProfile(
                Guid.NewGuid(),
                $"Loopback {protocol}",
                protocol,
                config.Host,
                port,
                config.Username,
                authentication,
                SshKeyPath: sshKeyPath,
                InitialRemotePath: "/",
                InitialLocalPath: root);
            if (protocol == ConnectionProtocol.Sftp)
            {
                var binding = SftpHostKeyManager.CreateBinding(profile);
                await hostKeys.SaveAsync(new(
                    binding,
                    config.SftpHostKeyAlgorithm,
                    config.SftpHostKeyBase64,
                    config.SftpHostKeyFingerprint),
                    TestContext.Current.CancellationToken);
            }

            var jobs = new JobCoordinator();
            var processHost = new TrackingProcessHost(
                new TlsCaProcessHost(new LftpProcessHost(), config.TlsCaPath));
            var options = AgentWorkspaceOptions.CreateDefault(Path.Combine(root, "agent")) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(20),
                BrowseTimeout = TimeSpan.FromSeconds(20),
                TransferTimeout = TimeSpan.FromMinutes(2),
                MirrorPreviewTimeout = TimeSpan.FromSeconds(30),
                ConsoleTimeout = TimeSpan.FromSeconds(30),
            };
            await using var service = new AgentWorkspaceService(
                profiles,
                secrets,
                new SftpHostKeyManager(hostKeys, new UnexpectedHostKeyProbe()),
                processHost,
                PackagedLftpRuntimeProvider.CreateTestOverride(executablePath),
                jobs,
                new MirrorPlanner(),
                options);

            var effectiveCredential = authentication == AuthenticationKind.Password
                ? config.Password
                : credential;
            if (protocol == ConnectionProtocol.Sftp)
                await service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
            if (!string.IsNullOrEmpty(effectiveCredential))
                await service.SaveProfileAsync(new(profile, effectiveCredential), TestContext.Current.CancellationToken);
            else if (protocol != ConnectionProtocol.Sftp)
                await service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
            var session = await service.ConnectAsync(
                new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
            var initial = await service.BrowseRemoteAsync(
                new(session.SessionId, "/", Fresh: true), TestContext.Current.CancellationToken);
            Assert.Contains(initial.Entries, entry => entry.Name == config.SeedName && !entry.IsDirectory);

            const string remoteDirectory = "/roundtrip-雪";
            const string uploadedPath = "/roundtrip-雪/upload-雪.bin";
            const string renamedPath = "/roundtrip-雪/renamed-雪.bin";
            await service.CreateDirectoryAsync(
                new(PaneKind.Remote, remoteDirectory, session.SessionId), TestContext.Current.CancellationToken);

            var payload = Enumerable.Range(0, 768 * 1024)
                .Select(index => (byte)((index * 31 + 17) % 251))
                .ToArray();
            var uploadSource = Path.Combine(root, "upload-雪.bin");
            await File.WriteAllBytesAsync(uploadSource, payload, TestContext.Current.CancellationToken);
            var upload = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Upload,
                    uploadSource, uploadedPath, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, upload.Job.Id, protocol);

            var remoteDirectoryListing = await service.BrowseRemoteAsync(
                new(session.SessionId, remoteDirectory, Fresh: true), TestContext.Current.CancellationToken);
            Assert.Contains(remoteDirectoryListing.Entries, entry => entry.Name == "upload-雪.bin");

            var retrySource = Path.Combine(root, "retry-after-failure.bin");
            const string retryTarget = "/roundtrip-雪/retried-雪.bin";
            var retryPlan = new TransferPlan(
                Guid.NewGuid(), profile.Id, TransferDirection.Upload,
                retrySource, retryTarget, TransferMode.Overwrite);
            var failed = await service.EnqueueTransferAsync(
                new(session.SessionId, retryPlan), TestContext.Current.CancellationToken);
            Assert.Equal(JobState.Failed, failed.Job.State);
            Assert.True(failed.Job.RetryAvailable);
            await File.WriteAllBytesAsync(retrySource, payload, TestContext.Current.CancellationToken);
            var retried = await service.RetryJobAsync(new(failed.Job.Id), TestContext.Current.CancellationToken);
            Assert.Equal(failed.Job.Id, retried.Job.Id);
            await WaitForCompletedAsync(jobs, retried.Job.Id, protocol);

            var downloadDirectory = Path.Combine(root, "download");
            Directory.CreateDirectory(downloadDirectory);
            var downloadTarget = Path.Combine(downloadDirectory, "download-雪.bin");
            await File.WriteAllBytesAsync(
                downloadTarget,
                payload.AsMemory(0, payload.Length / 4),
                TestContext.Current.CancellationToken);
            var download = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Download,
                    uploadedPath, downloadTarget, TransferMode.Resume, Segments: 3)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, download.Job.Id, protocol);
            Assert.Equal(payload, await File.ReadAllBytesAsync(downloadTarget, TestContext.Current.CancellationToken));

            var cancelledTarget = Path.Combine(downloadDirectory, "cancelled-雪.bin");
            var cancellationSourcePath = $"/cancel-source-{profile.Id:N}.bin";
            var cancellationSource = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Upload,
                    uploadSource, cancellationSourcePath, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, cancellationSource.Job.Id, protocol);
            var cancelled = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Download,
                    cancellationSourcePath, cancelledTarget, TransferMode.Resume,
                    Segments: 2,
                    RateLimitBytesPerSecond: 64 * 1024)),
                TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => processHost.Commands.Any(item =>
                    item.Role == $"transfer-policy-{cancelled.Job.Id:N}" &&
                    item.Command.Contains("pget -n 2", StringComparison.Ordinal) &&
                    item.Command.Contains(cancellationSourcePath, StringComparison.Ordinal)),
                $"{protocol} rate-limited transfer never entered its isolated LFTP process.");
            Assert.True(service.TryCancelOperation(cancelled.Job.Id, "Controlled active cancellation."));
            await WaitForStateAsync(jobs, cancelled.Job.Id, JobState.Cancelled, protocol);
            await WaitUntilAsync(
                () => processHost.DisposedRoles.Contains($"transfer-policy-{cancelled.Job.Id:N}"),
                $"{protocol} cancelled transfer process was not disposed.");
            if (File.Exists(cancelledTarget))
                Assert.True(new FileInfo(cancelledTarget).Length < payload.Length);

            var postCancellationTarget = Path.Combine(downloadDirectory, "after-cancel-雪.bin");
            var afterCancellation = await service.EnqueueTransferAsync(new(
                session.SessionId,
                new TransferPlan(
                    Guid.NewGuid(), profile.Id, TransferDirection.Download,
                    uploadedPath, postCancellationTarget, TransferMode.Overwrite)),
                TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, afterCancellation.Job.Id, protocol);
            Assert.Equal(payload, await File.ReadAllBytesAsync(postCancellationTarget, TestContext.Current.CancellationToken));

            await service.MoveEntryAsync(
                new(PaneKind.Remote, uploadedPath, renamedPath, session.SessionId), TestContext.Current.CancellationToken);
            var exportId = Guid.NewGuid();
            _ = await service.StartExplorerExportAsync(
                new(exportId, session.SessionId, [renamedPath]), TestContext.Current.CancellationToken);
            await WaitForCompletedAsync(jobs, exportId, protocol);
            var exported = service.GetExplorerExport(new(exportId));
            var exportedPath = Assert.Single(exported.LocalPaths);
            Assert.Equal(payload, await File.ReadAllBytesAsync(exportedPath, TestContext.Current.CancellationToken));
            Assert.True(service.ReleaseExplorerExport(new(exportId)));
            Assert.False(File.Exists(exportedPath));

            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [renamedPath, retryTarget], session.SessionId, Confirmed: true), TestContext.Current.CancellationToken);
            await service.DeleteEntriesAsync(
                new(PaneKind.Remote, [remoteDirectory], session.SessionId, Confirmed: true), TestContext.Current.CancellationToken);
            var final = await service.BrowseRemoteAsync(
                new(session.SessionId, "/", Fresh: true), TestContext.Current.CancellationToken);
            Assert.DoesNotContain(final.Entries, entry => entry.Name == "roundtrip-雪");
            await service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForCompletedAsync(JobCoordinator jobs, Guid jobId, ConnectionProtocol protocol)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var job = jobs.GetJobs().Single(candidate => candidate.Id == jobId);
            if (job.State == JobState.Completed) return;
            if (job.State is JobState.Failed or JobState.Cancelled or JobState.Missed)
            {
                Assert.Fail($"{protocol} job {jobId} ended as {job.State}: {job.Error?.Message ?? job.Status}");
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"{protocol} job {jobId} did not complete within two minutes.");
    }

    private static async Task WaitForStateAsync(
        JobCoordinator jobs,
        Guid jobId,
        JobState expectedState,
        ConnectionProtocol protocol)
    {
        await WaitUntilAsync(
            () => jobs.GetJobs().Single(candidate => candidate.Id == jobId).State == expectedState,
            $"{protocol} job {jobId} did not reach {expectedState}.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            if (condition()) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Fail(failureMessage);
    }

    private static async Task<ProtocolLabConfig> LoadConfigAsync(string configPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<ProtocolLabConfig>(
                    await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("The controlled protocol-lab configuration is empty.");
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), TestContext.Current.CancellationToken);
            }
        }
    }

    private sealed record ProtocolLabConfig(
        string Host,
        string Username,
        string Password,
        [property: JsonPropertyName("key_passphrase")] string KeyPassphrase,
        [property: JsonPropertyName("seed_name")] string SeedName,
        [property: JsonPropertyName("seed_content")] string SeedContent,
        [property: JsonPropertyName("seed_sha256")] string SeedSha256,
        [property: JsonPropertyName("tls_ca_path")] string TlsCaPath,
        [property: JsonPropertyName("sftp_client_key_path")] string SftpClientKeyPath,
        [property: JsonPropertyName("sftp_encrypted_client_key_path")] string SftpEncryptedClientKeyPath,
        [property: JsonPropertyName("sftp_host_key_algorithm")] string SftpHostKeyAlgorithm,
        [property: JsonPropertyName("sftp_host_key_base64")] string SftpHostKeyBase64,
        [property: JsonPropertyName("sftp_host_key_fingerprint")] string SftpHostKeyFingerprint,
        [property: JsonPropertyName("sftp_host_key_generation")] int SftpHostKeyGeneration,
        [property: JsonPropertyName("sftp_rotate_host_key_path")] string SftpRotateHostKeyPath,
        [property: JsonPropertyName("fxp_rejection_paths")] ProtocolLabFxpRejectionPaths FxpRejectionPaths,
        [property: JsonPropertyName("remote_edit_promotion_failure_paths")] ProtocolLabRemoteEditPromotionFailurePaths RemoteEditPromotionFailurePaths,
        ProtocolLabEndpoints Endpoints);

    private sealed record ProtocolLabFxpRejectionPaths(string Source, string Destination);
    private sealed record ProtocolLabRemoteEditPromotionFailurePaths(string Ftp, string Sftp);

    private sealed record ProtocolLabEndpoints(
        int Ftp,
        [property: JsonPropertyName("ftp_peer")] int FtpPeer,
        [property: JsonPropertyName("ftp_no_fxp_source")] int FtpNoFxpSource,
        [property: JsonPropertyName("ftp_no_fxp_destination")] int FtpNoFxpDestination,
        [property: JsonPropertyName("ftp_opportunistic_tls")] int FtpOpportunisticTls,
        [property: JsonPropertyName("ftps_explicit")] int FtpsExplicit,
        [property: JsonPropertyName("ftps_implicit")] int FtpsImplicit,
        int Sftp);

    private sealed class MemoryProfileStore : IProfileStore
    {
        private readonly ConcurrentDictionary<Guid, ConnectionProfile> _profiles = [];
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryRemove(profileId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<SecretBinding, string> _secrets = [];
        public Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[secret.Binding] = secret.Value;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.TryGetValue(binding, out var secret) ? secret : null);
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var binding in _secrets.Keys.Where(binding => binding.ProfileId == profileId))
                _secrets.TryRemove(binding, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryHostKeyStore : IHostKeyStore
    {
        private readonly ConcurrentDictionary<HostKeyBinding, TrustedSftpHostKey> _keys = [];
        public Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_keys.TryGetValue(binding, out var key) ? key : null);
        }
        public Task SaveAsync(TrustedSftpHostKey key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _keys[key.Binding] = key;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var binding in _keys.Keys.Where(binding => binding.ProfileId == profileId))
                _keys.TryRemove(binding, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedHostKeyProbe : ISshHostKeyProbe
    {
        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TrustedSftpHostKey>(new InvalidOperationException(
                $"The pre-trusted loopback profile {profile.Id} unexpectedly probed {hostKeyAlias}."));
    }

    private sealed class TlsCaProcessHost(ILftpProcessHost inner, string caPath) : ILftpProcessHost
    {
        private readonly string _preamble = $"set ssl:ca-file {LftpCommandBuilder.Quote(LftpCommandBuilder.ToMsysPath(caPath))}";

        public async Task<ILftpSession> StartAsync(
            LftpProcessStartOptions options,
            CancellationToken cancellationToken = default) =>
            new TlsCaSession(await inner.StartAsync(options, cancellationToken), _preamble);
    }

    private sealed class TrackingProcessHost(ILftpProcessHost inner) : ILftpProcessHost
    {
        public ConcurrentBag<string> DisposedRoles { get; } = [];
        public ConcurrentBag<(string Role, string Command)> Commands { get; } = [];

        public async Task<ILftpSession> StartAsync(
            LftpProcessStartOptions options,
            CancellationToken cancellationToken = default) =>
            new TrackingSession(
                await inner.StartAsync(options, cancellationToken),
                options.Tag,
                DisposedRoles,
                Commands);
    }

    private sealed class TrackingSession : ILftpSession
    {
        private readonly ConcurrentBag<string> _disposedRoles;
        private readonly ILftpSession _inner;
        private readonly string _role;
        private readonly ConcurrentBag<(string Role, string Command)> _commands;
        private int _disposed;

        public TrackingSession(
            ILftpSession inner,
            string role,
            ConcurrentBag<string> disposedRoles,
            ConcurrentBag<(string Role, string Command)> commands)
        {
            _inner = inner;
            _role = role;
            _disposedRoles = disposedRoles;
            _commands = commands;
            _inner.OutputReceived += ForwardOutput;
            _inner.UnsolicitedOutput += ForwardUnsolicitedOutput;
        }

        public int ProcessId => _inner.ProcessId;
        public bool IsRunning => _inner.IsRunning;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput;
        public Task<LftpCommandResult> ExecuteAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _commands.Add((_role, command));
            return _inner.ExecuteAsync(command, timeout, cancellationToken);
        }
        public Task<LftpCommandResult> ExecuteToExitAsync(
            string command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _commands.Add((_role, command));
            return _inner.ExecuteToExitAsync(command, timeout, cancellationToken);
        }
        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default) =>
            _inner.StopAsync(force, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _inner.OutputReceived -= ForwardOutput;
            _inner.UnsolicitedOutput -= ForwardUnsolicitedOutput;
            try { await _inner.DisposeAsync(); }
            finally { _disposedRoles.Add(_role); }
        }

        private void ForwardOutput(object? sender, LftpOutputLine line) => OutputReceived?.Invoke(this, line);
        private void ForwardUnsolicitedOutput(object? sender, LftpOutputLine line) => UnsolicitedOutput?.Invoke(this, line);
    }

    private sealed class TlsCaSession : ILftpSession
    {
        private readonly ILftpSession _inner;
        private readonly string _preamble;

        public TlsCaSession(ILftpSession inner, string preamble)
        {
            _inner = inner;
            _preamble = preamble;
            _inner.OutputReceived += ForwardOutput;
            _inner.UnsolicitedOutput += ForwardUnsolicitedOutput;
        }

        public int ProcessId => _inner.ProcessId;
        public bool IsRunning => _inner.IsRunning;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput;
        public async Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return await _inner.ExecuteAsync($"{_preamble}; {command}", timeout, cancellationToken);
        }
        public Task<LftpCommandResult> ExecuteToExitAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            _inner.ExecuteToExitAsync($"{_preamble}; {command}", timeout, cancellationToken);
        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default) =>
            _inner.StopAsync(force, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            _inner.OutputReceived -= ForwardOutput;
            _inner.UnsolicitedOutput -= ForwardUnsolicitedOutput;
            await _inner.DisposeAsync();
        }
        private void ForwardOutput(object? sender, LftpOutputLine line) => OutputReceived?.Invoke(this, line);
        private void ForwardUnsolicitedOutput(object? sender, LftpOutputLine line) => UnsolicitedOutput?.Invoke(this, line);
    }
}
