using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TeslaBridge.Commands;

/// <summary>Kommandoer via tesla-http-proxy → Tesla Fleet API (cloud). Virker overalt, kræver 4G/WiFi i bilen.</summary>
public sealed class FleetApiTransport(TeslaProxyClient proxy, ILogger<FleetApiTransport> log) : ICommandTransport
{
    public string Name => "fleet-api";
    public int Priority => 100;

    public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(true);

    public async Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct)
    {
        try
        {
            var (status, body, raw) = await proxy.SendAsync(HttpMethod.Post,
                $"/api/1/vehicles/{vin}/command/{command.FleetEndpoint}", command.FleetBody, ct);
            return Interpret(status, body, raw);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Fleet API-kald fejlede for {Command}", command);
            return CommandResult.Failed(ex.Message);
        }
    }

    public async Task<CommandResult> WakeAsync(string vin, CancellationToken ct)
    {
        try
        {
            var (status, _, raw) = await proxy.SendAsync(HttpMethod.Post, $"/api/1/vehicles/{vin}/wake_up", null, ct);
            return status is HttpStatusCode.OK ? CommandResult.Ok() : CommandResult.Failed($"{(int)status}: {raw}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return CommandResult.Failed(ex.Message);
        }
    }

    internal static CommandResult Interpret(HttpStatusCode status, JsonNode? body, string raw)
    {
        var error = body?["error"]?.ToString() ?? "";
        if (status == HttpStatusCode.RequestTimeout ||
            Contains(error, "vehicle unavailable") || Contains(error, "offline") || Contains(error, "asleep") ||
            Contains(raw, "vehicle is offline or asleep"))
            return CommandResult.Asleep(error);

        if (status != HttpStatusCode.OK)
            return CommandResult.Failed($"{(int)status}: {(error.Length > 0 ? error : raw)}");

        var response = body?["response"];
        var result = response?["result"]?.GetValue<bool>() ?? false;
        var reason = response?["reason"]?.ToString() ?? "";
        if (result) return CommandResult.Ok();

        // "already_set", "already on" osv. = bilen er allerede i den ønskede tilstand.
        return Contains(reason, "already") ? CommandResult.Ok() : CommandResult.Refused(reason);
    }

    private static bool Contains(string s, string what) => s.Contains(what, StringComparison.OrdinalIgnoreCase);
}
