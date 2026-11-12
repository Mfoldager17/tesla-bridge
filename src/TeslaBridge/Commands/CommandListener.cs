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
/// Tager imod kommandoer på {bridge}/{vin}/cmd/{navn} og publicerer status på
/// {bridge}/{vin}/command/status (pending → waking → succeeded/failed), så et dashboard kan vise en spinner.
/// </summary>
public sealed class CommandListener(
    MqttConnection mqtt,
    CommandService commands,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    ILogger<CommandListener> log) : IHostedService
{
    private readonly string _base = options.Value.Mqtt.BridgeTopicBase;
    private readonly HashSet<string> _vins = options.Value.Vehicles.Select(v => v.Vin).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        commands.StatusChanged += OnStatus;
        mqtt.Subscribe($"{_base}/+/cmd/+", OnCommandAsync);
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
            log.LogWarning("Ignorerer kommando til ukendt VIN {Vin}", vin);
            return Task.CompletedTask;
        }

        var command = CommandParser.Parse(name, payload, key => store.Get(vin, key));
        if (command is null)
        {
            log.LogWarning("Ukendt kommando eller payload: {Name} = {Payload}", name, payload);
            return Task.CompletedTask;
        }

        // Kør i baggrunden: opvågning kan tage op til et minut, og MQTT-løkken må ikke blokere.
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await commands.ExecuteAsync(vin, command, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "Uventet fejl i {Command}", command); }
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
}
