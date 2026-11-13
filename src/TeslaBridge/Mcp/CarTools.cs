using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TeslaBridge.Commands;
using TeslaBridge.Map;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Settings;
using TeslaBridge.State;
using TeslaBridge.Usage;

namespace TeslaBridge.Mcp;

/// <summary>
/// The tools an AI assistant gets over MCP (/mcp): the car's state, where it is, the month's Fleet API usage, and
/// commands. Which commands are allowed is the owner's choice under Settings → AI assistants (MCP) – by default
/// everything except what opens the car (see McpCommands).
/// </summary>
[McpServerToolType]
public sealed class CarTools(
    IOptions<BridgeOptions> options,
    VehicleStateStore store,
    CommandService commands,
    SettingsStore settings,
    UsageTracker usage,
    ReverseGeocoder geocoder)
{
    private readonly BridgeOptions _opts = options.Value;

    [McpServerTool(Name = "list_cars", Title = "List the cars", ReadOnly = true, OpenWorld = false)]
    [Description("The cars this bridge knows, with VIN, name, whether they are online and when they were last heard from.")]
    public JsonArray ListCars() => new(_opts.Vehicles.Select(v => (JsonNode)new JsonObject
    {
        ["vin"] = v.Vin,
        ["name"] = v.Name,
        ["online"] = store.Get(v.Vin, StateKeys.Online)?.Value?.DeepClone(),
        ["last_seen"] = store.Get(v.Vin, StateKeys.LastSeen)?.Value?.DeepClone(),
    }).ToArray());

    [McpServerTool(Name = "get_car_status", Title = "Car status", ReadOnly = true, OpenWorld = false)]
    [Description("A summary of the car: battery and range, charging, locks, doors, windows, climate and temperatures, " +
                 "sentry, driving, where it is (with the street address when the map is on), navigation, odometer and " +
                 "software. Values are the last known; 'last_seen' says how fresh they are. Units: km, °C, kW, %.")]
    public async Task<JsonObject> GetCarStatus(
        [Description("The car's VIN. Can be left out when the bridge has one car.")] string? vin = null,
        CancellationToken ct = default)
    {
        var v = ResolveVin(vin);
        JsonNode? V(string key) => store.Get(v, key)?.Value?.DeepClone();
        var status = new JsonObject
        {
            ["vin"] = v,
            ["name"] = _opts.Vehicles.First(x => x.Vin.Equals(v, StringComparison.OrdinalIgnoreCase)).Name,
            ["online"] = V(StateKeys.Online),
            ["last_seen"] = V(StateKeys.LastSeen),
            ["battery"] = new JsonObject
            {
                ["level_percent"] = V("battery_level"),
                ["range_km"] = V("est_range"),
                ["charge_limit_percent"] = V("charge_limit"),
            },
            ["charging"] = new JsonObject
            {
                ["state"] = V("charging_state"),
                ["power_kw"] = V("ac_charging_power") ?? V("dc_charging_power"),
                ["hours_to_full"] = V("time_to_full_charge"),
                ["current_request_a"] = V("charge_current_request"),
                ["cable_connected"] = V("charge_cable_connected"),
                ["port_open"] = V("charge_port_open"),
            },
            ["security"] = new JsonObject
            {
                ["locked"] = V("locked"),
                ["sentry_mode"] = V("sentry_mode"),
                ["frunk_open"] = V("frunk_open"),
                ["trunk_open"] = V("trunk_open"),
                ["doors_open"] = new JsonObject
                {
                    ["driver_front"] = V("door_driver_front_open"),
                    ["passenger_front"] = V("door_passenger_front_open"),
                    ["driver_rear"] = V("door_driver_rear_open"),
                    ["passenger_rear"] = V("door_passenger_rear_open"),
                },
                ["windows_open"] = new JsonObject
                {
                    ["driver_front"] = V("window_driver_front_open"),
                    ["passenger_front"] = V("window_passenger_front_open"),
                    ["driver_rear"] = V("window_driver_rear_open"),
                    ["passenger_rear"] = V("window_passenger_rear_open"),
                },
            },
            ["climate"] = new JsonObject
            {
                ["on"] = V("climate_on"),
                ["setting_c"] = V("driver_temp_setting"),
                ["inside_c"] = V("inside_temp"),
                ["outside_c"] = V("outside_temp"),
                ["keeper_mode"] = V("climate_keeper_mode"),
                ["defrost"] = V("defrost"),
                ["seat_heater_left"] = V("seat_heater_left"),
                ["seat_heater_right"] = V("seat_heater_right"),
            },
            ["driving"] = new JsonObject
            {
                ["gear"] = V("gear"),
                ["speed_kmh"] = V("speed"),
                ["heading_deg"] = V("heading"),
                ["destination"] = V("destination"),
                ["minutes_to_arrival"] = V("minutes_to_arrival"),
            },
            ["odometer_km"] = V("odometer"),
            ["software"] = V("software_version"),
        };

        var location = new JsonObject { ["coordinates"] = V("location") };
        if (settings.Current.MapEnabled && store.Get(v, "location")?.Value is JsonObject loc
            && loc["latitude"]?.GetValue<double>() is { } lat && loc["longitude"]?.GetValue<double>() is { } lon)
        {
            try
            {
                if (await geocoder.LookupAsync(lat, lon, "en", ct) is { } a)
                    location["address"] = a.Line2 is null ? a.Line1 : $"{a.Line1}, {a.Line2}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                location["address_error"] = ex.Message;
            }
        }
        status["location"] = location;

        if (store.Get(v, StateKeys.Pending)?.Value is JsonObject pending) status["waiting_for_car"] = pending.DeepClone();
        return status;
    }

    [McpServerTool(Name = "get_car_fields", Title = "All fields", ReadOnly = true, OpenWorld = false)]
    [Description("Every value the bridge knows about the car, with when it was updated and where it came from " +
                 "(stream, snapshot, ble, optimistic …). More detail than get_car_status.")]
    public JsonObject GetCarFields([Description("The car's VIN. Can be left out when the bridge has one car.")] string? vin = null) =>
        FieldJson.Fields(store, ResolveVin(vin));

    [McpServerTool(Name = "get_usage", Title = "Fleet API usage", ReadOnly = true, OpenWorld = false)]
    [Description("This month's Tesla Fleet API usage and cost (signals, commands, wake-ups, data requests), the projection " +
                 "to the end of the month, the free monthly credit, and what Bluetooth saved.")]
    public JsonObject GetUsage() => UsageService.ToJson(usage.Snapshot());

    [McpServerTool(Name = "list_commands", Title = "Commands you may send", ReadOnly = true, OpenWorld = false)]
    [Description("The commands run_command takes – with how to write them and whether the owner allows them over MCP. " +
                 "'security' marks commands that open the car or lower its protection.")]
    public JsonObject ListCommands()
    {
        var m = settings.Current.Mcp;
        return new JsonObject
        {
            ["policy"] = m.Policy,
            ["commands"] = new JsonArray(McpCommands.Catalogue.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id,
                ["usage"] = e.Usage,
                ["security"] = e.Security,
                ["allowed"] = McpCommands.IsAllowed(m, e.Id),
            }).ToArray()),
        };
    }

    [McpServerTool(Name = "run_command", Title = "Send a command to the car", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Sends a command to the car and waits until the car has done it (waking it first if needed, up to about " +
                 "a minute). Call list_commands first: it shows which commands the owner allows and their values, e.g. " +
                 "'climate on', 'driver_temp 21', 'charge_limit 80', 'seat_heater_left high'. Always tell the user what you " +
                 "are about to do first, and don't retry a failed command on your own.")]
    public async Task<JsonObject> RunCommand(
        [Description("The command, e.g. \"climate\".")] string command,
        [Description("Its value, e.g. \"on\" or \"21\". Leave empty for commands without one (wake, flash_lights …).")] string? value = null,
        [Description("The car's VIN. Can be left out when the bridge has one car.")] string? vin = null,
        CancellationToken ct = default)
    {
        var v = ResolveVin(vin);
        var name = command.Trim().ToLowerInvariant();
        var parsed = CommandParser.Parse(name, string.IsNullOrWhiteSpace(value) ? "PRESS" : value, k => store.Get(v, k))
            ?? throw new McpException($"Unknown command or value: {name} = '{value}'. See the tool's description for the list.");
        var id = McpCommands.IdOf(parsed);
        if (!McpCommands.IsAllowed(settings.Current.Mcp, id))
            throw new McpException(
                $"The owner hasn't allowed '{$"{name} {value}".Trim()}' over MCP" +
                (id is not null && McpCommands.IsSecurity(id) ? " – it opens the car or lowers its protection" : "") +
                ". list_commands shows what is allowed. The owner can change it on the bridge's dashboard under " +
                "Settings → AI assistants (MCP), or do it in the Tesla app.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opts.Commands.WakeTimeoutSeconds + 30));
        var result = await commands.ExecuteAsync(v, parsed, timeout.Token);
        return new JsonObject
        {
            ["command"] = parsed.Name,
            ["ok"] = result.IsSuccess,
            ["outcome"] = result.Outcome.ToString(),
            ["reason"] = result.Reason,
        };
    }

    private string ResolveVin(string? vin)
    {
        if (!string.IsNullOrWhiteSpace(vin))
            return _opts.Vehicles.FirstOrDefault(x => x.Vin.Equals(vin.Trim(), StringComparison.OrdinalIgnoreCase))?.Vin
                   ?? throw new McpException($"Unknown VIN '{vin}'. Known: {string.Join(", ", _opts.Vehicles.Select(x => x.Vin))}");
        return _opts.Vehicles.Count switch
        {
            1 => _opts.Vehicles[0].Vin,
            0 => throw new McpException("No car is set up in the bridge (Bridge__Vehicles__0__Vin)."),
            _ => throw new McpException($"There are several cars – say which VIN: {string.Join(", ", _opts.Vehicles.Select(x => $"{x.Vin} ({x.Name})"))}"),
        };
    }
}
