using System.Threading.Channels;
using LFTPPilot.Core;

namespace LFTPPilot.Agent;

internal sealed class AgentEventHub
{
    private readonly object _gate = new();
    private readonly HashSet<Channel<EngineEvent>> _subscribers = [];
    private long _sequence;

    public EngineEvent Publish(EngineEventKind kind, string name, object? payload = null, Guid? jobId = null, Guid? sessionId = null)
    {
        var engineEvent = new EngineEvent(Interlocked.Increment(ref _sequence), kind, name, DateTimeOffset.UtcNow, payload, sessionId, jobId);
        lock (_gate)
        {
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(engineEvent);
        }
        return engineEvent;
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<EngineEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        lock (_gate) _subscribers.Add(channel);
        return new(this, channel);
    }

    internal sealed class Subscription(AgentEventHub owner, Channel<EngineEvent> channel) : IAsyncDisposable
    {
        public ChannelReader<EngineEvent> Reader => channel.Reader;

        public ValueTask DisposeAsync()
        {
            lock (owner._gate) owner._subscribers.Remove(channel);
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
