namespace LFTPPilot.Agent;

internal sealed class AgentIdleExitPolicy(Func<bool> hasBackgroundWork, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TaskCompletionSource _stateChanged = NewSignal();
    private int _connectedClients;
    private bool _agentReady;

    internal void AgentReady()
    {
        lock (_gate) _agentReady = true;
        SignalStateChanged();
    }

    internal void ClientConnected()
    {
        lock (_gate)
        {
            checked { _connectedClients++; }
        }
        SignalStateChanged();
    }

    internal void ClientDisconnected()
    {
        lock (_gate)
        {
            if (_connectedClients == 0)
                throw new InvalidOperationException("The Agent client count is already zero.");
            _connectedClients--;
        }
        SignalStateChanged();
    }

    internal void BackgroundWorkChanged() => SignalStateChanged();

    internal async Task WaitForIdleExitAsync(TimeSpan idleDelay, CancellationToken cancellationToken = default)
    {
        if (idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stateChanged = GetStateChangedTask();
            if (!IsEligibleForIdleExit())
            {
                await stateChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(idleDelay, _timeProvider, delayCancellation.Token);
            var completed = await Task.WhenAny(stateChanged, delay).ConfigureAwait(false);
            if (completed == delay)
            {
                await delay.ConfigureAwait(false);
                if (IsEligibleForIdleExit()) return;
                continue;
            }

            await delayCancellation.CancelAsync().ConfigureAwait(false);
            await stateChanged.ConfigureAwait(false);
        }
    }

    private bool IsEligibleForIdleExit()
    {
        lock (_gate)
        {
            if (!_agentReady || _connectedClients != 0) return false;
        }
        return !hasBackgroundWork();
    }

    private Task GetStateChangedTask()
    {
        lock (_gate) return _stateChanged.Task;
    }

    private void SignalStateChanged()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            signal = _stateChanged;
            _stateChanged = NewSignal();
        }
        signal.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
