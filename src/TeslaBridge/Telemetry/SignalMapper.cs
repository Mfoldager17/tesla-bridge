using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeslaBridge.Telemetry;

/// <summary>
/// Oversætter rå Fleet Telemetry-felter (som fleet-telemetry lægger på MQTT) til bridgens
/// normaliserede nøgler i metriske enheder. Tesla kan skifte typer mellem firmwareversioner
/// (fx 12.3 vs "12.3"), så alt parses tolerant.
/// </summary>
public static class SignalMapper
{
    private const double KmPerMile = 1.609344;

    public static IEnumerable<(string Key, JsonNode? Value)> Map(string field, JsonNode? raw)
    {
        switch (field)
        {
            // --- kørsel / position ---
            case "Location":
                yield return ("location", MapLocation(raw));
                break;
            case "GpsHeading": yield return ("heading", Round(Num(raw), 0)); break;
            case "VehicleSpeed": yield return ("speed", Round(Num(raw) * KmPerMile, 0)); break;
            case "Gear": yield return ("gear", Str(EnumTail(raw, "ShiftState")?.ToUpperInvariant())); break;
            case "Odometer": yield return ("odometer", Round(Num(raw) * KmPerMile, 1)); break;

            // --- batteri ---
            case "BatteryLevel": yield return ("battery_level", Round(Num(raw), 1)); break;
            case "Soc": yield return ("soc", Round(Num(raw), 1)); break;
            case "EstBatteryRange": yield return ("est_range", Round(Num(raw) * KmPerMile, 0)); break;
            case "IdealBatteryRange": yield return ("ideal_range", Round(Num(raw) * KmPerMile, 0)); break;
            case "RatedRange": yield return ("rated_range", Round(Num(raw) * KmPerMile, 0)); break;

            // --- opladning ---
            case "DetailedChargeState":
            {
                var state = EnumTail(raw, "DetailedChargeState");
                yield return ("charging_state", Str(state));
                yield return ("charge_cable_connected",
                    state is null or "unknown" ? null : JsonValue.Create(state != "disconnected"));
                break;
            }
            case "ChargeLimitSoc": yield return ("charge_limit", Round(Num(raw), 0)); break;
            case "ChargeCurrentRequest": yield return ("charge_current_request", Round(Num(raw), 0)); break;
            case "ChargePortDoorOpen": yield return ("charge_port_open", Bool(raw)); break;
            case "ACChargingPower": yield return ("ac_charging_power", Round(Num(raw), 1)); break;
            case "DCChargingPower": yield return ("dc_charging_power", Round(Num(raw), 1)); break;
            case "ACChargingEnergyIn": yield return ("ac_charging_energy_in", Round(Num(raw), 2)); break;
            case "DCChargingEnergyIn": yield return ("dc_charging_energy_in", Round(Num(raw), 2)); break;
            case "ChargeAmps": yield return ("charger_current", Round(Num(raw), 0)); break;
            case "ChargerVoltage": yield return ("charger_voltage", Round(Num(raw), 0)); break;
            case "ChargePortLatch":
            {
                var state = EnumTail(raw, "ChargePortLatch");
                yield return ("charge_port_latch", Str(state));
                yield return ("charge_cable_locked", state switch
                {
                    "engaged" => JsonValue.Create(true),
                    "disengaged" => JsonValue.Create(false),
                    _ => null,
                });
                break;
            }
            case "ScheduledChargingPending": yield return ("scheduled_charging_pending", Bool(raw)); break;
            case "FastChargerPresent": yield return ("fast_charger_present", Bool(raw)); break;
            case "EnergyRemaining": yield return ("energy_remaining", Round(Num(raw), 2)); break;
            case "TpmsPressureFl": yield return ("tire_pressure_front_left", Round(Num(raw), 2)); break;
            case "TpmsPressureFr": yield return ("tire_pressure_front_right", Round(Num(raw), 2)); break;
            case "TpmsPressureRl": yield return ("tire_pressure_rear_left", Round(Num(raw), 2)); break;
            case "TpmsPressureRr": yield return ("tire_pressure_rear_right", Round(Num(raw), 2)); break;
            case "TimeToFullCharge": yield return ("time_to_full_charge", Round(Num(raw), 2)); break;

            // --- sikkerhed / karosseri ---
            case "Locked": yield return ("locked", Bool(raw)); break;
            case "DoorState":
                foreach (var kv in MapDoors(raw)) yield return kv;
                break;
            case "FdWindow": yield return ("window_driver_front_open", WindowOpen(raw)); break;
            case "FpWindow": yield return ("window_passenger_front_open", WindowOpen(raw)); break;
            case "RdWindow": yield return ("window_driver_rear_open", WindowOpen(raw)); break;
            case "RpWindow": yield return ("window_passenger_rear_open", WindowOpen(raw)); break;
            case "SentryMode":
            {
                var state = EnumTail(raw, "SentryModeState");
                yield return ("sentry_state", Str(state));
                yield return ("sentry_mode", state is null or "unknown" ? null : JsonValue.Create(state != "off"));
                break;
            }
            case "DriverSeatOccupied": yield return ("user_present", Bool(raw)); break;
            case "DriverSeatBelt":
            {
                var state = EnumTail(raw, "BuckleStatus");
                yield return ("driver_seat_belt", state switch
                {
                    "latched" => JsonValue.Create(true),
                    "unlatched" => JsonValue.Create(false),
                    _ => Bool(raw),
                });
                break;
            }
            case "GuestModeEnabled": yield return ("guest_mode", Bool(raw)); break;
            case "ValetModeEnabled": yield return ("valet_mode", Bool(raw)); break;

            // --- klima ---
            case "HvacPower":
            {
                var state = EnumTail(raw, "HvacPowerState");
                yield return ("hvac_state", Str(state));
                yield return ("climate_on", state switch
                {
                    null or "unknown" => null,
                    "on" or "precondition" => JsonValue.Create(true),
                    _ => JsonValue.Create(false),
                });
                break;
            }
            case "HvacLeftTemperatureRequest": yield return ("driver_temp_setting", Round(Num(raw), 1)); break;
            case "DefrostMode":
            {
                var state = EnumTail(raw, "DefrostModeState");
                yield return ("defrost", state is null or "unknown" ? null : JsonValue.Create(state != "off"));
                break;
            }
            case "SeatHeaterLeft": yield return ("seat_heater_left", Round(Num(raw), 0)); break;
            case "SeatHeaterRight": yield return ("seat_heater_right", Round(Num(raw), 0)); break;
            case "SeatHeaterRearLeft": yield return ("seat_heater_rear_left", Round(Num(raw), 0)); break;
            case "SeatHeaterRearCenter": yield return ("seat_heater_rear_center", Round(Num(raw), 0)); break;
            case "SeatHeaterRearRight": yield return ("seat_heater_rear_right", Round(Num(raw), 0)); break;
            case "ClimateKeeperMode":
            {
                // off / keep / dog / camp (Tesla kalder camp for "Party" internt)
                var state = EnumTail(raw, "ClimateKeeperModeState");
                yield return ("climate_keeper_mode", Str(state switch
                {
                    null or "unknown" => null,
                    "on" => "keep",
                    "party" => "camp",
                    var s => s,
                }));
                break;
            }
            case "CabinOverheatProtectionMode":
            {
                var state = EnumTail(raw, "CabinOverheatProtectionModeState");
                yield return ("cabin_overheat_protection", Str(state is null or "unknown" ? null : state));
                break;
            }
            case "HvacSteeringWheelHeatLevel":
            {
                var level = SteeringLevel(raw);
                yield return ("steering_wheel_heat_level", level is null ? null : JsonValue.Create(level.Value));
                yield return ("steering_wheel_heat", level is null ? null : JsonValue.Create(level > 0));
                break;
            }
            case "HvacRightTemperatureRequest": yield return ("passenger_temp_setting", Round(Num(raw), 1)); break;
            case "AutoSeatClimateLeft": yield return ("auto_seat_climate_left", Bool(raw)); break;
            case "AutoSeatClimateRight": yield return ("auto_seat_climate_right", Bool(raw)); break;
            case "HvacSteeringWheelHeatAuto": yield return ("auto_steering_wheel_heat", Bool(raw)); break;
            case "CabinOverheatProtectionTemperatureLimit":
                yield return ("cop_temp_limit", EnumTail(raw, "ClimateOverheatProtectionTempLimit") switch
                {
                    "high" => JsonValue.Create(40),
                    "medium" => JsonValue.Create(35),
                    "low" => JsonValue.Create(30),
                    _ => null,
                });
                break;
            case "PreconditioningEnabled": yield return ("preconditioning_enabled", Bool(raw)); break;
            case "InsideTemp": yield return ("inside_temp", Round(Num(raw), 1)); break;
            case "OutsideTemp": yield return ("outside_temp", Round(Num(raw), 1)); break;

            // --- navigation ---
            case "DestinationName":
            {
                var s = raw is JsonValue v && v.TryGetValue(out string? str) ? str : null;
                yield return ("destination", string.IsNullOrWhiteSpace(s) ? null : JsonValue.Create(s));
                break;
            }
            case "MinutesToArrival": yield return ("minutes_to_arrival", Round(Num(raw), 0)); break;
            case "MilesToArrival": yield return ("km_to_arrival", Round(Num(raw) * KmPerMile, 1)); break;
            case "DestinationLocation": yield return ("destination_location", MapLocation(raw)); break;
            case "ExpectedEnergyPercentAtTripArrival": yield return ("soc_at_arrival", Round(Num(raw), 0)); break;

            case "Version": yield return ("software_version", Str(StrOf(raw))); break;

            // Alt andet du tilføjer i telemetry-fields.json kommer igennem uændret som snake_case.
            default:
                yield return (ToSnake(field), raw?.DeepClone());
                break;
        }
    }

