using System.Collections.Immutable;
using LFTPPilot.App.Models;
using LFTPPilot.Core;

namespace LFTPPilot.App.Services;

public sealed class DemoAgentWorkspaceClient : IAgentWorkspaceClient
{
    private readonly IAppUpdateService _updates;
    private readonly ConnectionProfile _demoProfile = new(
        Guid.Parse("a4a9a7b7-f92c-455e-a4a0-6e0de2035c66"),
        "Demo server",
        ConnectionProtocol.Sftp,
        "demo.example.com",
        22,
        "pilot",
        AuthenticationKind.AskOnConnect,
        InitialRemotePath: "/srv/releases",
        InitialLocalPath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private readonly ConnectionProfile _demoDestinationProfile = new(
        Guid.Parse("0ac493be-f2f1-4c2a-a0f4-c345bb7eec30"),
        "Demo FTP destination",
        ConnectionProtocol.FtpsExplicit,
        "uploads.example.com",
        21,
        "publisher",
        AuthenticationKind.AskOnConnect,
        InitialRemotePath: "/incoming",
        InitialLocalPath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public DemoAgentWorkspaceClient(IAppUpdateService updates) => _updates = updates;

    public bool IsConnected => false;
    public event EventHandler<EngineEvent>? EventReceived { add { } remove { } }
    public event EventHandler? StateInvalidated { add { } remove { } }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<UiWorkspaceBootstrap> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var session = CreateSession(_demoProfile);
        IReadOnlyList<JobSnapshot> jobs =
        [
            new(Guid.NewGuid(), JobKind.Transfer, _demoProfile.Id, "release-notes.pdf", JobState.Running, now.AddMinutes(-2), now, Progress: 0.64, Status: "Downloading · 8.1 MB/s"),
            new(Guid.NewGuid(), JobKind.Mirror, _demoProfile.Id, "Nightly upload", JobState.Queued, now.AddMinutes(-1), now, Status: "Waiting for transfer slot"),
        ];
        IReadOnlyList<HistoryRecord> history =
        [
            new(Guid.NewGuid(), Guid.NewGuid(), JobKind.Transfer, "hero-banner.png", JobState.Completed, now.AddMinutes(-18), now.AddMinutes(-17), 2_850_112, "Uploaded"),
        ];
        IReadOnlyList<ActivityLogEntry> log =
        [
            new(now, "Info", "App", "Agent unavailable; showing a safe interactive demo workspace."),
            new(now.AddSeconds(-1), "Info", "Session", "Demo SFTP session prepared."),
        ];

        return Task.FromResult(new UiWorkspaceBootstrap([_demoProfile, _demoDestinationProfile], [session], jobs, [], history, log, true, "Demo mode · Agent not connected"));
    }

    public Task<ConnectionProfile> SaveProfileAsync(ConnectionProfile profile, string? credential = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(profile);
    }

    public Task<bool> DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task<WorkspaceSessionSeed> ConnectAsync(ConnectionProfile profile, string? ephemeralCredential = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSession(profile));
    }

