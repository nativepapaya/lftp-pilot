using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.App.ViewModels;
using LFTPPilot.Core;

namespace LFTPPilot.Tests;

public sealed class ConnectionProfilesViewModelTests
{
    [Fact]
    public async Task CancellingEnrollmentKeepsEnteredCredentialInAppMemory()
    {
        var profile = Profile();
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired));
        var viewModel = ViewModel(agent, profile);
        viewModel.RememberCredential = true;
        viewModel.Credential = "entered-password";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPendingHostKeyReview);

        Assert.Equal("entered-password", viewModel.Credential);
        Assert.Empty(agent.SavedCredentials);
        Assert.Empty(agent.ConnectCredentials);
        Assert.Empty(agent.ReplaceDecisions);

        viewModel.CancelHostKeyReviewCommand.Execute(null);
        await WaitUntilAsync(() =>
            !viewModel.HasPendingHostKeyReview && viewModel.Status?.Contains("was not sent", StringComparison.Ordinal) == true);

        Assert.Equal("entered-password", viewModel.Credential);
        Assert.Empty(agent.SavedCredentials);
        Assert.Empty(agent.ConnectCredentials);
        Assert.Empty(agent.ReplaceDecisions);
        Assert.Contains("was not sent", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(0, refreshRequests);
    }

    [Fact]
    public async Task ClosingDuringHostKeyProbeReportsUnknownAgentOutcomeAndClearsCredential()
    {
        var profile = Profile();
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DelayInspection = true,
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "close-before-probe-finishes";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.ConnectCommand.Execute(null);
        await agent.InspectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.CancelPendingHostKeyReview(clearCredential: true);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Empty(viewModel.Credential);
        Assert.False(viewModel.HasPendingHostKeyReview);
        Assert.Empty(agent.ConnectCredentials);
        Assert.Empty(agent.ReplaceDecisions);
        Assert.DoesNotContain("not sent", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    [Theory]
    [InlineData(SftpHostKeyState.EnrollmentRequired, false)]
    [InlineData(SftpHostKeyState.Changed, true)]
    public async Task ExplicitReviewUsesTheRequiredApprovalModeBeforeConnecting(SftpHostKeyState state, bool replaceExisting)
    {
        var profile = Profile();
        var agent = new RecordingAgent(Inspection(profile, state));
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "ephemeral-password";

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPendingHostKeyReview);
        viewModel.ApproveHostKeyReviewCommand.Execute(null);
        await WaitUntilAsync(() => agent.ConnectCredentials.Count == 1);

        Assert.Equal([replaceExisting], agent.ReplaceDecisions);
        Assert.Equal(["ephemeral-password"], agent.ConnectCredentials);
        Assert.Empty(agent.SavedCredentials);
        Assert.Empty(viewModel.Credential);
    }

    [Fact]
    public async Task ReviewForDifferentEndpointIsRejectedBeforeApprovalOrCredentialDispatch()
    {
        var profile = Profile();
        var inspection = Inspection(profile, SftpHostKeyState.EnrollmentRequired);
        inspection = inspection with
        {
            Review = inspection.Review! with { Endpoint = "sftp://different.example:22" },
        };
        var agent = new RecordingAgent(inspection);
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "credential-for-captured-endpoint";

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Status?.Contains("different endpoint", StringComparison.OrdinalIgnoreCase) == true);

        Assert.False(viewModel.HasPendingHostKeyReview);
        Assert.Empty(agent.ReplaceDecisions);
        Assert.Empty(agent.ConnectCredentials);
        Assert.Equal("credential-for-captured-endpoint", viewModel.Credential);
    }

    [Fact]
    public async Task SavingRememberedSftpPasswordDefersItUntilHostKeyApproval()
    {
        var profile = Profile();
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired));
        var viewModel = ViewModel(agent, profile);
        viewModel.RememberCredential = true;
        viewModel.Credential = "remember-later";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasPendingHostKeyReview);

        Assert.Equal([null], agent.SavedCredentials);
        Assert.Equal("remember-later", viewModel.Credential);

        viewModel.CancelHostKeyReviewCommand.Execute(null);
        await WaitUntilAsync(() =>
            !viewModel.HasPendingHostKeyReview && viewModel.Status?.Contains("was not stored", StringComparison.Ordinal) == true);

        Assert.Equal([null], agent.SavedCredentials);
        Assert.Equal("remember-later", viewModel.Credential);
        Assert.Contains("was not stored", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(0, refreshRequests);
    }

    [Fact]
    public async Task SwitchingProfilesResetsRememberChoiceAndKeepsNextCredentialEphemeral()
    {
        var first = Profile(ConnectionProtocol.Ftp);
        var second = Profile(ConnectionProtocol.Ftp) with { Name = "Second FTP site" };
        var agent = new RecordingAgent(Inspection(first, SftpHostKeyState.EnrollmentRequired));
        var viewModel = new ConnectionProfilesViewModel(agent);
        viewModel.Load([first, second]);
        viewModel.RememberCredential = true;
        viewModel.Credential = "first-profile-password";

        viewModel.SelectedProfile = first with { Name = "Updated first FTP site" };

        Assert.True(viewModel.RememberCredential);
        Assert.Equal("first-profile-password", viewModel.Credential);

        viewModel.SelectedProfile = second;

        Assert.False(viewModel.RememberCredential);
        Assert.Empty(viewModel.Credential);

        viewModel.Credential = "second-profile-ephemeral-password";
        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() => agent.ConnectCredentials.Count == 1);

        Assert.Empty(agent.SavedCredentials);
        Assert.Equal(["second-profile-ephemeral-password"], agent.ConnectCredentials);
        Assert.Empty(viewModel.Credential);
    }

    [Fact]
    public async Task CreatingPasswordProfileRestoresExplicitRememberChoiceForNewProfile()
    {
        var initial = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(initial, SftpHostKeyState.EnrollmentRequired));
        var viewModel = ViewModel(agent, initial);
        viewModel.RememberCredential = true;
        viewModel.Credential = "remember-on-new-profile";

        viewModel.CreateAndConnectCommand.Execute(null);
        await WaitUntilAsync(() => agent.ConnectCredentials.Count == 1);

        Assert.Equal([null, "remember-on-new-profile"], agent.SavedCredentials);
        Assert.Equal([null], agent.ConnectCredentials);
        Assert.True(viewModel.RememberCredential);
        Assert.Empty(viewModel.Credential);
    }

    [Fact]
    public async Task CancelledConnectAfterDispatchReportsUnknownOutcomeAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DelayConnect = true,
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "possibly-sent";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.ConnectCommand.Execute(null);
        await agent.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.CancelPendingHostKeyReview(clearCredential: true);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal(["possibly-sent"], agent.ConnectCredentials);
        Assert.DoesNotContain("not sent", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task ConnectFailureAfterRememberedCredentialMutationReportsUnknownStateAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            SaveFailure = new InvalidOperationException("credential stored before connect preparation failed"),
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.RememberCredential = true;
        viewModel.Credential = "possibly-stored";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.ConnectCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal(["possibly-stored"], agent.SavedCredentials);
        Assert.Empty(agent.ConnectCredentials);
        Assert.Empty(viewModel.Credential);
        Assert.Contains("may have changed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credential stored before connect preparation failed", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task CancelledCreateAfterDispatchReportsUnknownOutcomeAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DelaySave = true,
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "held-for-connect";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.CreateAndConnectCommand.Execute(null);
        await agent.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.CancelPendingHostKeyReview(clearCredential: true);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal([null], agent.SavedCredentials);
        Assert.DoesNotContain("not sent", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task CancelledSaveAfterDispatchReportsUnknownOutcomeAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DelaySave = true,
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.RememberCredential = true;
        viewModel.Credential = "possibly-stored";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.SaveCommand.Execute(null);
        await agent.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.CancelPendingHostKeyReview(clearCredential: true);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal(["possibly-stored"], agent.SavedCredentials);
        Assert.DoesNotContain("not stored", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task CancelledDeleteAfterDispatchReportsUnknownOutcomeAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DelayDelete = true,
        };
        var viewModel = ViewModel(agent, profile);
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.DeleteCommand.Execute(null);
        await agent.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        viewModel.CancelPendingHostKeyReview(clearCredential: true);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal([profile.Id], agent.DeletedProfileIds);
        Assert.DoesNotContain("deletion cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task CreateFailureAfterProfileMutationReportsUnknownStateAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            SaveFailure = new InvalidOperationException("metadata committed before credential cleanup failed"),
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "possibly-used-later";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.CreateAndConnectCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal([null], agent.SavedCredentials);
        Assert.Empty(viewModel.Credential);
        Assert.Contains("may have changed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("metadata committed before credential cleanup failed", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task SaveFailureAfterMutationReportsUnknownStateAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            SaveFailure = new InvalidOperationException("credential stored before reply serialization failed"),
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.RememberCredential = true;
        viewModel.Credential = "possibly-stored";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal(["possibly-stored"], agent.SavedCredentials);
        Assert.Empty(viewModel.Credential);
        Assert.Contains("may have changed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credential stored before reply serialization failed", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task DeletePartialFailureReportsUnknownStateAndRequestsRefresh()
    {
        var profile = Profile(ConnectionProtocol.Ftp);
        var agent = new RecordingAgent(Inspection(profile, SftpHostKeyState.EnrollmentRequired))
        {
            DeleteFailure = new InvalidOperationException("sessions disconnected before metadata removal failed"),
        };
        var viewModel = ViewModel(agent, profile);
        viewModel.Credential = "clear-after-uncertain-delete";
        var refreshRequests = 0;
        viewModel.StateRefreshRequested += (_, _) => refreshRequests++;

        viewModel.DeleteCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.Status?.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase) == true &&
            Volatile.Read(ref refreshRequests) == 1);

        Assert.Equal([profile.Id], agent.DeletedProfileIds);
        Assert.Empty(viewModel.Credential);
        Assert.Contains("may have changed", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sessions disconnected before metadata removal failed", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("remain available", viewModel.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, refreshRequests);
    }

    private static ConnectionProfilesViewModel ViewModel(RecordingAgent agent, ConnectionProfile profile)
    {
        var viewModel = new ConnectionProfilesViewModel(agent);
        viewModel.Load([profile]);
        return viewModel;
    }

    private static ConnectionProfile Profile(ConnectionProtocol protocol = ConnectionProtocol.Sftp) => new(
        Guid.NewGuid(),
        $"{protocol} test",
        protocol,
        "example.test",
        protocol == ConnectionProtocol.Sftp ? 22 : 21,
        "alice",
        AuthenticationKind.Password);

    private static SftpHostKeyInspection Inspection(ConnectionProfile profile, SftpHostKeyState state)
    {
        string? trustedAlgorithm = state == SftpHostKeyState.Changed ? "ecdsa-sha2-nistp256" : null;
        string? trustedFingerprint = state == SftpHostKeyState.Changed ? Fingerprint(1) : null;
        var review = new SftpHostKeyReview(
            Guid.NewGuid(),
            profile.Id,
            $"sftp://{profile.Host}:{profile.Port}",
            state,
            "ssh-ed25519",
            Fingerprint(2),
            trustedAlgorithm,
            trustedFingerprint,
            DateTimeOffset.UtcNow.AddMinutes(5),
            new string('a', 64));
        return new(state, review);
    }

    private static string Fingerprint(byte value) =>
        "SHA256:" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()).TrimEnd('=');

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < expiresAt)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The asynchronous view-model operation did not reach the expected state.");
    }

    private sealed class RecordingAgent(SftpHostKeyInspection inspection) : IAgentWorkspaceClient
    {
        public List<string?> SavedCredentials { get; } = [];
        public List<string?> ConnectCredentials { get; } = [];
        public List<bool> ReplaceDecisions { get; } = [];
        public List<Guid> DeletedProfileIds { get; } = [];
        public TaskCompletionSource<bool> InspectionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayInspection { get; init; }
        public bool DelaySave { get; init; }
        public bool DelayConnect { get; init; }
        public bool DelayDelete { get; init; }
        public Exception? SaveFailure { get; init; }
        public Exception? DeleteFailure { get; init; }
        public bool IsConnected => true;
        public event EventHandler<EngineEvent>? EventReceived { add { } remove { } }
        public event EventHandler? StateInvalidated { add { } remove { } }

        public async Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedCredentials.Add(credential);
            SaveStarted.TrySetResult(true);
            if (DelaySave) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (SaveFailure is not null) throw SaveFailure;
            return profile;
        }

        public async Task<SftpHostKeyInspection> InspectSftpHostKeyAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(inspection.Review?.ProfileId, profile.Id);
            if (DelayInspection)
            {
                InspectionStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return inspection;
        }

        public Task<SftpHostKeyApproveResult> ApproveSftpHostKeyAsync(
            SftpHostKeyReview review,
            bool replaceExisting,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDecisions.Add(replaceExisting);
            return Task.FromResult(new SftpHostKeyApproveResult(review.ProfileId, review.Endpoint, review.PresentedFingerprintSha256));
        }

        public async Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCredentials.Add(ephemeralCredential);
            ConnectStarted.TrySetResult(true);
            if (DelayConnect) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var snapshot = new SessionSnapshot(
                Guid.NewGuid(), profile.Id, profile.Name, true,
                new(PaneKind.Local, @"C:\Users\Test"),
                new(PaneKind.Remote, "/"),
                DateTimeOffset.UtcNow);
            return new WorkspaceSessionSeed(snapshot, [], []);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default) => Unsupported<UiWorkspaceBootstrap>();
        public async Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedProfileIds.Add(profileId);
            DeleteStarted.TrySetResult(true);
            if (DelayDelete) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (DeleteFailure is not null) throw DeleteFailure;
            return true;
        }
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
        public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferPlan>();
        public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default) => Unsupported<RemoteTransferEnqueueResult>();
        public Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) => Unsupported<RemoteEditSession>();
        public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<RemoteEditReview>();
        public Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default) => Unsupported<RemoteEditActionResult>();
        public Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default) => Unsupported<bool>();
        public Task StopAgentAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();
        public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) => Unsupported<AppUpdateStatus>();
        public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) => Unsupported<object?>();

        private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("This operation is outside the focused view-model test."));
    }
}

