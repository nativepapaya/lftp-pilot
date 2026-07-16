using System.Collections.ObjectModel;
using LFTPPilot.App.Infrastructure;
using LFTPPilot.App.Models;
using LFTPPilot.App.Services;
using LFTPPilot.Core;

namespace LFTPPilot.App.ViewModels;

public sealed class ConsoleViewModel : ObservableObject
{
    private readonly IAgentWorkspaceClient _agent;
    private SessionViewModel? _selectedSession;
    private string _commandText = string.Empty;
    private string? _status;

    public ConsoleViewModel(IAgentWorkspaceClient agent)
    {
        _agent = agent;
        ExecuteCommand = new AsyncRelayCommand(_ => ExecuteAsync(), _ => SelectedSession is not null && !string.IsNullOrWhiteSpace(CommandText), ReportError);
    }

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ConsoleLine> Lines { get; } = [];
    public AsyncRelayCommand ExecuteCommand { get; }
    public SessionViewModel? SelectedSession { get => _selectedSession; set { if (SetProperty(ref _selectedSession, value)) ExecuteCommand.NotifyCanExecuteChanged(); } }
    public string CommandText { get => _commandText; set { if (SetProperty(ref _commandText, value)) ExecuteCommand.NotifyCanExecuteChanged(); } }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    public void LoadSessions(IEnumerable<SessionViewModel> sessions)
    {
        Sessions.Clear();
        foreach (var session in sessions) Sessions.Add(session);
        SelectedSession ??= Sessions.FirstOrDefault();
    }

    private async Task ExecuteAsync()
    {
        if (SelectedSession is null) return;
        var command = CommandText.Trim();
        var decision = SafeConsolePolicy.Evaluate(command, localShellEnabled: false);
        if (!decision.Allowed)
        {
            Status = decision.Reason;
            return;
        }

        Lines.Add(new ConsoleLine(DateTimeOffset.Now, "input", command));
        CommandText = string.Empty;
        var output = await _agent.ExecuteConsoleAsync(SelectedSession.SessionId, command).ConfigureAwait(true);
        foreach (var line in output) Lines.Add(new ConsoleLine(DateTimeOffset.Now, "output", line));
        Status = null;
    }

    private void ReportError(Exception exception)
    {
        Status = exception.Message;
        Lines.Add(new ConsoleLine(DateTimeOffset.Now, "error", exception.Message));
    }
}
