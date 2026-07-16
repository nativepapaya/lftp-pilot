using System.Collections.Immutable;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class MirrorViewModelTests
{
    [Fact]
    public async Task UnknownApprovalConsumesPreviewAndReconcilesOnlyTheSamePreviewId()
    {
        var profile = Profile();
        var agent = new ReplayGuardAgent();
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([profile]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSnapshot? reconciledJob = null;
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);
        viewModel.JobQueued += (_, job) => reconciledJob = job;

        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(1, agent.ApproveCalls);
        Assert.False(viewModel.HasPreview);
        Assert.False(viewModel.CanRun);
        Assert.False(viewModel.CanPreview);
        Assert.False(viewModel.DeletionsApproved);
        Assert.Empty(viewModel.PreviewActions);
        Assert.Contains("do not approve or preview", viewModel.Status, StringComparison.OrdinalIgnoreCase);

        viewModel.PreviewCommand.Execute(null);
        viewModel.RunCommand.Execute(null);
        Assert.Equal(1, agent.PreviewCalls);
        Assert.Equal(1, agent.ApproveCalls);

        var wrongJob = new JobSnapshot(
            Guid.NewGuid(), JobKind.Mirror, profile.Id, "wrong", JobState.Queued,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        viewModel.ObserveJob(wrongJob);
        Assert.False(viewModel.CanPreview);
        viewModel.LoadProfiles([profile]);
        viewModel.Name = "Changed while outcome is unknown";
        Assert.False(viewModel.CanPreview);

        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.ApproveCalls);
        Assert.Single(agent.ApprovedPreviewIds.Distinct());
        Assert.All(agent.DeletionApprovals, Assert.True);
        Assert.Equal(agent.ApprovedPreviewIds[0], reconciledJob?.Id);
        Assert.True(viewModel.CanPreview);
    }

    [Fact]
    public async Task RepeatedRefreshesNeverResubmitAnUnconfirmedMirrorMoreThanOnce()
    {
        var agent = new ReplayGuardAgent(alwaysUnknown: true);
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([Profile()]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.ReconcileWorkspaceAsync([]);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.ApproveCalls);
        Assert.Single(agent.ApprovedPreviewIds.Distinct());
        Assert.False(viewModel.CanPreview);
        Assert.Contains("already been attempted", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnlyTypedAgentRejectionUnlocksAnUncertainMirrorApproval()
    {
        var localFailureAgent = new ReplayGuardAgent(
            reconciliationFailure: new InvalidOperationException("local response parsing failed"));
        var localFailureViewModel = new MirrorViewModel(localFailureAgent);
        localFailureViewModel.LoadProfiles([Profile()]);
        var localRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        localFailureViewModel.StateRefreshRequested += (_, _) => localRefresh.TrySetResult(true);
        localFailureViewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => localFailureViewModel.HasPreview);
        localFailureViewModel.DeletionsApproved = true;
        localFailureViewModel.RunCommand.Execute(null);
        await localRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await localFailureViewModel.ReconcileWorkspaceAsync([]);

        Assert.False(localFailureViewModel.CanPreview);
        Assert.Contains("remains unconfirmed", localFailureViewModel.Status, StringComparison.OrdinalIgnoreCase);

        var rejectedAgent = new ReplayGuardAgent(
            reconciliationFailure: new AgentRequestRejectedException(
                new EngineRequestRejectedException(
                    WorkspaceMethods.MirrorApprove,
                    new ProtocolError("InvalidOperationException", "approval registration expired"))));
        var rejectedViewModel = new MirrorViewModel(rejectedAgent);
        rejectedViewModel.LoadProfiles([Profile()]);
        var rejectedRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rejectedViewModel.StateRefreshRequested += (_, _) => rejectedRefresh.TrySetResult(true);
        rejectedViewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => rejectedViewModel.HasPreview);
        rejectedViewModel.DeletionsApproved = true;
        rejectedViewModel.RunCommand.Execute(null);
        await rejectedRefresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await rejectedViewModel.ReconcileWorkspaceAsync([]);

        Assert.True(rejectedViewModel.CanPreview);
        Assert.False(rejectedViewModel.HasPreview);
        Assert.Contains("did not accept", rejectedViewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredInitialApprovalRejectionConsumesPreviewWithoutCreatingUnknownOutcome()
    {
        var agent = new ReplayGuardAgent(
            reconciliationFailure: ExpiredApprovalRejection(),
            unknownOnFirst: false);
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([Profile()]);
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CanPreview && !viewModel.HasPreview);

        Assert.Equal(1, agent.ApproveCalls);
        Assert.Equal(0, refreshRequests);
        Assert.Contains("rejected", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unconfirmed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredReconciliationRejectionUnlocksUnknownApproval()
    {
        var agent = new ReplayGuardAgent(reconciliationFailure: ExpiredApprovalRejection());
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([Profile()]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);

        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await viewModel.ReconcileWorkspaceAsync([]);

        Assert.Equal(2, agent.ApproveCalls);
        Assert.True(viewModel.CanPreview);
        Assert.False(viewModel.HasPreview);
        Assert.Contains("did not accept", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchingJobEventConfirmsUncertainApprovalWithoutResubmission()
    {
        var profile = Profile();
        var agent = new ReplayGuardAgent(alwaysUnknown: true);
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([profile]);
        var refresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.StateRefreshRequested += (_, _) => refresh.TrySetResult(true);
        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await refresh.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var previewId = Assert.Single(agent.ApprovedPreviewIds);

        viewModel.ObserveJob(new(
            previewId, JobKind.Mirror, profile.Id, "confirmed", JobState.Running,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Assert.True(viewModel.CanPreview);
        Assert.Equal(1, agent.ApproveCalls);
        Assert.Contains("confirmed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThrowingJobObserverCannotTurnConfirmedApprovalIntoUncertainState()
    {
        var agent = new ReplayGuardAgent(unknownOnFirst: false);
        var viewModel = new MirrorViewModel(agent);
        viewModel.LoadProfiles([Profile()]);
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;
        viewModel.JobQueued += (_, _) => throw new InvalidOperationException("simulated presentation failure");

        viewModel.PreviewCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPreview);
        viewModel.DeletionsApproved = true;
        viewModel.RunCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Status.Contains("queued", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, agent.ApproveCalls);
        Assert.Equal(0, refreshRequests);
        Assert.True(viewModel.CanPreview);
        Assert.False(viewModel.HasPreview);
        Assert.DoesNotContain("unconfirmed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectionProfile Profile() => new(
        Guid.NewGuid(), "Mirror", ConnectionProtocol.Ftp, "files.example", 21,
        "anonymous", AuthenticationKind.Anonymous);

    private static AgentRequestRejectedException ExpiredApprovalRejection() =>
        new(new EngineRequestRejectedException(
            WorkspaceMethods.MirrorApprove,
            new ProtocolError("InvalidOperationException", "approval registration expired")));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The mirror view model did not reach the expected state.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ReplayGuardAgent(
        bool alwaysUnknown = false,
        Exception? reconciliationFailure = null,
        bool unknownOnFirst = true) : IAgentWorkspaceClient
    {
        public int PreviewCalls { get; private set; }
        public int ApproveCalls { get; private set; }
        public List<Guid> ApprovedPreviewIds { get; } = [];
        public List<bool> DeletionApprovals { get; } = [];
        public bool IsConnected => true;
        public event EventHandler<EngineEvent>? EventReceived { add { } remove { } }
        public event EventHandler? StateInvalidated { add { } remove { } }

        public Task<MirrorUiPreview> PreviewMirrorAsync(
            MirrorDefinition definition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreviewCalls++;
            var now = DateTimeOffset.UtcNow;
            var preview = new MirrorPreview(
                Guid.NewGuid(),
                definition.Id,
                now,
                now.AddMinutes(5),
                ImmutableArray.Create(new MirrorAction(MirrorActionKind.DeleteFile, "stale.txt")),
                "test-fingerprint",
                "test-approval-token");
            return Task.FromResult(new MirrorUiPreview(definition, preview));
        }

        public Task<JobSnapshot> ApproveMirrorAsync(
            MirrorUiPreview preview,
            bool deletionsApproved,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApproveCalls++;
            ApprovedPreviewIds.Add(preview.Preview.Id);
            DeletionApprovals.Add(deletionsApproved);
            if (alwaysUnknown || (unknownOnFirst && ApproveCalls == 1))
            {
                return Task.FromException<JobSnapshot>(new AgentRequestOutcomeUnknownException(
                    WorkspaceMethods.MirrorApprove,
                    new IOException("simulated lost reply")));
            }
            if (reconciliationFailure is not null)
                return Task.FromException<JobSnapshot>(reconciliationFailure);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new JobSnapshot(
                preview.Preview.Id,
                JobKind.Mirror,
                preview.Definition.ProfileId,
                preview.Definition.Name,
                JobState.Queued,
                now,
                now));
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
        public Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default) => Unsupported<IReadOnlyList<string>>();
        public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferPlan>();
        public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferEnqueueResult>();
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
