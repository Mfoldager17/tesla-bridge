using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Publishing;

/// <summary>
/// Publicerer bridgens normaliserede tilstand, til HA og alle andre klienter:
///   {bridge}/{vin}/state/{key}   retained, JSON-værdi (fx 72.5, true, "charging")
///   {bridge}/{vin}/json          retained, hele snapshot med updated_at og source pr. felt
/// </summary>
public sealed class StatePublisher(
    MqttConnection mqtt,
    VehicleStateStore store,
    IOptions<BridgeOptions> options) : BackgroundService
{
    private readonly string _base = options.Value.Mqtt.BridgeTopicBase;
    private readonly ConcurrentDictionary<string, string> _lastPublished = new();
    private readonly ConcurrentDictionary<string, byte> _dirtySnapshots = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += OnChanged;
        mqtt.Connected += RepublishAllAsync;

        // Snapshots samles og sendes højst én gang i sekundet pr. bil.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var vin in _dirtySnapshots.Keys.ToArray())
                {
                    _dirtySnapshots.TryRemove(vin, out _);
                    await PublishSnapshotAsync(vin, stoppingToken);
                }
            }
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
        _ = PublishFieldAsync(change.Vin, change.Key, change.Effective, force: false, CancellationToken.None);
        _dirtySnapshots[change.Vin] = 0;
    }

    private async Task RepublishAllAsync(CancellationToken ct)
    {
        _lastPublished.Clear();
        foreach (var vin in store.Vins)
        {
            foreach (var (key, value) in store.Snapshot(vin))
                await PublishFieldAsync(vin, key, value, force: true, ct);
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

    private async Task PublishSnapshotAsync(string vin, CancellationToken ct)
    {
        var fields = new JsonObject();
        foreach (var (key, fv) in store.Snapshot(vin).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            fields[key] = new JsonObject
            {
                ["value"] = fv.Value?.DeepClone(),
                ["updated_at"] = fv.UpdatedAt.ToString("O"),
                ["source"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(fv.Source.ToString()),
            };
        }
        var doc = new JsonObject { ["vin"] = vin, ["fields"] = fields };
        await mqtt.PublishAsync($"{_base}/{vin}/json", doc.ToJsonString(Json.Relaxed), retain: true, ct);
    }
}
