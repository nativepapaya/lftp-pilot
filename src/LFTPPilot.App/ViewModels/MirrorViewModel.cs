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
    private MirrorDefinition? _selectedDefinition;
    private MirrorDirection _direction = MirrorDirection.Upload;
    private string _name = "New mirror";
    private string _localRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private string _remoteRoot = "/srv/releases";
    private string _includes = string.Empty;
    private string _excludes = string.Join(Environment.NewLine, ".git/**", "*.tmp");
    private bool _deleteExtraneous;
    private int _parallelFiles = 2;
    private int _segmentsPerFile = 1;
    private double _rateLimitMibPerSecond;
    private bool _deletionsApproved;
    private MirrorUiPreview? _currentPreview;
    private MirrorUiPreview? _uncertainPreview;
    private bool _uncertainDeletionsApproved;
    private bool _awaitingWorkspaceResync;
    private bool _uncertainReconciliationAttempted;
    private int _previewRevision;
    private int _reconciliationInProgress;
    private bool _definitionMutationInProgress;
    private bool _definitionMutationUncertain;
    private Guid? _pendingDefinitionId;
    private Guid _draftDefinitionId = Guid.NewGuid();
    private string _status = "Create a dry-run preview before starting.";

    public MirrorViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        NewDefinitionCommand = new RelayCommand(_ => StartNewDefinition(), _ => !DefinitionInteractionBlocked);
        SaveDefinitionCommand = new AsyncRelayCommand(_ => SaveDefinitionAsync(), _ => CanSaveDefinition, ReportError);
        DeleteDefinitionCommand = new AsyncRelayCommand(_ => DeleteDefinitionAsync(), _ => CanDeleteDefinition, ReportError);
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => CanPreview, ReportError);
        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanRun, ReportError);
    }

    public event EventHandler<JobSnapshot>? JobQueued;
    public event EventHandler? StateRefreshRequested;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<MirrorDefinition> SavedDefinitions { get; } = [];
    public ObservableCollection<MirrorAction> PreviewActions { get; } = [];
    public IReadOnlyList<MirrorDirection> Directions { get; } = Enum.GetValues<MirrorDirection>();
    public RelayCommand NewDefinitionCommand { get; }
    public AsyncRelayCommand SaveDefinitionCommand { get; }
    public AsyncRelayCommand DeleteDefinitionCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand RunCommand { get; }

    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            OnPropertyChanged();
            InvalidatePreview();
        }
    }
    public MirrorDefinition? SelectedDefinition
    {
        get => _selectedDefinition;
        set
        {
            if (ReferenceEquals(_selectedDefinition, value)) return;
            _selectedDefinition = value;
            OnPropertyChanged();
            InvalidatePreview();
            if (value is not null) ApplyDefinition(value);
            NotifyDefinitionCommands();
        }
    }
    public MirrorDirection Direction { get => _direction; set { if (SetProperty(ref _direction, value)) InvalidatePreview(); } }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) InvalidatePreview(); } }
    public string LocalRoot { get => _localRoot; set { if (SetProperty(ref _localRoot, value)) InvalidatePreview(); } }
    public string RemoteRoot { get => _remoteRoot; set { if (SetProperty(ref _remoteRoot, value)) InvalidatePreview(); } }
    public string Includes { get => _includes; set { if (SetProperty(ref _includes, value)) InvalidatePreview(); } }
    public string Excludes { get => _excludes; set { if (SetProperty(ref _excludes, value)) InvalidatePreview(); } }
    public bool DeleteExtraneous { get => _deleteExtraneous; set { if (SetProperty(ref _deleteExtraneous, value)) InvalidatePreview(); } }
    public int ParallelFiles { get => _parallelFiles; set { if (SetProperty(ref _parallelFiles, Math.Clamp(value, 1, 16))) InvalidatePreview(); } }
    public int SegmentsPerFile
    {
        get => _segmentsPerFile;
        set
        {
            if (SetProperty(ref _segmentsPerFile, Math.Clamp(value, 1, MirrorDefinitionPolicy.MaximumSegmentsPerFile)))
                InvalidatePreview();
        }
    }
    public double RateLimitMibPerSecond
    {
        get => _rateLimitMibPerSecond;
        set
        {
            var maximum = MirrorDefinitionPolicy.MaximumRateLimitBytesPerSecond / (1024d * 1024d);
            var bounded = double.IsFinite(value) ? Math.Clamp(value, 0, maximum) : 0;
            if (SetProperty(ref _rateLimitMibPerSecond, bounded)) InvalidatePreview();
        }
    }
    public bool DeletionsApproved { get => _deletionsApproved; set { if (SetProperty(ref _deletionsApproved, value)) { OnPropertyChanged(nameof(CanRun)); RunCommand.NotifyCanExecuteChanged(); } } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasPreview => _currentPreview is not null;
    public bool RequiresDeletionApproval => _currentPreview is { } current &&
        (current.Definition.DeleteExtraneous || current.Preview.ContainsDeletions);
    private bool DefinitionInteractionBlocked =>
        _awaitingWorkspaceResync || _definitionMutationInProgress || _definitionMutationUncertain;
    public bool CanSaveDefinition => !DefinitionInteractionBlocked && SelectedProfile is not null;
    public bool CanDeleteDefinition => !DefinitionInteractionBlocked && SelectedDefinition is not null;
    public bool CanPreview => !DefinitionInteractionBlocked && SelectedProfile is not null;
    public bool CanRun => !DefinitionInteractionBlocked && HasPreview &&
        (!RequiresDeletionApproval || DeletionsApproved);

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

    public void LoadDefinitions(IEnumerable<MirrorDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var mutationWasUncertain = _definitionMutationUncertain;
        var selectedId = _pendingDefinitionId ?? SelectedDefinition?.Id;
        var loaded = definitions.OrderBy(static definition => definition.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static definition => definition.Id)
            .ToArray();
        foreach (var definition in loaded) PlanValidator.Validate(definition);
        SavedDefinitions.Clear();
        foreach (var definition in loaded) SavedDefinitions.Add(definition);
        _definitionMutationUncertain = false;
        _pendingDefinitionId = null;
        SelectedDefinition = selectedId is { } id
            ? SavedDefinitions.FirstOrDefault(definition => definition.Id == id)
            : null;
        if (mutationWasUncertain)
        {
            Status = SelectedDefinition is null
                ? "Workspace refreshed. The uncertain saved-definition mutation left no saved definition; the current fields are now an unsaved mirror."
                : "Workspace refreshed. The selected saved mirror reflects the Agent's authoritative state; create a fresh dry-run preview before running it.";
        }
        NotifyDefinitionCommands();
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

    private void StartNewDefinition()
    {
        if (DefinitionInteractionBlocked) return;
        ResetDefinitionEditor();
        Status = "New unsaved mirror. Save it for reuse or create a fresh dry-run preview.";
    }

    private void ResetDefinitionEditor()
    {
        SelectedDefinition = null;
        _draftDefinitionId = Guid.NewGuid();
        Name = "New mirror";
        Direction = MirrorDirection.Upload;
        LocalRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        RemoteRoot = "/srv/releases";
        Includes = string.Empty;
        Excludes = string.Join(Environment.NewLine, ".git/**", "*.tmp");
        DeleteExtraneous = false;
        ParallelFiles = 2;
        SegmentsPerFile = 1;
        RateLimitMibPerSecond = 0;
        InvalidatePreview();
    }

    private async Task SaveDefinitionAsync()
    {
        if (!CanSaveDefinition || SelectedProfile is null) return;
        var definition = BuildDefinition(SelectedDefinition?.Id ?? _draftDefinitionId);
        PlanValidator.Validate(definition);
        _definitionMutationInProgress = true;
        InvalidatePreview();
        _pendingDefinitionId = definition.Id;
        Status = SelectedDefinition is null ? "Saving mirror definition..." : "Saving mirror definition changes...";
        try
        {
            var saved = await _agent.SaveMirrorDefinitionAsync(definition).ConfigureAwait(true);
            var savedItem = UpsertSavedDefinition(saved);
            _pendingDefinitionId = null;
            SelectedDefinition = savedItem;
            Status = "Mirror definition saved. Create a fresh dry-run preview before running it.";
        }
        catch (AgentRequestOutcomeUnknownException exception)
        {
            MarkDefinitionMutationUncertain($"The save outcome is unknown. {exception.Message}");
        }
        catch (AgentRequestRejectedException exception)
        {
            _pendingDefinitionId = null;
            Status = $"The Agent rejected the saved mirror definition. {exception.Message}";
            NotifyDefinitionCommands();
        }
        finally
        {
            _definitionMutationInProgress = false;
            if (!_definitionMutationUncertain) _pendingDefinitionId = null;
            NotifyPreviewState();
        }
    }

    private async Task DeleteDefinitionAsync()
    {
        if (!CanDeleteDefinition || SelectedDefinition is not { } definition) return;
        _definitionMutationInProgress = true;
        InvalidatePreview();
        _pendingDefinitionId = definition.Id;
        Status = "Deleting saved mirror definition...";
        try
        {
            _ = await _agent.DeleteMirrorDefinitionAsync(definition.Id).ConfigureAwait(true);
            var existing = SavedDefinitions.FirstOrDefault(item => item.Id == definition.Id);
            if (existing is not null) SavedDefinitions.Remove(existing);
            _pendingDefinitionId = null;
            ResetDefinitionEditor();
            Status = "Saved mirror definition deleted. Any earlier preview was discarded.";
        }
        catch (AgentRequestOutcomeUnknownException exception)
        {
            MarkDefinitionMutationUncertain($"The delete outcome is unknown. {exception.Message}");
        }
        catch (AgentRequestRejectedException exception)
        {
            _pendingDefinitionId = null;
            Status = $"The Agent rejected the saved mirror deletion. {exception.Message}";
            NotifyDefinitionCommands();
        }
        finally
        {
            _definitionMutationInProgress = false;
            if (!_definitionMutationUncertain) _pendingDefinitionId = null;
            NotifyPreviewState();
        }
    }

    private void ApplyDefinition(MirrorDefinition definition)
    {
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == definition.ProfileId);
        Name = definition.Name;
        Direction = definition.Direction;
        LocalRoot = definition.LocalRoot;
        RemoteRoot = definition.RemoteRoot;
        Includes = string.Join(Environment.NewLine, definition.EffectiveIncludes);
        Excludes = string.Join(Environment.NewLine, definition.EffectiveExcludes);
        DeleteExtraneous = definition.DeleteExtraneous;
        ParallelFiles = definition.ParallelFiles;
        SegmentsPerFile = definition.SegmentsPerFile;
        RateLimitMibPerSecond = definition.RateLimitBytesPerSecond is { } rate
            ? rate / (1024d * 1024d)
            : 0;
        InvalidatePreview();
        if (!_awaitingWorkspaceResync)
            Status = "Saved mirror loaded. Create a fresh dry-run preview before running it.";
    }

    private MirrorDefinition BuildDefinition(Guid id)
    {
        if (SelectedProfile is null) throw new InvalidOperationException("Select a connection for this mirror.");
        long? rateLimit = null;
        if (RateLimitMibPerSecond > 0)
        {
            var bytes = Math.Round(RateLimitMibPerSecond * 1024d * 1024d, MidpointRounding.AwayFromZero);
            rateLimit = Math.Clamp((long)bytes, 1, MirrorDefinitionPolicy.MaximumRateLimitBytesPerSecond);
        }
        return new(
            id,
            SelectedProfile.Id,
            Name.Trim(),
            Direction,
            LocalRoot.Trim(),
            RemoteRoot.Trim(),
            SplitPatterns(Includes),
            SplitPatterns(Excludes),
            DeleteExtraneous,
            ParallelFiles,
            SegmentsPerFile,
            rateLimit);
    }

    private MirrorDefinition UpsertSavedDefinition(MirrorDefinition definition)
    {
        var previous = SavedDefinitions.FirstOrDefault(item => item.Id == definition.Id);
        if (previous is not null && DefinitionsEqual(previous, definition)) return previous;
        if (previous is not null) SavedDefinitions.Remove(previous);
        var insertion = 0;
        while (insertion < SavedDefinitions.Count &&
            string.Compare(SavedDefinitions[insertion].Name, definition.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
        {
            insertion++;
        }
        SavedDefinitions.Insert(insertion, definition);
        return definition;
    }

    private static bool DefinitionsEqual(MirrorDefinition left, MirrorDefinition right) =>
        left.Id == right.Id &&
        left.ProfileId == right.ProfileId &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        left.Direction == right.Direction &&
        string.Equals(left.LocalRoot, right.LocalRoot, StringComparison.Ordinal) &&
        string.Equals(left.RemoteRoot, right.RemoteRoot, StringComparison.Ordinal) &&
        left.EffectiveIncludes.SequenceEqual(right.EffectiveIncludes, StringComparer.Ordinal) &&
        left.EffectiveExcludes.SequenceEqual(right.EffectiveExcludes, StringComparer.Ordinal) &&
        left.DeleteExtraneous == right.DeleteExtraneous &&
        left.ParallelFiles == right.ParallelFiles &&
        left.SegmentsPerFile == right.SegmentsPerFile &&
        left.RateLimitBytesPerSecond == right.RateLimitBytesPerSecond;

    private void MarkDefinitionMutationUncertain(string status)
    {
        _definitionMutationUncertain = true;
        InvalidatePreview();
        Status = $"{status} Saved-definition actions and new previews remain blocked while workspace state is refreshed.";
        NotifyPreviewState();
        StateRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task PreviewAsync()
    {
        if (!CanPreview || SelectedProfile is null) return;
        var revision = _previewRevision;
        var definition = BuildDefinition(SelectedDefinition?.Id ?? _draftDefinitionId);
        Status = "Running isolated LFTP dry run...";
        var preview = await _agent.PreviewMirrorAsync(definition).ConfigureAwait(true);
        if (revision != _previewRevision || DefinitionInteractionBlocked)
        {
            Status = _awaitingWorkspaceResync
                ? "The mirror approval outcome is still unconfirmed. The newly returned dry run was not made available."
                : DefinitionInteractionBlocked
                    ? "Saved-definition state is changing or unconfirmed. The newly returned dry run was not made available."
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
        NotifyDefinitionCommands();
    }

    private void NotifyDefinitionCommands()
    {
        NewDefinitionCommand.NotifyCanExecuteChanged();
        SaveDefinitionCommand.NotifyCanExecuteChanged();
        DeleteDefinitionCommand.NotifyCanExecuteChanged();
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
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .ToImmutableArray();

    private void ReportError(Exception exception) => Status = exception.Message;
}
