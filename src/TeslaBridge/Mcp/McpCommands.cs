using TeslaBridge.Commands;
using TeslaBridge.Settings;

namespace TeslaBridge.Mcp;

/// <summary>
/// Which commands an AI assistant may send over MCP. Every command the bridge knows belongs to one entry here; the
/// owner picks a policy under Settings → AI assistants (MCP): none, the safe ones, all, or a custom list.
/// </summary>
public static class McpCommands
{
    public const string ReadOnly = "read_only", Safe = "safe", All = "all", Custom = "custom";
    public static readonly string[] Policies = [ReadOnly, Safe, All, Custom];

    /// <param name="Id">what the policy and the dashboard call it</param>
    /// <param name="Group">for grouping on the dashboard</param>
    /// <param name="Security">opens the car, lets someone drive it, or lowers its protection</param>
    /// <param name="Usage">how to send it with run_command</param>
    public sealed record Entry(string Id, string Group, bool Security, string Usage);

    public static readonly Entry[] Catalogue =
    [
        new("wake", "car", false, "wake"),
        new("climate", "climate", false, "climate on|off"),
        new("driver_temp", "climate", false, "driver_temp 15–28"),
        new("climate_keeper", "climate", false, "climate_keeper off|keep|dog|camp"),
        new("defrost", "climate", false, "defrost on|off"),
        new("seat_heater", "climate", false, "seat_heater_left|seat_heater_right|seat_heater_rear_left|seat_heater_rear_center|seat_heater_rear_right off|low|medium|high; auto_seat_climate_left|auto_seat_climate_right on|off"),
        new("steering_wheel_heat", "climate", false, "steering_wheel_heat on|off; steering_wheel_heater off|low|high; auto_steering_wheel_heater on|off"),
        new("cabin_overheat_protection", "climate", false, "cabin_overheat_protection off|on|fan_only; cop_temp 30|35|40"),
        new("charging", "charging", false, "charging on|off"),
        new("charge_limit", "charging", false, "charge_limit 50–100"),
        new("charging_amps", "charging", false, "charging_amps 1–48"),
        new("charge_port_close", "charging", false, "charge_port_door close"),
        new("charge_port_open", "charging", true, "charge_port_door open (also releases the cable)"),
        new("lock", "access", false, "lock lock"),
        new("unlock", "access", true, "lock unlock"),
        new("frunk", "access", true, "frunk"),
        new("trunk", "access", true, "trunk open|close"),
        new("windows_close", "access", false, "windows close"),
        new("windows_vent", "access", true, "windows vent"),
        new("sentry_on", "access", false, "sentry on"),
        new("sentry_off", "access", true, "sentry off"),
        new("guest_mode", "access", true, "guest_mode on|off"),
        new("valet_mode", "access", true, "valet_mode on|off"),
        new("homelink", "access", true, "homelink (the car must be near it)"),
        new("keyless_driving", "access", true, "keyless_driving (driving without a key for two minutes)"),
        new("flash_lights", "car", false, "flash_lights"),
        new("honk_horn", "car", false, "honk_horn"),
        new("play_fart", "car", false, "play_fart"),
    ];

    /// <summary>The catalogue entry a command belongs to, or null for one this list doesn't know (never allowed).</summary>
    public static string? IdOf(VehicleCommand c) => c switch
    {
        Cmd.Wake => "wake",
        Cmd.ClimateOn or Cmd.ClimateOff => "climate",
        Cmd.SetDriverTemp => "driver_temp",
        Cmd.SetClimateKeeper => "climate_keeper",
        Cmd.SetDefrost => "defrost",
        Cmd.SetSeatHeater or Cmd.SetAutoSeatClimate => "seat_heater",
        Cmd.SetSteeringWheelHeat or Cmd.SetSteeringWheelHeatLevel or Cmd.SetAutoSteeringWheelHeat => "steering_wheel_heat",
        Cmd.SetCabinOverheatProtection or Cmd.SetCopTemp => "cabin_overheat_protection",
        Cmd.StartCharging or Cmd.StopCharging => "charging",
        Cmd.SetChargeLimit => "charge_limit",
        Cmd.SetChargingAmps => "charging_amps",
        Cmd.CloseChargePort => "charge_port_close",
        Cmd.OpenChargePort => "charge_port_open",
        Cmd.Lock => "lock",
        Cmd.Unlock => "unlock",
        Cmd.OpenFrunk => "frunk",
        Cmd.ToggleTrunk => "trunk",
        Cmd.CloseWindows => "windows_close",
        Cmd.VentWindows => "windows_vent",
        Cmd.SetSentry { On: true } => "sentry_on",
        Cmd.SetSentry => "sentry_off",
        Cmd.SetGuestMode => "guest_mode",
        Cmd.SetValetMode => "valet_mode",
        Cmd.TriggerHomelink => "homelink",
        Cmd.RemoteStartDrive => "keyless_driving",
        Cmd.FlashLights => "flash_lights",
        Cmd.HonkHorn => "honk_horn",
        Cmd.PlayFart => "play_fart",
        _ => null,
    };

    public static bool IsSecurity(string id) => Catalogue.Any(e => e.Id == id && e.Security);

    /// <summary>Whether the owner's policy lets an assistant send this command.</summary>
    public static bool IsAllowed(McpSetting m, string? id) => id is not null && (m.Policy switch
    {
        All => true,
        Safe => !IsSecurity(id),
        Custom => m.AllowedCommands.Contains(id),
        _ => false,   // read_only, or anything unknown
    });

    /// <summary>Keeps only ids that exist, in catalogue order.</summary>
    public static string[] Clean(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        return Catalogue.Where(e => set.Contains(e.Id)).Select(e => e.Id).ToArray();
    }
}
