using System.Globalization;
using System.Text.Json.Nodes;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>
/// Translates MQTT commands ({bridge}/{vin}/cmd/{name} + payload) to VehicleCommand. Payloads are deliberately
/// simple, so they work from HA, ESP32 panels and mosquitto_pub (a few Danish words are accepted too):
///   climate on|off · climate_keeper none|keep|dog|camp · lock LOCK|UNLOCK · charge_limit 80 · seat_heater_left off|low|medium|high|0-3 · wake PRESS
/// </summary>
public static class CommandParser
{
    public static readonly string[] SeatLevels = ["off", "low", "medium", "high"];

    public static VehicleCommand? Parse(string name, string payload, Func<string, FieldValue?> state)
    {
        var p = payload.Trim().Trim('"');
        name = Aliases.TryGetValue(name, out var alias) ? alias : name;
        return name switch
        {
            "climate" => OnOff(p) switch { true => new Cmd.ClimateOn(), false => new Cmd.ClimateOff(), _ => null },
            "driver_temp" => Num(p) is { } t ? new Cmd.SetDriverTemp(Math.Clamp(t, 15, 28)) : null,
            "defrost" => OnOff(p) is { } d ? new Cmd.SetDefrost(d) : null,
            "seat_heater_left" => Level(p) is { } l ? new Cmd.SetSeatHeater(Seat.FrontLeft, l) : null,
            "seat_heater_right" => Level(p) is { } r ? new Cmd.SetSeatHeater(Seat.FrontRight, r) : null,
            "seat_heater_rear_left" => Level(p) is { } rl ? new Cmd.SetSeatHeater(Seat.RearLeft, rl) : null,
            "seat_heater_rear_center" => Level(p) is { } rc ? new Cmd.SetSeatHeater(Seat.RearCenter, rc) : null,
            "seat_heater_rear_right" => Level(p) is { } rr ? new Cmd.SetSeatHeater(Seat.RearRight, rr) : null,
            "climate_keeper" => KeeperMode(p) is { } k ? new Cmd.SetClimateKeeper(k) : null,
            "cabin_overheat_protection" => p.ToLowerInvariant() switch
            {
                "off" or "fra" => new Cmd.SetCabinOverheatProtection("off"),
                "on" or "til" => new Cmd.SetCabinOverheatProtection("on"),
                "fan_only" or "kun blæser" => new Cmd.SetCabinOverheatProtection("fan_only"),
                _ => null,
            },
            "steering_wheel_heat" => OnOff(p) is { } s ? new Cmd.SetSteeringWheelHeat(s) : null,
            "lock" => p.ToUpperInvariant() switch
            {
                "LOCK" or "ON" or "TRUE" => new Cmd.Lock(),
                "UNLOCK" or "OFF" or "FALSE" => new Cmd.Unlock(),
                _ => null,
            },
            "sentry" => OnOff(p) is { } se ? new Cmd.SetSentry(se) : null,
            "charging" => OnOff(p) switch { true => new Cmd.StartCharging(), false => new Cmd.StopCharging(), _ => null },
            "charge_limit" => Num(p) is { } cl ? new Cmd.SetChargeLimit((int)Math.Clamp(Math.Round(cl), 50, 100)) : null,
            "charging_amps" => Num(p) is { } a ? new Cmd.SetChargingAmps((int)Math.Clamp(Math.Round(a), 1, 48)) : null,
            "charge_port" => OnOff(p) switch { true => new Cmd.OpenChargePort(), false => new Cmd.CloseChargePort(), _ => null },
            "charge_port_open" => new Cmd.OpenChargePort(),
            "charge_port_close" => new Cmd.CloseChargePort(),
            "frunk" => new Cmd.OpenFrunk(),
            "trunk" => p.ToUpperInvariant() switch
            {
                // The trunk toggles: don't open an open one or close a closed one (so it doesn't go the wrong way).
                "OPEN" => IsTrue(state, "trunk_open") ? null : new Cmd.ToggleTrunk(true),
                "CLOSE" => IsFalse(state, "trunk_open") ? null : new Cmd.ToggleTrunk(false),
                _ => new Cmd.ToggleTrunk(),
            },
            "vent_windows" => Location(state) is var (lat, lon) ? new Cmd.VentWindows(lat, lon) : new Cmd.VentWindows(0, 0),
            "close_windows" => Location(state) is var (lat2, lon2) ? new Cmd.CloseWindows(lat2, lon2) : new Cmd.CloseWindows(0, 0),
            "auto_seat_climate_left" => OnOff(p) is { } al ? new Cmd.SetAutoSeatClimate(1, al) : null,
            "auto_seat_climate_right" => OnOff(p) is { } ar ? new Cmd.SetAutoSeatClimate(2, ar) : null,
            "auto_steering_wheel_heater" => OnOff(p) is { } aw ? new Cmd.SetAutoSteeringWheelHeat(aw) : null,
            "steering_wheel_heater" => p.ToLowerInvariant() switch
            {
                "off" or "0" => new Cmd.SetSteeringWheelHeatLevel(0),
                "low" or "1" => new Cmd.SetSteeringWheelHeatLevel(1),
                "high" or "3" => new Cmd.SetSteeringWheelHeatLevel(3),
                _ => null,
            },
            "guest_mode" => OnOff(p) is { } g ? new Cmd.SetGuestMode(g) : null,
            "valet_mode" => OnOff(p) is { } va ? new Cmd.SetValetMode(va) : null,
            "cop_temp" => Num(p) is { } ct ? new Cmd.SetCopTemp((int)Math.Round(ct)) : null,
            "windows" => p.ToUpperInvariant() switch
            {
                "OPEN" or "VENT" => Location(state) is var (wl, wo) ? new Cmd.VentWindows(wl, wo) : new Cmd.VentWindows(0, 0),
                "CLOSE" => Location(state) is var (cl2, co2) ? new Cmd.CloseWindows(cl2, co2) : new Cmd.CloseWindows(0, 0),
                _ => null,
            },
            "charge_port_door" => p.ToUpperInvariant() switch { "OPEN" => new Cmd.OpenChargePort(), "CLOSE" => new Cmd.CloseChargePort(), _ => null },
            "charge_cable_lock" => p.ToUpperInvariant() switch { "UNLOCK" => new Cmd.OpenChargePort(), "LOCK" => new Cmd.CloseChargePort(), _ => null },
            "homelink" => Location(state) is var (hl, ho) ? new Cmd.TriggerHomelink(hl, ho) : null,
            "keyless_driving" => new Cmd.RemoteStartDrive(),
            "play_fart" => new Cmd.PlayFart(),
            "flash_lights" => new Cmd.FlashLights(),
            "honk_horn" => new Cmd.HonkHorn(),
            "wake" => new Cmd.Wake(),
            _ => null,
        };
    }

