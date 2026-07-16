using System.Runtime.CompilerServices;
using System.Text.Json;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class LiveAgentWorkspaceClientTests
{
    [Fact]
    public async Task DisposeCancelsConnectionAdmissionBeforeDisposingCandidate()
    {
        const int processId = 101;
        var engine = new ControllableEngineClient(processId, blockPing: true);
        var client = CreateClient(engine, processId);
        try
        {
            var load = client.LoadAsync(TestContext.Current.CancellationToken);
            await engine.PingStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            await client.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await load);
            Assert.True(engine.IsDisposed);
            Assert.False(engine.DisposedWhileRequestActive);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.LoadAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeCancelsAndDrainsAdmittedRequestBeforeInnerClient()
    {
        const int processId = 202;
        var engine = new ControllableEngineClient(processId, blockPing: false);
        var client = CreateClient(engine, processId);
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            "Test",
            ConnectionProtocol.Ftp,
            "example.test",
            21,
            "user",
            AuthenticationKind.AskOnConnect);
        try
        {
            var save = client.SaveProfileAsync(profile, cancellationToken: TestContext.Current.CancellationToken);
            await engine.OperationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            await client.DisposeAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await save);
            Assert.True(engine.IsDisposed);
            Assert.False(engine.DisposedWhileRequestActive);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.SaveProfileAsync(profile, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task MutatingLostReplyIsNotRetried()
    {
        const int processId = 303;
        var engine = new LostReplyEngineClient(processId);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            "No replay",
            ConnectionProtocol.Ftp,
            "example.test",
            21,
            "user",
            AuthenticationKind.Password);
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.SaveProfileAsync(profile, "possibly-stored", TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.ProfileSave, exception.Method);
            Assert.Contains("may have reached the Agent", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("credential", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, engine.SaveAttempts);
            Assert.Equal(1, engine.PingAttempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task MismatchedSavedProfileReplyIsAnUnknownOutcome()
    {
        const int processId = 304;
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            "Exact profile",
            ConnectionProtocol.Sftp,
            "example.test",
            22,
            "user",
            AuthenticationKind.SshKey,
            @"C:\keys\id_ed25519",
            "/incoming",
            @"C:\incoming",
            ["First", "second"]);
        var mismatched = profile with { Bookmarks = ["First", "SECOND"] };
        var engine = new MutationReplyEngineClient(processId, WorkspaceMethods.ProfileSave, mismatched);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.SaveProfileAsync(profile, "possibly-stored", TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.ProfileSave, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, engine.Attempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadOnlyBootstrapLostReplyReconnectsAndRetriesOnce()
    {
        const int processId = 404;
        var engine = new LostReplyEngineClient(processId);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var workspace = await client.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Empty(workspace.Profiles);
            Assert.Empty(workspace.Sessions);
            Assert.Equal(2, engine.BootstrapAttempts);
            Assert.Equal(2, engine.PingAttempts);
            Assert.True(invalidations >= 1);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConfirmedConnectSurvivesPaneBrowseFailuresWithoutDuplicateConnect()
    {
        const int processId = 505;
        var engine = new ConfirmedConnectBrowseFailureEngineClient(processId);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            "Confirmed session",
            ConnectionProtocol.Ftp,
            "example.test",
            21,
            "user",
            AuthenticationKind.Password);
        try
        {
            var seed = await client.ConnectAsync(
                profile,
                "credential-used-by-confirmed-connect",
                TestContext.Current.CancellationToken);

            Assert.Equal(profile.Id, seed.Snapshot.ProfileId);
            Assert.True(seed.Snapshot.IsConnected);
            Assert.Empty(seed.LocalEntries);
            Assert.Empty(seed.RemoteEntries);
            Assert.Equal(1, engine.ConnectAttempts);
            Assert.Equal(2, engine.BrowseAttempts);
            Assert.True(invalidations >= 1);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisconnectedBootstrapTabsRetainOrderAndPathsWithoutAnyBrowseRequest()
    {
        const int processId = 507;
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Restored FTP", ConnectionProtocol.Ftp, "example.test", 21, "user", AuthenticationKind.AskOnConnect);
        var now = DateTimeOffset.UtcNow;
        var first = new SessionSnapshot(
            Guid.NewGuid(), profile.Id, profile.Name, false,
            new(PaneKind.Local, @"C:\First"), new(PaneKind.Remote, "/first"), now,
            new("agent-restarted", "The Agent restarted."));
        var second = new SessionSnapshot(
            Guid.NewGuid(), profile.Id, profile.Name, false,
            new(PaneKind.Local, @"C:\Second"), new(PaneKind.Remote, "/second"), now,
            new("credential-required", "Reconnect explicitly."));
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new RuntimeStatus(true, true, "test"),
            [profile],
            [second, first],
            [],
            []);
        var engine = new MutationReplyEngineClient(processId, "unused", new { }, bootstrap: bootstrap);
        var client = CreateClient(engine, processId);
        try
        {
            var workspace = await client.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal([second.SessionId, first.SessionId], workspace.Sessions.Select(static seed => seed.Snapshot.SessionId));
            Assert.Equal(@"C:\Second", workspace.Sessions[0].Snapshot.LocalLocation.Path);
            Assert.Equal("/second", workspace.Sessions[0].Snapshot.RemoteLocation.Path);
            Assert.All(workspace.Sessions, seed =>
            {
                Assert.Empty(seed.LocalEntries);
                Assert.Empty(seed.RemoteEntries);
            });
            Assert.Equal(0, engine.Attempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidDisconnectedBootstrapIdentityIsRejectedBeforeBrowse(bool duplicateSessionId)
    {
        const int processId = 508;
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Restored FTP", ConnectionProtocol.Ftp, "example.test", 21, "user", AuthenticationKind.Password);
        var now = DateTimeOffset.UtcNow;
        var first = new SessionSnapshot(
            Guid.NewGuid(), profile.Id, profile.Name, false,
            new(PaneKind.Local, @"C:\First"), new(PaneKind.Remote, "/first"), now);
        var second = first with
        {
            SessionId = duplicateSessionId ? first.SessionId : Guid.NewGuid(),
            ProfileId = duplicateSessionId ? profile.Id : Guid.NewGuid(),
        };
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new RuntimeStatus(true, true, "test"),
            [profile],
            [first, second],
            [],
            []);
        var engine = new MutationReplyEngineClient(processId, "unused", new { }, bootstrap: bootstrap);
        var client = CreateClient(engine, processId);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.LoadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, engine.Attempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExplicitReconnectSendsRestoredSessionIdAndRequiresExactIdInReply()
    {
        const int processId = 509;
        var restoredSessionId = Guid.NewGuid();
        var engine = new ConfirmedConnectBrowseFailureEngineClient(processId, returnedSessionId: restoredSessionId);
        var client = CreateClient(engine, processId);
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Restored FTP", ConnectionProtocol.Ftp, "example.test", 21, "user", AuthenticationKind.AskOnConnect);
        try
        {
            var seed = await client.ConnectAsync(
                profile,
                "once-only-secret",
                TestContext.Current.CancellationToken,
                existingSessionId: restoredSessionId);

            Assert.Equal(restoredSessionId, seed.Snapshot.SessionId);
            Assert.Equal(restoredSessionId, engine.LastConnectRequest?.ExistingSessionId);
            Assert.Equal("once-only-secret", engine.LastConnectRequest?.EphemeralCredential);
            Assert.Equal(1, engine.ConnectAttempts);
            Assert.Equal(2, engine.BrowseAttempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconnectReplyForDifferentSessionIdIsAnUnknownMutationOutcome()
    {
        const int processId = 510;
        var engine = new ConfirmedConnectBrowseFailureEngineClient(processId);
        var client = CreateClient(engine, processId);
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "Restored FTP", ConnectionProtocol.Ftp, "example.test", 21, "user", AuthenticationKind.Password);
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() => client.ConnectAsync(
                profile,
                cancellationToken: TestContext.Current.CancellationToken,
                existingSessionId: Guid.NewGuid()));

            Assert.Equal(WorkspaceMethods.SessionConnect, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, engine.ConnectAttempts);
            Assert.Equal(0, engine.BrowseAttempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task MismatchedConnectSessionIsAnUnknownOutcomeBeforePaneHydration()
    {
        const int processId = 506;
        var engine = new ConfirmedConnectBrowseFailureEngineClient(processId, mismatchedProfile: true);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        var profile = new ConnectionProfile(
            Guid.NewGuid(),
            "Mismatched session",
            ConnectionProtocol.Ftp,
            "example.test",
            21,
            "user",
            AuthenticationKind.Password);
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.ConnectAsync(profile, "credential-used-by-connect", TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.SessionConnect, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, engine.ConnectAttempts);
            Assert.Equal(0, engine.BrowseAttempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task MalformedRemoteTransferSuccessReplyIsAnUnknownOutcomeAndIsNotRetried()
    {
        const int processId = 606;
        var engine = new MalformedRemoteTransferReplyEngineClient(processId);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        var plan = new RemoteTransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/source.bin", "/target.bin", RemoteTransferMode.Fxp);
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.EnqueueRemoteTransferAsync(plan, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.RemoteTransferEnqueue, exception.Method);
            Assert.Equal(1, engine.EnqueueAttempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task WellFramedRemoteTransferRejectionHasTypedAuthoritativeOutcome()
    {
        const int processId = 607;
        var engine = new MalformedRemoteTransferReplyEngineClient(processId, reject: true);
        var client = CreateClient(engine, processId);
        var plan = new RemoteTransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/source.bin", "/target.bin", RemoteTransferMode.Fxp);
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestRejectedException>(() =>
                client.EnqueueRemoteTransferAsync(plan, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.RemoteTransferEnqueue, exception.Method);
            Assert.Contains("authoritatively rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, engine.EnqueueAttempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task MalformedOrdinaryTransferSuccessReplyIsAnUnknownOutcomeAndIsNotRetried()
    {
        const int processId = 608;
        var engine = new MutationReplyEngineClient(processId, WorkspaceMethods.TransferEnqueue, new { });
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        var profileId = Guid.NewGuid();
        var plan = new TransferPlan(Guid.NewGuid(), profileId, TransferDirection.Download, "/source.bin", @"C:\target.bin");
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.EnqueueTransferAsync(Guid.NewGuid(), plan, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.TransferEnqueue, exception.Method);
            Assert.Equal(1, engine.Attempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task CompleteMutationReplyIsPreservedWhenCallerCancelsAfterResponse()
    {
        const int processId = 610;
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = new TransferPlan(Guid.NewGuid(), profileId, TransferDirection.Download, "/source.bin", @"C:\target.bin");
        var now = DateTimeOffset.UtcNow;
        var job = new JobSnapshot(plan.Id, JobKind.Transfer, profileId, "confirmed transfer", JobState.Queued, now, now);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.TransferEnqueue,
            new TransferEnqueueResult(job),
            cancellation.Cancel);
        var client = CreateClient(engine, processId);
        try
        {
            var confirmed = await client.EnqueueTransferAsync(sessionId, plan, cancellation.Token);

            Assert.Equal(job, confirmed);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, engine.Attempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongOrdinaryTransferJobIdIsAnUnknownOutcome()
    {
        const int processId = 611;
        var profileId = Guid.NewGuid();
        var plan = new TransferPlan(Guid.NewGuid(), profileId, TransferDirection.Download, "/source.bin", @"C:\target.bin");
        var now = DateTimeOffset.UtcNow;
        var wrongJob = new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, profileId, "wrong transfer", JobState.Queued, now, now);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.TransferEnqueue,
            new TransferEnqueueResult(wrongJob));
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.EnqueueTransferAsync(Guid.NewGuid(), plan, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.TransferEnqueue, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongMirrorJobIdIsAnUnknownOutcome()
    {
        const int processId = 612;
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profileId, "reviewed mirror", MirrorDirection.Download, @"C:\mirror", "/mirror");
        var preview = new MirrorPreview(
            Guid.NewGuid(), definition.Id, now, now.AddMinutes(5), [], MirrorPlanner.Fingerprint(definition), new string('a', 64));
        var wrongJob = new JobSnapshot(Guid.NewGuid(), JobKind.Mirror, profileId, "wrong mirror", JobState.Queued, now, now);
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new RuntimeStatus(true, true, "test"),
            [],
            [new SessionSnapshot(
                sessionId,
                profileId,
                "Mirror session",
                true,
                new(PaneKind.Local, @"C:\mirror"),
                new(PaneKind.Remote, "/mirror"),
                now)],
            [],
            []);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.MirrorApprove,
            new MirrorApproveResult(wrongJob),
            bootstrap: bootstrap);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.ApproveMirrorAsync(new MirrorUiPreview(definition, preview), false, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.MirrorApprove, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("wrong-definition")]
    [InlineData("invalid-lifetime")]
    [InlineData("invalid-action")]
    [InlineData("blank-token")]
    public async Task MalformedMirrorPreviewReplyIsReadOnlyInvalidData(string defect)
    {
        const int processId = 615;
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profileId, "reviewed mirror", MirrorDirection.Download, @"C:\mirror", "/mirror");
        var preview = new MirrorPreview(
            Guid.NewGuid(),
            definition.Id,
            now,
            now.AddMinutes(5),
            [],
            MirrorPlanner.Fingerprint(definition),
            "approval-token");
        var malformed = defect switch
        {
            "empty-id" => preview with { Id = Guid.Empty },
            "wrong-definition" => preview with { DefinitionId = Guid.NewGuid() },
            "invalid-lifetime" => preview with { ExpiresAt = preview.GeneratedAt },
            "invalid-action" => preview with { Actions = [new((MirrorActionKind)int.MaxValue, string.Empty)] },
            "blank-token" => preview with { ApprovalToken = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new RuntimeStatus(true, true, "test"),
            [],
            [new SessionSnapshot(
                sessionId,
                profileId,
                "Mirror session",
                true,
                new(PaneKind.Local, @"C:\mirror"),
                new(PaneKind.Remote, "/mirror"),
                now)],
            [],
            []);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.MirrorPreview,
            malformed,
            bootstrap: bootstrap);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.PreviewMirrorAsync(definition, TestContext.Current.CancellationToken));

            Assert.Equal(1, engine.Attempts);
            Assert.Equal(0, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExpiredMirrorApprovalStillDispatchesForAuthoritativeAgentRejection()
    {
        const int processId = 616;
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var definition = new MirrorDefinition(
            Guid.NewGuid(), profileId, "expired mirror", MirrorDirection.Download, @"C:\mirror", "/mirror");
        var preview = new MirrorPreview(
            Guid.NewGuid(),
            definition.Id,
            now.AddMinutes(-6),
            now.AddMinutes(-1),
            [],
            MirrorPlanner.Fingerprint(definition),
            "approval-token");
        var bootstrap = new WorkspaceBootstrap(
            AgentProtocol.CurrentVersion,
            new RuntimeStatus(true, true, "test"),
            [],
            [new SessionSnapshot(
                sessionId,
                profileId,
                "Mirror session",
                true,
                new(PaneKind.Local, @"C:\mirror"),
                new(PaneKind.Remote, "/mirror"),
                now)],
            [],
            []);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.MirrorApprove,
            new { },
            bootstrap: bootstrap,
            requestFailure: new EngineRequestRejectedException(
                WorkspaceMethods.MirrorApprove,
                new ProtocolError("InvalidOperationException", "The mirror preview is stale.")));
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestRejectedException>(() =>
                client.ApproveMirrorAsync(new MirrorUiPreview(definition, preview), false, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.MirrorApprove, exception.Method);
            Assert.Contains("stale", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, engine.Attempts);
            Assert.Equal(0, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongRemoteTransferJobIdIsAnUnknownOutcome()
    {
        const int processId = 613;
        var plan = new RemoteTransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/source.bin", "/target.bin", RemoteTransferMode.Fxp);
        var now = DateTimeOffset.UtcNow;
        var wrongJob = new JobSnapshot(
            Guid.NewGuid(), JobKind.RemoteTransfer, plan.SourceProfileId, "wrong remote transfer", JobState.Queued, now, now);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.RemoteTransferEnqueue,
            new RemoteTransferEnqueueResult(wrongJob, plan.Mode, "route"));
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.EnqueueRemoteTransferAsync(plan, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.RemoteTransferEnqueue, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("source-profile")]
    [InlineData("destination-profile")]
    [InlineData("same-profiles")]
    [InlineData("source-path")]
    [InlineData("destination-path")]
    [InlineData("overwrite")]
    [InlineData("mode")]
    public async Task RemoteTransferPlanResponseMustBeValidAndExactlyMatchRequest(string mismatch)
    {
        const int processId = 615;
        var request = new RemoteTransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/source.bin", "/target.bin", RemoteTransferMode.ClientRelay, Overwrite: true);
        var response = new RemoteTransferPlan(
            Guid.NewGuid(), request.SourceProfileId, request.DestinationProfileId,
            request.SourcePath, request.DestinationPath, RemoteTransferMode.Fxp, request.Overwrite);
        response = mismatch switch
        {
            "empty-id" => response with { Id = Guid.Empty },
            "source-profile" => response with { SourceProfileId = Guid.NewGuid() },
            "destination-profile" => response with { DestinationProfileId = Guid.NewGuid() },
            "same-profiles" => response with { DestinationProfileId = response.SourceProfileId },
            "source-path" => response with { SourcePath = "/different-source.bin" },
            "destination-path" => response with { DestinationPath = "/different-target.bin" },
            "overwrite" => response with { Overwrite = !response.Overwrite },
            "mode" => response with { Mode = (RemoteTransferMode)99 },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.RemoteTransferPlan,
            response);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.PlanRemoteTransferAsync(request, TestContext.Current.CancellationToken));

            Assert.Contains("remote-transfer", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, engine.Attempts);
            Assert.Equal(0, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExactRemoteTransferPlanResponseIsAccepted()
    {
        const int processId = 616;
        var request = new RemoteTransferPlan(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/source.bin", "/target.bin", RemoteTransferMode.ClientRelay, Overwrite: true);
        var response = request with { Id = Guid.NewGuid(), Mode = RemoteTransferMode.Fxp };
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.RemoteTransferPlan,
            response);
        var client = CreateClient(engine, processId);
        try
        {
            var actual = await client.PlanRemoteTransferAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(response, actual);
            Assert.Equal(1, engine.Attempts);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task WrongRetryJobIdIsAnUnknownOutcome()
    {
        const int processId = 614;
        var requestedJobId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var wrongJob = new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, profileId, "wrong retry", JobState.Queued, now, now);
        var engine = new MutationReplyEngineClient(
            processId,
            WorkspaceMethods.JobRetry,
            new JobRetryResult(wrongJob));
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.RetryJobAsync(requestedJobId, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.JobRetry, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task HostKeyApprovalSemanticMismatchIsAnUnknownOutcome()
    {
        const int processId = 609;
        var profileId = Guid.NewGuid();
        var fingerprint = "SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=');
        var review = new SftpHostKeyReview(
            Guid.NewGuid(),
            profileId,
            "sftp://example.test:22",
            SftpHostKeyState.EnrollmentRequired,
            "ssh-ed25519",
            fingerprint,
            null,
            null,
            DateTimeOffset.UtcNow.AddMinutes(5),
            new string('a', 64));
        var mismatched = new SftpHostKeyApproveResult(Guid.NewGuid(), review.Endpoint, fingerprint);
        var engine = new MutationReplyEngineClient(processId, WorkspaceMethods.SftpHostKeyApprove, mismatched);
        var client = CreateClient(engine, processId);
        var invalidations = 0;
        client.StateInvalidated += (_, _) => invalidations++;
        try
        {
            var exception = await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
                client.ApproveSftpHostKeyAsync(review, replaceExisting: false, TestContext.Current.CancellationToken));

            Assert.Equal(WorkspaceMethods.SftpHostKeyApprove, exception.Method);
            Assert.IsType<InvalidDataException>(exception.InnerException);
            Assert.Equal(1, engine.Attempts);
            Assert.Equal(1, invalidations);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    private static LiveAgentWorkspaceClient CreateClient(IEngineClient engine, int processId) =>
        new(
            new StubUpdateService(),
            () => [processId],
            () => throw new InvalidOperationException("The test Agent should already be discoverable."),
            _ => { },
            _ => engine);

    private sealed class LostReplyEngineClient(int processId) : IEngineClient
    {
        private int _bootstrapAttempts;
        private int _pingAttempts;
        private int _saveAttempts;
        private int _disposed;

        public int BootstrapAttempts => Volatile.Read(ref _bootstrapAttempts);
        public int PingAttempts => Volatile.Read(ref _pingAttempts);
        public int SaveAttempts => Volatile.Read(ref _saveAttempts);

        public Task<JsonElement> RequestAsync(
            string method,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(method, "ping", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pingAttempts);
                return Task.FromResult(JsonSerializer.SerializeToElement(
                    new
                    {
                        protocolVersion = AgentProtocol.CurrentVersion,
                        processId,
                        clientProcessId = Environment.ProcessId,
                    },
                    FramedJsonStream.SerializerOptions));
            }

            if (string.Equals(method, WorkspaceMethods.Bootstrap, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _bootstrapAttempts) == 1)
                    return Task.FromException<JsonElement>(new IOException("The read-only reply was lost."));
                var bootstrap = new WorkspaceBootstrap(
                    AgentProtocol.CurrentVersion,
                    new RuntimeStatus(true, true, "test"),
                    [],
                    [],
                    [],
                    []);
                return Task.FromResult(JsonSerializer.SerializeToElement(bootstrap, FramedJsonStream.SerializerOptions));
            }

            if (string.Equals(method, WorkspaceMethods.ProfileSave, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _saveAttempts);
                return Task.FromException<JsonElement>(new IOException("The mutating reply was lost after execution."));
            }

            return Task.FromException<JsonElement>(new NotSupportedException($"Unexpected test method: {method}"));
        }

        public async IAsyncEnumerable<EngineEvent> Events(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConfirmedConnectBrowseFailureEngineClient(
        int processId,
        bool mismatchedProfile = false,
        Guid? returnedSessionId = null) : IEngineClient
    {
        private readonly Guid _sessionId = returnedSessionId ?? Guid.NewGuid();
        private int _browseAttempts;
        private int _connectAttempts;
        private int _disposed;

        public int BrowseAttempts => Volatile.Read(ref _browseAttempts);
        public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
        public SessionConnectRequest? LastConnectRequest { get; private set; }

        public Task<JsonElement> RequestAsync(
            string method,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(method, "ping", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(
                    new
                    {
                        protocolVersion = AgentProtocol.CurrentVersion,
                        processId,
                        clientProcessId = Environment.ProcessId,
                    },
                    FramedJsonStream.SerializerOptions));
            }

            if (string.Equals(method, WorkspaceMethods.SessionConnect, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _connectAttempts);
                var request = Assert.IsType<SessionConnectRequest>(payload);
                LastConnectRequest = request;
                var snapshot = new SessionSnapshot(
                    _sessionId,
                    mismatchedProfile ? Guid.NewGuid() : request.ExpectedIdentity.ProfileId,
                    "Confirmed session",
                    true,
                    new(PaneKind.Local, @"C:\Users\Test"),
                    new(PaneKind.Remote, "/"),
                    DateTimeOffset.UtcNow);
                return Task.FromResult(JsonSerializer.SerializeToElement(snapshot, FramedJsonStream.SerializerOptions));
            }

            if (method is WorkspaceMethods.BrowseLocal or WorkspaceMethods.BrowseRemote)
            {
                Interlocked.Increment(ref _browseAttempts);
                return Task.FromException<JsonElement>(new IOException("The pane reply failed after the session was confirmed."));
            }

            return Task.FromException<JsonElement>(new NotSupportedException($"Unexpected test method: {method}"));
        }

        public async IAsyncEnumerable<EngineEvent> Events(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MalformedRemoteTransferReplyEngineClient(int processId, bool reject = false) : IEngineClient
    {
        private int _enqueueAttempts;
        public int EnqueueAttempts => Volatile.Read(ref _enqueueAttempts);

        public Task<JsonElement> RequestAsync(
            string method,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(method, "ping", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = AgentProtocol.CurrentVersion,
                    processId,
                    clientProcessId = Environment.ProcessId,
                }, FramedJsonStream.SerializerOptions));
            }
            if (string.Equals(method, WorkspaceMethods.RemoteTransferEnqueue, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _enqueueAttempts);
                if (reject)
                    return Task.FromException<JsonElement>(new EngineRequestRejectedException(
                        method,
                        new ProtocolError("InvalidOperationException", "The Agent authoritatively rejected this plan.")));
                return Task.FromResult(JsonSerializer.SerializeToElement("malformed success reply"));
            }
            return Task.FromException<JsonElement>(new NotSupportedException($"Unexpected test method: {method}"));
        }

        public async IAsyncEnumerable<EngineEvent> Events(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutationReplyEngineClient(
        int processId,
        string mutationMethod,
        object reply,
        Action? responseCompleted = null,
        WorkspaceBootstrap? bootstrap = null,
        Exception? requestFailure = null) : IEngineClient
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public Task<JsonElement> RequestAsync(
            string method,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(method, "ping", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = AgentProtocol.CurrentVersion,
                    processId,
                    clientProcessId = Environment.ProcessId,
                }, FramedJsonStream.SerializerOptions));
            }
            if (string.Equals(method, WorkspaceMethods.Bootstrap, StringComparison.Ordinal) && bootstrap is not null)
                return Task.FromResult(JsonSerializer.SerializeToElement(bootstrap, FramedJsonStream.SerializerOptions));
            if (string.Equals(method, mutationMethod, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _attempts);
                if (requestFailure is not null) return Task.FromException<JsonElement>(requestFailure);
                var element = JsonSerializer.SerializeToElement(reply, FramedJsonStream.SerializerOptions);
                responseCompleted?.Invoke();
                return Task.FromResult(element);
            }
            return Task.FromException<JsonElement>(new NotSupportedException($"Unexpected test method: {method}"));
        }

        public async IAsyncEnumerable<EngineEvent> Events(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ControllableEngineClient(int processId, bool blockPing) : IEngineClient
    {
        private int _activeRequests;
        private int _disposed;

        public TaskCompletionSource<bool> PingStarted { get; } = NewSignal();
        public TaskCompletionSource<bool> OperationStarted { get; } = NewSignal();
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool DisposedWhileRequestActive { get; private set; }

        public async Task<JsonElement> RequestAsync(
            string method,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Interlocked.Increment(ref _activeRequests);
            try
            {
                if (string.Equals(method, "ping", StringComparison.Ordinal))
                {
                    PingStarted.TrySetResult(true);
                    if (blockPing) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return JsonSerializer.SerializeToElement(
                        new
                        {
                            protocolVersion = AgentProtocol.CurrentVersion,
                            processId,
                            clientProcessId = Environment.ProcessId,
                        },
                        FramedJsonStream.SerializerOptions);
                }

                OperationStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking test request unexpectedly completed.");
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        public async IAsyncEnumerable<EngineEvent> Events(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            DisposedWhileRequestActive = Volatile.Read(ref _activeRequests) != 0;
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class StubUpdateService : IAppUpdateService
    {
        public Task<AppUpdateStatus> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppUpdateStatus(new Version(1, 0), UpdateAvailability.Current));

        public Task OpenInstallerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
