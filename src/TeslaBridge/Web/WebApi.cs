using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.State;
using TeslaBridge.Telemetry;
using TeslaBridge.Usage;

namespace TeslaBridge.Web;

/// <summary>Lille HTTP-API til test-dashboardet. Kører i samme proces som bridgen.</summary>
public static class WebApi
{
    public static void MapBridgeApi(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var token = opts.Web.Token;

        // Simpel token-beskyttelse: API'et kan låse bilen op, så sæt Bridge__Web__Token hvis porten er tilgængelig for andre.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (!string.IsNullOrEmpty(token) && path.StartsWithSegments("/api") && path != "/api/info")
            {
                var given = ctx.Request.Headers.Authorization.ToString() is { Length: > 7 } h && h.StartsWith("Bearer ")
                    ? h[7..] : ctx.Request.Query["token"].ToString();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token)))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        var api = app.MapGroup("/api");
        api.MapSetupApi(opts);
        api.MapSettingsApi(opts);
        api.MapCarModelApi(opts);

        api.MapGet("/info", (MqttConnection mqtt, LiveEvents events) => Results.Json(new
        {
            started_at = events.StartedAt,
            mqtt_connected = mqtt.IsConnected,
            embedded_broker = opts.Mqtt.EmbeddedBroker,
            auth_required = !string.IsNullOrEmpty(token),
            simulator = opts.Web.Simulator,
            telemetry_hostname = opts.Telemetry.Hostname,
            telemetry_port = opts.Telemetry.Port,
            vehicles = opts.Vehicles.Select(v => new { vin = v.Vin, name = v.Name }),
        }));

        api.MapGet("/state", (VehicleStateStore store) =>
        {
            var vins = opts.Vehicles.Select(v => v.Vin).Union(store.Vins, StringComparer.OrdinalIgnoreCase);
            var result = new JsonArray();
            foreach (var vin in vins)
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
                    // Låse/covers: LOCKING/opening osv. mens en kommando venter (samme som MQTT-vejen)
                    if (StatePublisher.Transitions.ContainsKey(key))
                        fields[key + "_motion"] = new JsonObject
                        {
                            ["value"] = StatePublisher.Motion(key, store.HasPending(vin, key), fv.Value, store.GetConfirmed(vin, key)?.Value),
                            ["updated_at"] = fv.UpdatedAt.ToString("O"),
                            ["source"] = "bridge",
                        };
                }
                result.Add(new JsonObject
                {
                    ["vin"] = vin,
                    ["name"] = opts.Vehicles.FirstOrDefault(v => v.Vin.Equals(vin, StringComparison.OrdinalIgnoreCase))?.Name ?? vin,
                    ["fields"] = fields,
                });
            }
            return Results.Content(result.ToJsonString(Json.Relaxed), "application/json");
        });

        // Entitetsdefinitioner til HA-integrationen (custom_components/tesla_bridge)
        api.MapGet("/entities", () =>
            Results.Content(TeslaBridge.Publishing.HomeAssistantDiscovery.ApiDefinitions(opts).ToJsonString(Json.Relaxed), "application/json"));

        api.MapGet("/usage", (UsageTracker usage) =>
            Results.Content(UsageService.ToJson(usage.Snapshot()).ToJsonString(Json.Relaxed), "application/json"));

        api.MapGet("/events/recent", (LiveEvents events) =>
            Results.Content(new JsonArray(events.Recent().Select(ToJson).ToArray<JsonNode?>()).ToJsonString(Json.Relaxed), "application/json"));

        // Server-Sent Events: alt der sker, live.
        api.MapGet("/events", async (HttpContext ctx, LiveEvents events, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var (id, reader) = events.Subscribe();
            try
            {
                await ctx.Response.WriteAsync(": connected\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var hbTask = heartbeat.WaitForNextTickAsync(ct).AsTask();
                while (!ct.IsCancellationRequested)
                {
                    var readTask = reader.WaitToReadAsync(ct).AsTask();
                    var done = await Task.WhenAny(readTask, hbTask);
                    if (done == hbTask)
                    {
                        await ctx.Response.WriteAsync(": ping\n\n", ct);
                        hbTask = heartbeat.WaitForNextTickAsync(ct).AsTask();
                    }
                    else if (!await readTask) break;
                    while (reader.TryRead(out var e))
                        await ctx.Response.WriteAsync($"data: {ToJson(e).ToJsonString(Json.Relaxed)}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { events.Unsubscribe(id); }
        });

        api.MapPost("/vehicles/{vin}/cmd/{name}", async (string vin, string name, HttpRequest req,
            VehicleStateStore store, CommandService commands, IHostApplicationLifetime life, ILoggerFactory lf) =>
        {
            var payload = (await new StreamReader(req.Body).ReadToEndAsync()).Trim();
            var command = CommandParser.Parse(name, payload.Length == 0 ? "PRESS" : payload, k => store.Get(vin, k));
            if (command is null) return Results.BadRequest(new { error = $"Ukendt kommando eller payload: {name} = '{payload}'" });

            var log = lf.CreateLogger("WebApi");
            _ = Task.Run(async () =>
            {
                try { await commands.ExecuteAsync(vin, command, life.ApplicationStopping); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { log.LogError(ex, "Fejl i {Command}", command); }
            });
            return Results.Accepted(value: new { command = command.Name });
        });

        api.MapGet("/vehicles/{vin}/telemetry-config", async (string vin, TelemetryConfigService svc, CancellationToken ct) =>
            await Wrap(() => svc.GetStatusAsync(vin, ct)));

        api.MapGet("/vehicles/{vin}/telemetry-errors", async (string vin, TelemetryConfigService svc, CancellationToken ct) =>
            await Wrap(() => svc.GetErrorsAsync(vin, ct)));

        api.MapPost("/vehicles/{vin}/telemetry-config", async (string vin, TelemetryConfigService svc, CancellationToken ct) =>
            await Wrap(() => svc.PushAsync([vin], ct)));

        if (opts.Web.Simulator)
        {
            api.MapPost("/vehicles/{vin}/simulate", async (string vin, MqttConnection mqtt) =>
            {
                // Går gennem MQTT → ingest præcis som fleet-telemetry, så hele kæden testes.
                var b = opts.Mqtt.TelemetryTopicBase;
                var rnd = Random.Shared;
                var samples = new (string Field, string Json)[]
                {
                    ("BatteryLevel", (40 + rnd.NextDouble() * 40).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
                    ("EstBatteryRange", rnd.Next(120, 260).ToString()),
                    ("InsideTemp", (15 + rnd.NextDouble() * 8).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
                    ("OutsideTemp", "3.5"),
                    ("Locked", rnd.Next(2) == 0 ? "true" : "false"),
                    ("HvacPower", rnd.Next(2) == 0 ? "\"HvacPowerStateOn\"" : "\"HvacPowerStateOff\""),
                    ("DetailedChargeState", "\"DetailedChargeStateCharging\""),
                    ("ACChargingPower", "11.0"),
                    ("Location", "{\"latitude\":56.1185,\"longitude\":10.1235}"),
                    ("DoorState", "{\"DriverFront\":false,\"PassengerFront\":false,\"DriverRear\":false,\"PassengerRear\":false,\"TrunkFront\":false,\"TrunkRear\":false}"),
                };
                await mqtt.PublishAsync($"{b}/{vin}/connectivity", "{\"Status\":\"CONNECTED\",\"ConnectionId\":\"simulator\"}");
                foreach (var (field, json) in samples)
                    await mqtt.PublishAsync($"{b}/{vin}/v/{field}", json);
                return Results.Ok(new { sent = samples.Length });
            });
        }
    }

    private static JsonObject ToJson(LiveEvent e) => new()
    {
        ["type"] = e.Type,
        ["vin"] = e.Vin,
        ["at"] = e.At.ToString("O"),
        ["data"] = e.Data.DeepClone(),
    };

    private static async Task<IResult> Wrap(Func<Task<JsonNode?>> call)
    {
        try
        {
            var r = await call();
            return Results.Content(r?.ToJsonString(Json.Relaxed) ?? "null", "application/json");
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
