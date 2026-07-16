using LFTPPilot.App.Infrastructure;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class RemoteEditItemViewModel(RemoteEditSession snapshot) : ObservableObject
{
    private RemoteEditSession _snapshot = snapshot;

    public RemoteEditSession Snapshot => _snapshot;
    public string EditId => _snapshot.EditId;
    public Guid SessionId => _snapshot.SessionId;
    public string DisplayName => _snapshot.DisplayName;
    public string RemotePath => _snapshot.RemotePath;
    public string LocalPath => _snapshot.LocalPath;
    public bool Dirty => _snapshot.Dirty;
    public bool WatcherFailed => _snapshot.WatcherFailed;
    public DateTimeOffset? LastLocalChangeAt => _snapshot.LastLocalChangeAt;
    public string StatusText => WatcherFailed
        ? "Save monitoring needs attention"
        : Dirty
            ? "Local changes are waiting for review"
            : "Monitoring the managed copy";
    public string LastLocalChangeText => LastLocalChangeAt is { } changedAt
        ? $"Last local change {changedAt.ToLocalTime():g}"
        : "No local change detected";
    public string BaselineText =>
        $"Remote baseline: {_snapshot.Baseline.Size:N0} bytes | {_snapshot.Baseline.ModifiedAt.ToLocalTime():g}";

    public void Update(RemoteEditSession snapshot)
    {
        if (!string.Equals(EditId, snapshot.EditId, StringComparison.Ordinal))
            throw new InvalidOperationException("A managed edit row cannot be replaced with a different edit.");

        _snapshot = snapshot;
        RaiseSnapshotProperties();
    }

    public void Apply(RemoteEditLocalChange change)
    {
        if (!string.Equals(EditId, change.EditId, StringComparison.Ordinal)) return;
        _snapshot = change.Kind == RemoteEditLocalChangeKind.WatcherError
            ? _snapshot with { WatcherFailed = true }
            : _snapshot with { Dirty = true, LastLocalChangeAt = change.DetectedAt };
        RaiseSnapshotProperties();
    }

    private void RaiseSnapshotProperties()
    {
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RemotePath));
        OnPropertyChanged(nameof(LocalPath));
        OnPropertyChanged(nameof(Dirty));
        OnPropertyChanged(nameof(WatcherFailed));
        OnPropertyChanged(nameof(LastLocalChangeAt));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LastLocalChangeText));
        OnPropertyChanged(nameof(BaselineText));
    }
}
