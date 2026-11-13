using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.State;
using TeslaBridge.Usage;
using TeslaBridge.Web;

namespace TeslaBridge.Telemetry;

/// <summary>
/// Listens to fleet-telemetry's raw topics and feeds the state store:
///   {telemetry}/{vin}/v/{Field}       → normalised fields
///   {telemetry}/{vin}/connectivity    → online / last_seen
/// </summary>
public sealed class TelemetryIngestService(
    MqttConnection mqtt,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    LiveEvents events,
    UsageTracker usage,
    ILogger<TelemetryIngestService> log) : IHostedService
{
    private readonly string _base = options.Value.Mqtt.TelemetryTopicBase;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        mqtt.Subscribe($"{_base}/+/v/+", OnFieldAsync);
        mqtt.Subscribe($"{_base}/+/connectivity", OnConnectivityAsync);
        mqtt.Subscribe($"{_base}/+/errors/+", OnErrorAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal Task OnFieldAsync(string topic, string payload, CancellationToken ct)
    {
        var parts = topic.Split('/');           // telemetry / VIN / v / Field
        if (parts.Length < 4) return Task.CompletedTask;
        var vin = parts[^3];
        var field = parts[^1];
        var now = time.GetUtcNow();

        JsonNode? raw;
        try { raw = JsonNode.Parse(payload); }
        catch (JsonException) { raw = JsonValue.Create(payload); }

        usage.CountSignal(field);
        events.Publish("raw", vin, new JsonObject { ["field"] = field, ["value"] = raw?.DeepClone() });

        var windows = false;
        foreach (var (key, value) in SignalMapper.Map(field, raw))
        {
            store.Apply(vin, key, value, now);
            windows |= key.StartsWith("window_", StringComparison.Ordinal);
        }
        if (windows)
            store.Apply(vin, "windows_open", SignalMapper.WindowsOpen(k => store.GetConfirmed(vin, k)?.Value), now);

        // Data means the car is awake and connected.
        MarkSeen(vin, now);
        return Task.CompletedTask;
    }

    internal Task OnConnectivityAsync(string topic, string payload, CancellationToken ct)
    {
        var parts = topic.Split('/');
        if (parts.Length < 3) return Task.CompletedTask;
        var vin = parts[^2];
        var now = time.GetUtcNow();

        string? status = null;
        try { status = JsonNode.Parse(payload)?["Status"]?.GetValue<string>(); }
        catch (Exception) { /* ignorer ugyldigt payload */ }

        if (status is null) return Task.CompletedTask;
        var online = status.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase);
        log.LogInformation("{Vin} is now {Status}", vin, online ? "online" : "offline");
        events.Publish("connectivity", vin, new JsonObject { ["status"] = status });
        store.Apply(vin, StateKeys.Online, JsonValue.Create(online), now, ValueSource.Bridge);
        if (online) store.Apply(vin, StateKeys.LastSeen, JsonValue.Create(now.ToString("O")), now, ValueSource.Bridge);
        return Task.CompletedTask;
    }

    private Task OnErrorAsync(string topic, string payload, CancellationToken ct)
    {
        log.LogWarning("Telemetry error from the car on {Topic}: {Payload}", topic, payload);
        JsonNode? body = null;
        try { body = JsonNode.Parse(payload); } catch (JsonException) { }
        events.Publish("telemetry_error", topic.Split('/')[^3], new JsonObject { ["topic"] = topic, ["body"] = body ?? JsonValue.Create(payload) });
        return Task.CompletedTask;
    }

    private void MarkSeen(string vin, DateTimeOffset now)
    {
        if (store.GetConfirmed(vin, StateKeys.Online)?.Value?.GetValue<bool>() != true)
            store.Apply(vin, StateKeys.Online, JsonValue.Create(true), now, ValueSource.Bridge);

        // last_seen is updated at most every 30 seconds, so MQTT isn't flooded.
        var last = store.GetConfirmed(vin, StateKeys.LastSeen);
        if (last is null || now - last.UpdatedAt > TimeSpan.FromSeconds(30))
            store.Apply(vin, StateKeys.LastSeen, JsonValue.Create(now.ToString("O")), now, ValueSource.Bridge);
    }
}
