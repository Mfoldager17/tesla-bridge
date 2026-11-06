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
        if (Transitions.ContainsKey(change.Key))
            _ = PublishMotionAsync(change.Vin, change.Key, force: false, CancellationToken.None);
        _dirtySnapshots[change.Vin] = 0;
    }

    /// <summary>
    /// Låse og covers får en "{key}_motion"-tilstand med mellemtrin, mens en kommando venter på bilen
    /// (LOCKING/UNLOCKING, opening/closing) – ligesom Teslemetry viser i HA.
    /// </summary>
    internal static readonly Dictionary<string, (string True, string False, string ToTrue, string ToFalse)> Transitions = new()
    {
        ["locked"] = ("LOCKED", "UNLOCKED", "LOCKING", "UNLOCKING"),
        ["charge_cable_locked"] = ("LOCKED", "UNLOCKED", "LOCKING", "UNLOCKING"),
        ["frunk_open"] = ("open", "closed", "opening", "closing"),
        ["trunk_open"] = ("open", "closed", "opening", "closing"),
        ["charge_port_open"] = ("open", "closed", "opening", "closing"),
        ["windows_open"] = ("open", "closed", "opening", "closing"),
    };

    internal static string? Motion(string key, bool pending, JsonNode? effective, JsonNode? confirmed)
    {
        var t = Transitions[key];
        static bool? AsBool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;
        if (pending && AsBool(effective) is { } target && AsBool(confirmed) != target)
            return target ? t.ToTrue : t.ToFalse;
        return AsBool(effective) switch { true => t.True, false => t.False, _ => null };
    }

    private Task PublishMotionAsync(string vin, string key, bool force, CancellationToken ct)
    {
        var motion = Motion(key, store.HasPending(vin, key), store.Get(vin, key)?.Value, store.GetConfirmed(vin, key)?.Value);
        var value = new FieldValue(motion is null ? null : JsonValue.Create(motion), DateTimeOffset.UtcNow, ValueSource.Bridge);
        return PublishFieldAsync(vin, key + "_motion", value, force, ct);
    }

    private async Task RepublishAllAsync(CancellationToken ct)
    {
        _lastPublished.Clear();
        foreach (var vin in store.Vins)
        {
            foreach (var (key, value) in store.Snapshot(vin))
            {
                await PublishFieldAsync(vin, key, value, force: true, ct);
                if (Transitions.ContainsKey(key)) await PublishMotionAsync(vin, key, force: true, ct);
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
