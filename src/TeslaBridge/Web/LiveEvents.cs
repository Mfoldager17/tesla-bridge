using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace TeslaBridge.Web;

/// <summary>One event for the dashboard: a field change, raw telemetry, a command status or a log line.</summary>
public sealed record LiveEvent(string Type, string? Vin, JsonObject Data, DateTimeOffset At);

/// <summary>
/// In-memory event bus for the dashboard and the HA integration (Server-Sent Events).
/// </summary>
public sealed class LiveEvents(TimeProvider time)
{
    private const int RecentCapacity = 300;
    private readonly ConcurrentDictionary<Guid, Channel<LiveEvent>> _subscribers = new();
    private readonly LinkedList<LiveEvent> _recent = new();
    private readonly Lock _lock = new();

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public void Publish(string type, string? vin, JsonObject data)
    {
        var e = new LiveEvent(type, vin, data, time.GetUtcNow());
        if (type is "raw" or "command" or "connectivity" or "telemetry_error")
        {
            lock (_lock)
            {
                _recent.AddLast(e);
                while (_recent.Count > RecentCapacity) _recent.RemoveFirst();
            }
        }
        foreach (var ch in _subscribers.Values)
            ch.Writer.TryWrite(e);   // slow clients just miss events (bounded, DropOldest)
    }

    public IReadOnlyList<LiveEvent> Recent()
    {
        lock (_lock) return _recent.ToArray();
    }

    public (Guid Id, ChannelReader<LiveEvent> Reader) Subscribe()
    {
        var ch = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }
}
