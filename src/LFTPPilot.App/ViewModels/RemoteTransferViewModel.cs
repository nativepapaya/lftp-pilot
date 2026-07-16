using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class RemoteTransferViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private ConnectionProfile? _sourceProfile;
    private ConnectionProfile? _destinationProfile;
    private string _sourcePath = "/srv/releases/file.zip";
    private string _destinationPath = "/incoming/file.zip";
    private bool _overwrite;
    private bool _routeApproved;
    private RemoteTransferPlan? _reviewedPlan;
    private RemoteTransferPlan? _uncertainPlan;
    private int _planRevision;
    private bool _awaitingWorkspaceResync;
    private bool _uncertainReconciliationAttempted;
    private int _reconciliationInProgress;
    private string _status = "Choose two connections and create a route plan. No transfer starts until you review and enqueue it.";

    public RemoteTransferViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        PlanCommand = new AsyncRelayCommand(_ => PlanAsync(), _ => CanPlan, ReportError);
        EnqueueCommand = new AsyncRelayCommand(_ => EnqueueAsync(), _ => CanEnqueue, ReportError);
    }

    public event EventHandler<JobSnapshot>? JobQueued;
    public event EventHandler? StateRefreshRequested;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public AsyncRelayCommand PlanCommand { get; }
    public AsyncRelayCommand EnqueueCommand { get; }
    public ConnectionProfile? SourceProfile { get => _sourceProfile; set { if (SetProperty(ref _sourceProfile, value)) InvalidatePlan(); } }
    public ConnectionProfile? DestinationProfile { get => _destinationProfile; set { if (SetProperty(ref _destinationProfile, value)) InvalidatePlan(); } }
    public string SourcePath { get => _sourcePath; set { if (SetProperty(ref _sourcePath, value)) InvalidatePlan(); } }
    public string DestinationPath { get => _destinationPath; set { if (SetProperty(ref _destinationPath, value)) InvalidatePlan(); } }
    public bool Overwrite { get => _overwrite; set { if (SetProperty(ref _overwrite, value)) InvalidatePlan(); } }
    public bool RouteApproved { get => _routeApproved; set { if (SetProperty(ref _routeApproved, value)) NotifyPlanState(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasPlan => _reviewedPlan is not null;
    public bool CanPlan => !_awaitingWorkspaceResync && SourceProfile is not null && DestinationProfile is not null && SourceProfile.Id != DestinationProfile.Id &&
        !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(DestinationPath);
    public bool CanEnqueue => HasPlan && RouteApproved;
    public string RouteReviewTitle => _reviewedPlan?.Mode == RemoteTransferMode.Fxp ? "FXP preferred" : "Client relay required";
    public string RouteReviewNote
    {
        get
        {
            if (_reviewedPlan is not { } plan) return string.Empty;
            var route = plan.Mode == RemoteTransferMode.Fxp
                ? "LFTP will prefer direct server-to-server FXP. If either server refuses FXP, LFTP can relay through this PC."
                : "LFTP must relay the file through this PC, so both connections must remain active until completion.";
            var overwrite = plan.Overwrite ? "An existing destination file may be overwritten." : "An existing destination file will not be overwritten.";
            return $"{route} Source: {plan.SourcePath} Destination: {plan.DestinationPath} {overwrite}";
        }
    }

    public void LoadProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        var sourceId = SourceProfile?.Id;
        var destinationId = DestinationProfile?.Id;
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(profile);
        SourceProfile = sourceId is { } source ? Profiles.FirstOrDefault(profile => profile.Id == source) : Profiles.FirstOrDefault();
        DestinationProfile = destinationId is { } destination ? Profiles.FirstOrDefault(profile => profile.Id == destination) : Profiles.Skip(1).FirstOrDefault();
        InvalidatePlan();
    }

    public async Task ReconcileWorkspaceAsync(IReadOnlyList<JobSnapshot> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (_uncertainPlan is not { } uncertainPlan ||
            Interlocked.Exchange(ref _reconciliationInProgress, 1) != 0) return;
        try
        {
            var sameIdJob = jobs.FirstOrDefault(job => job.Id == uncertainPlan.Id);
            if (sameIdJob is not null)
            {
                if (!IsMatchingJob(sameIdJob, uncertainPlan))
                {
                    _uncertainReconciliationAttempted = true;
                    Status = "The refreshed workspace returned the original plan identifier on a job with a different kind or source profile. That identity collision consumed automatic reconciliation; fresh planning remains blocked until an exact remote-transfer job is confirmed or the Agent rejects the original plan authoritatively.";
                    return;
                }
                ResolveUncertainPlan($"The refreshed workspace confirms remote transfer job {sameIdJob.Id}.");
                PublishJobQueued(sameIdJob);
                return;
            }
            if (_uncertainReconciliationAttempted)
            {
                Status = "The original remote transfer is still unconfirmed and no matching job is visible. Automatic reconciliation has already been attempted; fresh planning remains blocked until the Agent reports the original job or rejects that plan authoritatively.";
                return;
            }

            try
            {
                _uncertainReconciliationAttempted = true;
                var result = await _agent.EnqueueRemoteTransferAsync(uncertainPlan).ConfigureAwait(true);
                if (!IsMatchingJob(result.Job, uncertainPlan) || result.Mode != uncertainPlan.Mode)
                {
                    Status = "The Agent returned a reconciliation result with the original plan identifier but a different job kind, source profile, or routing mode. The one safe same-plan reconciliation attempt was consumed; fresh planning remains blocked pending an exact job event or workspace snapshot.";
                    return;
                }
                ResolveUncertainPlan(result.Job.State == JobState.Failed
                    ? $"The Agent reconciled the original reviewed plan as failed: {result.Job.Error?.Message ?? result.Job.Status ?? "The transfer could not start."}"
                    : $"The Agent reconciled the original reviewed plan. {result.RoutingNote}");
                PublishJobQueued(result.Job);
            }
            catch (AgentRequestOutcomeUnknownException exception)
            {
                Status = $"The original remote transfer is still unconfirmed. It has not been submitted with a new plan ID. {exception.Message} Refresh workspace state again before creating another route plan.";
            }
            catch (AgentRequestRejectedException exception)
            {
                ResolveUncertainPlan($"The refreshed Agent did not accept the original reviewed plan. {exception.Message} No matching job exists; create and review a fresh route plan.");
            }
            catch (Exception exception) when (!IsFatalRuntimeException(exception))
            {
                Status = $"The original remote transfer remains unconfirmed because its reconciliation reply was invalid or interrupted. {exception.Message} Fresh planning remains blocked until the Agent reports the original job or rejects that plan authoritatively.";
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
        if (_uncertainPlan is not { } uncertainPlan || uncertainPlan.Id != job.Id) return;
        if (!IsMatchingJob(job, uncertainPlan))
        {
            _uncertainReconciliationAttempted = true;
            Status = "The Agent reported the original plan identifier on a job with a different kind or source profile. That identity collision consumed automatic reconciliation; fresh planning remains blocked until an exact remote-transfer job is confirmed.";
            return;
        }
        ResolveUncertainPlan($"The Agent confirmed remote transfer job {job.Id}.");
    }

    private async Task PlanAsync()
    {
        if (!CanPlan || SourceProfile is null || DestinationProfile is null) return;
        var revision = _planRevision;
        var request = new RemoteTransferPlan(
            Guid.NewGuid(), SourceProfile.Id, DestinationProfile.Id, SourcePath.Trim(), DestinationPath.Trim(), RemoteTransferMode.ClientRelay, Overwrite);
        Status = "Checking the route with the Agent…";
        var result = await _agent.PlanRemoteTransferAsync(request).ConfigureAwait(true);
        if (revision != _planRevision)
        {
            Status = "Inputs changed while the route was checked. Create a fresh plan.";
            return;
        }

        _reviewedPlan = result;
        RouteApproved = false;
        Status = "Route ready for explicit review. Nothing has been queued yet.";
        NotifyPlanState();
    }

    private async Task EnqueueAsync()
    {
        if (_reviewedPlan is null || !CanEnqueue) return;
        var reviewedPlan = _reviewedPlan;
        _reviewedPlan = null;
        RouteApproved = false;
        NotifyPlanState();
        Status = "Submitting the reviewed remote transfer to the Agent...";
        try
        {
            var result = await _agent.EnqueueRemoteTransferAsync(reviewedPlan).ConfigureAwait(true);
            if (!IsMatchingJob(result.Job, reviewedPlan) || result.Mode != reviewedPlan.Mode)
                throw new InvalidDataException("The Agent returned a job or routing mode that did not match the reviewed remote-transfer plan.");
            Status = result.Job.State == JobState.Failed
                ? $"Remote transfer could not start; the Agent recorded a failed job. {result.Job.Error?.Message ?? result.Job.Status}"
                : $"Remote transfer queued. {result.RoutingNote}";
            PublishJobQueued(result.Job);
        }
        catch (AgentRequestOutcomeUnknownException exception)
        {
            MarkPlanUncertain(reviewedPlan, exception);
        }
        catch (AgentRequestRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatalRuntimeException(exception))
        {
            MarkPlanUncertain(reviewedPlan, exception);
        }
    }

    private void InvalidatePlan()
    {
        _planRevision++;
        _reviewedPlan = null;
        RouteApproved = false;
        NotifyPlanState();
    }

    private void NotifyPlanState()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(CanPlan));
        OnPropertyChanged(nameof(CanEnqueue));
        OnPropertyChanged(nameof(RouteReviewTitle));
        OnPropertyChanged(nameof(RouteReviewNote));
        PlanCommand.NotifyCanExecuteChanged();
        EnqueueCommand.NotifyCanExecuteChanged();
    }

    private void ResolveUncertainPlan(string status)
    {
        _uncertainPlan = null;
        _uncertainReconciliationAttempted = false;
        _awaitingWorkspaceResync = false;
        Status = status;
        NotifyPlanState();
    }

    private void MarkPlanUncertain(RemoteTransferPlan plan, Exception exception)
    {
        _uncertainPlan = plan;
        _uncertainReconciliationAttempted = false;
        _awaitingWorkspaceResync = true;
        Status = $"Remote transfer submission could not be confirmed. The job may already exist; do not submit it again. {exception.Message} Review refreshed jobs before creating a fresh route plan.";
        NotifyPlanState();
        StateRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsMatchingJob(JobSnapshot job, RemoteTransferPlan plan) =>
        job.Id == plan.Id &&
        job.Kind == JobKind.RemoteTransfer &&
        job.ProfileId == plan.SourceProfileId;

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
                // Presentation observers cannot turn a confirmed Agent result
                // into an uncertain remote-transfer outcome.
            }
        }
    }

    private static bool IsFatalRuntimeException(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    private void ReportError(Exception exception) => Status = exception.Message;
}
