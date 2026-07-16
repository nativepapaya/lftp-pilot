using System.Collections.Immutable;
using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class MirrorViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private ConnectionProfile? _selectedProfile;
    private MirrorDirection _direction = MirrorDirection.Upload;
    private string _name = "New mirror";
    private string _localRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _remoteRoot = "/srv/releases";
    private string _includes = string.Empty;
    private string _excludes = ".git/**;*.tmp";
    private bool _deleteExtraneous;
    private int _parallelFiles = 2;
    private int _segmentsPerFile = 1;
    private bool _deletionsApproved;
    private MirrorUiPreview? _currentPreview;
    private MirrorUiPreview? _uncertainPreview;
    private bool _uncertainDeletionsApproved;
    private bool _awaitingWorkspaceResync;
    private bool _uncertainReconciliationAttempted;
    private int _previewRevision;
    private int _reconciliationInProgress;
    private string _status = "Create a dry-run preview before starting.";

    public MirrorViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => CanPreview, ReportError);
        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanRun, ReportError);
    }

    public event EventHandler<JobSnapshot>? JobQueued;
    public event EventHandler? StateRefreshRequested;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<MirrorAction> PreviewActions { get; } = [];
    public IReadOnlyList<MirrorDirection> Directions { get; } = Enum.GetValues<MirrorDirection>();
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand RunCommand { get; }

    public ConnectionProfile? SelectedProfile { get => _selectedProfile; set { if (SetProperty(ref _selectedProfile, value)) InvalidatePreview(); } }
    public MirrorDirection Direction { get => _direction; set { if (SetProperty(ref _direction, value)) InvalidatePreview(); } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) InvalidatePreview(); } }
    public string LocalRoot { get => _localRoot; set { if (SetProperty(ref _localRoot, value)) InvalidatePreview(); } }
    public string RemoteRoot { get => _remoteRoot; set { if (SetProperty(ref _remoteRoot, value)) InvalidatePreview(); } }
    public string Includes { get => _includes; set { if (SetProperty(ref _includes, value)) InvalidatePreview(); } }
    public string Excludes { get => _excludes; set { if (SetProperty(ref _excludes, value)) InvalidatePreview(); } }
    public bool DeleteExtraneous { get => _deleteExtraneous; set { if (SetProperty(ref _deleteExtraneous, value)) InvalidatePreview(); } }
    public int ParallelFiles { get => _parallelFiles; set { if (SetProperty(ref _parallelFiles, Math.Clamp(value, 1, 16))) InvalidatePreview(); } }
    public int SegmentsPerFile { get => _segmentsPerFile; set { if (SetProperty(ref _segmentsPerFile, Math.Clamp(value, 1, 16))) InvalidatePreview(); } }
    public bool DeletionsApproved { get => _deletionsApproved; set { if (SetProperty(ref _deletionsApproved, value)) { OnPropertyChanged(nameof(CanRun)); RunCommand.NotifyCanExecuteChanged(); } } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasPreview => _currentPreview is not null;
    public bool RequiresDeletionApproval => _currentPreview is { } current &&
        (current.Definition.DeleteExtraneous || current.Preview.ContainsDeletions);
    public bool CanPreview => !_awaitingWorkspaceResync && SelectedProfile is not null;
    public bool CanRun => HasPreview && (!RequiresDeletionApproval || DeletionsApproved);

    public void LoadProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(profile);
        SelectedProfile = selectedId is { } id
            ? Profiles.FirstOrDefault(profile => profile.Id == id) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();
        NotifyPreviewState();
    }

    public async Task ReconcileWorkspaceAsync(IReadOnlyList<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (_uncertainPreview is not { } uncertainPreview ||
            Interlocked.Exchange(ref _reconciliationInProgress, 1) != 0) return;
        try
        {
            var matchingJob = FindMatchingJob(jobs, uncertainPreview);
            if (matchingJob is not null)
            {
                ResolveUncertainApproval($"The refreshed workspace confirms mirror job {matchingJob.Id}.");
                PublishJobQueued(matchingJob);
                return;
            }
            if (_uncertainReconciliationAttempted)
            {
                Status = "The original mirror approval is still unconfirmed and no matching job is visible. Automatic reconciliation has already been attempted; fresh preview and run actions remain blocked until the Agent reports the original job or rejects that approval authoritatively.";
                return;
            }

            try
            {
                _uncertainReconciliationAttempted = true;
                var job = await _agent.ApproveMirrorAsync(
                    uncertainPreview,
                    _uncertainDeletionsApproved).ConfigureAwait(true);
                if (!IsMatchingJob(job, uncertainPreview))
                    throw new InvalidDataException("The Agent returned a job that does not match the original mirror preview identifier.");
                ResolveUncertainApproval(job.State == JobState.Failed
                    ? $"The Agent reconciled the original mirror approval as failed: {job.Error?.Message ?? job.Status ?? "The mirror could not start."}"
                    : $"The Agent reconciled the original mirror approval as job {job.Id}.");
                PublishJobQueued(job);
            }
            catch (AgentRequestOutcomeUnknownException exception)
            {
                Status = $"The original mirror approval is still unconfirmed. It has not been submitted with a new preview ID. {exception.Message} Refresh workspace state again before creating another preview.";
            }
            catch (AgentRequestRejectedException exception)
            {
                ResolveUncertainApproval($"The refreshed Agent did not accept the original mirror approval. {exception.Message} No matching job exists; create and review a fresh preview.");
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                Status = $"The original mirror approval remains unconfirmed because its reconciliation reply was invalid or interrupted. {exception.Message} Fresh preview and run actions remain blocked until the Agent reports the original job or rejects that approval authoritatively.";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconciliationInProgress, 0);
        }
    }

    public void ObserveJob(JobSnapshot job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_uncertainPreview is not { } uncertainPreview || !IsMatchingJob(job, uncertainPreview)) return;
        ResolveUncertainApproval($"The Agent confirmed mirror job {job.Id}.");
    }

    private async Task PreviewAsync()
    {
        if (!CanPreview || SelectedProfile is null) return;
        var revision = _previewRevision;
        var definition = new MirrorDefinition(
            Guid.NewGuid(), SelectedProfile.Id, Name.Trim(), Direction, LocalRoot.Trim(), RemoteRoot.Trim(),
            SplitPatterns(Includes), SplitPatterns(Excludes), DeleteExtraneous, ParallelFiles, SegmentsPerFile);
        Status = "Running isolated LFTP dry run...";
        var preview = await _agent.PreviewMirrorAsync(definition).ConfigureAwait(true);
        if (revision != _previewRevision || _awaitingWorkspaceResync)
        {
            Status = _awaitingWorkspaceResync
                ? "The mirror approval outcome is still unconfirmed. The newly returned dry run was not made available."
                : "Settings changed while the dry run was running. Create a fresh preview.";
            return;
        }
        _currentPreview = preview;
        PreviewActions.Clear();
        foreach (var action in _currentPreview.Preview.Actions) PreviewActions.Add(action);
        DeletionsApproved = false;
        Status = $"Preview created - {PreviewActions.Count} proposed action{(PreviewActions.Count == 1 ? string.Empty : "s")}";
        NotifyPreviewState();
    }

    private async Task RunAsync()
    {
        if (_currentPreview is null || !CanRun) return;
        var reviewedPreview = _currentPreview;
        var deletionsApproved = DeletionsApproved;
        _currentPreview = null;
        PreviewActions.Clear();
        DeletionsApproved = false;
        NotifyPreviewState();
        Status = "Submitting the reviewed mirror approval to the Agent...";
        try
        {
            var job = await _agent.ApproveMirrorAsync(reviewedPreview, deletionsApproved).ConfigureAwait(true);
            if (!IsMatchingJob(job, reviewedPreview))
                throw new InvalidDataException("The Agent returned a job that does not match the approved mirror preview identifier.");
            Status = job.State == JobState.Failed
                ? $"Mirror could not start; the Agent recorded a failed job. {job.Error?.Message ?? job.Status}"
                : "Mirror queued from the reviewed definition.";
            PublishJobQueued(job);
        }
        catch (AgentRequestRejectedException exception)
        {
            Status = $"The Agent rejected the reviewed mirror approval. {exception.Message} Create and review a fresh preview.";
            NotifyPreviewState();
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            MarkApprovalUncertain(reviewedPreview, deletionsApproved, exception);
        }
    }

    private void InvalidatePreview()
    {
        _previewRevision++;
        if (_awaitingWorkspaceResync)
        {
            Status = "The original mirror approval remains unconfirmed. Fresh preview and run actions stay blocked until workspace reconciliation completes.";
            NotifyPreviewState();
            return;
        }
        if (_currentPreview is null)
        {
            NotifyPreviewState();
            return;
        }
        _currentPreview = null;
        PreviewActions.Clear();
        DeletionsApproved = false;
        Status = "Settings changed. Create a fresh dry-run preview.";
        NotifyPreviewState();
    }

    private void NotifyPreviewState()
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(RequiresDeletionApproval));
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanRun));
        PreviewCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
    }

    private void ResolveUncertainApproval(string status)
    {
        _uncertainPreview = null;
        _uncertainDeletionsApproved = false;
        _uncertainReconciliationAttempted = false;
        _awaitingWorkspaceResync = false;
        Status = status;
        NotifyPreviewState();
    }

    private void MarkApprovalUncertain(MirrorUiPreview preview, bool deletionsApproved, Exception exception)
    {
        _uncertainPreview = preview;
        _uncertainDeletionsApproved = deletionsApproved;
        _uncertainReconciliationAttempted = false;
        _awaitingWorkspaceResync = true;
        Status = $"Mirror approval could not be confirmed. Job {preview.Preview.Id} may already exist; do not approve or preview it again. {exception.Message} Reviewing refreshed jobs before allowing another mirror.";
        NotifyPreviewState();
        StateRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private static JobSnapshot? FindMatchingJob(
        IReadOnlyList<JobSnapshot> jobs,
        MirrorUiPreview preview) => jobs.FirstOrDefault(job => IsMatchingJob(job, preview));

    private static bool IsMatchingJob(JobSnapshot job, MirrorUiPreview preview) =>
        job.Id == preview.Preview.Id &&
        job.Kind == JobKind.Mirror &&
        job.ProfileId == preview.Definition.ProfileId;

    private void PublishJobQueued(JobSnapshot job)
    {
        var handlers = JobQueued;
        if (handlers is null) return;
        foreach (EventHandler<JobSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, job);
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                // A presentation observer cannot change a confirmed Agent
                // result into an uncertain approval outcome.
            }
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private static ImmutableArray<string> SplitPatterns(string value) => value
        .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToImmutableArray();

    private void ReportError(Exception exception) => Status = exception.Message;
}
