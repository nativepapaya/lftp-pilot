using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class RemoteTransferViewModelTests
{
    [Fact]
    public async Task UnknownEnqueueOutcomeConsumesReviewAndBlocksReplanUntilWorkspaceResync()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent();
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSnapshot? reconciledJob = null;
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);
        viewModel.JobQueued += (_, job) => reconciledJob = job;

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, agent.EnqueueCalls);
        Assert.False(viewModel.HasPlan);
        Assert.False(viewModel.CanEnqueue);
        Assert.False(viewModel.CanPlan);
        Assert.False(viewModel.EnqueueCommand.CanExecute(null));
        Assert.Contains("do not submit it again", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refreshed jobs", viewModel.Status, StringComparison.OrdinalIgnoreCase);

        viewModel.EnqueueCommand.Execute(null);
        Assert.Equal(1, agent.EnqueueCalls);
        viewModel.LoadProfiles([source, destination, Profile("Unrelated", "unrelated.example")]);
        Assert.False(viewModel.CanPlan);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.EnqueueCalls);
        Assert.Single(agent.EnqueuePlanIds.Distinct());
        Assert.Equal(agent.EnqueuePlanIds[0], reconciledJob?.Id);
        Assert.True(viewModel.CanPlan);
    }

    [Fact]
    public async Task RepeatedWorkspaceRefreshesNeverResubmitAnUnconfirmedPlanMoreThanOnce()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent(alwaysUnknown: true);
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.ReconcileWorkspaceAsync([]);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.EnqueueCalls);
        Assert.Single(agent.EnqueuePlanIds.Distinct());
        Assert.False(viewModel.CanPlan);
        Assert.Contains("already been attempted", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalInvalidOperationDuringReconciliationNeverUnlocksFreshPlanning()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent(reconciliationFailure: new InvalidOperationException("local Agent connection unavailable"));
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.ReconcileWorkspaceAsync([]);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.EnqueueCalls);
        Assert.False(viewModel.CanPlan);
        Assert.Contains("still unconfirmed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameIdBootstrapMismatchStaysBlockedAndConsumesAutomaticRetry()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent();
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var planId = Assert.Single(agent.EnqueuePlanIds);
        var now = DateTimeOffset.UtcNow;
        var wrongJob = new JobSnapshot(
            planId, JobKind.Mirror, source.Id, "wrong kind", JobState.Queued, now, now);

        await viewModel.ReconcileWorkspaceAsync([wrongJob]);

        Assert.Equal(1, agent.EnqueueCalls);
        Assert.False(viewModel.CanPlan);
        Assert.Contains("different kind or source profile", viewModel.Status, StringComparison.OrdinalIgnoreCase);

        await viewModel.ReconcileWorkspaceAsync([]);
        Assert.Equal(1, agent.EnqueueCalls);
        Assert.False(viewModel.CanPlan);
    }

    [Fact]
    public async Task SameIdEventMismatchCannotConfirmUncertainPlan()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent();
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var planId = Assert.Single(agent.EnqueuePlanIds);
        var now = DateTimeOffset.UtcNow;

        viewModel.ObserveJob(new(
            planId, JobKind.RemoteTransfer, destination.Id, "wrong profile", JobState.Queued, now, now));
        Assert.False(viewModel.CanPlan);
        Assert.Contains("different kind or source profile", viewModel.Status, StringComparison.OrdinalIgnoreCase);

        await viewModel.ReconcileWorkspaceAsync([]);
        Assert.Equal(1, agent.EnqueueCalls);
        Assert.False(viewModel.CanPlan);

        viewModel.ObserveJob(new(
            planId, JobKind.RemoteTransfer, source.Id, "exact job", JobState.Queued, now, now));
        Assert.True(viewModel.CanPlan);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("profile")]
    [InlineData("mode")]
    public async Task ReconciliationResultMismatchStaysBlockedAndConsumesOneSafeRetry(string mismatch)
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent
        {
            ReconciliationResultFactory = plan =>
            {
                var now = DateTimeOffset.UtcNow;
                var job = new JobSnapshot(
                    mismatch == "id" ? Guid.NewGuid() : plan.Id,
                    mismatch == "kind" ? JobKind.Mirror : JobKind.RemoteTransfer,
                    mismatch == "profile" ? plan.DestinationProfileId : plan.SourceProfileId,
                    "mismatched result",
                    JobState.Queued,
                    now,
                    now);
                return new(job,
                    mismatch == "mode" ? RemoteTransferMode.ClientRelay : plan.Mode,
                    "mismatched route");
            },
        };
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);
        viewModel.JobQueued += (_, _) => published++;

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.EnqueueCalls);
        Assert.Equal(0, published);
        Assert.False(viewModel.CanPlan);
        Assert.Contains("safe same-plan reconciliation attempt was consumed", viewModel.Status, StringComparison.OrdinalIgnoreCase);

        await viewModel.ReconcileWorkspaceAsync([]);
        Assert.Equal(2, agent.EnqueueCalls);
        Assert.False(viewModel.CanPlan);
    }

    [Fact]
    public async Task ThrowingJobObserverCannotMakeConfirmedEnqueueUncertain()
    {
        var source = Profile("Source", "source.example");
        var destination = Profile("Destination", "destination.example");
        var agent = new ReplayGuardAgent(firstOutcomeUnknown: false);
        var viewModel = new RemoteTransferViewModel(agent);
        viewModel.LoadProfiles([source, destination]);
        var delivered = 0;
        viewModel.JobQueued += (_, _) => throw new InvalidOperationException("presentation observer failed");
        viewModel.JobQueued += (_, _) => delivered++;

        viewModel.PlanCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPlan);
        viewModel.RouteApproved = true;
        viewModel.EnqueueCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Status.Contains("queued", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, agent.EnqueueCalls);
        Assert.Equal(1, delivered);
        Assert.True(viewModel.CanPlan);
        Assert.DoesNotContain("unconfirmed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectionProfile Profile(string name, string host) => new(
        Guid.NewGuid(), name, ConnectionProtocol.Ftp, host, 21, "anonymous", AuthenticationKind.Anonymous);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The view model did not reach the expected state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ReplayGuardAgent(
        bool alwaysUnknown = false,
        Exception? reconciliationFailure = null,
        bool firstOutcomeUnknown = true) : IAgentWorkspaceClient
    {
        public int EnqueueCalls { get; private set; }
        public List<Guid> EnqueuePlanIds { get; } = [];
        public Func<RemoteTransferPlan, RemoteTransferEnqueueResult>? ReconciliationResultFactory { get; init; }
        public bool IsConnected => true;
        public event EventHandler<EngineEvent>? EventReceived { add { } remove { } }
        public event EventHandler? StateInvalidated { add { } remove { } }

        public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(plan with { Id = Guid.NewGuid(), Mode = RemoteTransferMode.Fxp });
        }

        public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueCalls++;
            EnqueuePlanIds.Add(plan.Id);
            if (alwaysUnknown || (firstOutcomeUnknown && EnqueueCalls == 1))
                return Task.FromException<RemoteTransferEnqueueResult>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.RemoteTransferEnqueue, new IOException("simulated lost reply")));
            if (reconciliationFailure is not null)
                return Task.FromException<RemoteTransferEnqueueResult>(reconciliationFailure);
            if (ReconciliationResultFactory is not null)
                return Task.FromResult(ReconciliationResultFactory(plan));
            var now = DateTimeOffset.UtcNow;
            var job = new JobSnapshot(plan.Id, JobKind.RemoteTransfer, plan.SourceProfileId, "reconciled", JobState.Queued, now, now);
            return Task.FromResult(new RemoteTransferEnqueueResult(job, plan.Mode, "Reconciled without a second job."));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default) => Unsupported<UiWorkspaceBootstrap>();
        public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default) => Unsupported<ConnectionProfile>();
        public Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyInspection>();
        public Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(SftpHostKeyReview review, bool replaceExisting, CancellationToken cancellationToken = default) => Unsupported<SftpHostKeyApproveResult>();
        public Task<WorkspaceSessionSeed> ConnectAsync(
            ConnectionProfile profile,
            string? ephemeralCredential = null,
            CancellationToken cancellationToken = default,
            Guid? existingSessionId = null) => Unsupported<WorkspaceSessionSeed>();
        public Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<FileEntry>>();
        public Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default) => Unsupported<FileMutationResult>();
        public Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task<JobSnapshot> RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default) => Unsupported<MirrorUiPreview>();
        public Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default) => Unsupported<JobSnapshot>();
        public Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<string>>();
        public Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => Unsupported<RemoteEditSession>();
        public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<RemoteEditReview>();
        public Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default) => Unsupported<RemoteEditActionResult>();
        public Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task StopAgentAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();
        public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Unsupported<AppUpdateStatus>();
        public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();

        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException());
    }
}
