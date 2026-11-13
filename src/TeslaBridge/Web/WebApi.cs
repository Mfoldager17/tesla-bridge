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
        api.MapBleApi(opts);
        api.MapMapApi();

        api.MapGet("/info", (MqttConnection mqtt, LiveEvents events) => Results.Json(new
        {
            version = TeslaBridge.Versioning.BuildInfo.Version,
            version_display = TeslaBridge.Versioning.BuildInfo.Display,
            commit = TeslaBridge.Versioning.BuildInfo.Commit,
            channel = TeslaBridge.Versioning.BuildInfo.Channel,
            api_version = 2,
            started_at = events.StartedAt,
            mqtt_connected = mqtt.IsConnected,
            embedded_broker = opts.Mqtt.EmbeddedBroker,
            auth_required = !string.IsNullOrEmpty(token),
            simulator = opts.Web.Simulator,
            telemetry_hostname = opts.Telemetry.Hostname,
            telemetry_port = opts.Telemetry.Port,
            vehicles = opts.Vehicles.Select(v => new { vin = v.Vin, name = v.Name }),
        }));

        // Newest release vs. this version; POST checks again now.
        api.MapGet("/update", (TeslaBridge.Versioning.UpdateCheckService u) => Results.Json(UpdateJson(u)));
        api.MapPost("/update/check", async (TeslaBridge.Versioning.UpdateCheckService u, CancellationToken ct) =>
        {
            await u.CheckAsync(ct);
            return Results.Json(UpdateJson(u));
        });

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
            var run = Task.Run(async () =>
            {
                try { return await commands.ExecuteAsync(vin, command, life.ApplicationStopping); }
                catch (OperationCanceledException) { return CommandResult.Failed("The bridge is stopping"); }
                catch (Exception ex) { log.LogError(ex, "Fejl i {Command}", command); return CommandResult.Failed(ex.Message); }
            });
            // ?wait=true answers when the car has done it (woken first if needed) – like Teslemetry, so Home Assistant
            // shows the call as busy meanwhile and shows the car's error if it fails. Without it: 202 straight away.
            if (!string.Equals(req.Query["wait"], "true", StringComparison.OrdinalIgnoreCase))
                return Results.Accepted(value: new { command = command.Name });
            var result = await run;   // keeps running even if the caller gives up
            return result.IsSuccess
                ? Results.Ok(new { command = command.Name, ok = true })
                : Results.Json(new { command = command.Name, ok = false, error = result.Reason ?? result.Outcome.ToString() },
                    statusCode: result.Outcome == CommandOutcome.Refused ? 409 : 502);
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
                    ("DoorState", "{\"DriverFront\":false,\"PassengerFront\":false,\"DriverRear\":false,\"PassengerRear\":false,\"TrunkFront\":false,\"TrunkRear\":false}"),
                }.ToList();
                // Every other press the car drives a little around Aarhus towards a destination; otherwise it is parked.
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var driving = rnd.Next(2) == 0;
                var (lat, lon) = (56.1185 + (driving ? (rnd.NextDouble() - .5) * .02 : 0), 10.1235 + (driving ? (rnd.NextDouble() - .5) * .03 : 0));
                samples.Add(("Location", string.Format(inv, "{{\"latitude\":{0:0.000000},\"longitude\":{1:0.000000}}}", lat, lon)));
                samples.Add(("GpsHeading", rnd.Next(0, 360).ToString(inv)));
                samples.Add(("Gear", driving ? "\"ShiftStateD\"" : "\"ShiftStateP\""));
                samples.Add(("VehicleSpeed", driving ? rnd.Next(20, 50).ToString(inv) : "0"));
                samples.Add(("DestinationName", driving ? "\"Aarhus Ø\"" : "\"\""));
                if (driving)
                {
                    samples.Add(("DestinationLocation", "{\"latitude\":56.1612,\"longitude\":10.2190}"));
                    samples.Add(("MinutesToArrival", rnd.Next(5, 20).ToString(inv)));
                }
                await mqtt.PublishAsync($"{b}/{vin}/connectivity", "{\"Status\":\"CONNECTED\",\"ConnectionId\":\"simulator\"}");
                foreach (var (field, json) in samples)
                    await mqtt.PublishAsync($"{b}/{vin}/v/{field}", json);
                return Results.Ok(new { sent = samples.Count });
            });

            // Puts the pretend car to sleep, so the next command has to wake it first.
            api.MapPost("/vehicles/{vin}/simulate/sleep", (string vin, IEnumerable<TeslaBridge.Commands.ICommandTransport> transports) =>
            {
                transports.OfType<TeslaBridge.Commands.SimulatorTransport>().FirstOrDefault()?.Sleep(vin);
                return Results.Ok(new { asleep = true });
            });
        }
    }

    private static JsonObject UpdateJson(TeslaBridge.Versioning.UpdateCheckService u)
    {
        var i = u.Current;
        var o = u.StatePayload();
        o["installed_display"] = TeslaBridge.Versioning.BuildInfo.Display;
        o["channel"] = i.Channel;
        o["update_available"] = i.UpdateAvailable;
        o["release_notes"] = i.ReleaseNotes;
        o["published_at"] = i.PublishedAt?.ToString("O");
        o["checked_at"] = i.CheckedAt?.ToString("O");
        o["error"] = i.Error;
        return o;
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
