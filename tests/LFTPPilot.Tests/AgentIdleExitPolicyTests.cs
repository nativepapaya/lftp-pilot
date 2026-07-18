using LFTPPilot.Agent;

namespace LFTPPilot.Tests;

public sealed class AgentIdleExitPolicyTests
{
    [Fact]
    public async Task AgentDoesNotIdleExitBeforeItsWorkspaceIsReady()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new AgentIdleExitPolicy(() => false, time);
        using var cancellation = new CancellationTokenSource();
        var wait = policy.WaitForIdleExitAsync(TimeSpan.FromMinutes(2), cancellation.Token);

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(wait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task ReadyAgentWithoutAConnectedAppExitsAfterItsGracePeriod()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new AgentIdleExitPolicy(() => false, time);
        policy.AgentReady();
        var wait = policy.WaitForIdleExitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(2));
        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisconnectedIdleAgentExitsAfterItsGracePeriod()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new AgentIdleExitPolicy(() => false, time);
        policy.AgentReady();
        policy.ClientConnected();
        policy.ClientDisconnected();
        var wait = policy.WaitForIdleExitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(2));
        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BackgroundWorkKeepsDisconnectedAgentAliveUntilWorkFinishes()
    {
        var hasBackgroundWork = true;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new AgentIdleExitPolicy(() => hasBackgroundWork, time);
        policy.AgentReady();
        policy.ClientConnected();
        policy.ClientDisconnected();
        var wait = policy.WaitForIdleExitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(wait.IsCompleted);
        hasBackgroundWork = false;
        policy.BackgroundWorkChanged();
        await WaitForTimerCountAsync(time, 1);
        time.Advance(TimeSpan.FromMinutes(2));

        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReconnectedAppCancelsAnInProgressIdleCountdown()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var policy = new AgentIdleExitPolicy(() => false, time);
        policy.AgentReady();
        policy.ClientConnected();
        policy.ClientDisconnected();
        var wait = policy.WaitForIdleExitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMinutes(1));
        policy.ClientConnected();
        await WaitForTimerCountAsync(time, 0);
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(wait.IsCompleted);
        policy.ClientDisconnected();
        await WaitForTimerCountAsync(time, 1);
        time.Advance(TimeSpan.FromMinutes(2));

        await wait.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    private static async Task WaitForTimerCountAsync(ManualTimeProvider time, int expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (time.ScheduledTimerCount == expected) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Equal(expected, time.ScheduledTimerCount);
    }
}
