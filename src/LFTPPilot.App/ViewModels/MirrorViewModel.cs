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
    private string _status = "Create a dry-run preview before starting.";

    public MirrorViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => SelectedProfile is not null, ReportError);
        RunCommand = new AsyncRelayCommand(_ => RunAsync(), _ => CanRun, ReportError);
    }

    public event EventHandler<JobSnapshot>? JobQueued;
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<MirrorAction> PreviewActions { get; } = [];
    public IReadOnlyList<MirrorDirection> Directions { get; } = Enum.GetValues<MirrorDirection>();
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand RunCommand { get; }

    public ConnectionProfile? SelectedProfile { get => _selectedProfile; set { if (SetProperty(ref _selectedProfile, value)) { PreviewCommand.NotifyCanExecuteChanged(); InvalidatePreview(); } } }
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
    public bool CanRun => HasPreview && (!RequiresDeletionApproval || DeletionsApproved);

    public void LoadProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        Profiles.Clear();
        foreach (var profile in profiles) Profiles.Add(profile);
        SelectedProfile ??= Profiles.FirstOrDefault();
    }

    private async Task PreviewAsync()
    {
        if (SelectedProfile is null) return;
        var definition = new MirrorDefinition(
            Guid.NewGuid(), SelectedProfile.Id, Name.Trim(), Direction, LocalRoot.Trim(), RemoteRoot.Trim(),
            SplitPatterns(Includes), SplitPatterns(Excludes), DeleteExtraneous, ParallelFiles, SegmentsPerFile);
        Status = "Running isolated LFTP dry run…";
        _currentPreview = await _agent.PreviewMirrorAsync(definition).ConfigureAwait(true);
        PreviewActions.Clear();
        foreach (var action in _currentPreview.Preview.Actions) PreviewActions.Add(action);
        DeletionsApproved = false;
        Status = $"Preview created · {PreviewActions.Count} proposed action{(PreviewActions.Count == 1 ? string.Empty : "s")}";
        NotifyPreviewState();
    }

    private async Task RunAsync()
    {
        if (_currentPreview is null || !CanRun) return;
        var job = await _agent.ApproveMirrorAsync(_currentPreview, DeletionsApproved).ConfigureAwait(true);
        Status = "Mirror queued from the reviewed definition.";
        JobQueued?.Invoke(this, job);
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        if (_currentPreview is null) return;
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
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    private static ImmutableArray<string> SplitPatterns(string value) => value
        .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToImmutableArray();

    private void ReportError(Exception exception) => Status = exception.Message;
}
