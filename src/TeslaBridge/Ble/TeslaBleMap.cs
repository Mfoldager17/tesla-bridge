using System.Globalization;
using System.Text.Json.Nodes;
using TeslaBridge.Commands;

namespace TeslaBridge.Ble;

/// <summary>An action on the ESP32: POST /{Domain}/{Name}/{Action}?{Query}.</summary>
public sealed record EspAction(string Domain, string Name, string Action, string? Query = null);

/// <summary>
/// How the bridge's commands and state keys line up with the entities of the ESPHome Tesla BLE component
/// (github.com/PedroKTFC/esphome-tesla-ble, English entity names – the default). Kept apart from the
/// transport so it can be tested without a device.
/// </summary>
public static class TeslaBleMap
{
    private const double KmPerMile = 1.609344;

    // Entities the bridge uses for its own bookkeeping
    public const string Presence = "BLE Status";        // binary_sensor: the car's Bluetooth is seen nearby
    public const string Link = "BLE Connection";        // switch: the ESP32 holds a connection to the car
    public const string Asleep = "Asleep";              // binary_sensor
    public const string Signal = "BLE Signal";          // sensor: RSSI dBm
    public const string WakeButton = "Wake up";
    public const string PairButton = "Pair BLE key";
    public const string Version = "Version";            // text_sensor: ESPHome version

    /// <summary>The ESP32 action for a command, or null when it can't be done over Bluetooth.</summary>
    public static EspAction? ActionFor(VehicleCommand command) => command switch
    {
        Cmd.ClimateOn => new("switch", "Climate", "turn_on"),
        Cmd.ClimateOff => new("switch", "Climate", "turn_off"),
        Cmd.SetDriverTemp t => new("number", "Climate temperature", "set", Value(Math.Round(t.Celsius))),
        Cmd.SetDefrost d => new("switch", "Defrost car", d.On ? "turn_on" : "turn_off"),
        Cmd.SetSteeringWheelHeat s => new("switch", "Steering wheel heat", s.On ? "turn_on" : "turn_off"),
        Cmd.Lock => new("lock", "Lock car", "lock"),
        Cmd.Unlock => new("lock", "Lock car", "unlock"),
        Cmd.SetSentry s => new("switch", "Sentry mode", s.On ? "turn_on" : "turn_off"),
        Cmd.StartCharging => new("switch", "Charger", "turn_on"),
        Cmd.StopCharging => new("switch", "Charger", "turn_off"),
        Cmd.SetChargeLimit l => new("number", "Charging limit", "set", Value(l.Percent)),
        Cmd.SetChargingAmps a => new("number", "Charging amps", "set", Value(a.Amps)),
        Cmd.OpenChargePort => new("cover", "Charge port", "open"),
        Cmd.CloseChargePort => new("cover", "Charge port", "close"),
        Cmd.OpenFrunk => new("cover", "Frunk", "open"),
        Cmd.ToggleTrunk { Open: true } => new("cover", "Boot", "open"),
        Cmd.ToggleTrunk { Open: false } => new("cover", "Boot", "close"),
        Cmd.ToggleTrunk => new("cover", "Boot", "toggle"),
        Cmd.VentWindows => new("cover", "Vent", "open"),
        Cmd.CloseWindows => new("cover", "Vent", "close"),
        Cmd.SetCabinOverheatProtection c => new("select", "Cabin overheat protection", "set",
            "option=" + (c.Mode switch { "off" => "Off", "fan_only" => "Fan", _ => "On" })),
        Cmd.SetCopTemp c => new("select", "Cabin overheat limit", "set",
            "option=" + (c.Celsius switch { <= 30 => "Low", <= 35 => "Medium", _ => "High" })),
        Cmd.FlashLights => new("button", "Flash light", "press"),
        Cmd.HonkHorn => new("button", "Sound horn", "press"),
        Cmd.Wake => new("button", WakeButton, "press"),
        _ => null,
    };

