using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Publishing;

/// <summary>
/// Publishes the bridge's normalised state, for HA and every other client:
///   {bridge}/{vin}/state/{key}   retained, a JSON value (e.g. 72.5, true, "charging")
///   {bridge}/{vin}/json          retained, the whole snapshot with updated_at and source per field
/// </summary>
public sealed class StatePublisher(
    MqttConnection mqtt,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    TimeProvider time) : BackgroundService
{
    private readonly string _base = options.Value.Mqtt.BridgeTopicBase;
    private readonly ConcurrentDictionary<string, string> _lastPublished = new();
    private readonly ConcurrentDictionary<string, byte> _dirtySnapshots = new();

    // Changes are published one at a time, in order, with the value the store has *when it is sent* – so a burst
    // of changes from the stream and Bluetooth at once can never leave an older value retained on the broker.
    private readonly Channel<(string Vin, string Key)> _changes =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += OnChanged;
        mqtt.Connected += RepublishAllAsync;
        try
        {
            await Task.WhenAll(PublishChangesAsync(stoppingToken), PublishSnapshotsAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally
        {
            store.Changed -= OnChanged;
            mqtt.Connected -= RepublishAllAsync;
        }
    }

    private void OnChanged(StateChange change)
    {
        _changes.Writer.TryWrite((change.Vin, change.Key));
        _dirtySnapshots[change.Vin] = 0;
    }

    private async Task PublishChangesAsync(CancellationToken ct)
    {
        await foreach (var (vin, key) in _changes.Reader.ReadAllAsync(ct))
        {
            await PublishFieldAsync(vin, key, store.Get(vin, key), force: false, ct);
            if (FieldJson.Transitions.ContainsKey(key))
                await PublishFieldAsync(vin, key + FieldJson.MotionSuffix, FieldJson.MotionValue(store, vin, key, time.GetUtcNow()), force: false, ct);
        }
    }

    // Snapshots are gathered and sent at most once a second per car.
    private async Task PublishSnapshotsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            foreach (var vin in _dirtySnapshots.Keys.ToArray())
            {
                _dirtySnapshots.TryRemove(vin, out _);
                await PublishSnapshotAsync(vin, ct);
            }
        }
    }

    private async Task RepublishAllAsync(CancellationToken ct)
    {
        _lastPublished.Clear();
        foreach (var vin in store.Vins)
        {
            foreach (var (key, value) in store.Snapshot(vin))
            {
                await PublishFieldAsync(vin, key, value, force: true, ct);
                if (FieldJson.Transitions.ContainsKey(key))
                    await PublishFieldAsync(vin, key + FieldJson.MotionSuffix, FieldJson.MotionValue(store, vin, key, time.GetUtcNow()), force: true, ct);
            }
            await PublishSnapshotAsync(vin, ct);
        }
    }

    private async Task PublishFieldAsync(string vin, string key, FieldValue? value, bool force, CancellationToken ct)
    {
        var topic = $"{_base}/{vin}/state/{key}";
        var payload = value?.Value?.ToJsonString(Json.Relaxed) ?? "null";
        if (!force && _lastPublished.TryGetValue(topic, out var last) && last == payload) return;
        _lastPublished[topic] = payload;
        await mqtt.PublishAsync(topic, payload, retain: true, ct);
    }

    private Task PublishSnapshotAsync(string vin, CancellationToken ct)
    {
        var doc = new JsonObject { ["vin"] = vin, ["fields"] = FieldJson.Fields(store, vin) };
        return mqtt.PublishAsync($"{_base}/{vin}/json", doc.ToJsonString(Json.Relaxed), retain: true, ct);
    }
}