    public static readonly string[] WindowKeys =
        ["window_driver_front_open", "window_passenger_front_open", "window_driver_rear_open", "window_passenger_rear_open"];

    /// <summary>Samlet rudestatus til HA's "Windows"-cover: true hvis én rude er åben, null hvis ingen er kendt.</summary>
    public static JsonNode? WindowsOpen(Func<string, JsonNode?> get)
    {
        var known = WindowKeys.Select(get).Where(v => v is JsonValue).Select(v => v!.GetValue<bool>()).ToList();
        return known.Count == 0 ? null : JsonValue.Create(known.Any(x => x));
    }

    // ---------- felttyper ----------

    private static JsonNode? MapLocation(JsonNode? raw)
    {
        if (raw is not JsonObject o) return null;
        var lat = Num(o["latitude"]);
        var lon = Num(o["longitude"]);
        if (lat is null || lon is null) return null;
        return new JsonObject { ["latitude"] = lat, ["longitude"] = lon };
    }

    private static readonly (string Raw, string Key)[] Doors =
    [
        ("DriverFront", "door_driver_front_open"),
        ("PassengerFront", "door_passenger_front_open"),
        ("DriverRear", "door_driver_rear_open"),
        ("PassengerRear", "door_passenger_rear_open"),
        ("TrunkFront", "frunk_open"),
        ("TrunkRear", "trunk_open"),
    ];

