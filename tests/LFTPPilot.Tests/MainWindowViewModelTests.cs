using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task FirstLaunchShowsBrowsableLocalWorkspaceWithoutProfileOrSession()
    {
        var profile = Profile();
        var localEntry = new FileEntry(
            "Documents",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"),
            EntryKind.Directory,
            null,
            DateTimeOffset.UtcNow);
        var agent = new RecordingSessionAgent(profile, []);
        agent.OfflineLocalEntries.Add(localEntry);
        agent.CommitProfileDeleteAndPublish();
        var viewModel = CreateViewModelWithoutUiContext(agent);

        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasNoSessions);
        Assert.False(viewModel.HasSessions);
        Assert.Empty(viewModel.Connections.Profiles);
        Assert.Equal(Guid.Empty, viewModel.OfflineLocalPane.SessionId);
        Assert.False(viewModel.OfflineLocalPane.SupportsTransfers);
        Assert.Equal(localEntry, Assert.Single(viewModel.OfflineLocalPane.Entries).Entry);
        Assert.Single(agent.OfflineBrowseRequests);
        Assert.Contains("Local files are ready", viewModel.OfflineWorkspaceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectFailureAfterMutationReportsUnknownStateAndRequestsResync()
    {
        var profile = new ConnectionProfile(
            Guid.NewGuid(), "FTP test", ConnectionProtocol.Ftp, "example.test", 21, "alice", AuthenticationKind.Password);
        var snapshot = new SessionSnapshot(
            Guid.NewGuid(), profile.Id, profile.Name, true,
            new(PaneKind.Local, @"C:\Users\Test"),
            new(PaneKind.Remote, "/"),
            DateTimeOffset.UtcNow);
        var seed = new WorkspaceSessionSeed(snapshot, [], []);
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            ThrowAfterDisconnectMutation = true,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);

        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.CloseSessionAsync(session));

        Assert.Equal("sessions removed before reply serialization failed", exception.Message);
        Assert.True(agent.DisconnectMutationApplied);
        Assert.Same(session, Assert.Single(viewModel.Sessions));
        Assert.Contains("could not be confirmed", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Session state may have changed", viewModel.AgentStatus, StringComparison.Ordinal);
        Assert.Contains(exception.Message, viewModel.AgentStatus, StringComparison.Ordinal);

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.Empty(viewModel.Sessions);
    }

    [Fact]
    public async Task BootstrapRestoresDistinctSameProfileSessionsAndBothCanDisconnect()
    {
        var profile = Profile();
        var firstSeed = Seed(profile);
        var secondSeed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [firstSeed, secondSeed]);
        var viewModel = CreateViewModelWithoutUiContext(agent);

        await viewModel.InitializeAsync();

        Assert.Equal(
            [firstSeed.Snapshot.SessionId, secondSeed.Snapshot.SessionId],
            viewModel.Sessions.Select(static session => session.SessionId));
        Assert.True(viewModel.HasSessions);
        Assert.False(viewModel.HasNoSessions);

        await viewModel.CloseSessionAsync(viewModel.Sessions[0]);
        Assert.Equal(secondSeed.Snapshot.SessionId, Assert.Single(viewModel.Sessions).SessionId);
        await viewModel.CloseSessionAsync(viewModel.Sessions[0]);

        Assert.Empty(viewModel.Sessions);
        Assert.False(viewModel.HasSessions);
        Assert.True(viewModel.HasNoSessions);
        Assert.Single(agent.OfflineBrowseRequests);
        Assert.Equal(
            [firstSeed.Snapshot.SessionId, secondSeed.Snapshot.SessionId],
            agent.DisconnectedSessionIds);
    }

    [Fact]
    public async Task NewConnectAddsSecondSameProfileSessionAndBothCanDisconnect()
    {
        var profile = Profile();
        var restoredSeed = Seed(profile);
        var connectedSeed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [restoredSeed]);
        agent.ConnectResults.Enqueue(connectedSeed);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();

        await viewModel.AddDefaultSessionAsync();

        Assert.Equal(
            [restoredSeed.Snapshot.SessionId, connectedSeed.Snapshot.SessionId],
            viewModel.Sessions.Select(static session => session.SessionId));
        Assert.Equal(connectedSeed.Snapshot.SessionId, viewModel.SelectedSession?.SessionId);

        await viewModel.CloseSessionAsync(viewModel.Sessions[1]);
        await viewModel.CloseSessionAsync(viewModel.Sessions[0]);

        Assert.Empty(viewModel.Sessions);
        Assert.Equal(
            [connectedSeed.Snapshot.SessionId, restoredSeed.Snapshot.SessionId],
            agent.DisconnectedSessionIds);
    }

    [Fact]
    public async Task RepeatedBootstrapPreservesSelectedTabInstancesAndAuthoritativeOrderAndPaths()
    {
        var profile = Profile();
        var first = Seed(profile, localPath: @"C:\First", remotePath: "/first");
        var second = Seed(profile, localPath: @"C:\Second", remotePath: "/second");
        var agent = new RecordingSessionAgent(profile, [first, second]);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var firstViewModel = viewModel.Sessions[0];
        var secondViewModel = viewModel.Sessions[1];
        viewModel.SelectedSession = firstViewModel;

        agent.ReplaceSessions([
            second with
            {
                Snapshot = second.Snapshot with
                {
                    LocalLocation = new(PaneKind.Local, @"C:\Second\Updated"),
                    RemoteLocation = new(PaneKind.Remote, "/second/updated"),
                },
            },
            first,
        ]);
        await viewModel.InitializeAsync();

        Assert.Equal(
            [second.Snapshot.SessionId, first.Snapshot.SessionId],
            viewModel.Sessions.Select(static session => session.SessionId));
        Assert.Same(secondViewModel, viewModel.Sessions[0]);
        Assert.Same(firstViewModel, viewModel.Sessions[1]);
        Assert.Same(firstViewModel, viewModel.SelectedSession);
        Assert.Equal(@"C:\Second\Updated", secondViewModel.LocalPane.Path);
        Assert.Equal("/second/updated", secondViewModel.RemotePane.Path);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Sessions.Count);
        Assert.Same(secondViewModel, viewModel.Sessions[0]);
        Assert.Same(firstViewModel, viewModel.Sessions[1]);
    }

    [Fact]
    public async Task DisconnectedAskOnConnectTabIsInertUntilExplicitSameIdReconnect()
    {
        var profile = Profile() with { Authentication = AuthenticationKind.AskOnConnect };
        var sessionId = Guid.NewGuid();
        var disconnected = Seed(
            profile,
            includeRemoteFile: true,
            sessionId: sessionId,
            isConnected: false,
            localPath: @"C:\Remembered\Local",
            remotePath: "/remembered/remote",
            error: new("agent-restarted", "The Agent restarted before this tab could reconnect."));
        var connected = Seed(
            profile,
            includeRemoteFile: true,
            sessionId: sessionId,
            localPath: @"C:\Remembered\Local",
            remotePath: "/remembered/remote");
        var agent = new RecordingSessionAgent(profile, [disconnected]);
        agent.ConnectResults.Enqueue(connected);
        var viewModel = CreateViewModelWithoutUiContext(agent);

        await viewModel.InitializeAsync();

        var session = Assert.Single(viewModel.Sessions);
        Assert.False(session.IsConnected);
        Assert.True(session.RequiresCredentialForReconnect);
        Assert.Equal(@"C:\Remembered\Local", session.LocalPane.Path);
        Assert.Equal("/remembered/remote", session.RemotePane.Path);
        Assert.False(session.UploadCommand.CanExecute(null));
        Assert.False(session.DownloadCommand.CanExecute(null));
        Assert.False(session.RefreshCommand.CanExecute(null));
        Assert.Empty(viewModel.Console.Sessions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remembered/remote/file.bin", TransferSourceKind.File)]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.ReconnectAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(agent.ConnectRequests);

        await session.ReconnectAsync("once-only-secret", TestContext.Current.CancellationToken);

        var request = Assert.Single(agent.ConnectRequests);
        Assert.Equal(sessionId, request.ExistingSessionId);
        Assert.Equal("once-only-secret", request.EphemeralCredential);
        Assert.True(session.IsConnected);
        Assert.True(session.RefreshCommand.CanExecute(null));
        Assert.Same(session, Assert.Single(viewModel.Console.Sessions));
        Assert.Equal(sessionId, Assert.Single(viewModel.Sessions).SessionId);
    }

    [Fact]
    public async Task NewTabConnectMutationFailureDoesNotEscapeAndRequestsResync()
    {
        var profile = Profile();
        var restoredSeed = Seed(profile);
        var possiblyConnectedSeed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [restoredSeed])
        {
            ThrowAfterConnectMutation = true,
        };
        agent.ConnectResults.Enqueue(possiblyConnectedSeed);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();

        await viewModel.AddDefaultSessionAsync();

        Assert.False(viewModel.HasAgentError);
        Assert.Contains("New session could not be confirmed", viewModel.AgentStatus, StringComparison.Ordinal);
        Assert.Contains("connect completed before reply serialization failed", viewModel.AgentStatus, StringComparison.Ordinal);
        Assert.Equal(restoredSeed.Snapshot.SessionId, Assert.Single(viewModel.Sessions).SessionId);

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.Equal(
            [restoredSeed.Snapshot.SessionId, possiblyConnectedSeed.Snapshot.SessionId],
            viewModel.Sessions.Select(static session => session.SessionId));
    }

    [Fact]
    public async Task RequestedProfileConnectMutationFailureKeepsBootstrapAndRequestsResync()
    {
        var profile = Profile();
        var possiblyConnectedSeed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [])
        {
            ThrowAfterConnectMutation = true,
        };
        agent.ConnectResults.Enqueue(possiblyConnectedSeed);
        var viewModel = CreateViewModelWithoutUiContext(agent);

        await viewModel.InitializeAsync(profile.Id);

        Assert.False(viewModel.HasAgentError);
        Assert.False(viewModel.IsLoading);
        Assert.Equal(profile.Id, Assert.Single(viewModel.Connections.Profiles).Id);
        Assert.Empty(viewModel.Sessions);
        Assert.Contains("Requested connection could not be confirmed", viewModel.AgentStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent unavailable", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect completed before reply serialization failed", viewModel.AgentStatus, StringComparison.Ordinal);

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.Equal(possiblyConnectedSeed.Snapshot.SessionId, Assert.Single(viewModel.Sessions).SessionId);
    }

    [Fact]
    public async Task RemoteEditResolveMutationFailureReportsUnknownOutcomeAndRequestsResync()
    {
        var profile = Profile();
        var now = DateTimeOffset.UtcNow;
        var baseline = new RemoteFileIdentity("/remote/file.txt", 12, now.AddMinutes(-2), new string('a', 64));
        var initialEdit = new RemoteEditSession(
            "edit-1", Guid.NewGuid(), "file.txt", "/remote/file.txt", @"C:\cache\file.txt", baseline,
            Dirty: true, LastLocalChangeAt: now.AddMinutes(-1));
        var updatedEdit = initialEdit with
        {
            Baseline = baseline with { Size = 24, ModifiedAt = now, ContentSha256 = new string('b', 64) },
            Dirty = false,
        };
        var agent = new RecordingSessionAgent(profile, []);
        agent.RemoteEdits.Add(initialEdit);
        agent.ResolveMutation = updatedEdit;
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            viewModel.ResolveRemoteEditAsync(initialEdit.EditId, "review-token", RemoteEditResolution.Upload));

        Assert.Equal("upload committed before reply serialization failed", exception.Message);
        Assert.False(viewModel.HasAgentError);
        Assert.True(Assert.Single(viewModel.ActiveRemoteEdits).Dirty);
        Assert.Contains("Remote edit action could not be confirmed", viewModel.AgentStatus, StringComparison.Ordinal);
        Assert.Contains("remote file or managed copy may have changed", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(exception.Message, viewModel.AgentStatus, StringComparison.Ordinal);

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        var refreshed = Assert.Single(viewModel.ActiveRemoteEdits);
        Assert.False(refreshed.Dirty);
        Assert.Equal(24, refreshed.Snapshot.Baseline.Size);
    }

    [Fact]
    public async Task ConfirmedProfileDeleteRefreshRemovesDisconnectedSessionTabs()
    {
        var profile = Profile();
        var seed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [seed]);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var refreshRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Connections.StateRefreshRequested += (_, _) => refreshRequested.TrySetResult(true);

        viewModel.Connections.DeleteCommand.Execute(null);
        await refreshRequested.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(agent.ProfileDeleted);
        Assert.Empty(viewModel.Connections.Profiles);
        Assert.Single(viewModel.Sessions);

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.Empty(viewModel.Connections.Profiles);
        Assert.Empty(viewModel.Sessions);
    }

    [Fact]
    public async Task CommittedProfileDeletedEventRequestsPostCommitRefresh()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)]);
        var viewModel = CreateViewModelWithInlineUiContext(agent);
        await viewModel.InitializeAsync();

        agent.CommitProfileDeleteAndPublish();
        await WaitUntilAsync(() => agent.LoadCount >= 2 && viewModel.Sessions.Count == 0);

        Assert.Empty(viewModel.Connections.Profiles);
        Assert.Empty(viewModel.Sessions);
    }

    [Fact]
    public async Task CommittedProfileSaveRefreshRemovesIdentityInvalidatedRestoredTab()
    {
        var profile = Profile();
        var restored = Seed(profile, isConnected: false);
        var agent = new RecordingSessionAgent(profile, [restored]);
        var viewModel = CreateViewModelWithInlineUiContext(agent);
        await viewModel.InitializeAsync();

        agent.CommitProfileIdentityChangeAndPublish(profile with { Host = "changed.example.test" });
        await WaitUntilAsync(() => agent.LoadCount >= 2 && viewModel.Sessions.Count == 0);

        Assert.Empty(viewModel.Sessions);
    }

    [Fact]
    public async Task LateSessionConnectedEventRequestsSecondRefreshAndRevealsSession()
    {
        var profile = Profile();
        var restoredSeed = Seed(profile);
        var lateSeed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [restoredSeed])
        {
            DeferConnectMutationUntilPublish = true,
        };
        agent.ConnectResults.Enqueue(lateSeed);
        var viewModel = CreateViewModelWithInlineUiContext(agent);
        await viewModel.InitializeAsync();

        await viewModel.AddDefaultSessionAsync();
        await WaitUntilAsync(() => agent.LoadCount >= 2);

        Assert.Equal(restoredSeed.Snapshot.SessionId, Assert.Single(viewModel.Sessions).SessionId);

        agent.CommitPendingConnectAndPublish();
        await WaitUntilAsync(() => agent.LoadCount >= 3 && viewModel.Sessions.Count == 2);

        Assert.Equal(
            [restoredSeed.Snapshot.SessionId, lateSeed.Snapshot.SessionId],
            viewModel.Sessions.Select(static session => session.SessionId));
    }

    [Fact]
    public async Task RemoteTransferUnknownOutcomeRequestsMainWindowRefresh()
    {
        var profile = Profile();
        var destination = Profile() with { Name = "Destination FTP" };
        var agent = new RecordingSessionAgent(profile, [])
        {
            ThrowRemoteTransferOutcomeUnknown = true,
        };
        agent.AdditionalProfiles.Add(destination);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();

        viewModel.RemoteTransfer.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RemoteTransfer.HasPlan);
        viewModel.RemoteTransfer.RouteApproved = true;
        viewModel.RemoteTransfer.EnqueueCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.RemoteTransfer.Status.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase));

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.False(viewModel.RemoteTransfer.HasPlan);
        Assert.True(viewModel.RemoteTransfer.CanPlan);
        Assert.Equal(2, agent.RemoteTransferEnqueuePlanIds.Count);
        Assert.Single(agent.RemoteTransferEnqueuePlanIds.Distinct());
    }

    [Fact]
    public async Task TransferUnknownOutcomeSurvivesSessionRebuildAndReconcilesOnceWithSamePlanId()
    {
        var profile = Profile();
        var seed = Seed(profile, includeRemoteFile: true);
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            TransferUnknownResponses = 1,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);
        session.RemotePane.SelectedEntries.Add(Assert.Single(session.RemotePane.Entries));
        Assert.True(session.DownloadCommand.CanExecute(null));

        var exception = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));

        Assert.True(exception.HasUnknownOutcome);
        Assert.Equal(1, viewModel.UnconfirmedTransferCount);
        Assert.True(session.HasUnconfirmedTransfers);
        Assert.False(session.DownloadCommand.CanExecute(null));

        await viewModel.EnsureStateCurrentAsync();

        Assert.Equal(2, agent.LoadCount);
        Assert.Equal(2, agent.TransferEnqueuePlanIds.Count);
        Assert.Single(agent.TransferEnqueuePlanIds.Distinct());
        Assert.Equal(agent.TransferEnqueuePlanIds[0], Assert.Single(agent.Jobs).Id);
        Assert.Equal(0, viewModel.UnconfirmedTransferCount);
        Assert.False(Assert.Single(viewModel.Sessions).HasUnconfirmedTransfers);
    }

    [Fact]
    public async Task FolderTransferOptionsFlowIntoTheExactAgentPlan()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)]);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);
        var options = new TransferUiOptions(
            TransferMode.Resume,
            DownloadSegments: 8,
            RateLimitBytesPerSecond: 4096,
            RunAt: null,
            Includes: ["*.zip", "docs/**"],
            Excludes: ["cache/**"],
            ParallelFiles: 6);

        await session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/folder", TransferSourceKind.Directory)],
            options);

        var plan = Assert.Single(agent.TransferPlans);
        Assert.Equal(TransferSourceKind.Directory, plan.SourceKind);
        Assert.Equal(8, plan.Segments);
        Assert.Equal(6, plan.ParallelFiles);
        Assert.Equal(options.Includes, plan.EffectiveIncludes);
        Assert.Equal(options.Excludes, plan.EffectiveExcludes);
        Assert.Equal(4096, plan.RateLimitBytesPerSecond);

        await session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.zip", TransferSourceKind.File)],
            options);
        var filePlan = agent.TransferPlans[1];
        Assert.Empty(filePlan.EffectiveIncludes);
        Assert.Empty(filePlan.EffectiveExcludes);
        Assert.Equal(1, filePlan.ParallelFiles);
    }

    [Fact]
    public async Task UnknownFolderPresetMutationRequestsAuthoritativeStateRefresh()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)])
        {
            ThrowFolderPresetOutcomeUnknown = true,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);
        var refreshRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateRefreshRequested += (_, _) => refreshRequested.TrySetResult();

        await Assert.ThrowsAsync<AgentRequestOutcomeUnknownException>(() =>
            session.SaveFolderTransferPresetAsync(new(Guid.NewGuid(), "Release", ["*.msix"])));
        await refreshRequested.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Empty(session.FolderTransferPresets);
    }

    [Fact]
    public async Task TransferReconciliationNeverLoopsOrCreatesFreshIdAfterSecondUnknownOutcome()
    {
        var profile = Profile();
        var seed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            TransferUnknownResponses = int.MaxValue,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);

        _ = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));
        await viewModel.EnsureStateCurrentAsync();
        await viewModel.InitializeAsync();

        Assert.Equal(2, agent.TransferEnqueuePlanIds.Count);
        Assert.Single(agent.TransferEnqueuePlanIds.Distinct());
        Assert.Equal(1, viewModel.UnconfirmedTransferCount);
        Assert.True(Assert.Single(viewModel.Sessions).HasUnconfirmedTransfers);
        Assert.Contains("one same-ID reconciliation attempt", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchingBootstrapJobConfirmsUnknownTransferWithoutResubmission()
    {
        var profile = Profile();
        var seed = Seed(profile);
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            TransferUnknownResponses = 1,
            CommitTransferBeforeUnknownReply = true,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);

        _ = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));
        await viewModel.EnsureStateCurrentAsync();

        Assert.Single(agent.TransferEnqueuePlanIds);
        Assert.Equal(agent.TransferEnqueuePlanIds[0], Assert.Single(agent.Jobs).Id);
        Assert.Equal(0, viewModel.UnconfirmedTransferCount);
        Assert.Contains("refreshed workspace confirms", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmedFailedTransferIsAddedToActivityWithoutBecomingUncertainOrFreshRetry()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)])
        {
            ReturnFailedTransfer = true,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);

        var exception = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));

        Assert.False(exception.HasUnknownOutcome);
        Assert.Equal(1, exception.Result.ConfirmedTerminalCount);
        Assert.Contains("only through Activity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, viewModel.UnconfirmedTransferCount);
        Assert.False(session.HasUnconfirmedTransfers);
        Assert.Equal(JobState.Failed, Assert.Single(viewModel.Activity.Jobs).State);
        Assert.Single(agent.TransferEnqueuePlanIds);
    }

    [Fact]
    public async Task SameIdBootstrapJobWithWrongKindOrProfileCannotConfirmUncertainTransfer()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)])
        {
            TransferUnknownResponses = 1,
            CommitTransferBeforeUnknownReply = true,
            CommitWrongTransferIdentity = true,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);

        _ = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));
        await viewModel.EnsureStateCurrentAsync();

        Assert.Single(agent.TransferEnqueuePlanIds);
        Assert.Equal(1, viewModel.UnconfirmedTransferCount);
        Assert.True(Assert.Single(viewModel.Sessions).HasUnconfirmedTransfers);
        Assert.Contains("does not match", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(viewModel.Activity.Log, entry =>
            entry.Source == "Transfer reconciliation" && entry.Message.Contains("expected Transfer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobEventMustMatchTransferKindAndProfileBeforeResolvingUncertainPlan()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, [Seed(profile)])
        {
            TransferUnknownResponses = int.MaxValue,
        };
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var session = Assert.Single(viewModel.Sessions);
        _ = await Assert.ThrowsAsync<TransferQueueException>(() => session.QueueSourcesAsync(
            TransferDirection.Download,
            [new("/remote/file.bin", TransferSourceKind.File)]));
        var plan = Assert.Single(agent.TransferPlans);
        var now = DateTimeOffset.UtcNow;
        var applyEvent = typeof(MainWindowViewModel).GetMethod(
            "ApplyAgentEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Agent event application method was not found.");
        var wrong = new JobSnapshot(
            plan.Id, JobKind.Mirror, Guid.NewGuid(), "collision", JobState.Failed, now, now,
            Error: new("collision", "wrong identity"));

        applyEvent.Invoke(viewModel, [new EngineEvent(
            10, EngineEventKind.Job, "job.changed", now, wrong, wrong.Id)]);

        Assert.Equal(1, viewModel.UnconfirmedTransferCount);
        Assert.True(session.HasUnconfirmedTransfers);
        Assert.Contains("does not match", viewModel.AgentStatus, StringComparison.OrdinalIgnoreCase);

        var matching = wrong with
        {
            Kind = JobKind.Transfer,
            ProfileId = plan.ProfileId,
            DisplayName = "matching transfer",
        };
        applyEvent.Invoke(viewModel, [new EngineEvent(
            11, EngineEventKind.Job, "job.changed", now, matching, matching.Id)]);

        Assert.Equal(0, viewModel.UnconfirmedTransferCount);
        Assert.False(session.HasUnconfirmedTransfers);
    }

    [Fact]
    public void HistoryEventUpsertsAValidatedTerminalRecord()
    {
        var profile = Profile();
        var viewModel = CreateViewModelWithoutUiContext(new RecordingSessionAgent(profile, []));
        var applyEvent = typeof(MainWindowViewModel).GetMethod(
            "ApplyAgentEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Agent event application method was not found.");
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var record = new HistoryRecord(
            id,
            id,
            JobKind.Transfer,
            "Finished transfer",
            JobState.Completed,
            now.AddSeconds(-1),
            now,
            Detail: "Complete");

        applyEvent.Invoke(viewModel, [new EngineEvent(
            20, EngineEventKind.Job, "history.appended", now, record, JobId: record.JobId)]);
        applyEvent.Invoke(viewModel, [new EngineEvent(
            21, EngineEventKind.Job, "history.appended", now, record with { Outcome = JobState.Cancelled }, JobId: record.JobId)]);

        var updated = Assert.Single(viewModel.Activity.History);
        Assert.Equal(JobState.Cancelled, updated.Outcome);
        Assert.Empty(viewModel.Activity.Log);
    }

    [Fact]
    public async Task ProgressEventUpdatesTheMatchingRunningTransfer()
    {
        var profile = Profile();
        var agent = new RecordingSessionAgent(profile, []);
        var now = DateTimeOffset.UtcNow;
        var job = new JobSnapshot(
            Guid.NewGuid(),
            JobKind.Transfer,
            profile.Id,
            "Large download",
            JobState.Running,
            now.AddMinutes(-1),
            now,
            Status: "Running");
        agent.Jobs.Add(job);
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var applyEvent = typeof(MainWindowViewModel).GetMethod(
            "ApplyAgentEvent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The Agent event application method was not found.");
        var progress = new TransferProgressSnapshot(job.Id, 262_144, 1_048_576, 65_536, now.AddSeconds(1));

        applyEvent.Invoke(viewModel, [new EngineEvent(
            22, EngineEventKind.Job, "job.progress", progress.ObservedAt, progress, JobId: job.Id)]);

        var updated = Assert.Single(viewModel.Activity.Jobs);
        Assert.Equal(0.25, updated.Progress);
        Assert.Equal("256 KB of 1 MB · 64 KB/s", updated.Status);
        Assert.Empty(viewModel.Activity.Log);
    }

    [Fact]
    public async Task RemotePaneExplorerExportPollsToCompletionAndReleasesManagedFiles()
    {
        var profile = Profile();
        var seed = Seed(profile, includeRemoteFile: true);
        var exportId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var queuedJob = new JobSnapshot(exportId, JobKind.Transfer, profile.Id,
            "Explorer export", JobState.Queued, now, now);
        var completedJob = queuedJob with
        {
            State = JobState.Completed,
            Progress = 1,
            Status = "Managed local copies are ready for Explorer.",
            UpdatedAt = now.AddSeconds(1),
        };
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            ExplorerStartReply = new(exportId, seed.Snapshot.SessionId, queuedJob, [], now.AddMinutes(30)),
        };
        agent.ExplorerGetReplies.Enqueue(new(
            exportId,
            seed.Snapshot.SessionId,
            completedJob,
            [Path.GetFullPath("managed-export.bin")],
            now.AddMinutes(30)));
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();
        var pane = Assert.Single(viewModel.Sessions).RemotePane;

        var result = await pane.PrepareExplorerExportAsync(
            exportId,
            [new("/remote/file.bin", TransferSourceKind.File)],
            DateTimeOffset.UtcNow.AddSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, result.Job.State);
        Assert.Equal(exportId, Assert.Single(agent.ExplorerStartRequests).ExportId);
        Assert.Equal(1, agent.ExplorerGetCalls);
        Assert.True(await pane.ReleaseExplorerExportAsync(exportId, TestContext.Current.CancellationToken));
        Assert.Equal(exportId, Assert.Single(agent.ReleasedExplorerExports));
    }

    [Fact]
    public async Task RemotePaneExplorerExportReconcilesAnUnknownStartByStableIdentifier()
    {
        var profile = Profile();
        var seed = Seed(profile, includeRemoteFile: true);
        var exportId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var completed = new JobSnapshot(exportId, JobKind.Transfer, profile.Id,
            "Explorer export", JobState.Completed, now, now, Progress: 1);
        var agent = new RecordingSessionAgent(profile, [seed])
        {
            ExplorerStartOutcomeUnknown = true,
        };
        agent.ExplorerGetReplies.Enqueue(new(
            exportId,
            seed.Snapshot.SessionId,
            completed,
            [Path.GetFullPath("managed-export.bin")],
            now.AddMinutes(30)));
        var viewModel = CreateViewModelWithoutUiContext(agent);
        await viewModel.InitializeAsync();

        var result = await Assert.Single(viewModel.Sessions).RemotePane.PrepareExplorerExportAsync(
            exportId,
            [new("/remote/file.bin", TransferSourceKind.File)],
            DateTimeOffset.UtcNow.AddSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Completed, result.Job.State);
        Assert.Equal(1, agent.ExplorerGetCalls);
        Assert.Single(agent.ExplorerStartRequests);
    }

    private static ConnectionProfile Profile() => new(
        Guid.NewGuid(), "FTP test", ConnectionProtocol.Ftp, "example.test", 21, "alice", AuthenticationKind.Password);

    private static WorkspaceSessionSeed Seed(
        ConnectionProfile profile,
        bool includeRemoteFile = false,
        Guid? sessionId = null,
        bool isConnected = true,
        string localPath = @"C:\Users\Test",
        string remotePath = "/",
        EngineError? error = null) => new(
        new SessionSnapshot(
            sessionId ?? Guid.NewGuid(), profile.Id, profile.Name, isConnected,
            new(PaneKind.Local, localPath),
            new(PaneKind.Remote, remotePath),
            DateTimeOffset.UtcNow,
            error),
        [],
        includeRemoteFile
            ? [new FileEntry("file.bin", "/remote/file.bin", EntryKind.File, 12, DateTimeOffset.UtcNow)]
            : []);

    private static MainWindowViewModel CreateViewModelWithoutUiContext(IAgentWorkspaceClient agent)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            // A null captured UI context keeps resync deterministic in pure view-model tests.
            return new MainWindowViewModel(agent);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static MainWindowViewModel CreateViewModelWithInlineUiContext(IAgentWorkspaceClient agent)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            return new MainWindowViewModel(agent);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < expiresAt)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The asynchronous view-model operation did not reach the expected state.");
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class RecordingSessionAgent(
        ConnectionProfile profile,
        IEnumerable<WorkspaceSessionSeed> initialSessions) : IAgentWorkspaceClient
    {
        private readonly List<WorkspaceSessionSeed> _sessions = [.. initialSessions];
        private WorkspaceSessionSeed? _pendingConnectedSeed;
        private bool _profileDeleted;

        public int LoadCount { get; private set; }
        public bool DisconnectMutationApplied => DisconnectedSessionIds.Count > 0;
        public bool ThrowAfterDisconnectMutation { get; init; }
        public bool ThrowAfterConnectMutation { get; init; }
        public bool DeferConnectMutationUntilPublish { get; init; }
        public bool ThrowRemoteTransferOutcomeUnknown { get; init; }
        public bool ThrowFolderPresetOutcomeUnknown { get; init; }
        public int TransferUnknownResponses { get; init; }
        public bool CommitTransferBeforeUnknownReply { get; init; }
        public bool ReturnFailedTransfer { get; init; }
        public bool CommitWrongTransferIdentity { get; init; }
        public List<Guid> RemoteTransferEnqueuePlanIds { get; } = [];
        public List<Guid> TransferEnqueuePlanIds { get; } = [];
        public List<TransferPlan> TransferPlans { get; } = [];
        public List<JobSnapshot> Jobs { get; } = [];
        public Queue<WorkspaceSessionSeed> ConnectResults { get; } = [];
        public List<(Guid? ExistingSessionId, string? EphemeralCredential)> ConnectRequests { get; } = [];
        public List<Guid> DisconnectedSessionIds { get; } = [];
        public List<RemoteEditSession> RemoteEdits { get; } = [];
        public List<ConnectionProfile> AdditionalProfiles { get; } = [];
        public List<ExplorerExportStartRequest> ExplorerStartRequests { get; } = [];
        public Queue<ExplorerExportSnapshot> ExplorerGetReplies { get; } = [];
        public List<Guid> ReleasedExplorerExports { get; } = [];
        public List<FileEntry> OfflineLocalEntries { get; } = [];
        public List<string> OfflineBrowseRequests { get; } = [];
        public ExplorerExportSnapshot? ExplorerStartReply { get; init; }
        public bool ExplorerStartOutcomeUnknown { get; init; }
        public int ExplorerGetCalls { get; private set; }
        public RemoteEditSession? ResolveMutation { get; set; }
        public bool ProfileDeleted => _profileDeleted;
        public bool IsConnected => true;
        public event EventHandler<EngineEvent>? EventReceived;
        public event EventHandler? StateInvalidated { add { } remove { } }

        public Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            var profiles = _profileDeleted
                ? AdditionalProfiles.ToArray()
                : new[] { profile }.Concat(AdditionalProfiles).ToArray();
            return Task.FromResult(new UiWorkspaceBootstrap(
                profiles, _sessions.ToArray(), Jobs.ToArray(), RemoteEdits.ToArray(), [], [], false, "Agent ready"));
        }

        public void ReplaceSessions(IEnumerable<WorkspaceSessionSeed> sessions)
        {
            _sessions.Clear();
            _sessions.AddRange(sessions);
        }

        public void CommitProfileDeleteAndPublish()
        {
            _profileDeleted = true;
            _sessions.RemoveAll(seed => seed.Snapshot.ProfileId == profile.Id);
            EventReceived?.Invoke(this, new EngineEvent(
                1, EngineEventKind.Session, "profile.deleted", DateTimeOffset.UtcNow, profile.Id));
        }

        public void CommitProfileIdentityChangeAndPublish(ConnectionProfile updated)
        {
            _sessions.RemoveAll(seed => seed.Snapshot.ProfileId == updated.Id);
            EventReceived?.Invoke(this, new EngineEvent(
                3, EngineEventKind.Session, "profile.saved", DateTimeOffset.UtcNow, updated));
        }

        public void CommitPendingConnectAndPublish()
        {
            var seed = _pendingConnectedSeed ?? throw new InvalidOperationException("No deferred connection is pending.");
            _pendingConnectedSeed = null;
            _sessions.Add(seed);
            EventReceived?.Invoke(this, new EngineEvent(
                2, EngineEventKind.Session, "session.connected", DateTimeOffset.UtcNow,
                seed.Snapshot, seed.Snapshot.SessionId));
        }

        public Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = _sessions.RemoveAll(seed => seed.Snapshot.SessionId == sessionId) == 1;
            if (!removed) return Task.FromResult(false);
            DisconnectedSessionIds.Add(sessionId);
            if (ThrowAfterDisconnectMutation)
                throw new InvalidOperationException("sessions removed before reply serialization failed");
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile value, string? credential = null, CancellationToken cancellationToken = default) => Unsupported<ConnectionProfile>();
        public Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, profileId);
            _profileDeleted = true;
            _sessions.RemoveAll(seed => seed.Snapshot.ProfileId == profileId);
            return Task.FromResult(true);
        }
        public Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile value, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyInspection>();
        public Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(SftpHostKeyReview review, bool replaceExisting, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyApproveResult>();
        public Task<WorkspaceSessionSeed> ConnectAsync(
            ConnectionProfile value,
            string? ephemeralCredential = null,
            CancellationToken cancellationToken = default,
            Guid? existingSessionId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(profile.Id, value.Id);
            ConnectRequests.Add((existingSessionId, ephemeralCredential));
            if (!ConnectResults.TryDequeue(out var seed)) return Unsupported<WorkspaceSessionSeed>();
            if (DeferConnectMutationUntilPublish)
            {
                _pendingConnectedSeed = seed;
                return Task.FromException<WorkspaceSessionSeed>(
                    new IOException("connect outcome became unknown before the late commit"));
            }
            if (existingSessionId is { } restoredSessionId)
            {
                var existingIndex = _sessions.FindIndex(candidate => candidate.Snapshot.SessionId == restoredSessionId);
                Assert.True(existingIndex >= 0);
                _sessions[existingIndex] = seed;
            }
            else
            {
                _sessions.Add(seed);
            }
            if (ThrowAfterConnectMutation)
                throw new InvalidOperationException("connect completed before reply serialization failed");
            return Task.FromResult(seed);
        }
        public Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessionId != Guid.Empty || pane != PaneKind.Local) return Unsupported<IReadOnlyList<FileEntry>>();
            OfflineBrowseRequests.Add(path);
            return Task.FromResult<IReadOnlyList<FileEntry>>(OfflineLocalEntries.ToArray());
        }
        public Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferEnqueuePlanIds.Add(plan.Id);
            TransferPlans.Add(plan);
            var now = DateTimeOffset.UtcNow;
            var job = new JobSnapshot(
                plan.Id,
                JobKind.Transfer,
                plan.ProfileId,
                "transfer",
                ReturnFailedTransfer ? JobState.Failed : plan.RunAt is null ? JobState.Queued : JobState.Scheduled,
                now,
                now,
                plan.RunAt,
                Error: ReturnFailedTransfer ? new("transfer-submission-failed", "Validation failed.") : null,
                RetryAvailable: ReturnFailedTransfer);
            if (CommitTransferBeforeUnknownReply && Jobs.All(existing => existing.Id != job.Id))
            {
                Jobs.Add(CommitWrongTransferIdentity
                    ? job with { Kind = JobKind.Mirror, ProfileId = Guid.NewGuid() }
                    : job);
            }
            if (TransferEnqueuePlanIds.Count <= TransferUnknownResponses)
                return Task.FromException<JobSnapshot>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.TransferEnqueue,
                    new IOException("transfer reply lost")));
            if (Jobs.All(existing => existing.Id != job.Id)) Jobs.Add(job);
            return Task.FromResult(Jobs.Single(existing => existing.Id == job.Id));
        }
        public Task<FolderTransferPreset> SaveFolderTransferPresetAsync(
            FolderTransferPreset preset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ThrowFolderPresetOutcomeUnknown
                ? Task.FromException<FolderTransferPreset>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.FolderTransferPresetSave,
                    new IOException("folder preset reply lost")))
                : Task.FromResult(preset);
        }
        public Task<bool> DeleteFolderTransferPresetAsync(
            Guid presetId,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default) => Unsupported<MirrorUiPreview>();
        public Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<string>>();
        public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(plan with { Mode = RemoteTransferMode.Fxp });
        }
        public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoteTransferEnqueuePlanIds.Add(plan.Id);
            if (ThrowRemoteTransferOutcomeUnknown && RemoteTransferEnqueuePlanIds.Count == 1)
                return Task.FromException<RemoteTransferEnqueueResult>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.RemoteTransferEnqueue,
                    new IOException("remote transfer reply lost")));
            if (!ThrowRemoteTransferOutcomeUnknown) return Unsupported<RemoteTransferEnqueueResult>();
            var now = DateTimeOffset.UtcNow;
            var job = new JobSnapshot(plan.Id, JobKind.RemoteTransfer, plan.SourceProfileId,
                "reconciled remote transfer", JobState.Queued, now, now);
            return Task.FromResult(new RemoteTransferEnqueueResult(job, plan.Mode, "Reconciled original plan."));
        }
        public Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => Unsupported<RemoteEditSession>();
        public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<RemoteEditReview>();
        public Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResolveMutation is not { } mutation || !string.Equals(mutation.EditId, editId, StringComparison.Ordinal))
                return Unsupported<RemoteEditActionResult>();
            var index = RemoteEdits.FindIndex(edit => string.Equals(edit.EditId, editId, StringComparison.Ordinal));
            Assert.True(index >= 0);
            RemoteEdits[index] = mutation;
            return Task.FromException<RemoteEditActionResult>(new IOException("upload committed before reply serialization failed"));
        }
        public Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<ExplorerExportSnapshot> StartExplorerExportAsync(
            ExplorerExportStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExplorerStartRequests.Add(request);
            if (ExplorerStartOutcomeUnknown)
                return Task.FromException<ExplorerExportSnapshot>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.ExplorerExportStart,
                    new IOException("Explorer export reply was lost.")));
            return ExplorerStartReply is { } reply
                ? Task.FromResult(reply)
                : Unsupported<ExplorerExportSnapshot>();
        }
        public Task<ExplorerExportSnapshot> GetExplorerExportAsync(Guid exportId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExplorerGetCalls++;
            if (!ExplorerGetReplies.TryDequeue(out var reply)) return Unsupported<ExplorerExportSnapshot>();
            Assert.Equal(exportId, reply.ExportId);
            return Task.FromResult(reply);
        }
        public Task<bool> ReleaseExplorerExportAsync(Guid exportId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleasedExplorerExports.Add(exportId);
            return Task.FromResult(true);
        }
        public Task StopAgentAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();
        public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Unsupported<AppUpdateStatus>();
        public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();

        private static Task<T> Unsupported<T>() =>
            Task.FromException<T>(new NotSupportedException("This operation is outside the focused disconnect test."));
    }
}
