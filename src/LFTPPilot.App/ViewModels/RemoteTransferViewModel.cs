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
    private int _planRevision;
    private string _status = "Choose two connections and create a route plan. No transfer starts until you review and enqueue it.";

    public RemoteTransferViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        PlanCommand = new AsyncRelayCommand(_ => PlanAsync(), _ => CanPlan, ReportError);
        EnqueueCommand = new AsyncRelayCommand(_ => EnqueueAsync(), _ => CanEnqueue, ReportError);
    }

    public event EventHandler<JobSnapshot>? JobQueued;
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
    public bool CanPlan => SourceProfile is not null && DestinationProfile is not null && SourceProfile.Id != DestinationProfile.Id &&
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
        var result = await _agent.EnqueueRemoteTransferAsync(_reviewedPlan).ConfigureAwait(true);
        Status = $"Remote transfer queued. {result.RoutingNote}";
        JobQueued?.Invoke(this, result.Job);
        _reviewedPlan = null;
        RouteApproved = false;
        NotifyPlanState();
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

    private void ReportError(Exception exception) => Status = exception.Message;
}
