using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Options;

namespace TeslaBridge.Telemetry;

/// <summary>
/// Sender telemetri-konfigurationen til bilen ("send disse felter til mit domæne") via tesla-http-proxy,
/// som signerer den med appens nøgle. Konfigurationen udløber (exp), så den fornyes automatisk.
/// </summary>
public sealed class TelemetryConfigService(
    TeslaProxyClient proxy,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<TelemetryConfigService> log) : BackgroundService
{
    private readonly BridgeOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Telemetry.AutoPush) return;
        if (string.IsNullOrWhiteSpace(_opts.Telemetry.Hostname))
        {
            log.LogWarning("Bridge:Telemetry:Hostname er ikke sat – sender ingen telemetri-konfiguration til bilen");
            return;
        }

        // Lille forsinkelse så proxy og token er klar.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            foreach (var v in _opts.Vehicles)
            {
                try { await EnsureFreshAsync(v.Vin, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Kunne ikke kontrollere/forny telemetri-konfiguration for {Vin}: {Error}", v.Vin, ex.Message);
                }
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Forny hvis bilens konfiguration mangler, peger andetsteds hen eller snart udløber.</summary>
    public async Task EnsureFreshAsync(string vin, CancellationToken ct)
    {
        var current = await GetStatusAsync(vin, ct);
        var cfg = current?["response"]?["config"];
        var host = cfg?["hostname"]?.ToString();
        var exp = cfg?["exp"] is JsonValue e && e.TryGetValue(out long expUnix) ? DateTimeOffset.FromUnixTimeSeconds(expUnix) : (DateTimeOffset?)null;

        var reason = cfg is null ? "ingen konfiguration på bilen"
            : !string.Equals(host, _opts.Telemetry.Hostname, StringComparison.OrdinalIgnoreCase) ? $"bilen sender til '{host}'"
            : exp is null ? "ukendt udløb"
            : exp.Value - time.GetUtcNow() < TimeSpan.FromDays(_opts.Telemetry.RenewWhenDaysLeft) ? $"udløber {exp:yyyy-MM-dd}"
            : FieldsDiffer(cfg?["fields"] as JsonObject, BuildRequest([vin])["config"]?["fields"] as JsonObject) is { } diff ? diff
            : null;

        if (reason is null)
        {
            log.LogInformation("Telemetri-konfiguration for {Vin} er frisk (udløber {Exp:yyyy-MM-dd}, synced={Synced})",
                vin, exp, current?["response"]?["synced"]);
            return;
        }

        log.LogInformation("Sender telemetri-konfiguration til {Vin}: {Reason}", vin, reason);
        await PushAsync([vin], ct);
    }

    /// <summary>Er felterne/intervallerne på bilen andre end dem, bridgen vil have (fx efter en opdatering)?</summary>
    internal static string? FieldsDiffer(JsonObject? onCar, JsonObject? wanted)
    {
        if (onCar is null || wanted is null) return null;      // Tesla returnerede ikke felterne – rør det ikke
        static int Interval(JsonNode? n) => n?["interval_seconds"] is JsonValue v && v.TryGetValue(out int i) ? i : -1;
        var missing = wanted.Where(kv => !onCar.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        var changed = wanted.Where(kv => onCar.ContainsKey(kv.Key) && Interval(onCar[kv.Key]) != Interval(kv.Value)).Select(kv => kv.Key).ToList();
        var extra = onCar.Where(kv => !wanted.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (missing.Count + changed.Count + extra.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"nye felter: {string.Join(", ", missing)}");
        if (changed.Count > 0) parts.Add($"nye intervaller: {string.Join(", ", changed)}");
        if (extra.Count > 0) parts.Add($"fjernede felter: {string.Join(", ", extra)}");
        return string.Join("; ", parts);
    }

    public async Task<JsonNode?> PushAsync(IReadOnlyList<string> vins, CancellationToken ct)
    {
        var body = BuildRequest(vins);
        var (status, resp, raw) = await proxy.SendAsync(HttpMethod.Post, "/api/1/vehicles/fleet_telemetry_config", body, ct);
        if ((int)status >= 300)
            throw new InvalidOperationException($"fleet_telemetry_config svarede {(int)status}: {raw}");

        var skipped = resp?["response"]?["skipped_vehicles"];
        if (skipped is JsonObject s && s.Any(kv => kv.Value is JsonArray a && a.Count > 0))
            log.LogWarning("Nogle biler blev sprunget over: {Skipped}. 'missing_key' = appens virtuelle nøgle er ikke i bilen.", skipped.ToJsonString());
        else
            log.LogInformation("Telemetri-konfiguration sendt: {Response}", resp?["response"]?.ToJsonString());
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
        var fieldsDoc = JsonNode.Parse(File.ReadAllText(t.FieldsFile))
            ?? throw new InvalidOperationException($"Tom {t.FieldsFile}");
        var fields = (fieldsDoc["fields"] as JsonObject)?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException($"{t.FieldsFile} mangler 'fields'");

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
