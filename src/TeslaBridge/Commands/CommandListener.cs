using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>
/// Takes commands on {bridge}/{vin}/cmd/{name} and publishes their status on {bridge}/{vin}/command/status
/// (pending → waking → succeeded/failed), so a dashboard can show progress.
///
/// Anyone who may publish to those topics on the broker can unlock the car, so the broker must require logins
/// (see docs/security.md). Bridge__Mqtt__Commands=false turns MQTT commands off – e.g. when only the Home Assistant
/// integration (HTTP, with a token) is used.
/// </summary>
public sealed class CommandListener(
    MqttConnection mqtt,
    CommandService commands,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    ILogger<CommandListener> log) : IHostedService, IDisposable
{
    private readonly string _base = options.Value.Mqtt.BridgeTopicBase;
    private readonly HashSet<string> _vins = options.Value.Vehicles.Select(v => v.Vin).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        commands.StatusChanged += OnStatus;
        if (options.Value.Mqtt.Commands) mqtt.Subscribe($"{_base}/+/cmd/+", OnCommandAsync);
        else log.LogInformation("MQTT commands are off (Bridge__Mqtt__Commands=false)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        commands.StatusChanged -= OnStatus;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private Task OnCommandAsync(string topic, string payload, CancellationToken messageCt)
    {
        var parts = topic.Split('/');
        var vin = parts[^3];
        var name = parts[^1];

        if (_vins.Count > 0 && !_vins.Contains(vin))
        {
            log.LogWarning("Ignoring a command for an unknown VIN {Vin}", vin);
            return Task.CompletedTask;
        }

        var command = CommandParser.Parse(name, payload, key => store.Get(vin, key));
        if (command is null)
        {
            log.LogWarning("Unknown command or value: {Name} = {Payload}", name, payload);
            return Task.CompletedTask;
        }

        // In the background: waking the car can take up to a minute, and the MQTT loop must not block.
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await commands.ExecuteAsync(vin, command, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "Unexpected error in {Command}", command); }
        }, ct);
        return Task.CompletedTask;
    }

    private void OnStatus(CommandStatus s)
    {
        var json = new JsonObject
        {
            ["command"] = s.Command,
            ["status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(s.Phase.ToString()),
            ["transport"] = s.Transport,
            ["error"] = s.Error,
            ["note"] = s.Note,
            ["at"] = s.At.ToString("O"),
        };
        _ = mqtt.PublishAsync($"{_base}/{s.Vin}/command/status", json.ToJsonString(Json.Relaxed), retain: true);
    }

    public void Dispose() => _cts?.Dispose();
}