    /// <summary>Teslemetry's entity names → the bridge's command names (both work).</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["charge"] = "charging",
        ["sentry_mode"] = "sentry",
        ["charge_current"] = "charging_amps",
        ["seat_heater_front_left"] = "seat_heater_left",
        ["seat_heater_front_right"] = "seat_heater_right",
    };

    /// <summary>HA presets with the same values as Teslemetry (off = normal) – dashboards can translate them.</summary>
    public static readonly (string Preset, string Key)[] KeeperPresets =
        [("off", "off"), ("keep", "keep"), ("dog", "dog"), ("camp", "camp")];

    private static int? KeeperMode(string p)
    {
        var s = p.ToLowerInvariant().Replace(" mode", "");          // "Dog mode" → "dog"
        if (s is "none" or "off" or "normal" or "fra" or "0") return 0;
        foreach (var (preset, key) in KeeperPresets)
            if (string.Equals(s, preset, StringComparison.OrdinalIgnoreCase) || s == key) return Array.IndexOf(Cmd.SetClimateKeeper.Names, key);
        if (s == "on") return 1;
        return Num(p) is { } n && n is >= 0 and <= 3 ? (int)n : null;
    }

    private static bool IsTrue(Func<string, FieldValue?> state, string key) =>
        state(key)?.Value is JsonValue v && v.TryGetValue(out bool b) && b;

    private static bool IsFalse(Func<string, FieldValue?> state, string key) =>
        state(key)?.Value is JsonValue v && v.TryGetValue(out bool b) && !b;

    private static bool? OnOff(string p) => p.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "start" => true,
        "off" or "false" or "0" or "stop" => false,
        _ => null,
    };

    private static double? Num(string p) =>
        double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Level(string p)
    {
        var i = Array.IndexOf(SeatLevels, p.ToLowerInvariant());
        if (i >= 0) return i;
        return Num(p) is { } n && n is >= 0 and <= 3 ? (int)n : null;
    }

    private static (double, double)? Location(Func<string, FieldValue?> state) =>
        state("location")?.Value is JsonObject o &&
        o["latitude"] is JsonValue la && la.TryGetValue(out double lat) &&
        o["longitude"] is JsonValue lo && lo.TryGetValue(out double lon)
            ? (lat, lon) : null;
}
