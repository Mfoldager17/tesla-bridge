using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Telemetry;

/// <summary>
/// Bilen streamer kun ændringer. Startede bridgen, mens bilen holdt stille, kender den derfor ikke
/// gear, ruder, ladekabellås osv., før de ændrer sig. Som Teslemetry henter vi ét snapshot
/// (GET vehicle_data, $0,002) når bilen kommer online, og udfylder KUN felter, streamen ikke har givet os.
/// Vækker aldrig bilen (vehicle_data svarer 408, hvis den sover). Højst én gang i timen pr. bil.
/// </summary>
public sealed class VehicleDataSeeder(
    VehicleStateStore store,
    TeslaProxyClient proxy,
    TimeProvider time,
    IOptions<BridgeOptions> options,
    ILogger<VehicleDataSeeder> log) : BackgroundService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeed = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Channels.Channel<string> _queue = System.Threading.Channels.Channel.CreateUnbounded<string>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += OnChanged;
        _ = Task.Run(() => CheckSleepStateAsync(stoppingToken), stoppingToken);
        try
        {
            await foreach (var vin in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try { await SeedAsync(vin, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning("Kunne ikke hente snapshot fra {Vin}: {Error}", vin, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { store.Changed -= OnChanged; }
    }

    /// <summary>
    /// Ved opstart: spørg Tesla om bilen sover (GET /vehicles/{vin}, vækker den ikke), så dashboard og HA
    /// kan vise "sover" i stedet for "venter på signal". Telemetrien overtager, så snart bilen sender noget.
    /// </summary>
    private async Task CheckSleepStateAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        foreach (var v in options.Value.Vehicles)
        {
            if (store.GetConfirmed(v.Vin, "online") is not null) continue;   // streamen nåede først
            try
            {
                var (status, body, _) = await proxy.SendAsync(HttpMethod.Get, $"/api/1/vehicles/{v.Vin}", null, ct);
                var state = body?["response"]?["state"]?.ToString();
                if ((int)status >= 300 || state is null) continue;
                if (store.GetConfirmed(v.Vin, "online") is not null) continue;
                store.Apply(v.Vin, "online", JsonValue.Create(state == "online"), time.GetUtcNow(), ValueSource.Snapshot);
                log.LogInformation("{Vin} er {State} ifølge Tesla", v.Vin, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogInformation("Kunne ikke spørge Tesla om {Vin} sover: {Error}", v.Vin, ex.Message);
            }
        }
    }

    private void OnChanged(StateChange c)
    {
        if (c.Key == "online" && c.Effective?.Value is JsonValue v && v.TryGetValue(out bool online) && online
            && options.Value.Vehicles.Any(x => string.Equals(x.Vin, c.Vin, StringComparison.OrdinalIgnoreCase)))
            _queue.Writer.TryWrite(c.Vin);
    }

    public async Task SeedAsync(string vin, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_lastSeed.TryGetValue(vin, out var last) && now - last < MinInterval) return;
        if (!NeedsSeed(vin)) return;
        _lastSeed[vin] = now;

        const string endpoints = "charge_state%3Bclimate_state%3Bvehicle_state%3Bdrive_state%3Blocation_data";
        var (status, body, raw) = await proxy.SendAsync(HttpMethod.Get, $"/api/1/vehicles/{vin}/vehicle_data?endpoints={endpoints}", null, ct);
        if ((int)status == 408) { log.LogInformation("{Vin} sover – snapshot springes over", vin); _lastSeed.TryRemove(vin, out _); return; }
        if ((int)status >= 300 || body?["response"] is not JsonObject response)
        {
            log.LogWarning("vehicle_data for {Vin} svarede {Status}: {Raw}", vin, (int)status, raw.Length > 300 ? raw[..300] : raw);
            return;
        }

        var filled = 0;
        foreach (var (key, value) in VehicleDataMapper.Map(response))
        {
            if (value is null) continue;
            var current = store.GetConfirmed(vin, key);
            if (current?.Value is not null && current.Source != ValueSource.Restored) continue;   // streamen vinder altid
            store.Apply(vin, key, value, now, ValueSource.Snapshot);
            filled++;
        }
        log.LogInformation("Snapshot fra {Vin}: udfyldte {Count} felter, streamen ikke havde givet endnu", vin, filled);
    }

    private bool NeedsSeed(string vin) =>
        VehicleDataMapper.Keys.Any(k => store.GetConfirmed(vin, k) is not { Value: not null } fv || fv.Source == ValueSource.Restored);
}

/// <summary>vehicle_data (Fleet API, miles/Fahrenheit-agnostisk: Tesla svarer i miles og °C) → bridgens nøgler.</summary>
public static class VehicleDataMapper
{
    private const double KmPerMile = 1.609344;

    /// <summary>Nøgler, snapshot'et kan udfylde (bruges til at afgøre om det overhovedet er nødvendigt).</summary>
    public static readonly string[] Keys =
    [
        "gear", "speed", "locked", "windows_open", "charge_cable_locked", "guest_mode", "valet_mode",
        "auto_seat_climate_left", "auto_steering_wheel_heat", "cabin_overheat_protection", "climate_keeper_mode",
        "seat_heater_rear_left", "est_range", "tire_pressure_front_left",
    ];

    public static IEnumerable<(string Key, JsonNode? Value)> Map(JsonObject r)
    {
        var cs = r["charge_state"] as JsonObject;
        var cl = r["climate_state"] as JsonObject;
        var vs = r["vehicle_state"] as JsonObject;
        var ds = r["drive_state"] as JsonObject;

        // --- opladning ---
        yield return ("battery_level", N(cs?["battery_level"]));
        yield return ("est_range", N(cs?["est_battery_range"], KmPerMile, 0));
        yield return ("ideal_range", N(cs?["ideal_battery_range"], KmPerMile, 0));
        yield return ("rated_range", N(cs?["battery_range"], KmPerMile, 0));
        yield return ("charge_limit", N(cs?["charge_limit_soc"]));
        yield return ("charge_current_request", N(cs?["charge_current_request"]));
        yield return ("charge_port_open", B(cs?["charge_port_door_open"]));
        var latch = S(cs?["charge_port_latch"])?.ToLowerInvariant();
        yield return ("charge_cable_locked", latch switch { "engaged" => V(true), "disengaged" => V(false), _ => null });
        var charging = S(cs?["charging_state"]);
        if (charging is not null)
        {
            var state = SignalMapper.ToSnake(charging);
            yield return ("charging_state", V(state));
            yield return ("charge_cable_connected", V(state != "disconnected"));
        }
        yield return ("ac_charging_energy_in", N(cs?["charge_energy_added"], 1, 2));
        yield return ("charger_voltage", N(cs?["charger_voltage"]));
        yield return ("charger_current", N(cs?["charger_actual_current"]));
        yield return ("ac_charging_power", N(cs?["charger_power"]));
        yield return ("time_to_full_charge", N(cs?["time_to_full_charge"], 1, 2));
        yield return ("scheduled_charging_pending", B(cs?["scheduled_charging_pending"]));
        yield return ("fast_charger_present", B(cs?["fast_charger_present"]));

        // --- klima ---
        yield return ("inside_temp", N(cl?["inside_temp"], 1, 1));
        yield return ("outside_temp", N(cl?["outside_temp"], 1, 1));
        yield return ("driver_temp_setting", N(cl?["driver_temp_setting"], 1, 1));
        yield return ("passenger_temp_setting", N(cl?["passenger_temp_setting"], 1, 1));
        yield return ("climate_on", B(cl?["is_climate_on"]));
        yield return ("climate_keeper_mode", S(cl?["climate_keeper_mode"])?.ToLowerInvariant() switch
        {
            "off" => V("off"), "on" => V("keep"), "dog" => V("dog"), "camp" or "party" => V("camp"), _ => null,
        });
        yield return ("cabin_overheat_protection", S(cl?["cabin_overheat_protection"])?.ToLowerInvariant() switch
        {
            "off" => V("off"), "on" => V("on"), "fanonly" or "fan_only" => V("fan_only"), _ => null,
        });
        yield return ("cop_temp_limit", S(cl?["cop_activation_temperature"])?.ToLowerInvariant() switch
        {
            "high" => V(40), "medium" => V(35), "low" => V(30), _ => null,
        });
        yield return ("defrost", cl?["defrost_mode"] is JsonNode dm ? V((N(dm)?.GetValue<double>() ?? 0) != 0) : B(cl?["is_front_defroster_on"]));
        yield return ("preconditioning_enabled", B(cl?["is_preconditioning"]));
        yield return ("seat_heater_left", N(cl?["seat_heater_left"]));
        yield return ("seat_heater_right", N(cl?["seat_heater_right"]));
        yield return ("seat_heater_rear_left", N(cl?["seat_heater_rear_left"]));
        yield return ("seat_heater_rear_center", N(cl?["seat_heater_rear_center"]));
        yield return ("seat_heater_rear_right", N(cl?["seat_heater_rear_right"]));
        yield return ("steering_wheel_heat_level", N(cl?["steering_wheel_heat_level"]));
        yield return ("auto_seat_climate_left", B(cl?["auto_seat_climate_left"]));
        yield return ("auto_seat_climate_right", B(cl?["auto_seat_climate_right"]));
        yield return ("auto_steering_wheel_heat", B(cl?["auto_steering_wheel_heat"]));

        // --- karosseri ---
        yield return ("locked", B(vs?["locked"]));
        yield return ("sentry_mode", B(vs?["sentry_mode"]));
        yield return ("valet_mode", B(vs?["valet_mode"]));
        yield return ("guest_mode", B(vs?["guest_mode_enabled"] ?? vs?["guest_mode"]));
        yield return ("door_driver_front_open", B(vs?["df"]));
        yield return ("door_driver_rear_open", B(vs?["dr"]));
        yield return ("door_passenger_front_open", B(vs?["pf"]));
        yield return ("door_passenger_rear_open", B(vs?["pr"]));
        yield return ("frunk_open", B(vs?["ft"]));
        yield return ("trunk_open", B(vs?["rt"]));
        var windows = new[] { "fd_window", "fp_window", "rd_window", "rp_window" }.Select(w => B(vs?[w])).ToArray();
        yield return ("window_driver_front_open", windows[0]);
        yield return ("window_passenger_front_open", windows[1]);
        yield return ("window_driver_rear_open", windows[2]);
        yield return ("window_passenger_rear_open", windows[3]);
        if (windows.Any(w => w is not null))
            yield return ("windows_open", V(windows.Any(w => w?.GetValue<bool>() == true)));
        yield return ("odometer", N(vs?["odometer"], KmPerMile, 1));
        yield return ("software_version", S(vs?["car_version"])?.Split(' ')[0] is { } ver ? V(ver) : null);
        yield return ("tire_pressure_front_left", N(vs?["tpms_pressure_fl"], 1, 2));
        yield return ("tire_pressure_front_right", N(vs?["tpms_pressure_fr"], 1, 2));
        yield return ("tire_pressure_rear_left", N(vs?["tpms_pressure_rl"], 1, 2));
        yield return ("tire_pressure_rear_right", N(vs?["tpms_pressure_rr"], 1, 2));
        yield return ("user_present", B(vs?["is_user_present"]));

        // --- kørsel ---
        if (ds is not null)
        {
            // Står bilen stille, er shift_state/speed null i vehicle_data.
            yield return ("gear", V((S(ds["shift_state"]) ?? "P").ToUpperInvariant()));
            yield return ("speed", ds["speed"] is null ? V(0d) : N(ds["speed"], KmPerMile, 0));
            yield return ("heading", N(ds["heading"]));
            if (N(ds["latitude"], 1, 6) is { } lat && N(ds["longitude"], 1, 6) is { } lon)
                yield return ("location", new JsonObject { ["latitude"] = lat.GetValue<double>(), ["longitude"] = lon.GetValue<double>() });
        }
    }

    private static JsonNode? V(string s) => JsonValue.Create(s);
    private static JsonNode? V(bool b) => JsonValue.Create(b);
    private static JsonNode? V(double d) => JsonValue.Create(d);
    private static JsonNode? V(int i) => JsonValue.Create(i);

    private static JsonNode? N(JsonNode? n, double factor = 1, int digits = 1) =>
        SignalMapper.Num(n) is { } d ? JsonValue.Create(Math.Round(d * factor, digits)) : null;

    private static JsonNode? B(JsonNode? n) => SignalMapper.Bool(n);

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