    public Task<bool> DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<FileEntry>> BrowseAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FileEntry>>(pane == PaneKind.Local ? CreateLocalEntries(path) : CreateRemoteEntries(path));
    }

    public Task<FileMutationResult> CreateDirectoryAsync(Guid sessionId, PaneKind pane, string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FileMutationResult(pane, [path]));
    }

    public Task<FileMutationResult> MoveEntryAsync(Guid sessionId, PaneKind pane, string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FileMutationResult(pane, [sourcePath, destinationPath]));
    }

    public Task<FileMutationResult> DeleteEntriesAsync(Guid sessionId, PaneKind pane, IReadOnlyList<string> paths, bool recursive, bool confirmed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed) throw new InvalidOperationException("File deletion requires explicit confirmation.");
        return Task.FromResult(new FileMutationResult(pane, paths.ToImmutableArray()));
    }

    public Task<JobSnapshot> EnqueueTransferAsync(Guid sessionId, TransferPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var state = plan.RunAt is null ? JobState.Queued : JobState.Scheduled;
        var status = plan.RunAt is null ? "Queued with reviewed transfer options" : $"Run once at {plan.RunAt.Value.ToLocalTime():g}";
        return Task.FromResult(new JobSnapshot(Guid.NewGuid(), JobKind.Transfer, plan.ProfileId,
            Path.GetFileName(plan.SourcePath), state, now, now, RunAt: plan.RunAt, Status: status));
    }

    public Task<bool> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(jobId != Guid.Empty);
    }

    public Task<MirrorUiPreview> PreviewMirrorAsync(MirrorDefinition definition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var actions = ImmutableArray.Create(
            new MirrorAction(definition.Direction == MirrorDirection.Upload ? MirrorActionKind.Upload : MirrorActionKind.Download, "assets/app-icon.png", "2.8 MB"),
            new MirrorAction(MirrorActionKind.CreateDirectory, "docs/archive"),
            new MirrorAction(MirrorActionKind.DeleteFile, "outdated.tmp", "Deletion requires review"));
        var preview = new MirrorPreview(Guid.NewGuid(), definition.Id, now, now.AddMinutes(5), actions, "demo-fingerprint", "demo-approval");
        return Task.FromResult(new MirrorUiPreview(definition, preview));
    }

    public Task<JobSnapshot> ApproveMirrorAsync(MirrorUiPreview preview, bool deletionsApproved, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preview.Definition.DeleteExtraneous && !deletionsApproved)
            throw new InvalidOperationException("Deletion-capable mirrors require explicit approval.");
        var now = DateTimeOffset.Now;
        return Task.FromResult(new JobSnapshot(Guid.NewGuid(), JobKind.Mirror, preview.Definition.ProfileId, preview.Definition.Name, JobState.Queued, now, now, Status: "Approved preview queued"));
    }

    public Task<IReadOnlyList<string>> ExecuteConsoleAsync(Guid sessionId, string command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> output = command.Trim().Equals("pwd", StringComparison.OrdinalIgnoreCase)
            ? ["/srv/releases"]
            : [$"Demo console accepted: {command}", "The isolated Agent session will provide live output when connected."];
        return Task.FromResult(output);
    }

    public Task<RemoteTransferPlan> PlanRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(plan);
    }

    public Task<RemoteTransferEnqueueResult> EnqueueRemoteTransferAsync(RemoteTransferPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.Now;
        var note = plan.Mode == RemoteTransferMode.Fxp
            ? "FXP preferred; LFTP may relay if the servers reject FXP."
            : "Client-relay routing through LFTP is required.";
        var job = new JobSnapshot(Guid.NewGuid(), JobKind.RemoteTransfer, plan.SourceProfileId, "Demo remote transfer", JobState.Queued, now, now, Status: note);
        return Task.FromResult(new RemoteTransferEnqueueResult(job, plan.Mode, note));
    }

    public Task<RemoteEditSession> StartRemoteEditAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        Task.FromException<RemoteEditSession>(new NotSupportedException("Managed remote editing requires the live Agent."));

    public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId, CancellationToken cancellationToken = default) =>
        Task.FromException<RemoteEditReview>(new NotSupportedException("Managed remote editing requires the live Agent."));

    public Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution, CancellationToken cancellationToken = default) =>
        Task.FromException<RemoteEditActionResult>(new NotSupportedException("Managed remote editing requires the live Agent."));

    public Task<bool> CompleteRemoteEditAsync(string editId, CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Managed remote editing requires the live Agent."));

    public Task StopAgentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<AppUpdateStatus> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        _updates.CheckAsync(cancellationToken);

    public Task OpenUpdateInstallerAsync(CancellationToken cancellationToken = default) =>
        _updates.OpenInstallerAsync(cancellationToken);

    private static WorkspaceSessionSeed CreateSession(ConnectionProfile profile)
    {
        var local = profile.InitialLocalPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var remote = profile.InitialRemotePath ?? "/";
        var snapshot = new SessionSnapshot(Guid.NewGuid(), profile.Id, profile.Name, true, new PaneLocation(PaneKind.Local, local), new PaneLocation(PaneKind.Remote, remote), DateTimeOffset.Now);
        return new WorkspaceSessionSeed(snapshot, CreateLocalEntries(local), CreateRemoteEntries(remote));
    }

    private static IReadOnlyList<FileEntry> CreateLocalEntries(string root) =>
    [
        new("Documents", Path.Combine(root, "Documents"), EntryKind.Directory, null, DateTimeOffset.Now.AddDays(-1)),
        new("Pictures", Path.Combine(root, "Pictures"), EntryKind.Directory, null, DateTimeOffset.Now.AddDays(-3)),
        new("build-output.zip", Path.Combine(root, "build-output.zip"), EntryKind.File, 48_331_776, DateTimeOffset.Now.AddMinutes(-22)),
        new("release-notes.pdf", Path.Combine(root, "release-notes.pdf"), EntryKind.File, 12_681_216, DateTimeOffset.Now.AddHours(-2)),
        new("README.md", Path.Combine(root, "README.md"), EntryKind.File, 9_184, DateTimeOffset.Now.AddMinutes(-8)),
    ];

    private static IReadOnlyList<FileEntry> CreateRemoteEntries(string root) =>
    [
        new("..", root.TrimEnd('/') + "/..", EntryKind.Directory, null, null),
        new("assets", root.TrimEnd('/') + "/assets", EntryKind.Directory, null, DateTimeOffset.Now.AddDays(-2), "drwxr-xr-x", "deploy", "web"),
        new("docs", root.TrimEnd('/') + "/docs", EntryKind.Directory, null, DateTimeOffset.Now.AddHours(-5), "drwxr-xr-x", "deploy", "web"),
        new("app-v1.0.0.msix", root.TrimEnd('/') + "/app-v1.0.0.msix", EntryKind.File, 96_482_304, DateTimeOffset.Now.AddHours(-3), "-rw-r--r--", "deploy", "web"),
        new("SHA256SUMS.txt", root.TrimEnd('/') + "/SHA256SUMS.txt", EntryKind.File, 1_248, DateTimeOffset.Now.AddHours(-3), "-rw-r--r--", "deploy", "web"),
    ];
}
