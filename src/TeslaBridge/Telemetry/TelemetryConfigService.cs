using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Options;
using TeslaBridge.Settings;

namespace TeslaBridge.Telemetry;

/// <summary>
/// Sends the telemetry configuration to the car ("send these fields to my domain") through tesla-http-proxy, which
/// signs it with the app's key. The configuration expires (exp), so it is renewed by itself.
/// </summary>
public sealed class TelemetryConfigService(
    TeslaProxyClient proxy,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<TelemetryConfigService> log,
    SettingsStore? settings = null) : BackgroundService
{
    private readonly BridgeOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Telemetry.AutoPush) return;
        if (string.IsNullOrWhiteSpace(_opts.Telemetry.Hostname))
        {
            log.LogWarning("Bridge__Telemetry__Hostname isn't set – no telemetry configuration is sent to the car");
            return;
        }

        // A short delay, so the proxy and the token are ready.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            foreach (var v in _opts.Vehicles)
            {
                try { await EnsureFreshAsync(v.Vin, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Couldn't check or renew the telemetry configuration for {Vin}: {Error}", v.Vin, ex.Message);
                }
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Renews when the car's configuration is missing, points elsewhere or expires soon.</summary>
    public async Task EnsureFreshAsync(string vin, CancellationToken ct)
    {
        var current = await GetStatusAsync(vin, ct);
        var cfg = current?["response"]?["config"];
        var host = cfg?["hostname"]?.ToString();
        var exp = cfg?["exp"] is JsonValue e && e.TryGetValue(out long expUnix) ? DateTimeOffset.FromUnixTimeSeconds(expUnix) : (DateTimeOffset?)null;

        var reason = cfg is null ? "no configuration on the car"
            : !string.Equals(host, _opts.Telemetry.Hostname, StringComparison.OrdinalIgnoreCase) ? $"the car sends to '{host}'"
            : exp is null ? "unknown expiry"
            : exp.Value - time.GetUtcNow() < TimeSpan.FromDays(_opts.Telemetry.RenewWhenDaysLeft) ? $"expires {exp:yyyy-MM-dd}"
            : FieldsDiffer(cfg?["fields"] as JsonObject, BuildRequest([vin])["config"]?["fields"] as JsonObject) is { } diff ? diff
            : null;

        if (reason is null)
        {
            log.LogInformation("The telemetry configuration for {Vin} is fresh (expires {Exp:yyyy-MM-dd}, synced={Synced})",
                vin, exp, current?["response"]?["synced"]);
            return;
        }

        log.LogInformation("Sending the telemetry configuration to {Vin}: {Reason}", vin, reason);
        await PushAsync([vin], ct);
    }

    /// <summary>Are the fields or intervals on the car different from what the bridge wants (e.g. after an update)?</summary>
    internal static string? FieldsDiffer(JsonObject? onCar, JsonObject? wanted)
    {
        if (onCar is null || wanted is null) return null;      // Tesla didn't return the fields – leave it alone
        static int Interval(JsonNode? n) => n?["interval_seconds"] is JsonValue v && v.TryGetValue(out int i) ? i : -1;
        var missing = wanted.Where(kv => !onCar.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        var changed = wanted.Where(kv => onCar.ContainsKey(kv.Key) && Interval(onCar[kv.Key]) != Interval(kv.Value)).Select(kv => kv.Key).ToList();
        var extra = onCar.Where(kv => !wanted.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (missing.Count + changed.Count + extra.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"new fields: {string.Join(", ", missing)}");
        if (changed.Count > 0) parts.Add($"nye intervaller: {string.Join(", ", changed)}");
        if (extra.Count > 0) parts.Add($"removed fields: {string.Join(", ", extra)}");
        return string.Join("; ", parts);
    }

    public async Task<JsonNode?> PushAsync(IReadOnlyList<string> vins, CancellationToken ct)
    {
        var body = BuildRequest(vins);
        var (status, resp, raw) = await proxy.SendAsync(HttpMethod.Post, "/api/1/vehicles/fleet_telemetry_config", body, ct);
        if ((int)status >= 300)
            throw new InvalidOperationException($"fleet_telemetry_config answered {(int)status}: {raw}");

        var skipped = resp?["response"]?["skipped_vehicles"];
        if (skipped is JsonObject s && s.Any(kv => kv.Value is JsonArray a && a.Count > 0))
            log.LogWarning("Some cars were skipped: {Skipped}. 'missing_key' = the app's virtual key isn't paired with the car.", skipped.ToJsonString());
        else
            log.LogInformation("Telemetry configuration sent: {Response}", resp?["response"]?.ToJsonString());
        return resp;
    }

    public async Task<JsonNode?> GetStatusAsync(string vin, CancellationToken ct)
    {
        var (status, resp, raw) = await proxy.SendAsync(HttpMethod.Get, $"/api/1/vehicles/{vin}/fleet_telemetry_config", null, ct);
        if ((int)status >= 300) throw new InvalidOperationException($"{(int)status}: {raw}");
        return resp;
    }

    public async Task<JsonNode?> GetErrorsAsync(string vin, CancellationToken ct)
    {
        var (status, resp, raw) = await proxy.SendAsync(HttpMethod.Get, $"/api/1/vehicles/{vin}/fleet_telemetry_errors", null, ct);
        if ((int)status >= 300) throw new InvalidOperationException($"{(int)status}: {raw}");
        return resp;
    }

    internal JsonObject BuildRequest(IReadOnlyList<string> vins)
    {
        var t = _opts.Telemetry;
        // The defaults from telemetry-fields.json with the changes made on the dashboard's settings page.
        var fields = (settings ?? new SettingsStore(null, t.FieldsFile)).EffectiveFields();

        var config = new JsonObject
        {
            ["hostname"] = t.Hostname,
            ["port"] = t.Port,
            ["ca"] = File.ReadAllText(t.CaFile),
            ["fields"] = fields,
            ["alert_types"] = new JsonArray("service"),
            ["exp"] = time.GetUtcNow().AddDays(t.ConfigLifetimeDays).ToUnixTimeSeconds(),
            ["prefer_typed"] = true,
        };
        return new JsonObject
        {
            ["vins"] = new JsonArray(vins.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["config"] = config,
        };
    }
}
