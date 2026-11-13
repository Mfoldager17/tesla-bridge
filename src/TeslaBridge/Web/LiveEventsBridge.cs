using System.Text.Json;
using System.Text.Json.Nodes;
using TeslaBridge.Commands;
using TeslaBridge.State;

namespace TeslaBridge.Web;

/// <summary>Connects the state store and the commands to the event bus.</summary>
public sealed class LiveEventsBridge(VehicleStateStore store, CommandService commands, LiveEvents events, TimeProvider time) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.Changed += OnChanged;
        commands.StatusChanged += OnCommand;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        store.Changed -= OnChanged;
        commands.StatusChanged -= OnCommand;
        return Task.CompletedTask;
    }

    private void OnChanged(StateChange c)
    {
        var field = FieldJson.Field(c.Effective);
        field["key"] = c.Key;
        events.Publish("field", c.Vin, field);

        // In-between steps for locks and covers (LOCKING, opening …), so the HA integration shows the same as MQTT.
        if (FieldJson.Transitions.ContainsKey(c.Key))
        {
            var motion = FieldJson.Field(FieldJson.MotionValue(store, c.Vin, c.Key, time.GetUtcNow()));
            motion["key"] = c.Key + FieldJson.MotionSuffix;
            events.Publish("field", c.Vin, motion);
        }
    }

    private void OnCommand(CommandStatus s) =>
        events.Publish("command", s.Vin, new JsonObject
        {
            ["command"] = s.Command,
            ["status"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(s.Phase.ToString()),
            ["transport"] = s.Transport,
            ["error"] = s.Error,
            ["note"] = s.Note,
        });
}
