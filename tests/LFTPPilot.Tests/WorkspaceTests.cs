using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LFTPPilot.Agent;
using LFTPPilot.Core;
using LFTPPilot.Engine;
using LFTPPilot.Windows.Storage;

namespace LFTPPilot.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task DurableSessionTabsRestoreDisconnectedWithStableIdsOrderAndLastPaths()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var first = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var second = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var localPath = Path.Combine(fixture.Directory.Path, "remembered-local");
        Directory.CreateDirectory(localPath);

        _ = await fixture.Service.BrowseLocalAsync(
            new(first.SessionId, localPath), TestContext.Current.CancellationToken);
        _ = await fixture.Service.BrowseRemoteAsync(
            new(first.SessionId, "/remote"), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal([first.SessionId, second.SessionId], persisted.EffectiveSessionTabs.Select(static tab => tab.SessionId));
        var firstTab = persisted.EffectiveSessionTabs[0];
        Assert.Equal(Path.GetFullPath(localPath), firstTab.LocalPath);
        Assert.Equal("/remote", firstTab.RemotePath);
        Assert.True(firstTab.ReconnectRequested);
        var startsBeforeRestore = fixture.ProcessHost.Starts.Count;

        await fixture.Service.DisposeAsync();
        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);

        var bootstrap = await restored.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.Equal([first.SessionId, second.SessionId], bootstrap.Sessions.Select(static session => session.SessionId));
        Assert.All(bootstrap.Sessions, static session => Assert.False(session.IsConnected));
        Assert.Equal(Path.GetFullPath(localPath), bootstrap.Sessions[0].LocalLocation.Path);
        Assert.Equal("/remote", bootstrap.Sessions[0].RemoteLocation.Path);
        Assert.Equal("agent-restarted", bootstrap.Sessions[0].Error?.Code);
        Assert.Equal(startsBeforeRestore, fixture.ProcessHost.Starts.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.BrowseRemoteAsync(
            new(first.SessionId, "/remote"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AskOnConnectRestoresWithoutNetworkOrCredentialAndExplicitReconnectIsSameIdempotentTab()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Prompt every time", ConnectionProtocol.Ftp, "files.example", 21,
            "alice", AuthenticationKind.AskOnConnect);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile), "first-ephemeral"), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();
        var startsBeforeRestore = fixture.ProcessHost.Starts.Count;

        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);
        Assert.Equal(startsBeforeRestore, fixture.ProcessHost.Starts.Count);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.False(Assert.Single((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions).IsConnected);

        var request = new SessionConnectRequest(
            ConnectionIdentity.FromProfile(profile), "second-ephemeral", connected.SessionId);
        var reconnects = await Task.WhenAll(
            restored.ConnectAsync(request, TestContext.Current.CancellationToken),
            restored.ConnectAsync(request, TestContext.Current.CancellationToken));

        Assert.All(reconnects, snapshot => Assert.Equal(connected.SessionId, snapshot.SessionId));
        Assert.All(reconnects, static snapshot => Assert.True(snapshot.IsConnected));
        Assert.Equal(startsBeforeRestore + 1, fixture.ProcessHost.Starts.Count);
        var idempotent = await restored.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile), ExistingSessionId: connected.SessionId),
            TestContext.Current.CancellationToken);
        Assert.Equal(connected.SessionId, idempotent.SessionId);
        Assert.Equal(startsBeforeRestore + 1, fixture.ProcessHost.Starts.Count);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start =>
            start.Secrets?.Contains("first-ephemeral", StringComparer.Ordinal) == true));
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start =>
            start.Secrets?.Contains("second-ephemeral", StringComparer.Ordinal) == true));
    }

    [Fact]
    public async Task ProfileIdentityChangePrunesRestoredTabBeforePublishingReplacement()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();

        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);
        Assert.Contains((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions,
            session => session.SessionId == connected.SessionId);

        var changed = profile with { Host = "replacement.example" };
        _ = await restored.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);

        Assert.Empty((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Empty((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).EffectiveSessionTabs);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => restored.ConnectAsync(
            new(ConnectionIdentity.FromProfile(changed), ExistingSessionId: connected.SessionId),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupFailureTombstoneCannotBeResurrectedByAnotherTabSave()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var closingProfile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var otherProfile = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with { Id = Guid.NewGuid(), Name = "Other" };
        await fixture.Service.SaveProfileAsync(new(closingProfile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(otherProfile), TestContext.Current.CancellationToken);
        var closing = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(closingProfile)), TestContext.Current.CancellationToken);
        fixture.ProcessHost.FailDisposeRole = "browse";
        fixture.ProcessHost.RemainingDisposeFailures = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DisconnectAsync(
            new(closing.SessionId), TestContext.Current.CancellationToken));
        Assert.DoesNotContain((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions,
            session => session.SessionId == closing.SessionId);
        var changed = closingProfile with { Host = "blocked.example" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(changed), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(closingProfile), ExistingSessionId: closing.SessionId),
            TestContext.Current.CancellationToken));

        var other = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(otherProfile)), TestContext.Current.CancellationToken);
        var durable = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(durable.EffectiveSessionTabs, tab => tab.SessionId == closing.SessionId);
        Assert.Contains(durable.EffectiveSessionTabs, tab => tab.SessionId == other.SessionId);

        fixture.ProcessHost.FailDisposeRole = null;
        Assert.True(await fixture.Service.DisconnectAsync(
            new(closing.SessionId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectAndDisconnectPersistenceFailuresLeaveTruthfulRuntimeState()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var blockedDirectory = BlockStateWrites(fixture);

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken));
        Assert.Empty((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);

        RestoreStateWrites(blockedDirectory);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        blockedDirectory = BlockStateWrites(fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.DisconnectAsync(
            new(connected.SessionId), TestContext.Current.CancellationToken));
        Assert.Contains((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions,
            session => session.SessionId == connected.SessionId && session.IsConnected);

        RestoreStateWrites(blockedDirectory);
        Assert.True(await fixture.Service.DisconnectAsync(
            new(connected.SessionId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PanePersistenceFailureRollsBackBeforeBootstrapCanObserveLocation()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var before = Assert.Single((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        var changedLocal = Path.Combine(fixture.Directory.Path, "must-rollback");
        Directory.CreateDirectory(changedLocal);
        var blockedDirectory = BlockStateWrites(fixture);

        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.BrowseLocalAsync(
            new(connected.SessionId, changedLocal), TestContext.Current.CancellationToken));
        var after = Assert.Single((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Equal(before.LocalLocation.Path, after.LocalLocation.Path);

        RestoreStateWrites(blockedDirectory);
    }

    [Fact]
    public async Task FailedReconnectRollbackCleanupKeepsDescriptorUntilExplicitCloseCommits()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();

        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);
        var blockedDirectory = BlockStateWrites(fixture);
        fixture.ProcessHost.FailDisposeRole = "browse";
        fixture.ProcessHost.RemainingDisposeFailures = 1;

        var reconnectFailure = await Assert.ThrowsAsync<AggregateException>(() => restored.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile), ExistingSessionId: connected.SessionId),
            TestContext.Current.CancellationToken));
        Assert.Contains(reconnectFailure.InnerExceptions, static exception => exception is IOException);
        Assert.Contains(reconnectFailure.InnerExceptions, static exception =>
            exception is InvalidOperationException &&
            exception.Message.Contains("simulated disposal failure", StringComparison.OrdinalIgnoreCase));
        var disconnected = Assert.Single((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Equal(connected.SessionId, disconnected.SessionId);
        Assert.False(disconnected.IsConnected);

        RestoreStateWrites(blockedDirectory);
        fixture.ProcessHost.FailDisposeRole = null;
        Assert.True(await restored.DisconnectAsync(
            new(connected.SessionId), TestContext.Current.CancellationToken));
        Assert.Empty((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Empty((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).EffectiveSessionTabs);
    }

    [Fact]
    public async Task SftpRestorePrunesMissingProfileWithoutTrustCredentialOrNetworkAccess()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "stored-password"), TestContext.Current.CancellationToken);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();
        await fixture.Profiles.DeleteAsync(profile.Id, TestContext.Current.CancellationToken);
        var startCount = fixture.ProcessHost.Starts.Count;
        var secretReadCount = fixture.Secrets.GetCalls.Count;

        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);

        Assert.Empty((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Empty((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).EffectiveSessionTabs);
        Assert.Equal(startCount, fixture.ProcessHost.Starts.Count);
        Assert.Equal(secretReadCount, fixture.Secrets.GetCalls.Count);
    }

    [Fact]
    public async Task RestorePrunesTabWhosePersistedConnectionIdentityNoLongerMatchesProfile()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();
        await fixture.Profiles.SaveAsync(
            profile with { Host = "out-of-band-change.example" }, TestContext.Current.CancellationToken);
        var startCount = fixture.ProcessHost.Starts.Count;

        await using var restored = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            stateStore: fixture.Store);
        await restored.RestoreSessionTabsAsync(
            persisted.EffectiveSessionTabs, TestContext.Current.CancellationToken);

        Assert.Empty((await restored.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        Assert.Empty((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).EffectiveSessionTabs);
        Assert.Equal(startCount, fixture.ProcessHost.Starts.Count);
    }

    [Fact]
    public async Task ConcurrentPaneNavigationMergesBothLocationsIntoDurableTab()
    {
        await using var fixture = new WorkspaceFixture(persistSessionTabs: true);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var localPath = Path.Combine(fixture.Directory.Path, "concurrent-local");
        Directory.CreateDirectory(localPath);

        await Task.WhenAll(
            fixture.Service.BrowseLocalAsync(
                new(connected.SessionId, localPath), TestContext.Current.CancellationToken),
            fixture.Service.BrowseRemoteAsync(
                new(connected.SessionId, "/remote"), TestContext.Current.CancellationToken));

        var runtime = Assert.Single((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).Sessions);
        var durable = Assert.Single((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).EffectiveSessionTabs);
        Assert.Equal(Path.GetFullPath(localPath), runtime.LocalLocation.Path);
        Assert.Equal("/remote", runtime.RemoteLocation.Path);
        Assert.Equal(runtime.LocalLocation.Path, durable.LocalPath);
        Assert.Equal(runtime.RemoteLocation.Path, durable.RemotePath);
    }

    [Fact]
    public async Task ProfileCredentialStaysInAgentAndConnectUsesHardenedLaunch()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "hunter2"), TestContext.Current.CancellationToken);
        var snapshot = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsConnected);
        var start = Assert.Single(fixture.ProcessHost.Starts);
        Assert.Equal(["--norc"], start.Arguments);
        Assert.Equal("C.UTF-8", start.Environment!["LC_ALL"]);
        Assert.Equal("1", start.Environment["CHERE_INVOKING"]);
        Assert.Equal(@"C:\fake\bin", start.Environment["PATH"]);
        Assert.Contains("hunter2", start.Secrets!);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.Contains("set xfer:make-backup no", StringComparison.Ordinal) &&
            command.Contains("set xfer:use-temp-file yes", StringComparison.Ordinal));

        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        var serialized = JsonSerializer.Serialize(bootstrap, FramedJsonStream.SerializerOptions);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SftpConnectWithoutApprovedHostKeyStopsBeforeSecretResolutionOrProcessLaunch()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "stored-password"), TestContext.Current.CancellationToken);
        fixture.HostKeys.AutoTrust = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken));

        Assert.Contains("approved host key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task SftpPasswordCannotBePersistedBeforeItsEndpointHostKeyIsApproved()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(profile, "must-not-be-stored"), TestContext.Current.CancellationToken));
        Assert.Contains("metadata without a credential", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Secrets.Values);

        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var untrusted = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(profile, "still-must-not-be-stored"), TestContext.Current.CancellationToken));
        Assert.Contains("approved host key", untrusted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.Values);
        var review = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, review.ReviewId, review.ApprovalToken), TestContext.Current.CancellationToken);
        _ = await fixture.Service.SaveProfileAsync(
            new(profile, "stored-after-trust"), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Secrets.Values);
    }

    [Fact]
    public async Task SftpApprovalConnectsWithStrictSingleEntryKnownHostsAndDisablesAutoConfirm()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);

        var inspection = await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var review = Assert.IsType<SftpHostKeyReview>(inspection.Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, review.ReviewId, review.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var connected = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        var binding = SftpHostKeyManager.CreateBinding(profile);
        var trusted = Assert.IsType<TrustedSftpHostKey>(await fixture.HostKeys.GetAsync(
            binding, TestContext.Current.CancellationToken));
        var alias = SftpHostKeyIdentity.CreateHostKeyAlias(binding);
        var knownHostsPath = Path.Combine(
            fixture.Options.TemporaryRoot,
            "sessions",
            connected.SessionId.ToString("N"),
            "browse",
            "known_hosts");
        Assert.True(File.Exists(knownHostsPath));
        var lines = await File.ReadAllLinesAsync(knownHostsPath, TestContext.Current.CancellationToken);
        Assert.Equal([SshKnownHostsParser.Format(alias, trusted).TrimEnd('\r', '\n')], lines);

        var initialization = Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "browse" && item.Command.Contains("open --user", StringComparison.Ordinal));
        Assert.Contains("set sftp:auto-confirm false", initialization.Command, StringComparison.Ordinal);
        Assert.Contains("StrictHostKeyChecking=yes", initialization.Command, StringComparison.Ordinal);
        Assert.Contains("GlobalKnownHostsFile=none", initialization.Command, StringComparison.Ordinal);
        Assert.Contains($"HostKeyAlias={alias}", initialization.Command, StringComparison.Ordinal);
        Assert.Contains(
            LftpCommandBuilder.BuildOpen(profile, "stored-password", knownHostsPath, alias),
            initialization.Command,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedSftpHostKeyReplacementWaitsUntilTheActiveSessionDisconnects()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        Assert.Equal(SftpHostKeyState.Changed, change.State);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken));
        Assert.Contains("must not be in use", blocked.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
        var approved = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(change.PresentedFingerprintSha256, approved.FingerprintSha256);
        var trusted = Assert.IsType<TrustedSftpHostKey>(await fixture.HostKeys.GetAsync(
            SftpHostKeyManager.CreateBinding(profile), TestContext.Current.CancellationToken));
        Assert.Equal(change.PresentedFingerprintSha256, trusted.FingerprintSha256);
    }

    [Fact]
    public async Task ChangedSftpHostKeyReplacementWaitsForDisconnectedProcessCleanup()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        fixture.ProcessHost.BlockDisposeRole = "browse";

        var disconnect = fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.DisposeEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var replacement = fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
        Assert.False(replacement.IsCompleted);

        fixture.ProcessHost.ReleaseDispose.TrySetResult(true);
        Assert.True(await disconnect);
        var approved = await replacement;
        Assert.Equal(change.PresentedFingerprintSha256, approved.FingerprintSha256);
    }

    [Fact]
    public async Task FailedDisconnectCleanupStaysTombstonedAndBlocksChangedHostKeyReplacement()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        fixture.ProcessHost.FailDisposeRole = "browse";
        fixture.ProcessHost.RemainingDisposeFailures = 1;

        var cleanupFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken));
        Assert.Contains("simulated disposal failure", cleanupFailure.Message, StringComparison.OrdinalIgnoreCase);
        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(bootstrap.Sessions, candidate => candidate.SessionId == session.SessionId);
        var replacement = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken));
        Assert.Contains("must not be in use", replacement.Message, StringComparison.OrdinalIgnoreCase);

        fixture.ProcessHost.FailDisposeRole = null;
        Assert.True(await fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken));
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SftpConnectAndChangedKeyReplacementCannotCrossTheQuiescenceDecision()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "stored-password"), TestContext.Current.CancellationToken);

        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var connect = fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var replacement = fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
        Assert.False(replacement.IsCompleted);

        releaseStart.TrySetResult(true);
        var session = await connect;
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
        Assert.Contains("must not be in use", blocked.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FtpConnectAndProfileDeleteCannotCreateAnOrphanSession()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var connect = fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var delete = fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken);
        Assert.False(delete.IsCompleted);

        releaseStart.TrySetResult(true);
        _ = await connect;
        Assert.True(await delete);
        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(bootstrap.Profiles, candidate => candidate.Id == profile.Id);
        Assert.DoesNotContain(bootstrap.Sessions, candidate => candidate.ProfileId == profile.Id);
        Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
    }

    [Fact]
    public async Task SftpProfileEndpointChangeAndDeleteInvalidateStoredHostTrust()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        var originalBinding = SftpHostKeyManager.CreateBinding(profile);
        Assert.NotNull(await fixture.HostKeys.GetAsync(originalBinding, TestContext.Current.CancellationToken));

        var changed = profile with { Host = "replacement.example" };
        await fixture.Service.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);
        Assert.Null(await fixture.HostKeys.GetAsync(originalBinding, TestContext.Current.CancellationToken));
        var changedBinding = SftpHostKeyManager.CreateBinding(changed);
        Assert.Null(await fixture.HostKeys.GetAsync(changedBinding, TestContext.Current.CancellationToken));

        var replacementEnrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(changed)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(changed.Id, replacementEnrollment.ReviewId, replacementEnrollment.ApprovalToken),
            TestContext.Current.CancellationToken);
        Assert.NotNull(await fixture.HostKeys.GetAsync(changedBinding, TestContext.Current.CancellationToken));

        Assert.True(await fixture.Service.DeleteProfileAsync(
            new(changed.Id), TestContext.Current.CancellationToken));
        Assert.Null(await fixture.HostKeys.GetAsync(changedBinding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedIdentitySaveCannotResurrectOldSecretOrHostTrust()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var original = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(original), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(original)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(original.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(original, "old-identity-secret"), TestContext.Current.CancellationToken);

        fixture.Profiles.SaveFailure = new IOException("simulated metadata persistence failure");
        var changed = original with { Host = "replacement.example" };
        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Service.SaveProfileAsync(
            new(changed), TestContext.Current.CancellationToken));
        Assert.Contains("persistence failure", failure.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Profiles.SaveFailure = null;

        Assert.Empty(fixture.Secrets.Values);
        Assert.Null(await fixture.HostKeys.GetAsync(
            SftpHostKeyManager.CreateBinding(original), TestContext.Current.CancellationToken));
        var persisted = Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, persisted);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(original)), TestContext.Current.CancellationToken));
        Assert.Contains("approved host key", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task CombinedSftpEndpointAndCredentialSaveCannotBypassFreshReview()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var original = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(original), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(original)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(original.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(original, "original-secret"), TestContext.Current.CancellationToken);

        var changed = original with { Host = "replacement.example" };
        var changedBinding = SftpHostKeyManager.CreateBinding(changed);
        await fixture.HostKeys.SaveAsync(
            CreateTestHostKey(changedBinding, 0x44), TestContext.Current.CancellationToken);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(changed, "must-not-be-persisted"), TestContext.Current.CancellationToken));

        Assert.Contains("metadata without a credential", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Single(fixture.Secrets.Values);
        Assert.NotNull(await fixture.HostKeys.GetAsync(
            SftpHostKeyManager.CreateBinding(original), TestContext.Current.CancellationToken));
        Assert.NotNull(await fixture.HostKeys.GetAsync(changedBinding, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task FailedProfileMetadataDeleteStillRevokesSecretAndHostTrust()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "delete-failure-secret"), TestContext.Current.CancellationToken);

        fixture.Profiles.DeleteFailure = new IOException("simulated metadata deletion failure");
        var failure = await Assert.ThrowsAsync<IOException>(() => fixture.Service.DeleteProfileAsync(
            new(profile.Id), TestContext.Current.CancellationToken));

        Assert.Contains("deletion failure", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(profile, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(fixture.Secrets.Values);
        Assert.Null(await fixture.HostKeys.GetAsync(
            SftpHostKeyManager.CreateBinding(profile), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task StaleCapturedIdentityCannotSendCredentialAfterSameIdProtocolDowngrade()
    {
        await using var fixture = new WorkspaceFixture();
        var captured = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(captured), TestContext.Current.CancellationToken);
        var downgraded = captured with
        {
            Protocol = ConnectionProtocol.Ftp,
            Port = 21,
        };
        await fixture.Service.SaveProfileAsync(
            new(downgraded, "credential-for-current-profile"), TestContext.Current.CancellationToken);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(captured), "credential-for-captured-sftp-profile"),
            TestContext.Current.CancellationToken));

        Assert.Contains("profile changed", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task ForgedExpectedIdentityIsRejectedBeforeStoredSecretReadOrProcessLaunch()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-for-real-identity"), TestContext.Current.CancellationToken);
        var forged = ConnectionIdentity.FromProfile(profile) with { UserName = "mallory" };

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(forged), TestContext.Current.CancellationToken));

        Assert.Contains("profile changed", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task ExplicitFtpsPasswordIsInvalidatedBeforeSameIdPlainFtpDowngrade()
    {
        await using var fixture = new WorkspaceFixture();
        var explicitTls = fixture.PasswordProfile(ConnectionProtocol.FtpsExplicit);
        await fixture.Service.SaveProfileAsync(
            new(explicitTls, "credential-bound-to-explicit-tls"), TestContext.Current.CancellationToken);
        var plainFtp = explicitTls with { Protocol = ConnectionProtocol.Ftp };

        await fixture.Service.SaveProfileAsync(new(plainFtp), TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Secrets.Values);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(plainFtp)), TestContext.Current.CancellationToken));
        Assert.Contains("No password", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task StaleHostKeyInspectionIdentityStopsBeforeServerProbe()
    {
        await using var fixture = new WorkspaceFixture();
        var captured = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(captured), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(captured with { Host = "different.example" }), TestContext.Current.CancellationToken);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(captured)), TestContext.Current.CancellationToken));

        Assert.Contains("profile changed", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.HostKeyProbe.Calls);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task ActiveSftpProfileCannotDiscardTrustByChangingItsEndpointOrProtocol()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var binding = SftpHostKeyManager.CreateBinding(profile);
        var trusted = Assert.IsType<TrustedSftpHostKey>(await fixture.HostKeys.GetAsync(
            binding, TestContext.Current.CancellationToken));

        var endpointChange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveProfileAsync(
                new(profile with { Host = "replacement.example" }),
                TestContext.Current.CancellationToken));
        Assert.Contains("Disconnect every session", endpointChange.Message, StringComparison.Ordinal);
        var protocolChange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveProfileAsync(
                new(profile with { Protocol = ConnectionProtocol.Ftp, Port = 21 }),
                TestContext.Current.CancellationToken));
        Assert.Contains("changing its endpoint, protocol", protocolChange.Message, StringComparison.Ordinal);

        Assert.Equal([profile], await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(trusted, await fixture.HostKeys.GetAsync(binding, TestContext.Current.CancellationToken));

        Assert.True(await fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken));
        var changed = profile with { Host = "replacement.example" };
        await fixture.Service.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);
        Assert.Null(await fixture.HostKeys.GetAsync(binding, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionRemovalWinsAgainstSearchQueuedBehindItsStateCommit()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var gateField = typeof(AgentWorkspaceService).GetField(
            "_sessionStateGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The session-state admission gate was not found.");
        var gate = Assert.IsType<SemaphoreSlim>(gateField.GetValue(fixture.Service));
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        Task<bool>? disconnect = null;
        Task<RemoteSearchPage>? start = null;
        try
        {
            disconnect = fixture.Service.DisconnectAsync(
                new(session.SessionId), TestContext.Current.CancellationToken);
            Assert.False(disconnect.IsCompleted);
            start = fixture.Service.StartRemoteSearchAsync(
                new(new(Guid.NewGuid(), session.SessionId, "/remote", "file")),
                TestContext.Current.CancellationToken);
            Assert.False(start.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        Assert.True(await disconnect!);
        var rejected = await Assert.ThrowsAnyAsync<Exception>(() => start!);
        Assert.True(rejected is InvalidOperationException or KeyNotFoundException,
            $"Expected an authoritative closing or missing-session rejection, but received {rejected.GetType().Name}.");
        Assert.True(
            rejected.Message.Contains("closing", StringComparison.OrdinalIgnoreCase) ||
            rejected.Message.Contains("not found", StringComparison.OrdinalIgnoreCase),
            "The queued search was not rejected by the session-removal boundary.");
        Assert.DoesNotContain(fixture.ProcessHost.Starts,
            item => item.Tag.StartsWith("remote-search-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveFtpProfileRejectsEndpointOrSecurityModeChange(bool changeSecurityMode)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var changed = changeSecurityMode
            ? profile with { Protocol = ConnectionProtocol.FtpsExplicit }
            : profile with { Host = "replacement.example" };

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(changed), TestContext.Current.CancellationToken));

        Assert.Contains("changing its endpoint, protocol", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(profile, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(fixture.Secrets.Values);
        Assert.Single(fixture.ProcessHost.Starts);

        var cosmetic = profile with
        {
            Name = "Renamed while connected",
            InitialRemotePath = "/incoming",
            Bookmarks = ["/incoming", "/archive"],
        };
        Assert.Equal(cosmetic, await fixture.Service.SaveProfileAsync(
            new(cosmetic), TestContext.Current.CancellationToken));
        Assert.Equal(cosmetic, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Single(fixture.ProcessHost.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSftpKeyProfileRejectsUsernameOrKeyPathChange(bool changeKeyPath)
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Key profile", ConnectionProtocol.Sftp, "sftp.example", 22,
            "alice", AuthenticationKind.SshKey, SshKeyPath: @"C:\Keys\id_ed25519");
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var binding = SftpHostKeyManager.CreateBinding(profile);
        var trusted = Assert.IsType<TrustedSftpHostKey>(await fixture.HostKeys.GetAsync(
            binding, TestContext.Current.CancellationToken));
        var changed = changeKeyPath
            ? profile with { SshKeyPath = @"C:\Keys\replacement_ed25519" }
            : profile with { UserName = "bob" };

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(changed), TestContext.Current.CancellationToken));

        Assert.Contains("username, authentication mode, or SSH key", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(profile, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(trusted, await fixture.HostKeys.GetAsync(binding, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Secrets.Values);
        Assert.Single(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task ActiveProfileRejectsCredentialReplacementWithoutMutatingStoredSecret()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "credential-in-active-processes"), TestContext.Current.CancellationToken);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveProfileAsync(
            new(profile, "replacement-must-not-be-stored"), TestContext.Current.CancellationToken));

        Assert.Contains("before saving a credential", blocked.Message, StringComparison.Ordinal);
        Assert.Equal(profile, Assert.Single(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(["credential-in-active-processes"], fixture.Secrets.Values.Values);
        Assert.DoesNotContain("replacement-must-not-be-stored", fixture.Secrets.Values.Values);
        Assert.Single(fixture.ProcessHost.Starts);
    }

    [Fact]
    public async Task MirrorPreviewHoldsHostKeyLifecycleGateUntilItsEphemeralProcessIsDisposed()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Lifecycle preview", MirrorDirection.Download,
            fixture.Directory.Path, "/remote");
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var preview = fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, definition), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var replacement = fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
        Assert.False(replacement.IsCompleted);

        releaseStart.TrySetResult(true);
        _ = await preview;
        Assert.Contains("mirror-preview", fixture.ProcessHost.DisposedRoles);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
        Assert.Contains("must not be in use", blocked.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(await fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken));
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProfileIdentityChangeInvalidatesBoundCredential()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(profile, "hunter2"), TestContext.Current.CancellationToken);
        var changed = profile with { Host = "changed.example" };
        await fixture.Service.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Secrets.Values);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(changed)), TestContext.Current.CancellationToken));
        Assert.Contains("No password", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskOnConnectCredentialIsEphemeralAndNeverPersisted()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile() with { Authentication = AuthenticationKind.AskOnConnect };
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken));
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile), "once"), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Secrets.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SshKeyPassphraseUsesRedactedEphemeralOrPersistedCredentialChannel(bool persist)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile() with
        {
            Authentication = AuthenticationKind.SshKey,
            SshKeyPath = @"C:\Keys\encrypted_ed25519",
        };
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        if (persist)
            await fixture.Service.SaveProfileAsync(new(profile, "private-key-passphrase"), TestContext.Current.CancellationToken);

        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile), persist ? null : "private-key-passphrase"),
            TestContext.Current.CancellationToken);

        Assert.Equal(persist ? 1 : 0, fixture.Secrets.Values.Count);
        Assert.Contains("private-key-passphrase", Assert.Single(fixture.ProcessHost.Starts).Secrets!);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.Contains("open --user \"alice\" --password \"private-key-passphrase\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnencryptedSshKeyStillConnectsWithoutStoredOrEphemeralPassphrase()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile() with
        {
            Authentication = AuthenticationKind.SshKey,
            SshKeyPath = @"C:\Keys\unencrypted_ed25519",
        };
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);

        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Secrets.Values);
        Assert.Empty(Assert.Single(fixture.ProcessHost.Starts).Secrets!);
        Assert.Contains(fixture.ProcessHost.Commands, command => command.Contains("--password \"\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangingSshKeyPathInvalidatesItsBoundStoredPassphrase()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile() with
        {
            Authentication = AuthenticationKind.SshKey,
            SshKeyPath = @"C:\Keys\first_ed25519",
        };
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "first-key-passphrase"), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Secrets.Values);

        var changed = profile with { SshKeyPath = @"C:\Keys\replacement_ed25519" };
        await fixture.Service.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Secrets.Values);
        _ = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(changed)), TestContext.Current.CancellationToken);
        Assert.Empty(Assert.Single(fixture.ProcessHost.Starts).Secrets!);
    }

    [Fact]
    public async Task LocalAndRemoteBrowsingReturnTypedSortedEntries()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        var localRoot = Path.Combine(fixture.Directory.Path, "local");
        Directory.CreateDirectory(Path.Combine(localRoot, "folder"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "file.txt"), "data", TestContext.Current.CancellationToken);
        var local = await fixture.Service.BrowseLocalAsync(new(session.SessionId, localRoot), TestContext.Current.CancellationToken);
        Assert.Equal(EntryKind.Directory, local.Entries[0].Kind);
        Assert.Equal("file.txt", local.Entries[1].Name);

        var remote = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/home"), TestContext.Current.CancellationToken);
        Assert.Equal("folder", remote.Entries[0].Name);
        Assert.True(remote.Entries[0].IsDirectory);
        Assert.Equal("曲.txt", remote.Entries[1].Name);
        Assert.Equal(12, remote.Entries[1].Size);
    }

    [Fact]
    public async Task LocalFileMutationsRequireExplicitDeleteConfirmation()
    {
        await using var fixture = new WorkspaceFixture();
        var root = Path.Combine(fixture.Directory.Path, "mutations");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        var destination = Path.Combine(root, "renamed.txt");
        var createdDirectory = Path.Combine(root, "created");
        await File.WriteAllTextAsync(source, "keep until approved", TestContext.Current.CancellationToken);

        var created = await fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Local, createdDirectory), TestContext.Current.CancellationToken);
        var moved = await fixture.Service.MoveEntryAsync(
            new(PaneKind.Local, source, destination), TestContext.Current.CancellationToken);

        Assert.Equal([createdDirectory], created.AffectedPaths);
        Assert.Equal([source, destination], moved.AffectedPaths);
        Assert.True(Directory.Exists(createdDirectory));
        Assert.True(File.Exists(destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Local, [destination, createdDirectory]), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(destination));
        Assert.True(Directory.Exists(createdDirectory));

        var deleted = await fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Local, [destination, createdDirectory], Confirmed: true), TestContext.Current.CancellationToken);
        Assert.Equal(2, deleted.AffectedPaths.Length);
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(createdDirectory));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Local, "relative"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoteFileMutationsUseTypedCommandsAndRejectUnconfirmedOrAmbiguousPaths()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        await fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Remote, "/created", session.SessionId), TestContext.Current.CancellationToken);
        await fixture.Service.MoveEntryAsync(
            new(PaneKind.Remote, "/source.txt", "/renamed.txt", session.SessionId), TestContext.Current.CancellationToken);
        var beforeUnconfirmedDelete = fixture.ProcessHost.Commands.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Remote, ["/delete.txt"], session.SessionId), TestContext.Current.CancellationToken));
        Assert.Equal(beforeUnconfirmedDelete, fixture.ProcessHost.Commands.Count);

        var deleted = await fixture.Service.DeleteEntriesAsync(
            new(PaneKind.Remote, ["/delete.txt", "/empty-dir", "/tree"], session.SessionId, Recursive: true, Confirmed: true),
            TestContext.Current.CancellationToken);
        Assert.Equal(3, deleted.AffectedPaths.Length);
        Assert.Contains("mkdir -p \"/created\"", fixture.ProcessHost.Commands);
        Assert.Contains("mv \"/source.txt\" \"/renamed.txt\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm \"/delete.txt\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm -r \"/empty-dir\"", fixture.ProcessHost.Commands);
        Assert.Contains("rm -r \"/tree\"", fixture.ProcessHost.Commands);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateDirectoryAsync(
            new(PaneKind.Remote, "/safe/../escape", session.SessionId), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.MoveEntryAsync(
            new(PaneKind.Remote, "/source.txt", "/bad\nquit", session.SessionId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TransferUsesLazyPersistentTransferSessionAndCompletesJob()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "downloads", "file.bin");
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination, TransferMode.Resume, 4);
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Assert.Equal(plan.Id, queued.Job.Id);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
        Assert.Contains("set cmd:queue-parallel 2; queue; queue start", fixture.ProcessHost.Commands);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) && command.Contains("pget -n 4 -c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentExactTransferReplayConvergesOnOnePlanJobAndOneValidation()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/exact-replay.bin",
            Path.Combine(fixture.Directory.Path, "exact-replay.bin"), TransferMode.Resume, 4);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var firstTask = fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var replayTask = fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Assert.False(replayTask.IsCompleted);
        releaseStart.TrySetResult(true);

        var results = await Task.WhenAll(firstTask, replayTask);
        Assert.All(results, result => Assert.Equal(plan.Id, result.Job.Id));
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
        Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "validation");
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TransferValidationFailureConsumesExactPlanBeforeAsyncValidationCanReplay()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/missing-transfer-source",
            Path.Combine(fixture.Directory.Path, "missing-replay.bin"));

        var first = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);
        var replay = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        Assert.Equal(plan.Id, first.Job.Id);
        Assert.Equal(JobState.Failed, first.Job.State);
        Assert.Equal(first.Job, replay.Job);
        Assert.Contains("not found", first.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs());
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.Contains("missing-transfer-source", StringComparison.Ordinal));

        var submissionField = typeof(AgentWorkspaceService).GetField(
            "_transferSubmissions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The transfer submission registry was not found.");
        var submissions = Assert.IsAssignableFrom<System.Collections.IDictionary>(submissionField.GetValue(fixture.Service));
        submissions.Remove(plan.Id);
        var evictedReplay = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));
        Assert.Contains("already consumed", evictedReplay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.Contains("missing-transfer-source", StringComparison.Ordinal));

        await fixture.Service.DisposeAsync();
        await using var restarted = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            scheduler: fixture.Scheduler);
        var restartedReplay = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));
        Assert.Contains("already consumed", restartedReplay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.Contains("missing-transfer-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedOrEvictedTransferReplayCannotCreateAnotherJobForPlanId()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/replay-guard.bin",
            Path.Combine(fixture.Directory.Path, "replay-guard.bin"));
        _ = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan with { DestinationPath = Path.Combine(fixture.Directory.Path, "changed.bin") }),
            TestContext.Current.CancellationToken));
        var submissionField = typeof(AgentWorkspaceService).GetField(
            "_transferSubmissions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The transfer submission registry was not found.");
        var submissions = Assert.IsAssignableFrom<System.Collections.IDictionary>(submissionField.GetValue(fixture.Service));
        submissions.Remove(plan.Id);

        var evicted = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));
        Assert.Contains("already consumed", evicted.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
        Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
    }

    [Fact]
    public async Task TransferTrackingFailureReturnsAndReplaysOneCommittedFailedPlanJob()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/tracking-failure.bin",
            Path.Combine(fixture.Directory.Path, "tracking-failure.bin"));
        var dependencyField = typeof(AgentWorkspaceService).GetField(
            "_activeJobProfileDependencies",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The active job dependency registry was not found.");
        var dependencies = Assert.IsType<ConcurrentDictionary<Guid, ImmutableHashSet<Guid>>>(
            dependencyField.GetValue(fixture.Service));
        Assert.True(dependencies.TryAdd(plan.Id, ImmutableHashSet.Create(profile.Id)));

        try
        {
            var first = await fixture.Service.EnqueueTransferAsync(
                new(session.SessionId, plan), TestContext.Current.CancellationToken);
            var replay = await fixture.Service.EnqueueTransferAsync(
                new(session.SessionId, plan), TestContext.Current.CancellationToken);

            Assert.Equal(plan.Id, first.Job.Id);
            Assert.Equal(JobState.Failed, first.Job.State);
            Assert.Equal(first.Job, replay.Job);
            Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
            Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
        }
        finally
        {
            dependencies.TryRemove(plan.Id, out _);
        }
    }

    [Fact]
    public async Task RetryTrackingFailureReturnsTruthfulFailedResultAfterQueuedCommit()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/failing-queue.bin",
            Path.Combine(fixture.Directory.Path, "retry-tracking-failure.bin"));
        _ = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var dependencyField = typeof(AgentWorkspaceService).GetField(
            "_activeJobProfileDependencies",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The active job dependency registry was not found.");
        var dependencies = Assert.IsType<ConcurrentDictionary<Guid, ImmutableHashSet<Guid>>>(
            dependencyField.GetValue(fixture.Service));
        Assert.True(dependencies.TryAdd(plan.Id, ImmutableHashSet.Create(profile.Id)));

        try
        {
            var retried = await fixture.Service.RetryJobAsync(new(plan.Id), TestContext.Current.CancellationToken);

            Assert.Equal(plan.Id, retried.Job.Id);
            Assert.Equal(JobState.Failed, retried.Job.State);
            Assert.Contains("active profile dependencies", retried.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
            Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
                item.Role == "transfer-queue" && item.Command.Contains("failing-queue.bin", StringComparison.Ordinal));
        }
        finally
        {
            dependencies.TryRemove(plan.Id, out _);
        }
    }

    [Fact]
    public async Task ScheduledPersistenceFailureReturnsMissedPlanJobInsteadOfRejectionOrGhost()
    {
        using var blockingDirectory = new TestDirectory();
        var blockingFile = Path.Combine(blockingDirectory.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block", TestContext.Current.CancellationToken);
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(
            timeProvider: time,
            durableStorePath: Path.Combine(blockingFile, "jobs.json"));
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/schedule-persist-failure.bin",
            Path.Combine(fixture.Directory.Path, "schedule-persist-failure.bin"),
            RunAt: time.GetUtcNow().AddHours(1));

        var result = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);
        var replay = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        Assert.Equal(plan.Id, result.Job.Id);
        Assert.Equal(JobState.Missed, result.Job.State);
        Assert.Equal(result.Job, replay.Job);
        Assert.False(fixture.Scheduler.IsRegistered(plan.Id));
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
    }

    [Fact]
    public async Task TypedDirectoryTransfersRemainTransferJobsAndCompleteThroughTheGuardedForegroundSession()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var localSource = Path.Combine(fixture.Directory.Path, "upload-directory");
        Directory.CreateDirectory(localSource);
        await File.WriteAllTextAsync(Path.Combine(localSource, "nested.txt"), "data", TestContext.Current.CancellationToken);

        var download = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source",
            Path.Combine(fixture.Directory.Path, "download-directory"), TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        var upload = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Upload, localSource, "/remote/new-directory-target",
            TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Where(job => job.Id == download.Job.Id || job.Id == upload.Job.Id)
            .All(job => job.State == JobState.Completed), TestContext.Current.CancellationToken);
        Assert.Equal(JobKind.Transfer, fixture.Jobs.GetJobs().Single(job => job.Id == download.Job.Id).Kind);
        Assert.Equal(JobKind.Transfer, fixture.Jobs.GetJobs().Single(job => job.Id == upload.Job.Id).Kind);
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror --continue", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("directory-transfer-source", StringComparison.Ordinal));
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror --reverse --continue", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("upload-directory", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("mirror", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransferEnqueueRequiresExistingMatchingRegularSources()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "download.bin");

        var missingRemote = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/missing-transfer-source", destination)),
            TestContext.Current.CancellationToken);
        var wrongRemoteKind = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        var remoteLink = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/transfer-link", destination)),
            TestContext.Current.CancellationToken);
        var remoteSpecial = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/transfer-special", destination)),
            TestContext.Current.CancellationToken);

        var missingLocal = Path.Combine(fixture.Directory.Path, "missing-upload.bin");
        var missingUpload = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, missingLocal, "/remote/target.bin")),
            TestContext.Current.CancellationToken);
        var localFile = Path.Combine(fixture.Directory.Path, "upload.bin");
        await File.WriteAllTextAsync(localFile, "data", TestContext.Current.CancellationToken);
        var wrongLocalKind = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/target.bin",
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        var uploadLink = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/transfer-link")),
            TestContext.Current.CancellationToken);

        var rejected = new[] { missingRemote, wrongRemoteKind, remoteLink, remoteSpecial, missingUpload, wrongLocalKind, uploadLink };
        Assert.All(rejected, result =>
        {
            Assert.Equal(JobState.Failed, result.Job.State);
            Assert.NotNull(result.Job.Error);
        });
        Assert.Contains("not found", missingRemote.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires a directory", wrongRemoteKind.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbolic link", remoteLink.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("special entry", remoteSpecial.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must exist", missingUpload.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("declares a directory", wrongLocalKind.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("symbolic link", uploadLink.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rejected.Length, fixture.Jobs.GetJobs().Count);
    }

    [Fact]
    public void LocalTransferAttributeClassificationRejectsReparsePointsAndSpecialEntries()
    {
        Assert.Equal(TransferSourceKind.File,
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Normal));
        Assert.Equal(TransferSourceKind.Directory,
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Directory));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.ReparsePoint));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ClassifyLocalTransferAttributes(FileAttributes.Device));
        AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Directory);
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.ReparsePoint));
        Assert.Throws<NotSupportedException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Device));
        Assert.Throws<IOException>(() =>
            AgentWorkspaceService.ValidateLocalTransferAncestorAttributes(FileAttributes.Normal));
    }

    [Fact]
    public async Task TransferEnqueueRejectsExistingDestinationsOfTheWrongKind()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var localDirectory = Path.Combine(fixture.Directory.Path, "existing-directory");
        Directory.CreateDirectory(localDirectory);
        var fileOverDirectory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", localDirectory)),
            TestContext.Current.CancellationToken);

        var localFile = Path.Combine(fixture.Directory.Path, "existing-file.bin");
        await File.WriteAllTextAsync(localFile, "data", TestContext.Current.CancellationToken);
        var directoryOverFile = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source", localFile,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        var nonDirectoryAncestor = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin",
                Path.Combine(localFile, "child.bin"))), TestContext.Current.CancellationToken);
        var uploadToDirectory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localFile, "/remote/directory-destination")),
            TestContext.Current.CancellationToken);

        var uploadDirectory = Path.Combine(fixture.Directory.Path, "upload-directory");
        Directory.CreateDirectory(uploadDirectory);
        var directoryToFile = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, uploadDirectory, "/remote/existing-file.bin",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);

        var rejected = new[] { fileOverDirectory, directoryOverFile, nonDirectoryAncestor, uploadToDirectory, directoryToFile };
        Assert.All(rejected, result =>
        {
            Assert.Equal(JobState.Failed, result.Job.State);
            Assert.NotNull(result.Job.Error);
        });
        Assert.Equal(rejected.Length, fixture.Jobs.GetJobs().Count);
    }

    [Fact]
    public async Task TransferEnqueueRejectsNonCanonicalRemoteAndDeviceLocalPathsWithoutSideEffects()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        var startsBefore = fixture.ProcessHost.Starts.Count;
        var destination = Path.Combine(fixture.Directory.Path, "invalid.bin");
        var invalidRemotePaths = new[]
        {
            "/safe/../outside",
            "/safe/./outside",
            "/safe//outside",
            "/safe/outside/",
            "/" + new string('a', 4096),
            "/" + string.Join('/', Enumerable.Repeat("part", 129)),
        };

        foreach (var remotePath in invalidRemotePaths)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Download, remotePath, destination)),
                TestContext.Current.CancellationToken));
        }

        foreach (var devicePath in new[] { @"\\?\C:\outside.bin", "//?/C:/outside.bin", @"\\.\C:\outside.bin", "//./C:/outside.bin" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, devicePath, "/remote/device.bin")),
                TestContext.Current.CancellationToken));
        }
        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Equal(startsBefore, fixture.ProcessHost.Starts.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task TransferCanonicalizesLocalDotSegmentsOnceBeforeValidationAndExecution()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "canonical-source.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        var backslashPlan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload,
            Path.Combine(fixture.Directory.Path, "reviewed", "..", "canonical-source.bin"), "/remote/canonical-backslash.bin");
        var slashPlan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload,
            backslashPlan.SourcePath.Replace('\\', '/'), "/remote/canonical-slash.bin");

        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, backslashPlan), TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, slashPlan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        var submissions = fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("canonical-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.All(submissions, item => Assert.DoesNotContain("..", item.Command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuickDirectoryTransfersRejectRemoteAndLocalRootsWithoutStartingWork()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        var startsBefore = fixture.ProcessHost.Starts.Count;
        var localDirectory = Path.Combine(fixture.Directory.Path, "non-root-source");
        Directory.CreateDirectory(localDirectory);
        var driveRoot = Path.GetPathRoot(fixture.Directory.Path)!;
        var plans = new[]
        {
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/",
                Path.Combine(fixture.Directory.Path, "server-root"), TransferMode.Resume,
                SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localDirectory, "/",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, driveRoot, "/remote/drive-root",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, @"\\server\share\", "/remote/share-root",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-transfer-source", driveRoot,
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory),
        };

        foreach (var plan in plans)
        {
            var error = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(
                new(session.SessionId, plan), TestContext.Current.CancellationToken));
            Assert.Contains("reviewed Mirror", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Equal(startsBefore, fixture.ProcessHost.Starts.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task RemoteAncestorLinkDriftBlocksFileAndDirectoryExecution()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var file = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/link-ancestor/file.bin",
                Path.Combine(fixture.Directory.Path, "ancestor-file.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == file.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("ancestor/file.bin", StringComparison.Ordinal));

        var directory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/directory-link-ancestor/source",
                Path.Combine(fixture.Directory.Path, "ancestor-directory"), TransferMode.Resume,
                SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("directory-link-ancestor/source", StringComparison.Ordinal) &&
            item.Command.Contains("mirror", StringComparison.Ordinal) && !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        Assert.All(new[] { file.Job.Id, directory.Job.Id }, id =>
            Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == id).Error?.Message,
                StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(MirrorDirection.Download, false)]
    [InlineData(MirrorDirection.Download, true)]
    [InlineData(MirrorDirection.Upload, false)]
    [InlineData(MirrorDirection.Upload, true)]
    public async Task MirrorPreviewRejectsLocalRootOrAncestorJunction(MirrorDirection direction, bool ancestor)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.MirrorJunction", Guid.NewGuid().ToString("N"));
        var junction = Path.Combine(fixture.Directory.Path, "junction");
        Directory.CreateDirectory(external);
        if (ancestor) Directory.CreateDirectory(Path.Combine(external, "child"));
        CreateDirectoryJunction(junction, external);
        var localRoot = ancestor ? Path.Combine(junction, "child") : junction;
        try
        {
            var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Unsafe junction", direction, localRoot, "/remote");
            await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.PreviewMirrorAsync(
                new(session.SessionId, definition), TestContext.Current.CancellationToken));

            Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "mirror-preview");
            Assert.Empty(fixture.Jobs.GetJobs());
        }
        finally
        {
            Directory.Delete(junction, recursive: false);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task MirrorFinalCheckRejectsLocalAncestorChangedToJunctionForBothDirections()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var blockerDefinition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Gate blocker", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var blockerPreview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, blockerDefinition), TestContext.Current.CancellationToken);
        var blocker = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, blockerDefinition, blockerPreview),
            TestContext.Current.CancellationToken);
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var parent = Path.Combine(fixture.Directory.Path, "reviewed-parent");
        var localRoot = Path.Combine(parent, "child");
        Directory.CreateDirectory(localRoot);
        var approved = new List<MirrorApproveResult>();
        foreach (var direction in new[] { MirrorDirection.Download, MirrorDirection.Upload })
        {
            var suffix = direction.ToString().ToLowerInvariant();
            var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, $"Junction drift {suffix}", direction,
                localRoot, $"/local-root-drift-{suffix}");
            var preview = await fixture.Service.PreviewMirrorAsync(
                new(session.SessionId, definition), TestContext.Current.CancellationToken);
            Assert.False(preview.ContainsDeletions);
            approved.Add(await fixture.Service.ApproveMirrorAsync(
                MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken));
        }

        Directory.Delete(parent, recursive: true);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.MirrorDrift", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(external, "child"));
        CreateDirectoryJunction(parent, external);
        try
        {
            fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == blocker.Job.Id).State == JobState.Completed,
                TestContext.Current.CancellationToken);
            foreach (var result in approved)
            {
                await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == result.Job.Id).State == JobState.Failed,
                    TestContext.Current.CancellationToken);
            }

            Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
                item.Command.Contains("/local-root-drift-", StringComparison.Ordinal) &&
                item.Command.Contains("mirror", StringComparison.Ordinal) &&
                !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(parent, recursive: false);
            Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryDownloadRejectsAnExistingLocalFileSystemRootBeforeRemoteStat()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var destinationRoot = Path.GetPathRoot(fixture.Directory.Path)!;
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/directory-transfer-source",
            destinationRoot,
            TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory);

        var commandsBefore = fixture.ProcessHost.TaggedCommands.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken));

        Assert.Equal(commandsBefore, fixture.ProcessHost.TaggedCommands.Count);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task FreshRemoteStatRejectsAListingForAPathOtherThanTheRequest()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        var failed = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/mismatched-stat",
                Path.Combine(fixture.Directory.Path, "mismatched-stat.bin"))), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Failed, failed.Job.State);
        Assert.Contains("different path", failed.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs());
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("mismatched-stat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshRemoteStatTreatsCleanEmptyUploadDestinationAsAbsent()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "empty-stat-upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
                "/remote/zero-line-missing-target")), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.All(fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Role == "validation" && item.Command.Contains("zero-line-missing-target", StringComparison.Ordinal)),
            item => Assert.StartsWith("recls -ldB ", item.Command, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkspaceFreshStatAcceptsPathlessOrCorrectlyBoundMissingDiagnosticsAndRejectsWrongPath()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "missing-diagnostic-upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);

        foreach (var target in new[] { "/remote/pathless-missing-target", "/remote/bound-missing-target" })
        {
            var transfer = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
                new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, target)),
                TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).State == JobState.Completed,
                TestContext.Current.CancellationToken);
        }

        var wrongPath = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/remote/wrong-bound-missing-target")),
            TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Failed, wrongPath.Job.State);
        Assert.Contains("ambiguous output", wrongPath.Job.Error?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutionFreshStatRejectsDirectoryChangedToLinkWithoutQueueSubmission()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download,
            "/remote/stat-drift-directory", Path.Combine(fixture.Directory.Path, "stat-drift-directory"),
            TransferMode.Resume, SourceKind: TransferSourceKind.Directory);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("symbolic link", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        var stats = fixture.ProcessHost.TaggedCommands.Where(item =>
            item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("stat-drift-directory", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, stats.Length);
        Assert.All(stats, item => Assert.StartsWith("recls -ldB ", item.Command, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            (item.Role == "transfer-queue" || item.Role == "transfer") &&
            item.Command.Contains("stat-drift-directory", StringComparison.Ordinal) &&
            !item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/remote/dir-over-file-directory")]
    [InlineData("/remote/unrecognized-destructive-directory")]
    public async Task AutomaticDirectoryDryRunBlocksDestructiveOrUnrecognizedOutput(string remoteSource)
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, remoteSource,
            Path.Combine(fixture.Directory.Path, "dry-run-destination"), TransferMode.Resume,
            SourceKind: TransferSourceKind.Directory);

        var failed = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Failed, failed.Job.State);
        Assert.Contains("reviewed Mirror workflow", failed.Job.Error?.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Jobs.GetJobs());
        var preview = Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "directory-transfer-preview" && item.Command.Contains(remoteSource, StringComparison.Ordinal));
        Assert.StartsWith("mirror --verbose=1 --dry-run", preview.Command, StringComparison.Ordinal);
        Assert.Contains("--no-symlinks --overwrite", preview.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("--delete", preview.Command, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
    }

    [Fact]
    public async Task FailedTransferRetryIsSingleShotRevalidatedAndCompletes()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/retry-once.bin",
            Path.Combine(fixture.Directory.Path, "retry-once.bin"),
            TransferMode.Resume,
            4);

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.True(failed.CanRetry);
        Assert.NotNull(failed.Error);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try
            {
                return await fixture.Service.RetryJobAsync(new(enqueued.Job.Id), TestContext.Current.CancellationToken);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }));
        var accepted = Assert.Single(attempts, static result => result is not null)!.Job;
        Assert.Equal(enqueued.Job.Id, accepted.Id);
        Assert.Null(accepted.Error);
        Assert.Null(accepted.Progress);
        Assert.Null(accepted.RunAt);

        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        var submissions = fixture.ProcessHost.TaggedCommands
            .Where(item => item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
                item.Command.Contains("retry-once.bin", StringComparison.Ordinal))
            .Select(static item => item.Command)
            .ToArray();
        Assert.Equal(2, submissions.Length);
        Assert.Equal(2, submissions.Select(static command => command.Split(' ', 2)[1]).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task RetryPreflightFailureLeavesOriginalFailureUntouched()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        await File.WriteAllTextAsync(source, "upload", TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/retry-target.bin", TransferMode.Resume);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        File.Delete(source);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
    }

    [Fact]
    public async Task DirectoryRetryRevalidatesTheDeclaredSourceKindBeforeResettingFailure()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/retry-directory-target", TransferMode.Resume, SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Directory.Delete(source);
        await File.WriteAllTextAsync(source, "replacement file", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));
        Assert.Contains("declares a directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryRetryDryRunFailurePreservesTheOriginalFailedAttempt()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "retry-once.bin");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/retry-preview-target", TransferMode.Resume, SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var original = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));

        Assert.Contains("reviewed Mirror workflow", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
        Assert.Equal(3, fixture.ProcessHost.TaggedCommands.Count(item =>
            (item.Role == "directory-transfer-preview" || item.Role == "transfer") &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("retry-preview-target", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RetryRequiresTheExactOriginatingSession()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var originalSession = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/retry-once.bin",
            Path.Combine(fixture.Directory.Path, "exact-session.bin"), TransferMode.Resume);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(originalSession.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var originalFailure = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);

        Assert.True(await fixture.Service.DisconnectAsync(new(originalSession.SessionId), TestContext.Current.CancellationToken));
        var replacement = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        Assert.NotEqual(originalSession.SessionId, replacement.SessionId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.RetryJobAsync(
            new(enqueued.Job.Id), TestContext.Current.CancellationToken));

        Assert.Equal(originalFailure, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id));
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("retry-once.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryRejectsNonTransferJobsWithoutReusingMirrorApproval()
    {
        await using var fixture = new WorkspaceFixture();
        var now = DateTimeOffset.UtcNow;
        var mirror = new JobSnapshot(Guid.NewGuid(), JobKind.Mirror, Guid.NewGuid(), "Reviewed mirror", JobState.Failed,
            now, now, Error: new("mirror-failed", "Changed after review"));
        fixture.Jobs.Restore([mirror]);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.RetryJobAsync(
            new(mirror.Id), TestContext.Current.CancellationToken));
        Assert.Contains("fresh preview", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobState.Failed, fixture.Jobs.GetJobs().Single().State);
    }

    [Fact]
    public async Task RunOnceTransferPersistsMetadataWaitsForSelectedTimeAndThenRuns()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.PasswordProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile, "scheduled-secret"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/scheduled.bin",
            Path.Combine(fixture.Directory.Path, "scheduled.bin"),
            RunAt: time.GetUtcNow().AddHours(1));

        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Assert.Equal(plan.Id, enqueued.Job.Id);
        Assert.Equal(JobState.Scheduled, enqueued.Job.State);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer-queue");
        var durable = Assert.Single((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Jobs);
        Assert.Equal(JobState.Scheduled, durable.State);
        Assert.Equal(plan.RunAt, durable.RunAt);
        Assert.DoesNotContain("scheduled-secret",
            await File.ReadAllTextAsync(fixture.StatePath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);

        time.Advance(TimeSpan.FromHours(1));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) && command.Contains("scheduled.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledDirectoryTransferRevalidatesTheDeclaredSourceKindAtRunTime()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "scheduled-directory-source");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/scheduled-directory-target", TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(15),
            SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        Directory.Delete(source);
        await File.WriteAllTextAsync(source, "replacement file", TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State != JobState.Scheduled,
            TestContext.Current.CancellationToken);
        _ = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Contains("declares a directory", failed.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-directory-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledDirectoryDryRunBlocksNewTypeCollisionWithoutQueueSubmission()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var source = Path.Combine(fixture.Directory.Path, "scheduled-preview-source");
        Directory.CreateDirectory(source);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source,
            "/remote/scheduled-preview-target", TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(15),
            SourceKind: TransferSourceKind.Directory);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State != JobState.Scheduled,
            TestContext.Current.CancellationToken);
        _ = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("reviewed Mirror workflow", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            (item.Role == "directory-transfer-preview" || item.Role == "transfer") &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-preview-target", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("scheduled-preview-source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingRunOnceTransferPreventsExecutionAfterItsSelectedTime()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/never-run.bin",
            Path.Combine(fixture.Directory.Path, "never-run.bin"),
            RunAt: time.GetUtcNow().AddMinutes(30));
        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancelled before start"));
        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Yield();

        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("never-run.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingDueTimeValidationPreservesBrowseAndRecreatesOnlyValidationRole()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var scheduled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/scheduled-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "scheduled-validation-cancel.bin"),
                RunAt: time.GetUtcNow().AddMinutes(10))), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await fixture.ProcessHost.ScheduledValidationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.True(fixture.Service.TryCancelOperation(scheduled.Job.Id, "Cancel due-time validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);

        var browse = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/remote"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(browse.Entries);
        var after = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-scheduled-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "after-scheduled-validation-cancel.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == after.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "browse"));
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("\"/remote/scheduled-validation-cancel.bin\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledSkipIsDecidedInsideReservedSlotWithoutSubmittingWhenDestinationAppears()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "scheduled-skip.bin");
        var scheduled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/scheduled-skip.bin", destination,
                TransferMode.Skip, RunAt: time.GetUtcNow().AddMinutes(10))), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(destination, "appeared", TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Contains("Skipped", fixture.Jobs.GetJobs().Single(job => job.Id == scheduled.Job.Id).Status,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role.StartsWith("transfer-policy-", StringComparison.Ordinal) &&
            item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-skip.bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledFailureCleanupCompletesBeforeRetryAndCancellationTargetsTheRetry()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download,
            "/remote/scheduled-retry-cancel.bin", Path.Combine(fixture.Directory.Path, "scheduled-retry-cancel.bin"),
            TransferMode.Resume, RunAt: time.GetUtcNow().AddMinutes(10));
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var retried = await fixture.Service.RetryJobAsync(new(enqueued.Job.Id), TestContext.Current.CancellationToken);
        Assert.Null(retried.Job.RunAt);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Running,
            TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancel retried schedule"));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.ProcessHost.StoppedRoles.Contains("transfer-queue"),
            TestContext.Current.CancellationToken);
        Assert.Equal("Cancel retried schedule", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Status);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("scheduled-retry-cancel.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScheduledTransferPreventsSessionOrProfileDisposalUntilCancelled()
    {
        var time = new ManualTimeProvider(new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new WorkspaceFixture(timeProvider: time);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/future.bin",
            Path.Combine(fixture.Directory.Path, "future.bin"),
            RunAt: time.GetUtcNow().AddHours(1));
        var enqueued = await fixture.Service.EnqueueTransferAsync(
            new(session.SessionId, plan), TestContext.Current.CancellationToken);

        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
        Assert.Contains("Cancel scheduled or active jobs", disconnect.Message, StringComparison.Ordinal);
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Contains("Cancel scheduled or active jobs", delete.Message, StringComparison.Ordinal);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Cancel before disconnect"));
        await WaitUntilAsync(
            () => !fixture.Scheduler.IsRegistered(enqueued.Job.Id),
            TestContext.Current.CancellationToken);
        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActiveRemoteEditSurvivesBootstrapAndBlocksSessionAndProfileRemoval()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var edit = await fixture.Service.StartRemoteEditAsync(
            new(session.SessionId, "/active.txt"), TestContext.Current.CancellationToken);

        var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        var restored = Assert.Single(bootstrap.RemoteEdits);
        Assert.Equal(edit.EditId, restored.EditId);
        Assert.False(restored.Dirty);
        Assert.False(restored.WatcherFailed);
        Assert.Null(restored.LastLocalChangeAt);

        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
        Assert.Contains("active remote edit", disconnect.Message, StringComparison.OrdinalIgnoreCase);
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Contains("active remote edit", delete.Message, StringComparison.OrdinalIgnoreCase);

        var afterFailures = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
        Assert.Contains(afterFailures.Profiles, candidate => candidate.Id == profile.Id);
        Assert.Contains(afterFailures.Sessions, candidate => candidate.SessionId == session.SessionId);
        Assert.Equal(edit.EditId, Assert.Single(afterFailures.RemoteEdits).EditId);

        Assert.True(await fixture.Service.CompleteRemoteEditAsync(
            new(edit.EditId), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DeleteProfileAsync(new(profile.Id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoteEditStartupBlocksDisconnectAndHostKeyReplacementUntilRegistration()
    {
        await using var fixture = new WorkspaceFixture();
        fixture.HostKeys.AutoTrust = false;
        var profile = fixture.PasswordProfile();
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var enrollment = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, enrollment.ReviewId, enrollment.ApprovalToken), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile, "stored-password"), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        fixture.HostKeyProbe.KeyMarker = 0x43;
        var change = Assert.IsType<SftpHostKeyReview>((await fixture.Service.InspectSftpHostKeyAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken)).Review);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var start = fixture.Service.StartRemoteEditAsync(
            new(session.SessionId, "/active.txt"), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var disconnect = fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken);
        var replacement = fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
        Assert.False(disconnect.IsCompleted);
        Assert.False(replacement.IsCompleted);

        releaseStart.TrySetResult(true);
        var edit = await start;
        var disconnectBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => disconnect);
        var replacementBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
        Assert.Contains("active remote edit", disconnectBlocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not be in use", replacementBlocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("remote-edit-download", fixture.ProcessHost.DisposedRoles);

        Assert.True(await fixture.Service.CompleteRemoteEditAsync(
            new(edit.EditId), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken));
        _ = await fixture.Service.ApproveSftpHostKeyAsync(
            new(profile.Id, change.ReviewId, change.ApprovalToken, ReplaceExisting: true),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RateLimitedTransfersUseParallelismOneInAnIsolatedQueueProcess()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/limited.bin",
            Path.Combine(fixture.Directory.Path, "limited.bin"),
            RateLimitBytesPerSecond: 4096);
        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        var isolatedRole = Assert.Single(fixture.ProcessHost.Starts,
            start => start.Tag.StartsWith("transfer-policy-", StringComparison.Ordinal)).Tag;
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == isolatedRole && item.Command == "set cmd:queue-parallel 1; queue; queue start");
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == isolatedRole && item.Command.Contains("set net:limit-rate 4096:4096", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("net:limit-rate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonfatalProcessLaunchFailureTransitionsTrackedTransferToFailed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        fixture.ProcessHost.FailRolePrefix = "transfer-queue";
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/process-failure.bin",
            Path.Combine(fixture.Directory.Path, "process-failure.bin"));

        var enqueued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Equal("lftp-job-failed", failed.Error?.Code);
        Assert.Contains("simulated process launch failure", failed.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkipModePreflightsDestinationAndDoesNotOverwriteExistingDownload()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var destination = Path.Combine(fixture.Directory.Path, "existing.bin");
        await File.WriteAllTextAsync(destination, "keep", TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file.bin", destination, TransferMode.Skip);

        var skipped = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan),
            TestContext.Current.CancellationToken);

        Assert.Equal(plan.Id, skipped.Job.Id);
        Assert.Equal(JobState.Completed, skipped.Job.State);
        Assert.Contains("Skipped", skipped.Job.Status, StringComparison.Ordinal);
        Assert.Contains(" -> ", skipped.Job.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "transfer");
        Assert.Equal("keep", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SkipDownloadUsesIsolatedNoClobberQueueAndSkipUploadFailsClosed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        var newDestination = Path.Combine(fixture.Directory.Path, "new.bin");
        var download = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/new.bin", newDestination, TransferMode.Skip)),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == download.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role.StartsWith("transfer-policy-", StringComparison.Ordinal) &&
            item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("set xfer:use-temp-file no", StringComparison.Ordinal) &&
            item.Command.Contains("set xfer:clobber no && get", StringComparison.Ordinal) &&
            item.Command.Split("set xfer:clobber yes", StringSplitOptions.None).Length == 3 &&
            item.Command.Split("set xfer:use-temp-file yes", StringSplitOptions.None).Length == 3);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.Contains("xfer:clobber", StringComparison.Ordinal));

        var source = Path.Combine(fixture.Directory.Path, "upload.bin");
        await File.WriteAllTextAsync(source, "data", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, source, "/existing.bin", TransferMode.Skip)),
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.StartsWith("cls -1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            command.Contains("put ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningTransferCancellationStopsTheOwnedOperationAndJob()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin",
            Path.Combine(fixture.Directory.Path, "blocking.bin"));
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId, plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Running, TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(queued.Job.Id, "User cancelled"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Cancelled, TestContext.Current.CancellationToken);
        Assert.Equal("User cancelled", fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).Status);
    }

    [Fact]
    public async Task NativeQueueRunsNeighborInParallelAndRecreatesSessionAfterCancellation()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin", Path.Combine(fixture.Directory.Path, "blocking.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Running, TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/next.bin", Path.Combine(fixture.Directory.Path, "next.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Next"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.StoppedRoles.Contains("transfer-queue"), TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State);
        var third = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-cancel.bin", Path.Combine(fixture.Directory.Path, "after-cancel.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == third.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task NativeQueueRevalidatesOnlyAfterReservingItsBoundedSlotAndKeepsQueueAfterValidationFailure()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/slot-block-one.bin",
                Path.Combine(fixture.Directory.Path, "slot-block-one.bin"))), TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/slot-block-two.bin",
                Path.Combine(fixture.Directory.Path, "slot-block-two.bin"))), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.QueueSlotsFilled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var drift = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/file-slot-drift.bin",
                Path.Combine(fixture.Directory.Path, "file-slot-drift.bin"))), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.StartsWith("recls -ldB ", StringComparison.Ordinal) &&
            item.Command.Contains("\"/remote/file-slot-drift.bin\"", StringComparison.Ordinal));

        fixture.ProcessHost.ReleaseReservedQueueSlots.TrySetResult(true);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == drift.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("symbolic link", fixture.Jobs.GetJobs().Single(job => job.Id == drift.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("file-slot-drift.bin", StringComparison.Ordinal));

        var afterFailure = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-validation-failure.bin",
                Path.Combine(fixture.Directory.Path, "after-validation-failure.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == afterFailure.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task CancellingFinalValidationRecreatesValidationSessionWithoutBreakingBrowseOrQueue()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var cancelled = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/cancel-validation.bin",
                Path.Combine(fixture.Directory.Path, "cancel-validation.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.TaggedCommands.Any(item =>
            item.Role == "validation" && item.Command.Contains("cancel-validation.bin", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(cancelled.Job.Id, "Cancel validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == cancelled.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("cancel-validation.bin", StringComparison.Ordinal));

        var after = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/after-validation-cancel.bin",
                Path.Combine(fixture.Directory.Path, "after-validation-cancel.bin"))), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == after.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        var browse = await fixture.Service.BrowseRemoteAsync(new(session.SessionId, "/remote"), TestContext.Current.CancellationToken);
        Assert.NotEmpty(browse.Entries);
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "browse"));
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "transfer-queue"));
    }

    [Fact]
    public async Task SharedAndIsolatedQueuesSerializeTheReusableValidationSessionAcrossCancellation()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/validation-gate-one.bin",
                Path.Combine(fixture.Directory.Path, "validation-gate-one.bin"),
                RateLimitBytesPerSecond: 1024)), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.ValidationGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var secondTask = fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/validation-gate-two.bin",
                Path.Combine(fixture.Directory.Path, "validation-gate-two.bin"),
                RateLimitBytesPerSecond: 2048)), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "validation" && item.Command.Contains("validation-gate-two.bin", StringComparison.Ordinal));

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Cancel serialized validation"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        var second = await secondTask;
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.ProcessHost.Starts.Count(start => start.Tag == "validation"));
    }

    [Fact]
    public async Task GuardedDirectoryTransferRepeatsDryRunAfterWaitingForTransferGate()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var blockerDefinition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Gate blocker", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var blockerPreview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, blockerDefinition), TestContext.Current.CancellationToken);
        var blocker = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, blockerDefinition, blockerPreview),
            TestContext.Current.CancellationToken);
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var localSource = Path.Combine(fixture.Directory.Path, "guarded-drift-source");
        Directory.CreateDirectory(localSource);
        var directory = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Upload, localSource, "/remote/guarded-drift-target",
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "directory-transfer-preview" && item.Command.Contains("guarded-drift-target", StringComparison.Ordinal));

        fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == blocker.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("reviewed Mirror workflow", fixture.Jobs.GetJobs().Single(job => job.Id == directory.Job.Id).Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("guarded-drift-target", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" && item.Command.Contains("guarded-drift-target", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardedDirectoryTransferRevalidatesRemotePrefixesAfterFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var transfer = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download,
                "/remote/post-directory-link-ancestor/source",
                Path.Combine(fixture.Directory.Path, "post-directory-destination"),
                TransferMode.Resume, SourceKind: TransferSourceKind.Directory)), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == transfer.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("post-directory-link-ancestor/source", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            item.Command.Contains("post-directory-link-ancestor/source", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InterleavedBackgroundErrorDoesNotPoisonExactQueueSubmission()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var queued = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/interleaved.bin",
                Path.Combine(fixture.Directory.Path, "interleaved.bin"))), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Completed, fixture.Jobs.GetJobs().Single(job => job.Id == queued.Job.Id).State);
    }

    [Fact]
    public async Task CancellingOneActiveNativeQueueItemFailsOtherUncorrelatedItemsClosed()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var first = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/blocking.bin", Path.Combine(fixture.Directory.Path, "blocking.bin"))),
            TestContext.Current.CancellationToken);
        var second = await fixture.Service.EnqueueTransferAsync(new(session.SessionId,
            new(Guid.NewGuid(), profile.Id, TransferDirection.Download, "/remote/also-blocking.bin", Path.Combine(fixture.Directory.Path, "also-blocking.bin"))),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Role == "transfer-queue" && item.Command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal) &&
            item.Command.Contains("blocking.bin", StringComparison.Ordinal)) == 2, TestContext.Current.CancellationToken);

        Assert.True(fixture.Service.TryCancelOperation(first.Job.Id, "Cancel one"));
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == first.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        var failedNeighbor = fixture.Jobs.GetJobs().Single(job => job.Id == second.Job.Id);
        Assert.Contains("shared per-profile LFTP queue was retired", failedNeighbor.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteMirrorRequiresAgentHeldFreshPreviewAndNeverExecutesDryRunText()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Clean mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/remote", DeleteExtraneous: true);
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.True(preview.ContainsDeletions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview, deletionsApproved: true, approvalToken: "tampered"),
            TestContext.Current.CancellationToken));
        var tamperedActions = preview with
        {
            Actions = preview.Actions.Add(new(MirrorActionKind.Download, "not-in-the-reviewed-dry-run.txt")),
        };
        var tamperedReview = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(
                session.SessionId,
                definition,
                preview,
                deletionsApproved: true,
                reviewedPreview: tamperedActions),
            TestContext.Current.CancellationToken));
        Assert.Contains("actions", tamperedReview.Message, StringComparison.OrdinalIgnoreCase);

        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview, deletionsApproved: true), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Completed, TestContext.Current.CancellationToken);
        Assert.Contains(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("--delete", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command => command.Contains("Removing old file", StringComparison.Ordinal));
        Assert.Equal(1, fixture.ProcessHost.DisposedRoles.Count(role => role == "mirror-preview"));
    }

    [Fact]
    public async Task MirrorPreviewRejectsControlDelimiterCollisionWithoutStartingProcess()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)),
            TestContext.Current.CancellationToken);
        var reviewed = new MirrorDefinition(
            Guid.NewGuid(),
            profile.Id,
            "Delimiter-safe mirror",
            MirrorDirection.Download,
            fixture.Directory.Path,
            "/remote",
            Includes: ["a\u001eb"],
            Excludes: ["same"],
            DeleteExtraneous: true);
        var startsBeforePreview = fixture.ProcessHost.Starts.Count;

        var exception = await Assert.ThrowsAsync<ModelValidationException>(() =>
            fixture.Service.PreviewMirrorAsync(
                new(session.SessionId, reviewed),
                TestContext.Current.CancellationToken));

        Assert.Contains("control character", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(startsBeforePreview, fixture.ProcessHost.Starts.Count);
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task ConcurrentExactMirrorApprovalReplaysOneJobAndExecutesOnce()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Concurrent approval", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var preview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var request = MirrorApproval(session.SessionId, definition, preview);

        var approvals = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            fixture.Service.ApproveMirrorAsync(request, TestContext.Current.CancellationToken)));
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        Assert.All(approvals, approval => Assert.Equal(preview.Id, approval.Job.Id));
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == preview.Id);
        var mismatchedReplay = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.ApproveMirrorAsync(
                request with { DeletionsApproved = true }, TestContext.Current.CancellationToken));
        Assert.Contains("differs", mismatchedReplay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.Contains("/gate-blocker", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));

        fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == preview.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestartedAgentRejectsMirrorApprovalReplayWhenDurableJobIdExists()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Restart replay", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        var preview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var request = MirrorApproval(session.SessionId, definition, preview);
        var approved = await fixture.Service.ApproveMirrorAsync(
            request, TestContext.Current.CancellationToken);
        Assert.Equal(preview.Id, approved.Job.Id);

        await using var restarted = new AgentWorkspaceService(
            fixture.Profiles,
            fixture.Secrets,
            fixture.HostKeyManager,
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.Jobs,
            new MirrorPlanner(),
            fixture.Options,
            scheduler: fixture.Scheduler);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.ApproveMirrorAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("already consumed", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == preview.Id);
        fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == preview.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.Contains("/gate-blocker", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypeCollisionDeletionInNonDeletingMirrorRequiresApprovalAndSecondPreview()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Collision mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/collision-review");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.True(preview.ContainsDeletions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken));
        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview, deletionsApproved: true),
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.ProcessHost.Commands.Count(command =>
            command.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
            command.Contains("/collision-review", StringComparison.Ordinal)));
        Assert.Single(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("/collision-review", StringComparison.Ordinal) &&
            !command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingMirrorRejectsChangesBetweenPreviewAndApproval()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Drift", MirrorDirection.Download,
            fixture.Directory.Path, "/drift", DeleteExtraneous: true);
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);

        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview, deletionsApproved: true), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed, TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id);

        Assert.Contains("changed", failed.Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.Commands, command =>
            command.Contains("mirror --verbose=1", StringComparison.Ordinal) &&
            command.Contains("--delete", StringComparison.Ordinal) &&
            !command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitiallyCleanMirrorRejectsADeletionThatAppearsAtFinalReview()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Clean drift", MirrorDirection.Download,
            fixture.Directory.Path, "/clean-collision-drift");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);

        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("changed", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
            item.Command.Contains("mirror", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorFinalRemotePrefixWalkRejectsAnAncestorThatChangedToLink()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Remote ancestor drift", MirrorDirection.Download,
            fixture.Directory.Path, "/remote/mirror-link-ancestor/root");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);

        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) &&
            item.Command.Contains("--dry-run", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorRevalidatesRemotePrefixesAfterMatchingFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Post-preview remote drift",
            MirrorDirection.Download, fixture.Directory.Path, "/remote/post-mirror-link-ancestor/root");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        Assert.False(preview.ContainsDeletions);
        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains("ancestor", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture.ProcessHost.TaggedCommands.Count(item =>
            item.Command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
            item.Command.Contains("post-mirror-link-ancestor/root", StringComparison.Ordinal)));
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            item.Command.Contains("post-mirror-link-ancestor/root", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MirrorRevalidatesLocalAncestorsAfterMatchingFinalDryRun()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var parent = Path.Combine(fixture.Directory.Path, "post-dryrun-parent");
        var localRoot = Path.Combine(parent, "child");
        Directory.CreateDirectory(localRoot);
        var external = Path.Combine(Path.GetTempPath(), "LFTPPilot.PostDryRun", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(external, "child"));
        fixture.ProcessHost.FinalDryRunAction = () =>
        {
            Directory.Delete(parent, recursive: true);
            CreateDirectoryJunction(parent, external);
        };
        var definition = new MirrorDefinition(Guid.NewGuid(), profile.Id, "Post-preview local drift",
            MirrorDirection.Download, localRoot, "/local-post-dryrun");
        var preview = await fixture.Service.PreviewMirrorAsync(new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var approved = await fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, preview), TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).State == JobState.Failed,
                TestContext.Current.CancellationToken);

            Assert.Contains("reparse", fixture.Jobs.GetJobs().Single(job => job.Id == approved.Job.Id).Error?.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
                item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
                item.Command.Contains("/local-post-dryrun", StringComparison.Ordinal) &&
                !item.Command.Contains("--dry-run", StringComparison.Ordinal));
        }
        finally
        {
            fixture.ProcessHost.FinalDryRunAction = null;
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task ConsoleIsLazyIsolatedAndEnforcesSafePolicy()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ExecuteConsoleAsync(
            new(session.SessionId, "cat x | sh"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "console");
        var result = await fixture.Service.ExecuteConsoleAsync(new(session.SessionId, "pwd"), TestContext.Current.CancellationToken);
        Assert.Contains(result.Result.Lines, line => line.Line == "/remote/home");
        Assert.Equal(1, fixture.ProcessHost.Starts.Count(start => start.Tag == "console"));
    }

    [Fact]
    public async Task DisconnectWaitsForAdmittedConsoleCreationBeforeDisposingPersistentRoles()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var console = fixture.Service.ExecuteConsoleAsync(
            new(session.SessionId, "pwd"), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var disconnect = fixture.Service.DisconnectAsync(
            new(session.SessionId), TestContext.Current.CancellationToken);
        Assert.False(disconnect.IsCompleted);

        releaseStart.TrySetResult(true);
        _ = await console;
        Assert.True(await disconnect);
        Assert.Contains("console", fixture.ProcessHost.DisposedRoles);
        Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
    }

    [Theory]
    [InlineData("mirror-preview")]
    [InlineData("directory-transfer-preview")]
    [InlineData("remote-edit-download")]
    [InlineData("remote-edit-commit")]
    public async Task SessionCloseWaitsForEveryAdmittedEphemeralRole(string role)
    {
        await using var fixture = new WorkspaceFixture();
        await using var registry = new SessionRegistry(
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.HostKeyManager,
            fixture.Options);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var snapshot = await registry.ConnectAsync(profile, null, TestContext.Current.CancellationToken);
        var session = registry.Get(snapshot.SessionId);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var operation = session.WithEphemeralSessionAsync(
            role,
            _ => Task.FromResult(true),
            TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var disconnect = registry.DisconnectAsync(snapshot.SessionId);
        Assert.False(disconnect.IsCompleted);

        releaseStart.TrySetResult(true);
        Assert.True(await operation);
        Assert.True(await disconnect);
        Assert.Contains(role, fixture.ProcessHost.DisposedRoles);
        Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.WithEphemeralSessionAsync(
            role,
            _ => Task.FromResult(true),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegistryCloseRejectsAndCleansAConnectionThatCompletesAfterAdmissionCloses()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        await using var registry = new SessionRegistry(
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.HostKeyManager,
            fixture.Options);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var connect = registry.ConnectAsync(profile, null, TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var close = registry.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted);

        releaseStart.TrySetResult(true);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => connect);
        await close;
        Assert.Empty(registry.GetSnapshots());
        Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
    }

    [Fact]
    public async Task RegistryCloseAttemptsEverySessionAndRetainsOnlyFailedCleanupForRetry()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        await using var registry = new SessionRegistry(
            fixture.ProcessHost,
            fixture.Runtime,
            fixture.HostKeyManager,
            fixture.Options);
        var first = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var second = first with { Id = Guid.NewGuid(), Name = "Second", Host = "second.example" };
        _ = await registry.ConnectAsync(first, null, TestContext.Current.CancellationToken);
        _ = await registry.ConnectAsync(second, null, TestContext.Current.CancellationToken);
        fixture.ProcessHost.FailDisposeRole = "browse";
        fixture.ProcessHost.RemainingDisposeFailures = 1;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DisposeAsync().AsTask());
        Assert.Contains("simulated disposal failure", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(registry.GetSnapshots());
        Assert.Single(fixture.ProcessHost.DisposedRoles, role => role == "browse");

        fixture.ProcessHost.FailDisposeRole = null;
        await registry.DisposeAsync();
        Assert.Empty(registry.GetSnapshots());
        Assert.Equal(2, fixture.ProcessHost.DisposedRoles.Count(role => role == "browse"));
    }

    [Fact]
    public async Task RemoteTransferPlanningChoosesFxpOnlyForFtpFamily()
    {
        await using var fixture = new WorkspaceFixture();
        var ftp = fixture.AnonymousProfile(ConnectionProtocol.FtpsExplicit);
        var ftp2 = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with { Id = Guid.NewGuid(), Name = "FTP 2", Host = "two.example" };
        var sftp = fixture.AnonymousProfile(ConnectionProtocol.Sftp) with { Id = Guid.NewGuid(), Name = "SFTP", Host = "sftp.example" };
        await fixture.Profiles.SaveAsync(ftp, TestContext.Current.CancellationToken);
        await fixture.Profiles.SaveAsync(ftp2, TestContext.Current.CancellationToken);
        await fixture.Profiles.SaveAsync(sftp, TestContext.Current.CancellationToken);

        var fxp = await fixture.Service.PlanRemoteTransferAsync(new(ftp.Id, ftp2.Id, "/a", "/b"), TestContext.Current.CancellationToken);
        Assert.Equal(RemoteTransferMode.Fxp, fxp.Mode);
        var uploadRelay = await fixture.Service.PlanRemoteTransferAsync(
            new(ftp.Id, sftp.Id, "/a", "/b"), TestContext.Current.CancellationToken);
        var downloadRelay = await fixture.Service.PlanRemoteTransferAsync(
            new(sftp.Id, ftp.Id, "/a", "/b"), TestContext.Current.CancellationToken);
        var sftp2 = sftp with { Id = Guid.NewGuid(), Name = "SFTP 2", Host = "sftp-two.example" };
        await fixture.Profiles.SaveAsync(sftp2, TestContext.Current.CancellationToken);
        var sftpRelay = await fixture.Service.PlanRemoteTransferAsync(
            new(sftp.Id, sftp2.Id, "/a", "/b"), TestContext.Current.CancellationToken);

        Assert.Equal(RemoteTransferMode.ClientRelay, uploadRelay.Mode);
        Assert.Equal(RemoteTransferMode.ClientRelay, downloadRelay.Mode);
        Assert.Equal(RemoteTransferMode.ClientRelay, sftpRelay.Mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(fxp), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SftpRemoteTransferRelaysThroughSeparatelyPinnedProcessesAndRemovesManagedPayload()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.PasswordProfile(ConnectionProtocol.Sftp, "Source", "source.example");
        var destination = fixture.PasswordProfile(ConnectionProtocol.Sftp, "Destination", "destination.example");
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(source, "source-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination, "destination-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteTransferMode.ClientRelay, enqueued.Mode);
        Assert.Contains("two isolated", enqueued.RoutingNote, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
        var sourceStart = Assert.Single(fixture.ProcessHost.Starts,
            start => start.Tag == $"remote-relay-source-{plan.Id:N}");
        var destinationStart = Assert.Single(fixture.ProcessHost.Starts,
            start => start.Tag == $"remote-relay-destination-{plan.Id:N}");
        Assert.Equal(["source-secret"], sourceStart.Secrets);
        Assert.Equal(["destination-secret"], destinationStart.Secrets);
        var sourceInitialization = Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == sourceStart.Tag && item.Command.Contains("sftp:connect-program", StringComparison.Ordinal));
        var destinationInitialization = Assert.Single(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == destinationStart.Tag && item.Command.Contains("sftp:connect-program", StringComparison.Ordinal));
        Assert.NotEqual(sourceInitialization.Command, destinationInitialization.Command);
        Assert.DoesNotContain(destination.Id.ToString("N"), sourceInitialization.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(source.Id.ToString("N"), destinationInitialization.Command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == sourceStart.Tag && item.Command.StartsWith("get \"/source.bin\" -o ", StringComparison.Ordinal));
        Assert.Contains(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == destinationStart.Tag && item.Command.Contains("put ", StringComparison.Ordinal) &&
            item.Command.EndsWith(" -o \"/new-target.bin\"; set xfer:clobber yes; set xfer:use-temp-file yes", StringComparison.Ordinal));
        var relayDirectory = Path.Combine(fixture.Options.TemporaryRoot, "remote-relays", plan.Id.ToString("N"));
        Assert.False(Directory.Exists(relayDirectory));
        var serialized = JsonSerializer.Serialize(new { enqueued, Jobs = fixture.Jobs.GetJobs() }, FramedJsonStream.SerializerOptions);
        Assert.DoesNotContain("source-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Options.TemporaryRoot, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientRelayCancellationDisposesSourceProcessAndRemovesManagedPayload()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/blocking-relay-source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);

        _ = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.ProcessHost.TaggedCommands.Any(item =>
                item.Role == $"remote-relay-source-{plan.Id:N}" && item.Command.StartsWith("get ", StringComparison.Ordinal)),
            TestContext.Current.CancellationToken);
        Assert.True(fixture.Service.TryCancelOperation(plan.Id, "User cancelled client relay"));
        await WaitUntilAsync(
            () => fixture.ProcessHost.DisposedRoles.Contains($"remote-relay-source-{plan.Id:N}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag.StartsWith("remote-relay-destination-", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.TemporaryRoot, "remote-relays", plan.Id.ToString("N"))));
    }

    [Fact]
    public async Task ClientRelaySourceFailureNeverStartsDestinationAndStillRemovesWorkspace()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/failing-relay-source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.Contains(fixture.ProcessHost.Starts, start => start.Tag == $"remote-relay-source-{plan.Id:N}");
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag.StartsWith("remote-relay-destination-", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.TemporaryRoot, "remote-relays", plan.Id.ToString("N"))));
        Assert.Contains("Permission denied", fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientRelayRevalidatesNoOverwriteDestinationAfterDownload()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Sftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/relay-collision.bin"), TestContext.Current.CancellationToken);

        _ = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role.StartsWith("remote-relay-destination-", StringComparison.Ordinal) && item.Command.Contains("put ", StringComparison.Ordinal));
        Assert.Contains("appeared after review", fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).Error?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.TemporaryRoot, "remote-relays", plan.Id.ToString("N"))));
    }

    [Fact]
    public async Task ClientRelayFailureRedactsManagedLocalPathFromDurableJobError()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Sftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/failing-relay-target.bin"), TestContext.Current.CancellationToken);

        _ = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<EngineError>(fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).Error);
        Assert.Contains("<managed-client-relay>", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Options.TemporaryRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(LftpCommandBuilder.ToMsysPath(fixture.Options.TemporaryRoot), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(fixture.Options.TemporaryRoot, "remote-relays", plan.Id.ToString("N"))));
    }

    [Fact]
    public async Task RemoteTransferExecutesFxpPreferredJobWithoutPuttingCredentialsInArgumentsOrResults()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.PasswordProfile(ConnectionProtocol.FtpsExplicit, "Source", "source.example");
        var destination = fixture.PasswordProfile(ConnectionProtocol.Ftp, "Destination", "destination.example");
        await fixture.Service.SaveProfileAsync(new(source, "source-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination, "destination-secret"), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);

        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteTransferMode.Fxp, enqueued.Mode);
        Assert.Contains("FXP preferred", enqueued.RoutingNote, StringComparison.Ordinal);
        var start = Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
        Assert.Equal(["--norc"], start.Arguments);
        Assert.Equal(2, start.Secrets!.Count);
        Assert.Contains("source-secret", start.Secrets);
        Assert.Contains("destination-secret", start.Secrets);
        Assert.DoesNotContain("source-secret", string.Join(' ', start.Arguments!), StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", string.Join(' ', start.Arguments!), StringComparison.Ordinal);
        var command = Assert.Single(fixture.ProcessHost.Commands,
            command => command.StartsWith("set ftp:use-fxp true", StringComparison.Ordinal));
        Assert.Contains("\"slot:source/source.bin\"", command, StringComparison.Ordinal);
        Assert.Contains("\"slot:destination/new-target.bin\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("source-secret", command, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", command, StringComparison.Ordinal);
        var serialized = JsonSerializer.Serialize(new { enqueued, Jobs = fixture.Jobs.GetJobs() }, FramedJsonStream.SerializerOptions);
        Assert.DoesNotContain("source-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentDuplicateRemoteTransferEnqueuesConvergeOnOneJobAndProcess()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await release.Task;
            return await fixture.Service.EnqueueRemoteTransferAsync(
                new(plan), TestContext.Current.CancellationToken);
        })).ToArray();

        release.TrySetResult(true);
        var results = await Task.WhenAll(requests);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal(plan.Id, result.Job.Id));
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
        Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
    }

    [Fact]
    public async Task WorkspaceBootstrapWaitsForInFlightRemoteTransferCommit()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var publicationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<JobSnapshot> handler = (_, job) =>
        {
            if (job.Id != plan.Id || job.State != JobState.Queued) return;
            publicationEntered.TrySetResult(true);
            releasePublication.Task.GetAwaiter().GetResult();
        };
        fixture.Jobs.JobChanged += handler;

        try
        {
            var enqueue = Task.Run(() => fixture.Service.EnqueueRemoteTransferAsync(
                new(plan), TestContext.Current.CancellationToken));
            await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var bootstrap = fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(bootstrap.IsCompleted);

            releasePublication.TrySetResult(true);
            var enqueued = await enqueue;
            var snapshot = await bootstrap;
            Assert.Contains(snapshot.Jobs, job => job.Id == enqueued.Job.Id);
        }
        finally
        {
            releasePublication.TrySetResult(true);
            fixture.Jobs.JobChanged -= handler;
        }
    }

    [Fact]
    public async Task RemoteTransferReplayAfterAgentRestartRejectsWithoutStartingAnotherProcess()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        _ = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == plan.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        await fixture.Service.DisposeAsync();

        await using var restarted = new AgentWorkspaceService(
            fixture.Profiles, fixture.Secrets, fixture.HostKeyManager, fixture.ProcessHost, fixture.Runtime,
            fixture.Jobs, new MirrorPlanner(), fixture.Options, scheduler: fixture.Scheduler);
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.EnqueueRemoteTransferAsync(
            new(plan), TestContext.Current.CancellationToken));

        Assert.Contains("already consumed", replay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
        Assert.Single(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoteTransferPlanRejectsChangedProfileIdentityAfterReconnect(bool changeSource)
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        var sourceSession = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        var destinationSession = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        Assert.True(await fixture.Service.DisconnectAsync(
            new(sourceSession.SessionId), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DisconnectAsync(
            new(destinationSession.SessionId), TestContext.Current.CancellationToken));
        var changed = (changeSource ? source : destination) with
        {
            Host = changeSource ? "changed-source.example" : "changed-destination.example",
            UserName = "changed-user",
        };
        await fixture.Service.SaveProfileAsync(new(changed), TestContext.Current.CancellationToken);
        var activeSource = changeSource ? changed : source;
        var activeDestination = changeSource ? destination : changed;
        await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(activeSource)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(activeDestination)), TestContext.Current.CancellationToken);

        var identityChange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken));
        var replay = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken));

        Assert.Contains("identity changed", identityChange.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not create a job", replay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
        Assert.DoesNotContain(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
    }

    [Fact]
    public async Task RemoteTransferTrackingFailureReturnsOneCommittedFailedJobInsteadOfARejection()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var dependencyField = typeof(AgentWorkspaceService).GetField(
            "_activeJobProfileDependencies",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The active job dependency registry was not found.");
        var dependencies = Assert.IsType<ConcurrentDictionary<Guid, ImmutableHashSet<Guid>>>(
            dependencyField.GetValue(fixture.Service));
        Assert.True(dependencies.TryAdd(plan.Id, ImmutableHashSet.Create(source.Id)));

        try
        {
            var first = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
            var replay = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);

            Assert.Equal(plan.Id, first.Job.Id);
            Assert.Equal(JobState.Failed, first.Job.State);
            Assert.Equal(first.Job, replay.Job);
            Assert.Single(fixture.Jobs.GetJobs(), job => job.Id == plan.Id);
            Assert.DoesNotContain(fixture.ProcessHost.Starts, start => start.Tag == "remote-transfer");
        }
        finally
        {
            dependencies.TryRemove(plan.Id, out _);
        }
    }

    [Fact]
    public async Task RemoteTransferRejectsDirectoriesUnapprovedOverwriteAndForgedRoutingMode()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);

        var directoryPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source-folder", "/new-target.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(directoryPlan), TestContext.Current.CancellationToken));
        var collisionPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/existing.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(collisionPlan), TestContext.Current.CancellationToken));
        var replayedCollision = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(collisionPlan), TestContext.Current.CancellationToken));
        Assert.Contains("did not create a job", replayedCollision.Message, StringComparison.OrdinalIgnoreCase);

        var relayPlan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/source.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.EnqueueRemoteTransferAsync(
            new(relayPlan with { Mode = RemoteTransferMode.ClientRelay }), TestContext.Current.CancellationToken));
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(relayPlan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Completed,
            TestContext.Current.CancellationToken);
        Assert.Equal(RemoteTransferMode.Fxp, enqueued.Mode);
        Assert.Contains("FXP preferred", enqueued.RoutingNote, StringComparison.Ordinal);
        Assert.Contains(fixture.ProcessHost.Commands,
            command => command.StartsWith("set ftp:use-fxp true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteTransferPublicationCannotRaceEitherProfileRemovalBeforeDependencyRegistration()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        var sourceSession = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        var destinationSession = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/blocking-r2r.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<JobSnapshot> handler = (_, job) =>
        {
            if (job.Kind != JobKind.RemoteTransfer || job.State != JobState.Queued) return;
            published.TrySetResult(true);
            releasePublication.Task.GetAwaiter().GetResult();
        };
        fixture.Jobs.JobChanged += handler;

        try
        {
            var enqueue = Task.Run(() => fixture.Service.EnqueueRemoteTransferAsync(
                new(plan), TestContext.Current.CancellationToken));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var disconnect = fixture.Service.DisconnectAsync(
                new(destinationSession.SessionId), TestContext.Current.CancellationToken);
            var delete = fixture.Service.DeleteProfileAsync(
                new(source.Id), TestContext.Current.CancellationToken);
            Assert.False(disconnect.IsCompleted);
            Assert.False(delete.IsCompleted);

            releasePublication.TrySetResult(true);
            var enqueued = await enqueue;
            var disconnectBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => disconnect);
            var deleteBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => delete);
            Assert.Contains("jobs", disconnectBlocked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("jobs", deleteBlocked.Message, StringComparison.OrdinalIgnoreCase);
            var bootstrap = await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken);
            Assert.Contains(bootstrap.Sessions,
                session => session.SessionId == sourceSession.SessionId);

            Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Test cleanup"));
            await WaitUntilAsync(
                () => fixture.ProcessHost.DisposedRoles.Contains("remote-transfer"),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            releasePublication.TrySetResult(true);
            fixture.Jobs.JobChanged -= handler;
        }
    }

    [Fact]
    public async Task RunningRemoteTransferCancellationStopsItsIsolatedProcessAndCancelsJob()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        var destinationSession = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/blocking-r2r.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        fixture.ProcessHost.BlockDisposeRole = "remote-transfer";
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Running,
            TestContext.Current.CancellationToken);

        var disconnectBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DisconnectAsync(
            new(destinationSession.SessionId), TestContext.Current.CancellationToken));
        Assert.Contains("jobs", disconnectBlocked.Message, StringComparison.OrdinalIgnoreCase);
        var deleteBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteProfileAsync(
            new(destination.Id), TestContext.Current.CancellationToken));
        Assert.Contains("jobs", deleteBlocked.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "User cancelled remote transfer"));
        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Cancelled,
            TestContext.Current.CancellationToken);
        Assert.Equal("User cancelled remote transfer", fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).Status);
        await fixture.ProcessHost.DisposeEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("remote-transfer", fixture.ProcessHost.DisposedRoles);
        var cleanupDisconnectBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DisconnectAsync(
            new(destinationSession.SessionId), TestContext.Current.CancellationToken));
        var cleanupDeleteBlocked = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.DeleteProfileAsync(
            new(source.Id), TestContext.Current.CancellationToken));
        Assert.Contains("jobs", cleanupDisconnectBlocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jobs", cleanupDeleteBlocked.Message, StringComparison.OrdinalIgnoreCase);

        fixture.ProcessHost.ReleaseDispose.TrySetResult(true);
        await WaitUntilAsync(
            () => fixture.ProcessHost.DisposedRoles.Contains("remote-transfer"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancellationSourceCannotBeDisposedAfterTheJobTransitionsButBeforeCancelSignals()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/completion-before-cancel.bin", "/new-target.bin"),
            TestContext.Current.CancellationToken);
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(
            new(plan), TestContext.Current.CancellationToken);
        await fixture.ProcessHost.TransferGateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        EventHandler<JobSnapshot> handler = (_, job) =>
        {
            if (job.Id != enqueued.Job.Id || job.State != JobState.Cancelled) return;
            fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
            if (!SpinWait.SpinUntil(
                () => fixture.ProcessHost.DisposedRoles.Contains("remote-transfer"),
                TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("The remote-transfer process did not finish before cancellation publication returned.");
            }
        };
        fixture.Jobs.JobChanged += handler;

        try
        {
            Assert.True(fixture.Service.TryCancelOperation(enqueued.Job.Id, "Deterministic cancellation race"));
            Assert.Equal(JobState.Cancelled, fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State);
        }
        finally
        {
            fixture.ProcessHost.ReleaseTransferGate.TrySetResult(true);
            fixture.Jobs.JobChanged -= handler;
        }
    }

    [Fact]
    public async Task RemoteTransferExecutionFailureTransitionsJobToFailed()
    {
        await using var fixture = new WorkspaceFixture();
        var source = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var destination = fixture.AnonymousProfile(ConnectionProtocol.Ftp) with
        {
            Id = Guid.NewGuid(),
            Name = "Destination",
            Host = "destination.example",
        };
        await fixture.Service.SaveProfileAsync(new(source), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(destination), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(source)), TestContext.Current.CancellationToken);
        await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(destination)), TestContext.Current.CancellationToken);
        var plan = await fixture.Service.PlanRemoteTransferAsync(
            new(source.Id, destination.Id, "/failing-r2r.bin", "/new-target.bin"), TestContext.Current.CancellationToken);
        var enqueued = await fixture.Service.EnqueueRemoteTransferAsync(new(plan), TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            () => fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id).State == JobState.Failed,
            TestContext.Current.CancellationToken);
        var failed = fixture.Jobs.GetJobs().Single(job => job.Id == enqueued.Job.Id);
        Assert.Equal("lftp-job-failed", failed.Error?.Code);
        Assert.Contains("Permission denied", failed.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedMirrorDefinitionsProvideValidatedDeterministicCrudWithoutStartingWork()
    {
        await using var fixture = new WorkspaceFixture();
        var firstProfile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var secondProfile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(firstProfile), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(new(secondProfile), TestContext.Current.CancellationToken);
        var first = new MirrorDefinition(
            Guid.NewGuid(), firstProfile.Id, "Zulu", MirrorDirection.Download,
            fixture.Directory.Path, "/remote/zulu");
        var second = new MirrorDefinition(
            Guid.NewGuid(), firstProfile.Id, "alpha", MirrorDirection.Upload,
            fixture.Directory.Path, "/remote/alpha");
        var third = new MirrorDefinition(
            Guid.NewGuid(), secondProfile.Id, "Beta", MirrorDirection.Download,
            fixture.Directory.Path, "/remote/beta");

        Assert.Equal(first, await fixture.Service.SaveMirrorDefinitionAsync(
            new(first), TestContext.Current.CancellationToken));
        Assert.Equal(second, await fixture.Service.SaveMirrorDefinitionAsync(
            new(second), TestContext.Current.CancellationToken));
        Assert.Equal(third, await fixture.Service.SaveMirrorDefinitionAsync(
            new(third), TestContext.Current.CancellationToken));

        var expected = new[] { first, second, third }
            .OrderBy(static definition => definition.ProfileId)
            .ThenBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static definition => definition.Name, StringComparer.Ordinal)
            .ThenBy(static definition => definition.Id)
            .ToArray();
        Assert.Equal(expected, await fixture.Service.ListMirrorDefinitionsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, (await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).MirrorDefinitions);

        var rebound = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveMirrorDefinitionAsync(
                new(first with { ProfileId = secondProfile.Id, Name = "Rebound" }),
                TestContext.Current.CancellationToken));
        Assert.Contains("rebound", rebound.Message, StringComparison.OrdinalIgnoreCase);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveMirrorDefinitionAsync(
                new(first with { Id = Guid.NewGuid(), Name = "ALPHA" }),
                TestContext.Current.CancellationToken));
        Assert.Contains("unique", duplicate.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Service.SaveMirrorDefinitionAsync(
            new(first with { Id = Guid.NewGuid(), ProfileId = Guid.NewGuid(), Name = "Orphan" }),
            TestContext.Current.CancellationToken));

        var updated = first with { ParallelFiles = 4 };
        Assert.Equal(updated, await fixture.Service.SaveMirrorDefinitionAsync(
            new(updated), TestContext.Current.CancellationToken));
        Assert.True(await fixture.Service.DeleteMirrorDefinitionAsync(
            new(second.Id), TestContext.Current.CancellationToken));
        Assert.False(await fixture.Service.DeleteMirrorDefinitionAsync(
            new(second.Id), TestContext.Current.CancellationToken));
        Assert.Equal(
            new[] { updated, third }
                .OrderBy(static definition => definition.ProfileId)
                .ThenBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static definition => definition.Name, StringComparer.Ordinal)
                .ThenBy(static definition => definition.Id),
            await fixture.Service.ListMirrorDefinitionsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.ProcessHost.Starts);
        Assert.Empty(fixture.Jobs.GetJobs());
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.HostKeyProbe.Calls);
    }

    [Fact]
    public async Task SavedMirrorBootstrapFailsClosedForOrphansDuplicatesFailuresAndAggregateOverflow()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        MirrorDefinition Definition(Guid id, Guid profileId, string name, ImmutableArray<string> includes = default) =>
            new(id, profileId, name, MirrorDirection.Download, fixture.Directory.Path, "/remote", Includes: includes);

        var orphanProfileId = Guid.NewGuid();
        fixture.MirrorDefinitions.Seed(Definition(Guid.NewGuid(), orphanProfileId, "Orphan"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.ListMirrorDefinitionsAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.SaveProfileAsync(
            new(profile with { Id = orphanProfileId, Name = "Do not resurrect" }),
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken),
            saved => saved.Id == orphanProfileId);

        fixture.MirrorDefinitions.Clear();
        fixture.MirrorDefinitions.Seed(
            Definition(Guid.NewGuid(), profile.Id, "Duplicate"),
            Definition(Guid.NewGuid(), profile.Id, "DUPLICATE"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken));

        fixture.MirrorDefinitions.Clear();
        var maximumPatterns = Enumerable.Repeat(
            new string('x', MirrorDefinitionPolicy.MaximumPatternLength),
            MirrorDefinitionPolicy.MaximumPatternCharactersPerDefinition /
                MirrorDefinitionPolicy.MaximumPatternLength).ToImmutableArray();
        fixture.MirrorDefinitions.Seed(
            Definition(Guid.NewGuid(), profile.Id, "Budget one", maximumPatterns),
            Definition(Guid.NewGuid(), profile.Id, "Budget two", maximumPatterns),
            Definition(Guid.NewGuid(), profile.Id, "Budget overflow", ["x"]));
        var overflow = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken));
        Assert.Contains("aggregate pattern", overflow.Message, StringComparison.OrdinalIgnoreCase);

        fixture.MirrorDefinitions.GetFailure = new InvalidDataException("corrupt saved definitions");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken));
        Assert.Empty(fixture.ProcessHost.Starts);
        Assert.Empty(fixture.Secrets.GetCalls);
        Assert.Empty(fixture.HostKeyProbe.Calls);
    }

    [Fact]
    public async Task ProfileDeleteCascadesSavedMirrorsBeforeMetadataAndFailureLeavesProfileDiscoverable()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.PasswordProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(
            new(profile, "cascade-secret"), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Cascade", MirrorDirection.Download,
            fixture.Directory.Path, "/remote");
        await fixture.Service.SaveMirrorDefinitionAsync(new(definition), TestContext.Current.CancellationToken);
        fixture.MirrorDefinitions.DeleteFailure = new IOException("definition cascade failed");

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.DeleteProfileAsync(
            new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Contains(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken),
            saved => saved.Id == profile.Id);
        Assert.NotEmpty(fixture.Secrets.Values);
        Assert.DoesNotContain(fixture.PersistenceOperations,
            operation => operation.StartsWith("profile.delete:", StringComparison.Ordinal));

        fixture.MirrorDefinitions.DeleteFailure = null;
        while (fixture.PersistenceOperations.TryDequeue(out _)) { }
        Assert.True(await fixture.Service.DeleteProfileAsync(
            new(profile.Id), TestContext.Current.CancellationToken));
        Assert.Equal(
            [$"mirror.delete:{definition.Id:N}", $"profile.delete:{profile.Id:N}"],
            fixture.PersistenceOperations.ToArray());
        Assert.Empty(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.MirrorDefinitions.GetAllAsync(TestContext.Current.CancellationToken));

        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        Assert.Empty((await fixture.Service.BootstrapAsync(TestContext.Current.CancellationToken)).MirrorDefinitions);
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task SavedMirrorMutationsAndProfileIdentityChangesInvalidateHeldPreviews()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Saved preview", MirrorDirection.Download,
            fixture.Directory.Path, "/remote");
        await fixture.Service.SaveMirrorDefinitionAsync(new(definition), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            new(Guid.NewGuid(), definition, Guid.NewGuid(), "no-preview", new string('a', 64)),
            TestContext.Current.CancellationToken));
        Assert.Empty(fixture.ProcessHost.Starts);

        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var previewBeforeSave = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var startsAfterPreview = fixture.ProcessHost.Starts.Count;
        var updated = definition with { ParallelFiles = 3 };
        await fixture.Service.SaveMirrorDefinitionAsync(new(updated), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, definition, previewBeforeSave), TestContext.Current.CancellationToken));
        Assert.Equal(startsAfterPreview, fixture.ProcessHost.Starts.Count);

        var previewBeforeDelete = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, updated), TestContext.Current.CancellationToken);
        Assert.True(await fixture.Service.DeleteMirrorDefinitionAsync(
            new(updated.Id), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, updated, previewBeforeDelete), TestContext.Current.CancellationToken));

        await fixture.Service.SaveMirrorDefinitionAsync(new(updated), TestContext.Current.CancellationToken);
        var previewBeforeIdentityChange = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, updated), TestContext.Current.CancellationToken);
        await fixture.Service.DisconnectAsync(new(session.SessionId), TestContext.Current.CancellationToken);
        await fixture.Service.SaveProfileAsync(
            new(profile with { Host = "new-files.example" }), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ApproveMirrorAsync(
            MirrorApproval(session.SessionId, updated, previewBeforeIdentityChange), TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Jobs.GetJobs());
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSavedMirrorDeleteAndApprovalsExecuteAtMostOnce()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(
            new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Delete race", MirrorDirection.Download,
            fixture.Directory.Path, "/gate-blocker");
        await fixture.Service.SaveMirrorDefinitionAsync(new(definition), TestContext.Current.CancellationToken);
        var preview = await fixture.Service.PreviewMirrorAsync(
            new(session.SessionId, definition), TestContext.Current.CancellationToken);
        var request = MirrorApproval(session.SessionId, definition, preview);
        fixture.MirrorDefinitions.DeleteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.MirrorDefinitions.ReleaseDelete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var delete = fixture.Service.DeleteMirrorDefinitionAsync(
            new(definition.Id), TestContext.Current.CancellationToken);
        await fixture.MirrorDefinitions.DeleteEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var firstApproval = Record.ExceptionAsync(() =>
            fixture.Service.ApproveMirrorAsync(request, TestContext.Current.CancellationToken)).AsTask();
        var secondApproval = Record.ExceptionAsync(() =>
            fixture.Service.ApproveMirrorAsync(request, TestContext.Current.CancellationToken)).AsTask();
        try
        {
            await Task.Yield();
            Assert.False(firstApproval.IsCompleted);
            Assert.False(secondApproval.IsCompleted);
        }
        finally
        {
            fixture.MirrorDefinitions.ReleaseDelete.TrySetResult(true);
        }
        var failures = await Task.WhenAll(firstApproval, secondApproval);
        Assert.True(await delete);
        Assert.All(failures, failure => Assert.IsType<InvalidOperationException>(failure));

        Assert.Empty(fixture.Jobs.GetJobs());
        Assert.DoesNotContain(fixture.ProcessHost.TaggedCommands, item =>
            item.Role == "transfer" &&
            item.Command.StartsWith("mirror --verbose=1", StringComparison.Ordinal) &&
            !item.Command.Contains("--dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsonSavedMirrorsSurviveServiceRestartButHeldPreviewApprovalDoesNot()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Profiles.SaveAsync(profile, TestContext.Current.CancellationToken);
        var storePath = Path.Combine(
            fixture.Directory.Path, "saved-mirrors", JsonMirrorDefinitionStore.FileName);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Restarted saved mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/remote");
        MirrorApproveRequest approval;
        await using (var first = new AgentWorkspaceService(
            fixture.Profiles, fixture.Secrets, fixture.HostKeyManager, fixture.ProcessHost,
            fixture.Runtime, fixture.Jobs, new MirrorPlanner(), fixture.Options,
            mirrorDefinitionStore: new JsonMirrorDefinitionStore(storePath)))
        {
            await first.SaveMirrorDefinitionAsync(new(definition), TestContext.Current.CancellationToken);
            var session = await first.ConnectAsync(
                new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
            var preview = await first.PreviewMirrorAsync(
                new(session.SessionId, definition), TestContext.Current.CancellationToken);
            approval = MirrorApproval(session.SessionId, definition, preview);
        }

        await using var restarted = new AgentWorkspaceService(
            fixture.Profiles, fixture.Secrets, fixture.HostKeyManager, fixture.ProcessHost,
            fixture.Runtime, fixture.Jobs, new MirrorPlanner(), fixture.Options,
            mirrorDefinitionStore: new JsonMirrorDefinitionStore(storePath));
        Assert.Equal(definition,
            Assert.Single((await restarted.BootstrapAsync(TestContext.Current.CancellationToken)).MirrorDefinitions));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.ApproveMirrorAsync(approval, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Jobs.GetJobs());
    }

    [Fact]
    public async Task DisposalWaitsForAnAdmittedConnectRequestAndRejectsNewRequests()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var connect = fixture.Service.HandleAsync(
            WorkspaceMethods.SessionConnect,
            JsonSerializer.SerializeToElement(new SessionConnectRequest(ConnectionIdentity.FromProfile(profile)), FramedJsonStream.SerializerOptions),
            TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        try
        {
            var disposal = fixture.Service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.HandleAsync(
                WorkspaceMethods.ProfileList,
                JsonSerializer.SerializeToElement(new { }, FramedJsonStream.SerializerOptions),
                TestContext.Current.CancellationToken));

            releaseStart.TrySetResult(true);
            var connected = (await connect).Deserialize<SessionSnapshot>(FramedJsonStream.SerializerOptions);
            Assert.NotNull(connected);
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
            Assert.Equal(
                fixture.ProcessHost.Starts.Count(start => start.Tag == "browse"),
                fixture.ProcessHost.DisposedRoles.Count(role => role == "browse"));
        }
        finally
        {
            releaseStart.TrySetResult(true);
        }
    }

    [Fact]
    public async Task DisposalWaitsForAnAdmittedJobToBeTrackedBeforeTakingItsOperationSnapshot()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var plan = new TransferPlan(
            Guid.NewGuid(),
            profile.Id,
            TransferDirection.Download,
            "/remote/file.bin",
            Path.Combine(fixture.Directory.Path, "dispose-admission.bin"));
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<JobSnapshot> handler = (_, job) =>
        {
            if (job.Kind != JobKind.Transfer || job.State != JobState.Queued) return;
            published.TrySetResult(true);
            releasePublication.Task.GetAwaiter().GetResult();
        };
        fixture.Jobs.JobChanged += handler;

        try
        {
            var enqueue = Task.Run(() => fixture.Service.HandleAsync(
                WorkspaceMethods.TransferEnqueue,
                JsonSerializer.SerializeToElement(
                    new TransferEnqueueRequest(session.SessionId, plan),
                    FramedJsonStream.SerializerOptions),
                TestContext.Current.CancellationToken));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var disposal = fixture.Service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);

            releasePublication.TrySetResult(true);
            var result = (await enqueue).Deserialize<TransferEnqueueResult>(FramedJsonStream.SerializerOptions);
            Assert.NotNull(result);
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(
                JobState.Cancelled,
                fixture.Jobs.GetJobs().Single(job => job.Id == result.Job.Id).State);
            Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
        }
        finally
        {
            releasePublication.TrySetResult(true);
            fixture.Jobs.JobChanged -= handler;
        }
    }

    [Fact]
    public async Task DisposalWaitsForAnAdmittedRemoteEditToBeRegisteredBeforeCleanup()
    {
        await using var fixture = new WorkspaceFixture();
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        await fixture.Service.SaveProfileAsync(new(profile), TestContext.Current.CancellationToken);
        var session = await fixture.Service.ConnectAsync(new(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken);
        var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ProcessHost.StartEntered = startEntered;
        fixture.ProcessHost.ReleaseStart = releaseStart;

        var start = fixture.Service.HandleAsync(
            WorkspaceMethods.RemoteEditStart,
            JsonSerializer.SerializeToElement(
                new RemoteEditStartRequest(session.SessionId, "/active.txt"),
                FramedJsonStream.SerializerOptions),
            TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        try
        {
            var disposal = fixture.Service.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);

            releaseStart.TrySetResult(true);
            var edit = (await start).Deserialize<RemoteEditSession>(FramedJsonStream.SerializerOptions);
            Assert.NotNull(edit);
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.False(File.Exists(edit.LocalPath));
            Assert.Contains("remote-edit-download", fixture.ProcessHost.DisposedRoles);
            Assert.Contains("browse", fixture.ProcessHost.DisposedRoles);
        }
        finally
        {
            releaseStart.TrySetResult(true);
        }
    }

    [Fact]
    public async Task VersionedPipeHostKeyReviewExposesOnlyFingerprintsAndApprovalToken()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        fixture.HostKeys.AutoTrust = false;
        await using var host = new AgentHost(
            Path.Combine(fixture.Directory.Path, "host-key-pipe-jobs.json"),
            profileStore: fixture.Profiles,
            secretStore: fixture.Secrets,
            hostKeyManager: fixture.HostKeyManager,
            processHost: fixture.ProcessHost,
            runtimeProvider: fixture.Runtime,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: fixture.Options,
            mirrorDefinitionStore: fixture.MirrorDefinitions);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        var profile = fixture.PasswordProfile();
        var blockedCredential = await Assert.ThrowsAsync<EngineRequestRejectedException>(() => client.RequestAsync(
            WorkspaceMethods.ProfileSave,
            new ProfileSaveRequest(profile, "must-not-cross-before-trust"),
            TestContext.Current.CancellationToken));
        Assert.Contains("metadata without a credential", blockedCredential.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.Values);
        Assert.Empty(await fixture.Profiles.GetAllAsync(TestContext.Current.CancellationToken));
        _ = await client.RequestAsync(
            WorkspaceMethods.ProfileSave,
            new ProfileSaveRequest(profile),
            TestContext.Current.CancellationToken);
        var untrustedCredential = await Assert.ThrowsAsync<EngineRequestRejectedException>(() => client.RequestAsync(
            WorkspaceMethods.ProfileSave,
            new ProfileSaveRequest(profile, "still-must-not-cross-before-trust"),
            TestContext.Current.CancellationToken));
        Assert.Contains("approved host key", untrustedCredential.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Secrets.Values);

        var expectedKey = CreateTestHostKey(SftpHostKeyManager.CreateBinding(profile));
        var inspectionElement = await client.RequestAsync(
            WorkspaceMethods.SftpHostKeyInspect,
            new SftpHostKeyInspectRequest(ConnectionIdentity.FromProfile(profile)),
            TestContext.Current.CancellationToken);
        var inspectionFields = EnumerateJsonFields(inspectionElement).ToArray();
        var inspection = inspectionElement.Deserialize<SftpHostKeyInspection>(FramedJsonStream.SerializerOptions)!;
        var review = Assert.IsType<SftpHostKeyReview>(inspection.Review);
        Assert.Equal(expectedKey.FingerprintSha256, review.PresentedFingerprintSha256);
        Assert.Contains(inspectionFields, field =>
            field.Name == "presentedFingerprintSha256" && field.StringValue == review.PresentedFingerprintSha256);
        Assert.Contains(inspectionFields, field =>
            field.Name == "approvalToken" && field.StringValue == review.ApprovalToken);
        Assert.DoesNotContain(inspectionFields, field =>
            field.Name.Contains("publicKey", StringComparison.OrdinalIgnoreCase) ||
            field.StringValue == expectedKey.PublicKeyBase64);

        var approvalElement = await client.RequestAsync(
            WorkspaceMethods.SftpHostKeyApprove,
            new SftpHostKeyApproveRequest(profile.Id, review.ReviewId, review.ApprovalToken),
            TestContext.Current.CancellationToken);
        var approvalFields = EnumerateJsonFields(approvalElement).ToArray();
        var approval = approvalElement.Deserialize<SftpHostKeyApproveResult>(FramedJsonStream.SerializerOptions)!;
        Assert.Equal(expectedKey.FingerprintSha256, approval.FingerprintSha256);
        Assert.Contains(approvalFields, field =>
            field.Name == "fingerprintSha256" && field.StringValue == expectedKey.FingerprintSha256);
        Assert.DoesNotContain(approvalFields, field =>
            field.Name.Contains("publicKey", StringComparison.OrdinalIgnoreCase) ||
            field.StringValue == expectedKey.PublicKeyBase64 ||
            field.StringValue == review.ApprovalToken);

        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VersionedPipeExposesWorkspaceMethods()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        var historyStore = new InMemoryHistoryStore();
        var historyId = Guid.NewGuid();
        var historyFinishedAt = DateTimeOffset.UtcNow;
        var historyRecord = new HistoryRecord(
            historyId,
            historyId,
            JobKind.Transfer,
            "Prior transfer",
            JobState.Completed,
            historyFinishedAt.AddSeconds(-1),
            historyFinishedAt,
            Detail: "Complete");
        await historyStore.AppendAsync(historyRecord, TestContext.Current.CancellationToken);
        await using var host = new AgentHost(
            Path.Combine(fixture.Directory.Path, "jobs.json"),
            profileStore: fixture.Profiles,
            secretStore: fixture.Secrets,
            hostKeyManager: fixture.HostKeyManager,
            processHost: fixture.ProcessHost,
            runtimeProvider: fixture.Runtime,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: fixture.Options,
            mirrorDefinitionStore: fixture.MirrorDefinitions,
            historyStore: historyStore);
        using var stop = new CancellationTokenSource();
        var run = host.RunAsync(stop.Token);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        var profile = fixture.AnonymousProfile(ConnectionProtocol.Ftp);
        var saved = (await client.RequestAsync(WorkspaceMethods.ProfileSave, new ProfileSaveRequest(profile), TestContext.Current.CancellationToken))
            .Deserialize<ConnectionProfile>(FramedJsonStream.SerializerOptions);
        Assert.Equal(profile.Id, saved?.Id);
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profile.Id, "Pipe saved mirror", MirrorDirection.Download,
            fixture.Directory.Path, "/remote");
        var savedDefinition = (await client.RequestAsync(
            WorkspaceMethods.MirrorDefinitionSave,
            new MirrorDefinitionSaveRequest(definition),
            TestContext.Current.CancellationToken)).Deserialize<MirrorDefinition>(FramedJsonStream.SerializerOptions);
        Assert.Equal(definition, savedDefinition);
        var definitions = (await client.RequestAsync(
            WorkspaceMethods.MirrorDefinitionList,
            cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<ImmutableArray<MirrorDefinition>>(FramedJsonStream.SerializerOptions);
        Assert.Equal(definition, Assert.Single(definitions));
        var bootstrap = (await client.RequestAsync(WorkspaceMethods.Bootstrap, cancellationToken: TestContext.Current.CancellationToken))
            .Deserialize<WorkspaceBootstrap>(FramedJsonStream.SerializerOptions);
        Assert.Equal(AgentProtocol.CurrentVersion, bootstrap?.ProtocolVersion);
        Assert.True(bootstrap?.Runtime.Available);
        Assert.Equal(definition, Assert.Single(bootstrap?.MirrorDefinitions ?? []));
        Assert.Equal(historyRecord, Assert.Single(bootstrap?.History ?? []));
        var directJob = JobCoordinatorTests.Job(JobState.Queued);
        var enqueueError = await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync("jobs.enqueue", directJob, TestContext.Current.CancellationToken));
        Assert.Contains("Direct job creation is disabled", enqueueError.Message, StringComparison.Ordinal);
        var transitionError = await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync("jobs.transition", new JobTransitionRequest(directJob.Id, JobState.Running), TestContext.Current.CancellationToken));
        Assert.Contains("Direct job mutation is disabled", transitionError.Message, StringComparison.Ordinal);
        var connected = (await client.RequestAsync(WorkspaceMethods.SessionConnect, new SessionConnectRequest(ConnectionIdentity.FromProfile(profile)), TestContext.Current.CancellationToken))
            .Deserialize<SessionSnapshot>(FramedJsonStream.SerializerOptions);
        Assert.True(connected?.IsConnected);
        var retryError = await Assert.ThrowsAsync<EngineRequestRejectedException>(() =>
            client.RequestAsync(WorkspaceMethods.JobRetry, new JobRetryRequest(Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Contains("not found", retryError.Message, StringComparison.OrdinalIgnoreCase);
        var deleted = (await client.RequestAsync(
            WorkspaceMethods.MirrorDefinitionDelete,
            new MirrorDefinitionDeleteRequest(definition.Id),
            TestContext.Current.CancellationToken)).Deserialize<bool>(FramedJsonStream.SerializerOptions);
        Assert.True(deleted);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VersionedPipePagesTenThousandUnicodeFileEntriesWithinFrameLimit()
    {
        await using var fixture = new WorkspaceFixture(createService: false);
        var directory = Path.Combine(fixture.Directory.Path, "ten-thousand");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < 10_000; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            using var file = File.Create(Path.Combine(directory, $"曲-{index:D5}.txt"));
        }

        await using var host = new AgentHost(
            Path.Combine(fixture.Directory.Path, "paged-jobs.json"),
            profileStore: fixture.Profiles,
            secretStore: fixture.Secrets,
            hostKeyManager: fixture.HostKeyManager,
            processHost: fixture.ProcessHost,
            runtimeProvider: fixture.Runtime,
            mirrorPlanner: new MirrorPlanner(),
            workspaceOptions: fixture.Options);
        var run = host.RunAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeEngineClient(Environment.ProcessId);
        string? continuation = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        do
        {
            var element = await client.RequestAsync(WorkspaceMethods.BrowseLocal,
                new BrowseRequest(null, directory, ContinuationToken: continuation, PageSize: 1_000),
                TestContext.Current.CancellationToken);
            var page = element.Deserialize<BrowseResult>(FramedJsonStream.SerializerOptions)!;
            Assert.InRange(page.Entries.Length, 1, 1_000);
            Assert.Equal(10_000, page.TotalCount);
            foreach (var entry in page.Entries) Assert.True(names.Add(entry.Name));
            continuation = page.ContinuationToken;
            pages++;
        } while (continuation is not null);

        Assert.Equal(10_000, names.Count);
        Assert.True(pages > 1);
        _ = await client.RequestAsync(AgentProtocol.StopMethod, cancellationToken: TestContext.Current.CancellationToken);
        await run.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void MsysPathConversionHandlesDriveUncAndInjection()
    {
        Assert.Equal("/c/Users/Alice/file.txt", LftpCommandBuilder.ToMsysPath(@"C:\Users\Alice\file.txt"));
        Assert.Equal("//server/share/file.txt", LftpCommandBuilder.ToMsysPath(@"\\server\share\file.txt"));
        Assert.Throws<ArgumentException>(() => LftpCommandBuilder.ToMsysPath("C:\\safe\n! calc"));
    }

    [Fact]
    public void ListingParserAcceptsFullReducedAndSymlinkLayouts()
    {
        var entries = LftpOutputParser.ParseLongListing([
            "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 full.txt",
            "-rw-r--r-- alice staff 7 2026-07-15 12:35 reduced.txt",
            "lrwxrwxrwx 4 2026-07-15 12:36 link@ -> target.txt",
        ], "/root");
        Assert.Equal(3, entries.Length);
        Assert.Equal("alice", entries[1].Owner);
        Assert.Equal(EntryKind.SymbolicLink, entries[2].Kind);
        Assert.Equal("target.txt", entries[2].LinkTarget);
    }

    private static MirrorApproveRequest MirrorApproval(
        Guid sessionId,
        MirrorDefinition definition,
        MirrorPreview preview,
        bool deletionsApproved = false,
        string? approvalToken = null,
        MirrorPreview? reviewedPreview = null) =>
        new(
            sessionId,
            definition,
            preview.Id,
            approvalToken ?? preview.ApprovalToken,
            MirrorPlanner.ReviewFingerprint(reviewedPreview ?? preview),
            deletionsApproved);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The job did not finish.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static string BlockStateWrites(WorkspaceFixture fixture)
    {
        var directory = Path.GetDirectoryName(fixture.StatePath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        File.WriteAllText(directory, "This file deliberately blocks creation of the durable-state directory.");
        return directory;
    }

    private static void RestoreStateWrites(string blockedDirectory)
    {
        if (File.Exists(blockedDirectory)) File.Delete(blockedDirectory);
        Directory.CreateDirectory(blockedDirectory);
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The directory-junction test helper did not start.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0,
            $"The directory-junction test helper failed: {process.StandardError.ReadToEnd()}");
        Assert.True((File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        public WorkspaceFixture(
            bool createService = true,
            TimeProvider? timeProvider = null,
            string? durableStorePath = null,
            bool persistSessionTabs = false)
        {
            Directory = new();
            PersistenceOperations = new();
            Profiles = new(PersistenceOperations);
            MirrorDefinitions = new(PersistenceOperations);
            Secrets = new();
            HostKeys = new();
            HostKeyProbe = new();
            HostKeyManager = new(HostKeys, HostKeyProbe, timeProvider);
            ProcessHost = new();
            Runtime = new();
            Jobs = new();
            StatePath = durableStorePath ?? Path.Combine(Directory.Path, "state", "agent-state.json");
            Store = new(StatePath);
            Scheduler = new(Jobs, Store, timeProvider);
            Options = AgentWorkspaceOptions.CreateDefault(Directory.Path) with
            {
                ConnectTimeout = TimeSpan.FromSeconds(1),
                BrowseTimeout = TimeSpan.FromSeconds(1),
                TransferTimeout = TimeSpan.FromSeconds(1),
                MirrorPreviewTimeout = TimeSpan.FromSeconds(1),
                ConsoleTimeout = TimeSpan.FromSeconds(1),
            };
            Service = createService
                ? new(
                    Profiles,
                    Secrets,
                    HostKeyManager,
                    ProcessHost,
                    Runtime,
                    Jobs,
                    new MirrorPlanner(),
                    Options,
                    scheduler: Scheduler,
                    stateStore: persistSessionTabs ? Store : null,
                    mirrorDefinitionStore: MirrorDefinitions)
                : null!;
        }

        public TestDirectory Directory { get; }
        public ConcurrentQueue<string> PersistenceOperations { get; }
        public MemoryProfileStore Profiles { get; }
        public MemoryMirrorDefinitionStore MirrorDefinitions { get; }
        public MemorySecretStore Secrets { get; }
        public MemoryHostKeyStore HostKeys { get; }
        public FakeSshHostKeyProbe HostKeyProbe { get; }
        public SftpHostKeyManager HostKeyManager { get; }
        public FakeProcessHost ProcessHost { get; }
        public FakeRuntimeProvider Runtime { get; }
        public JobCoordinator Jobs { get; }
        public string StatePath { get; }
        public DurableJobStore Store { get; }
        public RunOnceScheduler Scheduler { get; }
        public AgentWorkspaceOptions Options { get; }
        public AgentWorkspaceService Service { get; }

        public ConnectionProfile PasswordProfile(
            ConnectionProtocol protocol = ConnectionProtocol.Sftp,
            string name = "Secure",
            string host = "sftp.example") => new(
                Guid.NewGuid(), name, protocol, host, ProfileValidator.DefaultPort(protocol), "alice", AuthenticationKind.Password);

        public ConnectionProfile AnonymousProfile(ConnectionProtocol protocol) => new(
            Guid.NewGuid(), protocol.ToString(), protocol, "files.example", ProfileValidator.DefaultPort(protocol), "anonymous", AuthenticationKind.Anonymous);

        public async ValueTask DisposeAsync()
        {
            MirrorDefinitions.ReleaseDelete?.TrySetResult(true);
            ProcessHost.ReleaseReservedQueueSlots.TrySetResult(true);
            ProcessHost.ReleaseTransferGate.TrySetResult(true);
            ProcessHost.ReleaseStart?.TrySetResult(true);
            ProcessHost.ReleaseDispose.TrySetResult(true);
            await Scheduler.DisposeAsync();
            if (Service is not null) await Service.DisposeAsync();
            Directory.Dispose();
        }
    }

    private sealed class MemoryProfileStore : IProfileStore
    {
        private readonly ConcurrentDictionary<Guid, ConnectionProfile> _profiles = [];
        private readonly ConcurrentQueue<string>? _operations;
        public MemoryProfileStore(ConcurrentQueue<string>? operations = null) => _operations = operations;
        public Exception? SaveFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
        }
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _operations?.Enqueue($"profile.delete:{profileId:N}");
            if (DeleteFailure is not null) return Task.FromException(DeleteFailure);
            _profiles.TryRemove(profileId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryMirrorDefinitionStore(ConcurrentQueue<string>? operations = null) : IMirrorDefinitionStore
    {
        private readonly ConcurrentDictionary<Guid, MirrorDefinition> _definitions = [];
        public Exception? GetFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public Exception? DeleteFailure { get; set; }
        public TaskCompletionSource<bool>? DeleteEntered { get; set; }
        public TaskCompletionSource<bool>? ReleaseDelete { get; set; }
        public int GetCalls => Volatile.Read(ref _getCalls);

        public Task<IReadOnlyList<MirrorDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _getCalls);
            if (GetFailure is not null) return Task.FromException<IReadOnlyList<MirrorDefinition>>(GetFailure);
            return Task.FromResult<IReadOnlyList<MirrorDefinition>>(_definitions.Values.ToArray());
        }

        public Task SaveAsync(MirrorDefinition definition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveFailure is not null) return Task.FromException(SaveFailure);
            _definitions[definition.Id] = definition;
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations?.Enqueue($"mirror.delete:{definitionId:N}");
            DeleteEntered?.TrySetResult(true);
            if (ReleaseDelete is { } releaseDelete)
                await releaseDelete.Task.WaitAsync(cancellationToken);
            if (DeleteFailure is not null) throw DeleteFailure;
            _definitions.TryRemove(definitionId, out _);
        }

        public void Seed(params MirrorDefinition[] definitions)
        {
            foreach (var definition in definitions) _definitions[definition.Id] = definition;
        }

        public void Clear() => _definitions.Clear();

        private int _getCalls;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = [];
        public ConcurrentBag<SecretBinding> GetCalls { get; } = [];
        public Task SaveAsync(SecretValue secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[secret.Binding.CanonicalIdentity] = secret.Value;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(SecretBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls.Add(binding);
            return Task.FromResult(Values.TryGetValue(binding.CanonicalIdentity, out var value) ? value : null);
        }
        public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var key in Values.Keys.Where(key => key.StartsWith(profileId.ToString("N"), StringComparison.Ordinal))) Values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private static IEnumerable<(string Name, string? StringValue)> EnumerateJsonFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return (property.Name,
                    property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null);
                foreach (var nested in EnumerateJsonFields(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumerateJsonFields(item)) yield return nested;
        }
    }

    private sealed class MemoryHostKeyStore : IHostKeyStore
    {
        private readonly ConcurrentDictionary<HostKeyBinding, TrustedSftpHostKey> _keys = [];

        public bool AutoTrust { get; set; } = true;

        public Task<TrustedSftpHostKey?> GetAsync(HostKeyBinding binding, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_keys.TryGetValue(binding, out var key)) return Task.FromResult<TrustedSftpHostKey?>(key);
            return Task.FromResult<TrustedSftpHostKey?>(AutoTrust ? CreateTestHostKey(binding) : null);
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

    private sealed class FakeSshHostKeyProbe : ISshHostKeyProbe
    {
        public ConcurrentBag<(Guid ProfileId, string Alias)> Calls { get; } = [];
        public byte KeyMarker { get; set; } = 0x42;

        public Task<TrustedSftpHostKey> ProbeAsync(
            ConnectionProfile profile,
            string hostKeyAlias,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((profile.Id, hostKeyAlias));
            return Task.FromResult(CreateTestHostKey(SftpHostKeyManager.CreateBinding(profile), KeyMarker));
        }
    }

    private static TrustedSftpHostKey CreateTestHostKey(HostKeyBinding binding, byte marker = 0x42)
    {
        const string algorithm = "ssh-ed25519";
        var algorithmBytes = Encoding.ASCII.GetBytes(algorithm);
        var blob = new byte[4 + algorithmBytes.Length + 4 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(0, 4), (uint)algorithmBytes.Length);
        algorithmBytes.CopyTo(blob.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4 + algorithmBytes.Length, 4), 32);
        blob.AsSpan(8 + algorithmBytes.Length).Fill(marker);
        var encoded = Convert.ToBase64String(blob);
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return new(binding, algorithm, encoded, fingerprint);
    }

    private sealed class FakeRuntimeProvider : ILftpRuntimeProvider
    {
        public Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LftpRuntimeDescriptor(@"C:\fake", @"C:\fake\bin\lftp.exe", @"C:\fake\bin", false, "test", true));
        }
    }

    private sealed class FakeProcessHost : ILftpProcessHost
    {
        private int _nextId = 100;
        private readonly ConcurrentDictionary<string, int> _queueAttempts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _statAttempts = new(StringComparer.Ordinal);
        public ConcurrentBag<LftpProcessStartOptions> Starts { get; } = [];
        public ConcurrentBag<string> Commands { get; } = [];
        public ConcurrentBag<(string Role, string Command)> TaggedCommands { get; } = [];
        public ConcurrentBag<string> StoppedRoles { get; } = [];
        public ConcurrentBag<string> DisposedRoles { get; } = [];
        public TaskCompletionSource<bool> QueueSlotsFilled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReservedQueueSlots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TransferGateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseTransferGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ValidationGateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ScheduledValidationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? StartEntered { get; set; }
        public TaskCompletionSource<bool>? ReleaseStart { get; set; }
        public string? BlockDisposeRole { get; set; }
        public string? FailDisposeRole { get; set; }
        public int RemainingDisposeFailures
        {
            get => Volatile.Read(ref _remainingDisposeFailures);
            set => Volatile.Write(ref _remainingDisposeFailures, value);
        }
        public TaskCompletionSource<bool> DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? FinalDryRunAction { get; set; }
        public string? FailRolePrefix { get; set; }

        public async Task<ILftpSession> StartAsync(LftpProcessStartOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRolePrefix is not null && options.Tag.StartsWith(FailRolePrefix, StringComparison.Ordinal))
                throw new System.ComponentModel.Win32Exception("simulated process launch failure");
            Starts.Add(options);
            var startEntered = StartEntered;
            var releaseStart = ReleaseStart;
            StartEntered = null;
            startEntered?.TrySetResult(true);
            if (releaseStart is not null)
                await releaseStart.Task.WaitAsync(cancellationToken);
            if (ReferenceEquals(ReleaseStart, releaseStart)) ReleaseStart = null;
            return new FakeSession(
                Interlocked.Increment(ref _nextId), options.Tag, Commands, TaggedCommands, StoppedRoles, DisposedRoles,
                _queueAttempts, _statAttempts, QueueSlotsFilled, ReleaseReservedQueueSlots,
                TransferGateEntered, ReleaseTransferGate, ValidationGateEntered, ScheduledValidationEntered,
                () => FinalDryRunAction, () => BlockDisposeRole, ShouldFailDispose, DisposeEntered, ReleaseDispose);
        }

        private bool ShouldFailDispose(string role)
        {
            if (!string.Equals(FailDisposeRole, role, StringComparison.Ordinal)) return false;
            while (true)
            {
                var remaining = Volatile.Read(ref _remainingDisposeFailures);
                if (remaining <= 0) return false;
                if (Interlocked.CompareExchange(ref _remainingDisposeFailures, remaining - 1, remaining) == remaining) return true;
            }
        }

        private int _remainingDisposeFailures;
    }

    private sealed class FakeSession(
        int processId,
        string role,
        ConcurrentBag<string> commands,
        ConcurrentBag<(string Role, string Command)> taggedCommands,
        ConcurrentBag<string> stoppedRoles,
        ConcurrentBag<string> disposedRoles,
        ConcurrentDictionary<string, int> queueAttempts,
        ConcurrentDictionary<string, int> statAttempts,
        TaskCompletionSource<bool> queueSlotsFilled,
        TaskCompletionSource<bool> releaseReservedQueueSlots,
        TaskCompletionSource<bool> transferGateEntered,
        TaskCompletionSource<bool> releaseTransferGate,
        TaskCompletionSource<bool> validationGateEntered,
        TaskCompletionSource<bool> scheduledValidationEntered,
        Func<Action?> finalDryRunAction,
        Func<string?> blockDisposeRole,
        Func<string, bool> failDispose,
        TaskCompletionSource<bool> disposeEntered,
        TaskCompletionSource<bool> releaseDispose) : ILftpSession
    {
        public int ProcessId { get; } = processId;
        public bool IsRunning { get; private set; } = true;
        public event EventHandler<LftpOutputLine>? OutputReceived;
        public event EventHandler<LftpOutputLine>? UnsolicitedOutput;

        public Task<LftpCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning) return Task.FromResult(new LftpCommandResult([], Failure: "The LFTP session is not running."));
            commands.Add(command);
            taggedCommands.Add((role, command));
            if ((role == "transfer-queue" || role.StartsWith("transfer-policy-", StringComparison.Ordinal)) &&
                command.StartsWith("alias __LFTPPILOT_QUEUE_ALIAS_", StringComparison.Ordinal))
            {
                var scheduledRetryAttempt = command.Contains("scheduled-retry-cancel.bin", StringComparison.Ordinal)
                    ? queueAttempts.AddOrUpdate("scheduled-retry-cancel.bin", 1, static (_, count) => count + 1)
                    : 0;
                if (command.Contains("slot-block-", StringComparison.Ordinal))
                {
                    if (queueAttempts.AddOrUpdate("slot-blockers", 1, static (_, count) => count + 1) == 2)
                        queueSlotsFilled.TrySetResult(true);
                    _ = EmitQueueMarkerAfterReleaseAsync(command, releaseReservedQueueSlots.Task, cancellationToken);
                }
                else if (!command.Contains("blocking.bin", StringComparison.Ordinal) && scheduledRetryAttempt != 2)
                {
                    var retryOnceFailed = command.Contains("retry-once.bin", StringComparison.Ordinal) &&
                        queueAttempts.AddOrUpdate("retry-once.bin", 1, static (_, count) => count + 1) == 1;
                    EmitQueueMarker(command,
                        command.Contains("failing-queue.bin", StringComparison.Ordinal) || retryOnceFailed || scheduledRetryAttempt == 1
                            ? "_FAILED"
                            : "_OK",
                        submission: false);
                }
                var submissionMarker = FindQueueMarker(command, "_SUBMIT_OK", submission: true)
                    ?? throw new InvalidOperationException("The test queue command did not contain a submission marker.");
                var submissionOutput = ImmutableArray.CreateBuilder<LftpOutputLine>();
                if (command.Contains("interleaved.bin", StringComparison.Ordinal))
                    submissionOutput.Add(new("stderr", "get: Access failed: Permission denied"));
                submissionOutput.Add(new("stdout", submissionMarker));
                return Task.FromResult(new LftpCommandResult(submissionOutput.ToImmutable()));
            }
            if (role == "remote-transfer" && command.Contains("blocking-r2r.bin", StringComparison.Ordinal))
                return WaitForCancellationAsync(cancellationToken);
            if (role == "remote-transfer" && command.Contains("completion-before-cancel.bin", StringComparison.Ordinal))
            {
                transferGateEntered.TrySetResult(true);
                return WaitForReleaseAsync(releaseTransferGate.Task, cancellationToken);
            }
            if (role == "transfer" && command.Contains("mirror", StringComparison.Ordinal) &&
                !command.Contains("--dry-run", StringComparison.Ordinal) &&
                command.Contains("/gate-blocker", StringComparison.Ordinal))
            {
                transferGateEntered.TrySetResult(true);
                return WaitForReleaseAsync(releaseTransferGate.Task, cancellationToken);
            }
            if (role == "remote-transfer" && command.Contains("failing-r2r.bin", StringComparison.Ordinal))
                return Task.FromResult(new LftpCommandResult([new("stderr", "get: Access failed: Permission denied")]));
            if (role == "transfer" && command.Contains("mirror", StringComparison.Ordinal) &&
                !command.Contains("--dry-run", StringComparison.Ordinal) &&
                command.Contains("retry-once.bin", StringComparison.Ordinal))
            {
                var failed = queueAttempts.AddOrUpdate("directory-retry-once.bin", 1, static (_, count) => count + 1) == 1;
                return Task.FromResult(failed
                    ? new LftpCommandResult([new("stderr", "mirror: Access failed: simulated directory transfer failure")])
                    : new LftpCommandResult([]));
            }
            if (role.StartsWith("remote-relay-source-", StringComparison.Ordinal) &&
                command.StartsWith("get ", StringComparison.Ordinal) &&
                command.Contains("failing-relay-source.bin", StringComparison.Ordinal))
            {
                return Task.FromResult(new LftpCommandResult([new("stderr", "get: Access failed: Permission denied")]));
            }
            if (role.StartsWith("remote-relay-source-", StringComparison.Ordinal) &&
                command.StartsWith("get ", StringComparison.Ordinal) &&
                command.Contains("blocking-relay-source.bin", StringComparison.Ordinal))
            {
                return WaitForCancellationAsync(cancellationToken);
            }
            if (role.StartsWith("remote-relay-destination-", StringComparison.Ordinal) &&
                command.Contains("put ", StringComparison.Ordinal) &&
                command.Contains("failing-relay-target.bin", StringComparison.Ordinal))
            {
                return Task.FromResult(new LftpCommandResult([new("stderr", $"put: simulated failure ({command})")]));
            }
            if ((role == "remote-edit-download" || role.StartsWith("remote-relay-source-", StringComparison.Ordinal)) &&
                command.StartsWith("get ", StringComparison.Ordinal))
            {
                WriteRemoteEditDownload(command);
                return Task.FromResult(new LftpCommandResult([]));
            }
            if (IsStatCommand(command) && command.Contains("/relay-collision.bin", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("relay-collision", 1, static (_, count) => count + 1);
                return Task.FromResult(attempt == 1
                    ? new LftpCommandResult([new("stderr", "cls: Access failed: No such file")])
                    : new LftpCommandResult([new("stdout", RemoteEditListing(command))]));
            }
            if (IsStatCommand(command) && command.Contains("/stat-drift-directory", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("stat-drift-directory", 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteDirectoryListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/cancel-validation.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("cancel-validation", 1, static (_, count) => count + 1);
                if (attempt >= 2) return WaitForCancellationAsync(cancellationToken);
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/validation-gate-one.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("validation-gate-one", 1, static (_, count) => count + 1);
                if (attempt >= 2)
                {
                    validationGateEntered.TrySetResult(true);
                    return WaitForCancellationAsync(cancellationToken);
                }
            }
            if (role == "validation" && IsStatCommand(command) &&
                command.Contains("\"/remote/scheduled-validation-cancel.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("scheduled-validation-cancel", 1, static (_, count) => count + 1);
                if (attempt >= 2)
                {
                    scheduledValidationEntered.TrySetResult(true);
                    return WaitForCancellationAsync(cancellationToken);
                }
            }
            if (IsStatCommand(command) && command.Contains("\"/remote/file-slot-drift.bin\"", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("file-slot-drift", 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteEditListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsStatCommand(command) &&
                (command.Contains("\"/remote/link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/directory-link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/mirror-link-ancestor\"", StringComparison.Ordinal)))
            {
                var key = command.Contains("directory-link-ancestor", StringComparison.Ordinal)
                    ? "directory-link-ancestor"
                    : command.Contains("mirror-link-ancestor", StringComparison.Ordinal)
                        ? "mirror-link-ancestor"
                        : "link-ancestor";
                var attempt = statAttempts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                var listing = attempt == 1 ? RemoteDirectoryListing(command) : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsStatCommand(command) &&
                (command.Contains("\"/remote/post-directory-link-ancestor\"", StringComparison.Ordinal) ||
                 command.Contains("\"/remote/post-mirror-link-ancestor\"", StringComparison.Ordinal)))
            {
                var key = command.Contains("post-directory", StringComparison.Ordinal)
                    ? "post-directory-dry-run"
                    : "post-mirror-dry-run";
                var listing = statAttempts.GetOrAdd(key, 0) == 0
                    ? RemoteDirectoryListing(command)
                    : RemoteSymbolicLinkListing(command);
                return Task.FromResult(new LftpCommandResult([new("stdout", listing)]));
            }
            if (IsAutomaticDirectoryPreview(role, command) &&
                (command.Contains("/remote/retry-preview-target", StringComparison.Ordinal) ||
                 command.Contains("/remote/scheduled-preview-target", StringComparison.Ordinal)))
            {
                var key = command.Contains("retry-preview-target", StringComparison.Ordinal)
                    ? "retry-preview-target"
                    : "scheduled-preview-target";
                var attempt = statAttempts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                var destructiveAttempt = key == "retry-preview-target" ? 3 : 2;
                return Task.FromResult(attempt >= destructiveAttempt
                    ? new LftpCommandResult([new("stdout", "Removing old local file `collision'")])
                    : new LftpCommandResult([]));
            }
            if (IsAutomaticDirectoryPreview(role, command) && command.Contains("/guarded-drift-target", StringComparison.Ordinal))
            {
                var attempt = statAttempts.AddOrUpdate("guarded-drift-target", 1, static (_, count) => count + 1);
                return Task.FromResult(attempt >= 2
                    ? new LftpCommandResult([new("stdout", "Removing old local file `collision'")])
                    : new LftpCommandResult([]));
            }
            if (IsAutomaticDirectoryPreview(role, command) &&
                command.Contains("/remote/post-directory-link-ancestor/source", StringComparison.Ordinal))
            {
                if (role == "transfer") statAttempts["post-directory-dry-run"] = 1;
                return Task.FromResult(new LftpCommandResult([]));
            }
            if (role == "transfer" && command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                command.Contains("/remote/post-mirror-link-ancestor/root", StringComparison.Ordinal))
            {
                statAttempts["post-mirror-dry-run"] = 1;
                return Task.FromResult(new LftpCommandResult([new("stdout", "Transferring file `new.txt'")]));
            }
            if (role == "transfer" && command.StartsWith("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                command.Contains("/local-post-dryrun", StringComparison.Ordinal))
            {
                finalDryRunAction()?.Invoke();
                return Task.FromResult(new LftpCommandResult([new("stdout", "Transferring file `new.txt'")]));
            }
            ImmutableArray<LftpOutputLine> output = command switch
            {
                var value when value.Contains("mirror", StringComparison.Ordinal) &&
                    value.Contains("--dry-run", StringComparison.Ordinal) &&
                    value.Contains("dir-over-file-directory", StringComparison.Ordinal) =>
                    [new("stdout", "Removing old local file `collision'")],
                var value when value.Contains("mirror", StringComparison.Ordinal) &&
                    value.Contains("--dry-run", StringComparison.Ordinal) &&
                    value.Contains("unrecognized-destructive-directory", StringComparison.Ordinal) =>
                    [new("stdout", "Purging obsolete entry `collision'")],
                var value when IsAutomaticDirectoryPreview(role, value) => [],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/clean-collision-drift", StringComparison.Ordinal) &&
                    commands.Count(item => item.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) && item.Contains("/clean-collision-drift", StringComparison.Ordinal)) > 1 =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old local file `collision'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/clean-collision-drift", StringComparison.Ordinal) =>
                    [new("stdout", "Transferring file `new.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    (value.Contains("/gate-blocker", StringComparison.Ordinal) ||
                     value.Contains("/remote/mirror-link-ancestor/root", StringComparison.Ordinal) ||
                     value.Contains("/remote/post-mirror-link-ancestor/root", StringComparison.Ordinal) ||
                     value.Contains("/local-post-dryrun", StringComparison.Ordinal) ||
                     value.Contains("/local-root-drift-", StringComparison.Ordinal)) =>
                    [new("stdout", "Transferring file `new.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) &&
                    value.Contains("/drift", StringComparison.Ordinal) &&
                    commands.Count(item => item.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) && item.Contains("/drift", StringComparison.Ordinal)) > 1 =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old file `different-old.txt'")],
                var value when value.Contains("mirror --verbose=1 --dry-run", StringComparison.Ordinal) =>
                    [new("stdout", "Transferring file `new.txt'"), new("stdout", "Removing old file `old.txt'")],
                var value when value.StartsWith("cls -laB", StringComparison.Ordinal) =>
                    [new("stdout", "drwxr-xr-x 2 alice staff 0 2026-07-15 12:30 folder"), new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 曲.txt")],
                var value when value.StartsWith("cls -1 ", StringComparison.Ordinal) && value.Contains("missing.bin", StringComparison.Ordinal) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/missing-transfer-source", StringComparison.Ordinal) ||
                     value.Contains("/new-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/scheduled-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/retry-directory-target", StringComparison.Ordinal) ||
                     value.Contains("/scheduled-preview-target", StringComparison.Ordinal) ||
                     value.Contains("/retry-preview-target", StringComparison.Ordinal) ||
                     value.Contains("/guarded-drift-target", StringComparison.Ordinal)) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/directory-transfer-source", StringComparison.Ordinal) ||
                     value.Contains("/directory-destination", StringComparison.Ordinal) ||
                     value.Contains("/dir-over-file-directory", StringComparison.Ordinal) ||
                     value.Contains("/unrecognized-destructive-directory", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/link-ancestor/file.bin\"", StringComparison.Ordinal) =>
                    [new("stdout", RemoteEditListing(value))],
                var value when IsStatCommand(value) &&
                    (value.Contains("\"/remote\"", StringComparison.Ordinal) ||
                     value.Contains("\"/collision-review\"", StringComparison.Ordinal) ||
                     value.Contains("\"/drift\"", StringComparison.Ordinal) ||
                     value.Contains("\"/clean-collision-drift\"", StringComparison.Ordinal) ||
                     value.Contains("\"/gate-blocker\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-root-drift-download\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-root-drift-upload\"", StringComparison.Ordinal) ||
                     value.Contains("\"/local-post-dryrun\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/directory-link-ancestor/source\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/mirror-link-ancestor/root\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/post-directory-link-ancestor/source\"", StringComparison.Ordinal) ||
                     value.Contains("\"/remote/post-mirror-link-ancestor/root\"", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/transfer-link", StringComparison.Ordinal) =>
                    [new("stdout", RemoteSymbolicLinkListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/transfer-special", StringComparison.Ordinal) =>
                    [new("stdout", RemoteSpecialListing(value))],
                var value when IsStatCommand(value) &&
                    value.Contains("/mismatched-stat", StringComparison.Ordinal) =>
                    [new("stdout", "-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 different-name.bin")],
                var value when IsStatCommand(value) &&
                    value.Contains("/zero-line-missing-target", StringComparison.Ordinal) => [],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/pathless-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/bound-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "recls: Access failed: 550 No such file or directory. (/remote/bound-missing-target)")],
                var value when IsStatCommand(value) &&
                    value.Contains("\"/remote/wrong-bound-missing-target\"", StringComparison.Ordinal) =>
                    [new("stderr", "recls: Access failed: 550 No such file or directory. (/remote/different-target)")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/created", StringComparison.Ordinal) || value.Contains("/renamed.txt", StringComparison.Ordinal) ||
                     value.Contains("/failing-relay-target.bin", StringComparison.Ordinal) ||
                     value.Contains("/new-target.bin", StringComparison.Ordinal)) =>
                    [new("stderr", "cls: Access failed: No such file")],
                var value when IsStatCommand(value) &&
                    (value.Contains("/empty-dir", StringComparison.Ordinal) || value.Contains("/tree", StringComparison.Ordinal) ||
                     value.Contains("/source-folder", StringComparison.Ordinal)) =>
                    [new("stdout", RemoteDirectoryListing(value))],
                var value when IsStatCommand(value) =>
                    [new("stdout", RemoteEditListing(value))],
                "pwd" => [new("stdout", "/remote/home")],
                _ => [],
            };
            return Task.FromResult(new LftpCommandResult(output));
        }

        private static void WriteRemoteEditDownload(string command)
        {
            const string marker = " -o \"";
            var start = command.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("The fake remote-edit download had no managed output path.");
            start += marker.Length;
            var end = command.IndexOf('"', start);
            if (end < 0) throw new InvalidOperationException("The fake remote-edit download output path was unterminated.");
            var msysPath = command[start..end];
            var localPath = msysPath.Length >= 3 && msysPath[0] == '/' && char.IsAsciiLetter(msysPath[1]) && msysPath[2] == '/'
                ? $"{char.ToUpperInvariant(msysPath[1])}:\\{msysPath[3..].Replace('/', '\\')}"
                : msysPath.Replace('/', Path.DirectorySeparatorChar);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllBytes(localPath, Encoding.UTF8.GetBytes("remote-bytes"));
        }

        private static bool IsStatCommand(string command) =>
            command.StartsWith("recls -ldB --time-style=long-iso", StringComparison.Ordinal) ||
            command.StartsWith("cls -ldB --time-style=long-iso", StringComparison.Ordinal);

        private static bool IsAutomaticDirectoryPreview(string role, string command) =>
            (role == "directory-transfer-preview" || role == "transfer") &&
            command.Contains("mirror", StringComparison.Ordinal) &&
            command.Contains("--dry-run", StringComparison.Ordinal) &&
            !command.Contains("--parallel=", StringComparison.Ordinal);

        private static string RemoteEditListing(string command) =>
            $"-rw-r--r-- 1 alice staff 12 2026-07-15 12:34 {RemoteEntryName(command)}";

        private static string RemoteDirectoryListing(string command) =>
            $"drwxr-xr-x 2 alice staff 0 2026-07-15 12:30 {RemoteEntryName(command)}";

        private static string RemoteSymbolicLinkListing(string command) =>
            $"lrwxrwxrwx 1 alice staff 7 2026-07-15 12:30 {RemoteEntryName(command)} -> target";

        private static string RemoteSpecialListing(string command) =>
            $"prw-r--r-- 1 alice staff 0 2026-07-15 12:30 {RemoteEntryName(command)}";

        private static string RemoteEntryName(string command)
        {
            var end = command.LastIndexOf('"');
            var start = end > 0 ? command.LastIndexOf('"', end - 1) : -1;
            var remotePath = start >= 0 && end > start ? command[(start + 1)..end] : "/file.bin";
            var separator = remotePath.LastIndexOf('/');
            return separator >= 0 ? remotePath[(separator + 1)..] : remotePath;
        }

        private async Task<LftpCommandResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new([]);
            }
            finally
            {
                IsRunning = false;
            }
        }

        private static async Task<LftpCommandResult> WaitForReleaseAsync(Task release, CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            return new([]);
        }

        public Task StopAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            stoppedRoles.Add(role);
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (failDispose(role)) throw new InvalidOperationException($"Simulated disposal failure for {role}.");
            IsRunning = false;
            if (string.Equals(blockDisposeRole(), role, StringComparison.Ordinal))
            {
                disposeEntered.TrySetResult(true);
                await releaseDispose.Task;
            }
            disposedRoles.Add(role);
        }

        public void Raise(LftpOutputLine line)
        {
            OutputReceived?.Invoke(this, line);
            UnsolicitedOutput?.Invoke(this, line);
        }

        private void EmitQueueMarker(string command, string suffix, bool submission)
        {
            var marker = FindQueueMarker(command, suffix, submission);
            if (marker is not null) OutputReceived?.Invoke(this, new("stdout", marker));
        }

        private async Task EmitQueueMarkerAfterReleaseAsync(string command, Task release, CancellationToken cancellationToken)
        {
            try
            {
                await release.WaitAsync(cancellationToken);
                EmitQueueMarker(command, "_OK", submission: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private static string? FindQueueMarker(string command, string suffix, bool submission)
        {
            var offset = 0;
            while ((offset = command.IndexOf("__LFTPPILOT_QUEUE_", offset, StringComparison.Ordinal)) >= 0)
            {
                var end = offset;
                while (end < command.Length && (char.IsAsciiLetterOrDigit(command[end]) || command[end] is '_' or '-')) end++;
                var marker = command[offset..end];
                var isSubmission = marker.Contains("_SUBMIT_", StringComparison.Ordinal);
                if (isSubmission == submission && marker.EndsWith(suffix, StringComparison.Ordinal)) return marker;
                offset = end;
            }
            return null;
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LFTPPilot.WorkspaceTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, recursive: true); }
    }
}