    private static IEnumerable<(string, JsonNode?)> MapDoors(JsonNode? raw)
    {
        var o = raw as JsonObject;
        foreach (var (rawKey, key) in Doors)
            yield return (key, o is null ? null : JsonValue.Create(Bool(o[rawKey])?.GetValue<bool>() ?? false));
    }

    private static JsonNode? WindowOpen(JsonNode? raw)
    {
        var s = StrOf(raw);
        if (s is null || s.EndsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return JsonValue.Create(!s.EndsWith("Closed", StringComparison.OrdinalIgnoreCase));
    }

    private static int? SteeringLevel(JsonNode? raw)
    {
        if (Num(raw) is { } n) return (int)n;
        var s = StrOf(raw)?.ToLowerInvariant();
        if (s is null) return null;
        if (s.EndsWith("off")) return 0;
        if (s.EndsWith("low")) return 1;
        if (s.EndsWith("high")) return 3;
        return null;
    }

    // ---------- tolerant parsing ----------

    internal static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number: return v.GetValue<double>();
            case JsonValueKind.String:
                return double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            default: return null;
        }
    }

    internal static JsonNode? Bool(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Number => JsonValue.Create(v.GetValue<double>() != 0),
            JsonValueKind.String => v.GetValue<string>().ToLowerInvariant() switch
            {
                "true" or "1" or "on" => JsonValue.Create(true),
                "false" or "0" or "off" => JsonValue.Create(false),
                _ => null,
            },
            _ => null,
        };
    }

    private static string? StrOf(JsonNode? n) =>
        n is JsonValue v ? v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            _ => null,
        } : null;

    private static JsonNode? Str(string? s) => s is null ? null : JsonValue.Create(s);

    private static JsonNode? Round(double? d, int digits) =>
        d is null ? null : JsonValue.Create(Math.Round(d.Value, digits));

    /// <summary>"DetailedChargeStateNoPower" → "no_power". Accepterer også værdier uden præfiks.</summary>
    internal static string? EnumTail(JsonNode? raw, string prefix)
    {
        var s = StrOf(raw);
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        return s.Length == 0 ? null : ToSnake(s);
    }

    internal static string ToSnake(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]));
                var nextLower = i + 1 < s.Length && char.IsLower(s[i + 1]) && i > 0 && char.IsUpper(s[i - 1]);
                if (prevLower || nextLower) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
