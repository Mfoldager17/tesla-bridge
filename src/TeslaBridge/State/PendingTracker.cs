using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Options;

namespace TeslaBridge.State;

/// <summary>
/// Publishes the state key <c>pending</c> per car: <c>{"waking": true, "keys": ["climate_on"]}</c> – which fields
/// are waiting for the car to confirm a command, and whether the car is being woken for it. Home Assistant
/// shows it as attributes (<c>pending</c>, <c>waiting_for</c>) on the entities that are waiting, and the
/// integration also swaps their icon, so "on" right after a command reads as "on its way".
/// </summary>
public sealed class PendingTracker(VehicleStateStore store, CommandService commands, IOptions<BridgeOptions> options, TimeProvider time) : IHostedService
{
    public const string Key = StateKeys.Pending;
    private readonly Dictionary<string, bool> _waking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.PendingChanged += Publish;
        commands.StatusChanged += OnCommand;
        foreach (var v in options.Value.Vehicles) Publish(v.Vin);   // clears a stale value restored from disk
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.PendingChanged -= Publish;
        commands.StatusChanged -= OnCommand;
        return Task.CompletedTask;
    }

    private void OnCommand(CommandStatus s)
    {
        lock (_lock) _waking[s.Vin] = s.Phase == CommandPhase.Waking;
        Publish(s.Vin);
    }

    private void Publish(string vin)
    {
        JsonObject value;
        lock (_lock)
        {
            var keys = store.PendingKeys(vin).Where(k => k != Key).ToList();
            var waking = _waking.GetValueOrDefault(vin) && keys.Count > 0;
            value = new JsonObject
            {
                ["waking"] = waking,
                ["keys"] = new JsonArray(keys.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            };
            var text = value.ToJsonString();
            if (_last.TryGetValue(vin, out var prev) && prev == text) return;
            _last[vin] = text;
        }
        store.Apply(vin, Key, value, time.GetUtcNow(), ValueSource.Bridge);
    }
}
