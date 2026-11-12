using System.Text.Json;
using System.Text.Json.Nodes;
using TeslaBridge.Commands;
using TeslaBridge.State;

namespace TeslaBridge.Web;

/// <summary>Kobler state store og kommandoer på hændelsesbussen til UI'et.</summary>
public sealed class LiveEventsBridge(VehicleStateStore store, CommandService commands, LiveEvents events) : IHostedService
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
        events.Publish("field", c.Vin, new JsonObject
        {
            ["key"] = c.Key,
            ["value"] = c.Effective?.Value?.DeepClone(),
            ["updated_at"] = c.Effective?.UpdatedAt.ToString("O"),
            ["source"] = c.Effective is null ? null : JsonNamingPolicy.SnakeCaseLower.ConvertName(c.Effective.Source.ToString()),
        });

        // Mellemtrin for låse og covers (LOCKING, opening …), så HA-integrationen viser det samme som MQTT-vejen.
        if (TeslaBridge.Publishing.StatePublisher.Transitions.ContainsKey(c.Key))
            events.Publish("field", c.Vin, new JsonObject
            {
                ["key"] = c.Key + "_motion",
                ["value"] = TeslaBridge.Publishing.StatePublisher.Motion(c.Key, store.HasPending(c.Vin, c.Key),
                    c.Effective?.Value, store.GetConfirmed(c.Vin, c.Key)?.Value),
                ["updated_at"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "bridge",
            });
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
