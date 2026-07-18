using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class ActivityCenterViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private bool _isExpanded = true;

    public ActivityCenterViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        CancelJobCommand = new AsyncRelayCommand(CancelJobAsync, CanCancelJob, ReportError);
        RetryJobCommand = new AsyncRelayCommand(RetryJobAsync, CanRetryJob, ReportError);
    }

    public ObservableCollection<JobSnapshot> Jobs { get; } = [];
    public ObservableCollection<HistoryRecord> History { get; } = [];
    public ObservableCollection<ActivityLogEntry> Log { get; } = [];
    public AsyncRelayCommand CancelJobCommand { get; }
    public AsyncRelayCommand RetryJobCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public int ActiveCount => Jobs.Count(job => job.State is JobState.Queued or JobState.Running or JobState.Scheduled or JobState.Paused);

    public void Load(UiWorkspaceBootstrap bootstrap)
    {
        Replace(Jobs, bootstrap.Jobs);
        Replace(History, bootstrap.History);
        Replace(Log, bootstrap.Log);
        OnPropertyChanged(nameof(ActiveCount));
        CancelJobCommand.NotifyCanExecuteChanged();
        RetryJobCommand.NotifyCanExecuteChanged();
    }

    public void Add(JobSnapshot job)
    {
        var existing = Jobs.FirstOrDefault(candidate => candidate.Id == job.Id);
        if (existing is null) Jobs.Insert(0, job);
        else Jobs[Jobs.IndexOf(existing)] = job;
        OnPropertyChanged(nameof(ActiveCount));
        CancelJobCommand.NotifyCanExecuteChanged();
        RetryJobCommand.NotifyCanExecuteChanged();
    }

    public void AddHistory(HistoryRecord record)
    {
        var existing = History.FirstOrDefault(candidate => candidate.Id == record.Id);
        if (existing is not null) History.Remove(existing);
        var insertionIndex = 0;
        while (insertionIndex < History.Count && History[insertionIndex].FinishedAt >= record.FinishedAt)
            insertionIndex++;
        History.Insert(insertionIndex, record);
        while (History.Count > HistoryRecordPolicy.MaximumBootstrapRecords)
            History.RemoveAt(History.Count - 1);
    }

    public bool ApplyProgress(TransferProgressSnapshot progress)
    {
        var existing = Jobs.FirstOrDefault(candidate => candidate.Id == progress.JobId);
        if (existing is not { State: JobState.Running }) return false;
        var status = $"{FormatBytes(progress.BytesTransferred)} of {FormatBytes(progress.TotalBytes)}";
        if (progress.BytesPerSecond is { } rate) status += $" · {FormatBytes(rate)}/s";
        var updated = existing with
        {
            Progress = progress.Progress,
            Status = status,
            UpdatedAt = progress.ObservedAt > existing.UpdatedAt ? progress.ObservedAt : existing.UpdatedAt,
        };
        Jobs[Jobs.IndexOf(existing)] = updated;
        return true;
    }

    private static bool CanCancelJob(object? parameter) =>
        parameter is JobSnapshot { State: JobState.Queued or JobState.Running or JobState.Paused or JobState.Scheduled };

    private static bool CanRetryJob(object? parameter) => parameter is JobSnapshot { CanRetry: true };

    private async Task CancelJobAsync(object? parameter)
    {
        if (parameter is not JobSnapshot job || !CanCancelJob(job)) return;
        if (!await _agent.CancelJobAsync(job.Id).ConfigureAwait(true))
            Log.Insert(0, new(DateTimeOffset.Now, "Warning", "Agent", $"Job '{job.DisplayName}' could not be cancelled because its state already changed."));
    }

    private async Task RetryJobAsync(object? parameter)
    {
        if (parameter is not JobSnapshot job || !CanRetryJob(job)) return;
        Add(await _agent.RetryJobAsync(job.Id).ConfigureAwait(true));
    }

    private void ReportError(Exception exception) =>
        Log.Insert(0, new(DateTimeOffset.Now, "Error", "Agent", exception.Message));

    private static string FormatBytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
