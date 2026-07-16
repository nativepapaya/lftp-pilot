using System.Collections.ObjectModel;
using System.Text.Json;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private SessionViewModel? _selectedSession;
    private bool _isLoading = true;
    private bool _isDemoMode;
    private string _agentStatus = "Starting Agent…";
    private bool _hasAgentError;
    private readonly SynchronizationContext? _uiContext;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _resyncGate = new(1, 1);
    private readonly Dictionary<Guid, UnconfirmedTransferState> _unconfirmedTransfers = [];
    private RemoteEditItemViewModel? _selectedActiveRemoteEdit;
    private int _stateInvalidated;

    public MainWindowViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        _uiContext = SynchronizationContext.Current;
        Activity = new ActivityCenterViewModel(agent);
        Connections = new ConnectionProfilesViewModel(agent);
        Mirror = new MirrorViewModel(agent);
        Console = new ConsoleViewModel(agent);
        RemoteTransfer = new RemoteTransferViewModel(agent);
        Settings = new SettingsViewModel(agent);
        InitializeCommand = new AsyncRelayCommand(_ => InitializeAsync(), null, ReportError);
        Connections.SessionConnected += (_, seed) => AddSession(seed);
        Connections.StateRefreshRequested += (_, _) => RequestStateRefresh();
        Mirror.StateRefreshRequested += (_, _) => RequestStateRefresh();
        RemoteTransfer.StateRefreshRequested += (_, _) => RequestStateRefresh();
        Connections.Profiles.CollectionChanged += (_, _) =>
        {
            Mirror.LoadProfiles(Connections.Profiles);
            RemoteTransfer.LoadProfiles(Connections.Profiles);
        };
        Mirror.JobQueued += (_, job) => Activity.Add(job);
        RemoteTransfer.JobQueued += (_, job) => Activity.Add(job);
        _agent.EventReceived += Agent_EventReceived;
        _agent.StateInvalidated += Agent_StateInvalidated;
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<RemoteEditItemViewModel> ActiveRemoteEdits { get; } = [];
    public ActivityCenterViewModel Activity { get; }
    public ConnectionProfilesViewModel Connections { get; }
    public MirrorViewModel Mirror { get; }
    public ConsoleViewModel Console { get; }
    public RemoteTransferViewModel RemoteTransfer { get; }
    public SettingsViewModel Settings { get; }
    public AsyncRelayCommand InitializeCommand { get; }
    public event Action<RemoteEditLocalChange>? RemoteEditLocalChanged;

    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set => SetProperty(ref _selectedSession, value);
    }

    public RemoteEditItemViewModel? SelectedActiveRemoteEdit
    {
        get => _selectedActiveRemoteEdit;
        set => SetProperty(ref _selectedActiveRemoteEdit, value);
    }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsDemoMode { get => _isDemoMode; private set => SetProperty(ref _isDemoMode, value); }
    public string AgentStatus { get => _agentStatus; private set => SetProperty(ref _agentStatus, value); }
    public bool HasAgentError { get => _hasAgentError; private set => SetProperty(ref _hasAgentError, value); }
    public int ActiveRemoteEditCount => ActiveRemoteEdits.Count;
    public int UnconfirmedTransferCount => _unconfirmedTransfers.Count;

    public async Task InitializeAsync(Guid? requestedProfileId = null)
    {
        await _initializationGate.WaitAsync().ConfigureAwait(true);
        IsLoading = true;
        try
        {
            var bootstrap = await _agent.LoadAsync().ConfigureAwait(true);
            HasAgentError = false;
            IsDemoMode = bootstrap.IsDemoMode;
            AgentStatus = bootstrap.AgentStatus;
            Activity.Load(bootstrap);
            await ReconcileUnconfirmedTransfersAsync(bootstrap.Jobs).ConfigureAwait(true);
            LoadRemoteEdits(bootstrap.RemoteEdits);
            Connections.Load(bootstrap.Profiles);
            Mirror.LoadProfiles(bootstrap.Profiles);
            Mirror.LoadDefinitions(bootstrap.MirrorDefinitions);
            RemoteTransfer.LoadProfiles(bootstrap.Profiles);
            await Mirror.ReconcileWorkspaceAsync(bootstrap.Jobs).ConfigureAwait(true);
            await RemoteTransfer.ReconcileWorkspaceAsync(bootstrap.Jobs).ConfigureAwait(true);
            ApplySessionBootstrap(bootstrap.Sessions);

            if (requestedProfileId is Guid id)
            {
                var profile = bootstrap.Profiles.FirstOrDefault(candidate => candidate.Id == id);
                if (profile is not null)
                {
                    try
                    {
                        AddSession(await _agent.ConnectAsync(profile).ConfigureAwait(true));
                    }
                    catch (Exception exception)
                    {
                        ReportUnconfirmedSessionConnect("Requested connection", exception);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            HasAgentError = true;
            AgentStatus = $"Agent unavailable · {exception.Message}";
            Activity.Log.Insert(0, new(DateTimeOffset.Now, "Error", "Agent", exception.Message));
        }
        finally
        {
            IsLoading = false;
            _initializationGate.Release();
        }
    }

    public async Task EnsureStateCurrentAsync()
    {
        if (Volatile.Read(ref _stateInvalidated) == 0) return;
        await _resyncGate.WaitAsync().ConfigureAwait(true);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Task.Delay(100).ConfigureAwait(true);
                if (Interlocked.Exchange(ref _stateInvalidated, 0) == 0) return;
                await InitializeAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _resyncGate.Release();
        }
    }

    public async Task CloseSessionAsync(SessionViewModel session)
    {
        try
        {
            if (!await _agent.DisconnectAsync(session.SessionId).ConfigureAwait(true))
                throw new InvalidOperationException("The Agent declined the session disconnect request.");
        }
        catch (Exception exception)
        {
            AgentStatus = $"Disconnect could not be confirmed · Session state may have changed. {exception.Message} Refreshing workspace state.";
            RequestStateRefresh();
            throw;
        }

        RemoveSession(session);
    }

    public async Task AddDefaultSessionAsync()
    {
        var profile = Connections.SelectedProfile ?? Connections.Profiles.FirstOrDefault();
        if (profile is null) return;
        try
        {
            AddSession(await _agent.ConnectAsync(profile).ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            ReportUnconfirmedSessionConnect("New session", exception);
        }
    }

    public Task<RemoteEditReview> ReviewRemoteEditAsync(string editId) => _agent.ReviewRemoteEditAsync(editId);

    public async Task<RemoteEditActionResult> ResolveRemoteEditAsync(string editId, string reviewToken, RemoteEditResolution resolution)
    {
        try
        {
            var result = await _agent.ResolveRemoteEditAsync(editId, reviewToken, resolution).ConfigureAwait(true);
            UpsertRemoteEdit(result.Session);
            return result;
        }
        catch (Exception exception)
        {
            HasAgentError = false;
            AgentStatus = $"Remote edit action could not be confirmed · The remote file or managed copy may have changed. {exception.Message} Refreshing workspace state.";
            Activity.Log.Insert(0, new(DateTimeOffset.Now, "Warning", "Remote edit", AgentStatus));
            RequestStateRefresh();
            throw;
        }
    }

    public void RequestWorkspaceRefresh() => RequestStateRefresh();

    public async Task<bool> CompleteRemoteEditAsync(string editId)
    {
        var completed = await _agent.CompleteRemoteEditAsync(editId).ConfigureAwait(true);
        if (completed) RemoveRemoteEdit(editId);
        return completed;
    }

    private void AddSession(Models.WorkspaceSessionSeed seed)
    {
        var profile = Connections.Profiles.FirstOrDefault(candidate => candidate.Id == seed.Snapshot.ProfileId);
        if (profile is null) return;
        var existing = Sessions.FirstOrDefault(session => session.SessionId == seed.Snapshot.SessionId);
        if (existing is not null)
        {
            existing.ApplySeed(seed, profile);
            SelectedSession = existing;
            return;
        }

        var session = CreateSession(seed, profile);
        Sessions.Add(session);
        SelectedSession = session;
        Console.LoadSessions(Sessions);
    }

    private void ApplySessionBootstrap(IReadOnlyList<Models.WorkspaceSessionSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var selectedSessionId = SelectedSession?.SessionId;
        var seenSessionIds = new HashSet<Guid>();
        var desiredSessionIds = new HashSet<Guid>();
        var desired = new List<(Models.WorkspaceSessionSeed Seed, ConnectionProfile Profile)>();
        foreach (var seed in seeds)
        {
            if (!seenSessionIds.Add(seed.Snapshot.SessionId))
            {
                Activity.Log.Insert(0, new(
                    DateTimeOffset.Now,
                    "Warning",
                    "Session restore",
                    $"The Agent returned duplicate saved tab {seed.Snapshot.SessionId}; only the first record was shown."));
                continue;
            }

            var profile = Connections.Profiles.FirstOrDefault(candidate => candidate.Id == seed.Snapshot.ProfileId);
            if (profile is null)
            {
                Activity.Log.Insert(0, new(
                    DateTimeOffset.Now,
                    "Warning",
                    "Session restore",
                    $"Saved tab {seed.Snapshot.SessionId} referenced a missing profile and was not shown."));
                continue;
            }
            desiredSessionIds.Add(seed.Snapshot.SessionId);
            desired.Add((seed, profile));
        }

        for (var desiredIndex = 0; desiredIndex < desired.Count; desiredIndex++)
        {
            var (seed, profile) = desired[desiredIndex];
            var session = Sessions.FirstOrDefault(candidate => candidate.SessionId == seed.Snapshot.SessionId);
            if (session is null)
            {
                session = CreateSession(seed, profile);
                Sessions.Insert(desiredIndex, session);
            }
            else
            {
                session.ApplySeed(seed, profile);
                var currentIndex = Sessions.IndexOf(session);
                if (currentIndex != desiredIndex) Sessions.Move(currentIndex, desiredIndex);
            }
        }

        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!desiredSessionIds.Contains(Sessions[index].SessionId)) Sessions.RemoveAt(index);
        }

        SelectedSession = selectedSessionId is { } selectedId
            ? Sessions.FirstOrDefault(candidate => candidate.SessionId == selectedId) ?? Sessions.FirstOrDefault()
            : Sessions.FirstOrDefault();
        Console.LoadSessions(Sessions);
    }

    private SessionViewModel CreateSession(Models.WorkspaceSessionSeed seed, ConnectionProfile profile)
    {
        var session = new SessionViewModel(_agent, seed, profile);
        session.JobQueued += (_, job) => Activity.Add(job);
        session.TransferOutcomeUnconfirmed += (_, submission) => RememberUnconfirmedTransfer(submission);
        session.StateRefreshRequested += (_, _) => RequestStateRefresh();
        session.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(SessionViewModel.IsConnected), StringComparison.Ordinal))
                Console.LoadSessions(Sessions);
        };
        ApplyTransferSubmissionGuard(session);
        return session;
    }

    private void ReportError(Exception exception)
    {
        AgentStatus = $"Agent error · {exception.Message}";
        IsLoading = false;
    }

    private void ReportUnconfirmedSessionConnect(string operation, Exception exception)
    {
        HasAgentError = false;
        AgentStatus = $"{operation} could not be confirmed · Session state may have changed. {exception.Message} Refreshing workspace state.";
        Activity.Log.Insert(0, new(DateTimeOffset.Now, "Warning", "Agent", AgentStatus));
        RequestStateRefresh();
    }

    private void Agent_EventReceived(object? sender, EngineEvent engineEvent)
    {
        if (_uiContext is null) return;
        _uiContext.Post(_ => ApplyAgentEvent(engineEvent), null);
    }

    private void Agent_StateInvalidated(object? sender, EventArgs e)
    {
        RequestStateRefresh();
    }

    private void RequestStateRefresh()
    {
        Interlocked.Exchange(ref _stateInvalidated, 1);
        _uiContext?.Post(_ => _ = EnsureStateCurrentAsync(), null);
    }

    private void ApplyAgentEvent(EngineEvent engineEvent)
    {
        if (engineEvent.Name == "job.changed" && DeserializePayload<JobSnapshot>(engineEvent.Payload) is { } job)
        {
            Activity.Add(job);
            ResolveUnconfirmedTransfer(job, $"The Agent confirmed transfer job {job.Id}.");
            Mirror.ObserveJob(job);
            RemoteTransfer.ObserveJob(job);
        }
        else if (engineEvent.Name == "profile.saved" && DeserializePayload<ConnectionProfile>(engineEvent.Payload) is { } profile)
        {
            Connections.Upsert(profile);
            foreach (var session in Sessions.Where(candidate => candidate.ProfileId == profile.Id)) session.UpdateProfile(profile);
            // Identity-changing saves invalidate durable disconnected tabs in the
            // Agent. A bootstrap is authoritative for both that removal and
            // cosmetic profile updates that may race with this event.
            RequestStateRefresh();
        }
        else if (engineEvent.Name is "profile.deleted" or "session.connected")
        {
            // These events are post-commit signals. Always load an authoritative
            // bootstrap so a prior lost-reply refresh cannot win a race with the commit.
            RequestStateRefresh();
        }
        else if (engineEvent.Name == "session.disconnected" && PayloadGuid(engineEvent.Payload) is { } sessionId)
        {
            var session = Sessions.FirstOrDefault(candidate => candidate.SessionId == sessionId);
            if (session is not null) RemoveSession(session);
        }
        else if (engineEvent.Name == "remoteEdit.localChanged" && DeserializePayload<RemoteEditLocalChange>(engineEvent.Payload) is { } change)
        {
            Activity.Log.Insert(0, new(engineEvent.Timestamp,
                change.Kind == RemoteEditLocalChangeKind.Saved ? "Info" : "Warning", "Remote edit", change.Message));
            ActiveRemoteEdits.FirstOrDefault(candidate => string.Equals(candidate.EditId, change.EditId, StringComparison.Ordinal))?.Apply(change);
            RemoteEditLocalChanged?.Invoke(change);
        }
        else if (engineEvent.Name == "remoteEdit.started" && DeserializePayload<RemoteEditSession>(engineEvent.Payload) is { } edit)
        {
            UpsertRemoteEdit(edit);
        }
        else if (engineEvent.Name == "remoteEdit.completed" && DeserializePayload<RemoteEditCompleted>(engineEvent.Payload) is { } completed)
        {
            RemoveRemoteEdit(completed.EditId);
        }
        else
        {
            Activity.Log.Insert(0, new(engineEvent.Timestamp, "Info", engineEvent.Kind.ToString(), engineEvent.Name));
        }
    }

    private static T? DeserializePayload<T>(object? payload) where T : class => payload switch
    {
        T typed => typed,
        JsonElement element => element.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        _ => null,
    };

    private static Guid? PayloadGuid(object? payload) => payload switch
    {
        Guid value => value,
        JsonElement { ValueKind: JsonValueKind.String } element when element.TryGetGuid(out var value) => value,
        _ => null,
    };

    private void LoadRemoteEdits(IEnumerable<RemoteEditSession> edits)
    {
        var selectedId = SelectedActiveRemoteEdit?.EditId;
        ActiveRemoteEdits.Clear();
        foreach (var edit in edits.OrderByDescending(static edit => edit.LastLocalChangeAt ?? DateTimeOffset.MinValue))
            ActiveRemoteEdits.Add(new(edit));
        SelectedActiveRemoteEdit = selectedId is null
            ? ActiveRemoteEdits.FirstOrDefault()
            : ActiveRemoteEdits.FirstOrDefault(candidate => string.Equals(candidate.EditId, selectedId, StringComparison.Ordinal))
                ?? ActiveRemoteEdits.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveRemoteEditCount));
    }

    private void UpsertRemoteEdit(RemoteEditSession edit)
    {
        var existing = ActiveRemoteEdits.FirstOrDefault(candidate => string.Equals(candidate.EditId, edit.EditId, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Update(edit);
            return;
        }

        var item = new RemoteEditItemViewModel(edit);
        ActiveRemoteEdits.Insert(0, item);
        SelectedActiveRemoteEdit ??= item;
        OnPropertyChanged(nameof(ActiveRemoteEditCount));
    }

    private void RemoveRemoteEdit(string editId)
    {
        var existing = ActiveRemoteEdits.FirstOrDefault(candidate => string.Equals(candidate.EditId, editId, StringComparison.Ordinal));
        if (existing is null) return;
        ActiveRemoteEdits.Remove(existing);
        if (ReferenceEquals(SelectedActiveRemoteEdit, existing)) SelectedActiveRemoteEdit = ActiveRemoteEdits.FirstOrDefault();
        OnPropertyChanged(nameof(ActiveRemoteEditCount));
    }

    private void RemoveSession(SessionViewModel session)
    {
        var index = Sessions.IndexOf(session);
        if (index < 0) return;
        Sessions.RemoveAt(index);
        if (ReferenceEquals(SelectedSession, session))
        {
            SelectedSession = Sessions.ElementAtOrDefault(Math.Clamp(index - 1, 0, Math.Max(0, Sessions.Count - 1)));
        }
        Console.LoadSessions(Sessions);
    }

    private void RememberUnconfirmedTransfer(UnconfirmedTransferSubmission submission)
    {
        if (_unconfirmedTransfers.TryGetValue(submission.Plan.Id, out var existing))
        {
            if (existing.Submission != submission)
            {
                AgentStatus = $"Transfer plan {submission.Plan.Id} has conflicting unconfirmed details. Queueing remains blocked while workspace state is refreshed.";
            }
        }
        else
        {
            _unconfirmedTransfers.Add(submission.Plan.Id, new(submission));
            OnPropertyChanged(nameof(UnconfirmedTransferCount));
        }
        UpdateTransferSubmissionGuards();
        RequestStateRefresh();
    }

    private async Task ReconcileUnconfirmedTransfersAsync(IReadOnlyList<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        foreach (var state in _unconfirmedTransfers.Values.ToArray())
        {
            var matchingJob = jobs.FirstOrDefault(job => job.Id == state.Submission.Plan.Id);
            if (matchingJob is not null)
            {
                if (!MatchesUnconfirmedTransfer(state, matchingJob))
                {
                    state.ReconciliationAttempted = true;
                    ReportUnconfirmedTransferIdentityMismatch(state, matchingJob);
                    continue;
                }
                ResolveUnconfirmedTransfer(matchingJob, $"The refreshed workspace confirms transfer job {matchingJob.Id}.");
                continue;
            }
            if (state.ReconciliationAttempted)
            {
                AgentStatus = "A transfer submission remains unconfirmed after its one same-ID reconciliation attempt. New transfer plans stay blocked until the Agent reports the original job or rejects it authoritatively.";
                continue;
            }

            state.ReconciliationAttempted = true;
            try
            {
                var job = await _agent.EnqueueTransferAsync(
                    state.Submission.SessionId,
                    state.Submission.Plan).ConfigureAwait(true);
                ResolveUnconfirmedTransfer(job, job.State == JobState.Failed
                    ? $"The Agent reconciled transfer {job.Id} as failed: {job.Error?.Message ?? job.Status ?? "The transfer could not start."}"
                    : $"The Agent reconciled the original transfer plan as job {job.Id}.");
            }
            catch (AgentRequestOutcomeUnknownException exception)
            {
                AgentStatus = $"The original transfer remains unconfirmed after its one same-ID reconciliation attempt. {exception.Message} No new plan ID will be created.";
            }
            catch (AgentRequestRejectedException exception)
            {
                _unconfirmedTransfers.Remove(state.Submission.Plan.Id);
                OnPropertyChanged(nameof(UnconfirmedTransferCount));
                AgentStatus = $"The Agent authoritatively rejected the original transfer plan. {exception.Message} Refreshed Activity has no matching job; a fresh transfer may now be created.";
            }
            catch (Exception exception)
            {
                AgentStatus = $"The original transfer remains unconfirmed because reconciliation was interrupted or invalid. {exception.Message} No new plan ID will be created.";
            }
        }
        UpdateTransferSubmissionGuards();
    }

    private void ResolveUnconfirmedTransfer(JobSnapshot job, string status)
    {
        if (!_unconfirmedTransfers.TryGetValue(job.Id, out var state)) return;
        if (!MatchesUnconfirmedTransfer(state, job))
        {
            ReportUnconfirmedTransferIdentityMismatch(state, job);
            return;
        }
        _unconfirmedTransfers.Remove(job.Id);
        Activity.Add(job);
        OnPropertyChanged(nameof(UnconfirmedTransferCount));
        AgentStatus = status;
        UpdateTransferSubmissionGuards();
    }

    private void UpdateTransferSubmissionGuards()
    {
        foreach (var session in Sessions) ApplyTransferSubmissionGuard(session);
    }

    private void ApplyTransferSubmissionGuard(SessionViewModel session) =>
        session.SetUnconfirmedTransferIds(_unconfirmedTransfers.Keys);

    private static bool MatchesUnconfirmedTransfer(UnconfirmedTransferState state, JobSnapshot job) =>
        job.Kind == JobKind.Transfer && job.ProfileId == state.Submission.Plan.ProfileId;

    private void ReportUnconfirmedTransferIdentityMismatch(UnconfirmedTransferState state, JobSnapshot job)
    {
        AgentStatus = $"Job {job.Id} does not match the unconfirmed transfer kind and profile. " +
            "The original plan remains blocked; no fresh transfer ID will be created.";
        Activity.Log.Insert(0, new(
            DateTimeOffset.Now,
            "Warning",
            "Transfer reconciliation",
            $"Plan {state.Submission.Plan.Id} expected Transfer/{state.Submission.Plan.ProfileId}, " +
                $"but Activity reported {job.Kind}/{job.ProfileId}."));
        UpdateTransferSubmissionGuards();
    }

    private sealed class UnconfirmedTransferState(UnconfirmedTransferSubmission submission)
    {
        public UnconfirmedTransferSubmission Submission { get; } = submission;
        public bool ReconciliationAttempted { get; set; }
    }
}