public sealed class SftpHostKeyWireValidationTests
{
    [Fact]
    public void AcceptsCanonicalEnrollmentAndChangedReviews()
    {
        var enrollment = Review();
        SftpHostKeyWireValidation.ValidateReview(enrollment, enrollment.ProfileId);

        var changed = enrollment with
        {
            Endpoint = "sftp://[2001:db8::1]:2222",
            State = SftpHostKeyState.Changed,
            TrustedAlgorithm = "ecdsa-sha2-nistp256",
            TrustedFingerprintSha256 = Fingerprint(1),
        };
        SftpHostKeyWireValidation.ValidateReview(changed, changed.ProfileId);
    }

    [Theory]
    [InlineData("sftp://EXAMPLE.test:22")]
    [InlineData("SFTP://example.test:22")]
    [InlineData("sftp://example.test:022")]
    [InlineData("sftp://example.test:0")]
    [InlineData("sftp://example.test:65536")]
    [InlineData("sftp://example.test")]
    [InlineData("sftp://user@example.test:22")]
    [InlineData("sftp://example.test:22/")]
    [InlineData("sftp://example.test:22/path")]
    [InlineData("sftp://example.test:22?query")]
    [InlineData("sftp://2001:db8::1:22")]
    public void RejectsNonCanonicalOrNonAuthorityEndpoints(string endpoint)
    {
        var review = Review() with { Endpoint = endpoint };
        Assert.Throws<InvalidDataException>(() => SftpHostKeyWireValidation.ValidateReview(review, review.ProfileId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh ed25519")]
    [InlineData("ssh,ed25519")]
    [InlineData("ssh-ed25519\u007f")]
    public void RejectsInvalidAlgorithms(string algorithm)
    {
        var review = Review() with { PresentedAlgorithm = algorithm };
        Assert.Throws<InvalidDataException>(() => SftpHostKeyWireValidation.ValidateReview(review, review.ProfileId));
    }

    [Fact]
    public void RejectsOversizedAlgorithm()
    {
        var review = Review() with { PresentedAlgorithm = new string('a', 129) };
        Assert.Throws<InvalidDataException>(() => SftpHostKeyWireValidation.ValidateReview(review, review.ProfileId));
    }

    [Theory]
    [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("SHA256:not-a-fingerprint")]
    public void RejectsNonCanonicalFingerprints(string fingerprint)
    {
        var review = Review() with { PresentedFingerprintSha256 = fingerprint };
        Assert.Throws<InvalidDataException>(() => SftpHostKeyWireValidation.ValidateReview(review, review.ProfileId));
    }

    [Fact]
    public void RejectsMalformedApprovalTokens()
    {
        var review = Review();
        foreach (var token in new[] { new string('a', 63), new string('a', 63) + "g", new string('a', 63) + "\n" })
        {
            Assert.Throws<InvalidDataException>(() =>
                SftpHostKeyWireValidation.ValidateReview(review with { ApprovalToken = token }, review.ProfileId));
        }
    }

    private static SftpHostKeyReview Review() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "sftp://example.test:22",
        SftpHostKeyState.EnrollmentRequired,
        "ssh-ed25519",
        Fingerprint(0),
        null,
        null,
        DateTimeOffset.UtcNow.AddMinutes(5),
        new string('a', 64));

    private static string Fingerprint(byte value) =>
        "SHA256:" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray()).TrimEnd('=');
}