    private static string Value(double v) => "value=" + v.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The bridge state keys a changed ESP32 entity gives. Only read while the ESP32 is connected to the car;
    /// values the car hasn't reported (NaN, "Unknown") are skipped so they never overwrite good data.
    /// </summary>
    public static IEnumerable<(string Key, JsonNode? Value)> StateFor(EspEntity e)
    {
        var name = EspHomeDevice.Key(e.Domain, e.UrlName.Contains('/') ? e.UrlName[(e.UrlName.LastIndexOf('/') + 1)..] : e.UrlName);
        switch (name)
        {
            case "sensor:chargelevel": if (Num(e) is { } bl) yield return ("battery_level", V(Math.Round(bl, 1))); break;
            case "sensor:chargelimit": if (Num(e) is { } cl) yield return ("charge_limit", V(Math.Round(cl))); break;
            case "sensor:currentlimit": if (Num(e) is { } amps) yield return ("charge_current_request", V(Math.Round(amps))); break;
            case "sensor:chargecurrent": if (Num(e) is { } cc) yield return ("charger_current", V(Math.Round(cc))); break;
            case "sensor:chargevoltage": if (Num(e) is { } cv) yield return ("charger_voltage", V(Math.Round(cv))); break;
            case "sensor:chargepower": if (Num(e) is { } cp) yield return ("ac_charging_power", V(Math.Round(cp, 1))); break;
            case "sensor:minutestolimit": if (Num(e) is { } mins) yield return ("time_to_full_charge", V(Math.Round(mins / 60.0, 2))); break;
            case "sensor:range": if (Km(e) is { } r) yield return ("est_range", V(Math.Round(r))); break;
            case "sensor:odometer": if (Km(e) is { } km) yield return ("odometer", V(Math.Round(km, 1))); break;
            case "sensor:interior": if (Num(e) is { } it) yield return ("inside_temp", V(Math.Round(it, 1))); break;
            case "sensor:exterior": if (Num(e) is { } ot) yield return ("outside_temp", V(Math.Round(ot, 1))); break;
            case "sensor:climatetemperature": if (Num(e) is { } dt) yield return ("driver_temp_setting", V(Math.Round(dt, 1))); break;
            case "sensor:tyrepressurefrontleft": if (Num(e) is { } p1) yield return ("tire_pressure_front_left", V(Math.Round(p1, 2))); break;
            case "sensor:tyrepressurefrontright": if (Num(e) is { } p2) yield return ("tire_pressure_front_right", V(Math.Round(p2, 2))); break;
            case "sensor:tyrepressurerearleft": if (Num(e) is { } p3) yield return ("tire_pressure_rear_left", V(Math.Round(p3, 2))); break;
            case "sensor:tyrepressurerearright": if (Num(e) is { } p4) yield return ("tire_pressure_rear_right", V(Math.Round(p4, 2))); break;

            case "binary_sensor:doors": if (EspHomeDevice.Bool(e) is { } unlocked) yield return ("locked", JsonValue.Create(!unlocked)); break;
            case "binary_sensor:chargeflap": if (EspHomeDevice.Bool(e) is { } flap) yield return ("charge_port_open", JsonValue.Create(flap)); break;
            case "binary_sensor:boot": if (EspHomeDevice.Bool(e) is { } boot) yield return ("trunk_open", JsonValue.Create(boot)); break;
            case "binary_sensor:frunk": if (EspHomeDevice.Bool(e) is { } frunk) yield return ("frunk_open", JsonValue.Create(frunk)); break;
            case "binary_sensor:windows": if (EspHomeDevice.Bool(e) is { } win) yield return ("windows_open", JsonValue.Create(win)); break;
            case "binary_sensor:climate": if (EspHomeDevice.Bool(e) is { } clim) yield return ("climate_on", JsonValue.Create(clim)); break;
            case "binary_sensor:userpresence": if (EspHomeDevice.Bool(e) is { } up) yield return ("user_present", JsonValue.Create(up)); break;

            case "text_sensor:chargingstate":
                if (ChargingState(EspHomeDevice.Text(e)) is { } cs)
                {
                    yield return ("charging_state", JsonValue.Create(cs));
                    yield return ("charge_cable_connected", JsonValue.Create(cs != "disconnected"));
                }
                break;
            case "text_sensor:shiftstate":
                if (EspHomeDevice.Text(e) is { } g) yield return ("gear", JsonValue.Create(g is "P" or "R" or "N" or "D" ? g : "P"));
                break;
            case "text_sensor:defroststate":
                if (EspHomeDevice.Text(e) is { } df) yield return ("defrost", JsonValue.Create(!df.Equals("Off", StringComparison.OrdinalIgnoreCase)));
                break;
        }
    }

    /// <summary>"Charging", "NoPower" … → the stream's values ("charging", "no_power").</summary>
    internal static string? ChargingState(string? s) => s switch
    {
        null => null,
        "Charging" or "Calibrating" => "charging",
        "Starting" => "starting",
        "Complete" => "complete",
        "Stopped" => "stopped",
        "Disconnected" => "disconnected",
        "NoPower" or "No Power" or "No power" => "no_power",
        _ => null,
    };

    private static double? Num(EspEntity e) => EspHomeDevice.Number(e);

    /// <summary>Distances come in miles unless the YAML changed the unit.</summary>
    private static double? Km(EspEntity e) =>
        Num(e) is { } d ? (EspHomeDevice.Unit(e) is "km" ? d : d * KmPerMile) : null;

    private static JsonNode V(double d) => JsonValue.Create(d);
}
