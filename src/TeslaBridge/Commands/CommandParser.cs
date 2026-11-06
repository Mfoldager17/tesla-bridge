using System.Globalization;
using System.Text.Json.Nodes;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>
/// Oversætter MQTT-kommandoer ({bridge}/{vin}/cmd/{navn} + payload) til VehicleCommand.
/// Payloads er bevidst simple, så de virker fra HA, ESP32-paneler og mosquitto_pub:
///   climate on|off · climate_keeper none|keep|dog|camp · lock LOCK|UNLOCK · charge_limit 80 · seat_heater_left off|low|medium|high|0-3 · wake PRESS
/// </summary>
public static class CommandParser
{
    public static readonly string[] SeatLevels = ["off", "low", "medium", "high"];

    public static VehicleCommand? Parse(string name, string payload, Func<string, FieldValue?> state)
    {
        var p = payload.Trim().Trim('"');
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
            "trunk" => new Cmd.ToggleTrunk(),
            "vent_windows" => Location(state) is var (lat, lon) ? new Cmd.VentWindows(lat, lon) : new Cmd.VentWindows(0, 0),
            "close_windows" => Location(state) is var (lat2, lon2) ? new Cmd.CloseWindows(lat2, lon2) : new Cmd.CloseWindows(0, 0),
            "flash_lights" => new Cmd.FlashLights(),
            "honk_horn" => new Cmd.HonkHorn(),
            "wake" => new Cmd.Wake(),
            _ => null,
        };
    }

    /// <summary>HA-presets (Hold/Hund/Camp/none) og rå navne/tal.</summary>
    public static readonly (string Preset, string Key)[] KeeperPresets = [("Hold", "keep"), ("Hund", "dog"), ("Camp", "camp")];

    private static int? KeeperMode(string p)
    {
        var s = p.ToLowerInvariant();
        if (s is "none" or "off" or "fra" or "0") return 0;
        foreach (var (preset, key) in KeeperPresets)
            if (s == preset.ToLowerInvariant() || s == key) return Array.IndexOf(Cmd.SetClimateKeeper.Names, key);
        if (s == "on") return 1;
        return Num(p) is { } n && n is >= 0 and <= 3 ? (int)n : null;
    }

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
