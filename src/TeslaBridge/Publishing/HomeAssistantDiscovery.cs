using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Publishing;

/// <summary>
/// Publishes MQTT discovery, so Home Assistant creates one device per car with all its entities by itself.
/// Sent again when HA says "online" on {discovery}/status. Other clients can ignore this and read
/// {bridge}/{vin}/state/* directly.
/// </summary>
public sealed class HomeAssistantDiscovery(
    MqttConnection mqtt,
    IOptions<BridgeOptions> options,
    ILogger<HomeAssistantDiscovery> log) : IHostedService
{
    private readonly BridgeOptions _opts = options.Value;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_opts.Mqtt.DiscoveryPrefix)) return Task.CompletedTask;
        mqtt.Connected += PublishAllAsync;
        mqtt.Subscribe($"{_opts.Mqtt.DiscoveryPrefix}/status", async (_, payload, ct) =>
        {
            if (payload.Trim().Equals("online", StringComparison.OrdinalIgnoreCase))
                await PublishAllAsync(ct);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        mqtt.Connected -= PublishAllAsync;
        return Task.CompletedTask;
    }

    private async Task PublishAllAsync(CancellationToken ct)
    {
        foreach (var (topic, payload) in BuildUsage(_opts.Mqtt.BridgeTopicBase, _opts.Mqtt.DiscoveryPrefix, mqtt.StatusTopic))
            await mqtt.PublishAsync(topic, payload.ToJsonString(Json.Relaxed), retain: true, ct);

        foreach (var v in _opts.Vehicles)
        {
            var configs = Build(v, _opts.Mqtt.BridgeTopicBase, _opts.Mqtt.DiscoveryPrefix, mqtt.StatusTopic);
            foreach (var topic in LegacyTopics(v, _opts.Mqtt.DiscoveryPrefix, configs))
                await mqtt.PublishAsync(topic, "", retain: true, ct);
            foreach (var (topic, payload) in configs)
                await mqtt.PublishAsync(topic, payload.ToJsonString(Json.Relaxed), retain: true, ct);
            log.LogInformation("Udgav {Count} HA-entiteter for {Name} ({Vin})", configs.Count, v.Name, v.Vin);
        }
    }

    /// <summary>Config topics from earlier versions that are no longer used (emptied, so HA removes the entity).</summary>
    internal static IEnumerable<string> LegacyTopics(VehicleOptions v, string prefix, List<(string Topic, JsonObject Payload)> current)
    {
        var id = $"tesla_bridge_{v.Vin.ToLowerInvariant()}";
        var live = current.Select(c => c.Topic).ToHashSet();
        return Legacy.Select(l => $"{prefix}/{l.Component}/{id}/{l.Key}/config").Where(t => !live.Contains(t));
    }

    // ---------- the same definitions for the HA integration (without MQTT) ----------

    private const string ApiBase = "__bridge__";

    /// <summary>
    /// The entities as JSON for the HA integration, Mfoldager17/ha-tesla-bridge (GET /api/entities). They are the same discovery
    /// configs the MQTT way uses, with the topics swapped for keys: "state:&lt;key&gt;" (a field in /api/state),
    /// "cmd:&lt;name&gt;" (POST /api/vehicles/{vin}/cmd/&lt;name&gt;) and "usage". So the two ways can't drift apart:
    /// names, types, icons and templates are identical.
    /// </summary>
    public static JsonObject ApiDefinitions(BridgeOptions o)
    {
        var vehicles = new JsonArray();
        foreach (var v in o.Vehicles)
        {
            var configs = Build(v, ApiBase, "homeassistant", "availability");
            vehicles.Add(new JsonObject
            {
                ["vin"] = v.Vin,
                ["name"] = v.Name,
                ["slug"] = Slug(v.Name),
                ["entities"] = new JsonArray(configs.Select(c => (JsonNode?)ToApi(c.Topic, c.Payload, v.Vin)).ToArray()),
            });
        }
        var usage = BuildUsage(ApiBase, "homeassistant", "availability");
        return new JsonObject
        {
            ["version"] = 1,
            ["vehicles"] = vehicles,
            ["bridge"] = new JsonObject
            {
                ["entities"] = new JsonArray(usage.Select(c => (JsonNode?)ToApi(c.Topic, c.Payload, null)).ToArray()),
            },
        };
    }

    internal static JsonObject ToApi(string topic, JsonObject payload, string? vin)
    {
        var parts = topic.Split('/');        // {prefix}/{component}/{id}/{key}/config
        var cfg = new JsonObject();
        foreach (var (name, node) in payload)
        {
            if (name is "device" or "availability_topic") continue;
            if (name.EndsWith("_topic") && node is JsonValue tv && tv.TryGetValue(out string? t))
            {
                cfg[name] = t switch
                {
                    $"{ApiBase}/bridge/usage" => "usage",
                    $"{ApiBase}/bridge/update" => "update",
                    _ when vin is not null && t == $"{ApiBase}/{vin}/command/status" => "state:command_status",
                    _ when vin is not null && t.StartsWith($"{ApiBase}/{vin}/state/") => "state:" + t[$"{ApiBase}/{vin}/state/".Length..],
                    _ when vin is not null && t.StartsWith($"{ApiBase}/{vin}/cmd/") => "cmd:" + t[$"{ApiBase}/{vin}/cmd/".Length..],
                    _ => t,
                };
                continue;
            }
            cfg[name] = node?.DeepClone();
        }
        return new JsonObject { ["platform"] = parts[1], ["key"] = parts[3], ["config"] = cfg };
    }

    // ---------- entitetsdefinitioner ----------

    private const string Num = "{{ value_json }}";
    private const string BoolOnOff = "{% if value_json is none %}None{% elif value_json %}ON{% else %}OFF{% endif %}";

    private sealed record Sensor(string Key, string Name, string? Unit = null, string? DeviceClass = null,
        string? StateClass = null, string? Icon = null, string? Category = null, bool Enabled = true);

    private sealed record Binary(string Key, string Name, string? DeviceClass = null, string? Icon = null, string? Category = null);

    // Names and types follow the Teslemetry integration, so dashboards can be moved over 1:1
    // (sensor.tesla_battery_level → sensor.model_y_battery_level).
    private static readonly Sensor[] Sensors =
    [
        new("battery_level", "Battery level", "%", "battery", "measurement"),
        new("charging", "Charging", Icon: "mdi:ev-station"),
        new("charge_energy_added", "Charge energy added", "kWh", "energy", "total_increasing"),
        new("charger_power", "Charger power", "kW", "power", "measurement"),
        new("charger_voltage", "Charger voltage", "V", "voltage", "measurement"),
        new("charger_current", "Charger current", "A", "current", "measurement"),
        new("dc_charging_power", "DC charging power", "kW", "power", "measurement"),
        new("dc_charging_energy_in", "DC charging energy in", "kWh", "energy", "total_increasing", Enabled: false),
        new("time_to_full_charge", "Time to full charge", "h", "duration", null),
        new("energy_remaining", "Energy remaining", "kWh", "energy_storage", "measurement"),
        new("estimate_battery_range", "Estimate battery range", "km", "distance", "measurement"),
        new("ideal_battery_range", "Ideal battery range", "km", "distance", "measurement"),
        new("rated_range", "Rated range", "km", "distance", "measurement", Enabled: false),
        new("speed", "Speed", "km/h", "speed", "measurement"),
        new("shift_state", "Shift state", Icon: "mdi:car-shift-pattern"),
        new("odometer", "Odometer", "km", "distance", "total_increasing"),
        new("gps_heading", "GPS heading", "°", null, "measurement", "mdi:compass", Enabled: false),
        new("tire_pressure_front_left", "Tire pressure front left", "bar", "pressure", "measurement"),
        new("tire_pressure_front_right", "Tire pressure front right", "bar", "pressure", "measurement"),
        new("tire_pressure_rear_left", "Tire pressure rear left", "bar", "pressure", "measurement"),
        new("tire_pressure_rear_right", "Tire pressure rear right", "bar", "pressure", "measurement"),
        new("inside_temperature", "Inside temperature", "°C", "temperature", "measurement"),
        new("outside_temperature", "Outside temperature", "°C", "temperature", "measurement"),
        new("left_temperature_request", "Left temperature request", "°C", "temperature", null, Enabled: false),
        new("right_temperature_request", "Right temperature request", "°C", "temperature", null, Enabled: false),
        new("hvac_power_state", "HVAC power state", Icon: "mdi:fan", Category: "diagnostic"),
        new("sentry_mode", "Sentry mode", Icon: "mdi:cctv", Category: "diagnostic"),
        new("destination", "Destination", Icon: "mdi:map-marker"),
        new("time_to_arrival", "Time to arrival", null, "timestamp", null),   // a point in time, like Teslemetry
        new("distance_to_arrival", "Distance to arrival", "km", "distance", null),
        new("state_of_charge_at_arrival", "State of charge at arrival", "%", "battery", null),
        new("version", "Version", Icon: "mdi:update", Category: "diagnostic"),
    ];

    /// <summary>Teslemetry key → the bridge's state key, where they differ.</summary>
    private static readonly Dictionary<string, string> StateKeyOf = new()
    {
        ["charging"] = "charging_state",
        ["charge_energy_added"] = "ac_charging_energy_in",
        ["charger_power"] = "ac_charging_power",
        ["estimate_battery_range"] = "est_range",
        ["ideal_battery_range"] = "ideal_range",
        ["shift_state"] = "gear",
        ["gps_heading"] = "heading",
        ["inside_temperature"] = "inside_temp",
        ["outside_temperature"] = "outside_temp",
        ["left_temperature_request"] = "driver_temp_setting",
        ["right_temperature_request"] = "passenger_temp_setting",
        ["hvac_power_state"] = "hvac_state",
        ["sentry_mode"] = "sentry_state",
        ["time_to_arrival"] = "minutes_to_arrival",
        ["distance_to_arrival"] = "km_to_arrival",
        ["state_of_charge_at_arrival"] = "soc_at_arrival",
        ["version"] = "software_version",
        ["status"] = "online",
        ["charge_cable"] = "charge_cable_connected",
        ["front_driver_door"] = "door_driver_front_open",
        ["front_passenger_door"] = "door_passenger_front_open",
        ["rear_driver_door"] = "door_driver_rear_open",
        ["rear_passenger_door"] = "door_passenger_rear_open",
        ["front_driver_window"] = "window_driver_front_open",
        ["front_passenger_window"] = "window_passenger_front_open",
        ["rear_driver_window"] = "window_driver_rear_open",
        ["rear_passenger_window"] = "window_passenger_rear_open",
        ["driver_seat_occupied"] = "user_present",
        ["bluetooth"] = "ble_connected",
    };

    private static string StateKey(string key) => StateKeyOf.TryGetValue(key, out var k) ? k : key;

    private static readonly Binary[] Binaries =
    [
        new("status", "Status", "connectivity", Category: "diagnostic"),
        new("charge_cable", "Charge cable", "connectivity", "mdi:ev-plug-type2"),
        new("front_driver_door", "Front driver door", "door"),
        new("front_passenger_door", "Front passenger door", "door"),
        new("rear_driver_door", "Rear driver door", "door"),
        new("rear_passenger_door", "Rear passenger door", "door"),
        new("front_driver_window", "Front driver window", "window"),
        new("front_passenger_window", "Front passenger window", "window"),
        new("rear_driver_window", "Rear driver window", "window"),
        new("rear_passenger_window", "Rear passenger window", "window"),
        new("driver_seat_occupied", "Driver seat occupied", "occupancy"),
        new("driver_seat_belt", "Driver seat belt", null, "mdi:seatbelt", "diagnostic"),
        new("preconditioning_enabled", "Preconditioning enabled", null, "mdi:heat-wave", "diagnostic"),
        new("scheduled_charging_pending", "Scheduled charging pending", null, "mdi:calendar-clock", "diagnostic"),
        new("fast_charger_present", "Fast charger present", "plug", "mdi:ev-station", "diagnostic"),
        // Only has a value with an ESP32 (Bluetooth): on while the car is within range and commands go that way.
        new("bluetooth", "Bluetooth", "connectivity", "mdi:bluetooth", "diagnostic"),
    ];

    /// <summary>
    /// Entities from earlier versions of the bridge. Their discovery config is emptied, so HA removes them
    /// (the new ones have Teslemetry's names). Only keys that no longer exist are cleared.
    /// </summary>
    private static readonly (string Component, string Key)[] Legacy =
    [
        ("sensor", "est_range"), ("sensor", "ideal_range"), ("sensor", "inside_temp"), ("sensor", "outside_temp"),
        ("sensor", "driver_temp_setting"), ("sensor", "heading"), ("sensor", "gear"), ("sensor", "charging_state"),
        ("sensor", "charge_limit"), ("sensor", "ac_charging_power"), ("sensor", "ac_charging_energy_in"),
        ("sensor", "minutes_to_arrival"), ("sensor", "km_to_arrival"), ("sensor", "software_version"),
        ("sensor", "hvac_state"), ("sensor", "sentry_state"),
        ("binary_sensor", "online"), ("binary_sensor", "charge_cable_connected"), ("binary_sensor", "charge_port_open"),
        ("binary_sensor", "user_present"), ("binary_sensor", "door_driver_front_open"), ("binary_sensor", "door_passenger_front_open"),
        ("binary_sensor", "door_driver_rear_open"), ("binary_sensor", "door_passenger_rear_open"), ("binary_sensor", "frunk_open"),
        ("binary_sensor", "trunk_open"), ("binary_sensor", "window_driver_front_open"), ("binary_sensor", "window_passenger_front_open"),
        ("binary_sensor", "window_driver_rear_open"), ("binary_sensor", "window_passenger_rear_open"),
        ("switch", "climate"), ("switch", "steering_wheel_heat"), ("switch", "sentry"), ("switch", "charging"),
        ("number", "charge_limit_set"), ("number", "charging_amps_set"), ("number", "driver_temp_set"),
        ("select", "cabin_overheat_protection_set"), ("select", "seat_heater_left_set"), ("select", "seat_heater_right_set"),
        ("select", "seat_heater_rear_left_set"), ("select", "seat_heater_rear_center_set"), ("select", "seat_heater_rear_right_set"),
        ("button", "open_frunk"), ("button", "toggle_trunk"), ("button", "vent_windows"), ("button", "close_windows"),
        ("button", "open_charge_port"), ("button", "close_charge_port"),
    ];

    /// <summary>"Model Y" → "model_y". Only a-z, 0-9 and _ (valid in an entity id).</summary>
    internal static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c == 'æ') sb.Append("ae");
            else if (c == 'ø') sb.Append("oe");
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var slug = sb.ToString().Trim('_');
        return slug.Length == 0 ? "tesla" : slug;
    }

    /// <summary>One device, "Tesla Bridge", with the month's Fleet API usage (from {bridge}/bridge/usage).</summary>
    internal static List<(string Topic, JsonObject Payload)> BuildUsage(string bridgeBase, string prefix, string availabilityTopic)
    {
        const string id = "tesla_bridge";
        var topic = $"{bridgeBase}/bridge/usage";
        var list = new List<(string, JsonObject)>();

        void Add(string key, string name, string template, string? unit, string? deviceClass, string? stateClass, string icon, bool attributes = false)
        {
            var c = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = $"{id}_{key}",
                ["default_entity_id"] = $"sensor.{id}_{key}",
                ["state_topic"] = topic,
                ["value_template"] = template,
                ["availability_topic"] = availabilityTopic,
                ["icon"] = icon,
                ["device"] = BridgeDevice(),
            };
            if (unit is not null) c["unit_of_measurement"] = unit;
            if (deviceClass is not null) c["device_class"] = deviceClass;
            if (stateClass is not null) c["state_class"] = stateClass;
            if (attributes) c["json_attributes_topic"] = topic;
            // The counter starts over on the 1st; last_reset tells HA's statistics.
            if (stateClass == "total") c["last_reset_value_template"] = "{{ value_json.month_start }}";
            list.Add(($"{prefix}/sensor/{id}/{key}/config", c));
        }

        Add("cost_month", "Cost this month", "{{ value_json.total_usd }}", "USD", "monetary", "total", "mdi:currency-usd", attributes: true);
        Add("cost_projected", "Projected cost this month", "{{ value_json.projected_usd if value_json.projected_usd is not none else None }}", "USD", "monetary", null, "mdi:chart-line");
        Add("credit_left", "Free credit left", "{{ [value_json.credit_usd - value_json.total_usd, 0] | max | round(2) }}", "USD", "monetary", null, "mdi:gift-outline");
        Add("signals_month", "Signals this month", "{{ value_json.categories.signals.count }}", null, null, "total_increasing", "mdi:access-point");
        Add("commands_month", "Commands this month", "{{ value_json.categories.commands.count }}", null, null, "total_increasing", "mdi:console");
        Add("wakes_month", "Wake-ups this month", "{{ value_json.categories.wakes.count }}", null, null, "total_increasing", "mdi:sleep-off");
        Add("data_month", "Data requests this month", "{{ value_json.categories.data.count }}", null, null, "total_increasing", "mdi:database-search");

        // New releases show up under Settings → Updates. No install button: the bridge runs in Docker,
        // so updating stays `docker compose pull && docker compose up -d` (the release notes say so).
        list.Add(($"{prefix}/update/{id}/update/config", new JsonObject
        {
            ["name"] = null,   // = the device's name, "Tesla Bridge"
            ["unique_id"] = $"{id}_update",
            ["default_entity_id"] = $"update.{id}",
            ["state_topic"] = $"{bridgeBase}/bridge/update",
            ["availability_topic"] = availabilityTopic,
            ["entity_category"] = "diagnostic",
            ["device_class"] = "firmware",
            ["device"] = BridgeDevice(),
        }));
        return list;

        JsonObject BridgeDevice() => new()
        {
            ["identifiers"] = new JsonArray(id),
            ["name"] = "Tesla Bridge",
            ["manufacturer"] = "Tesla Bridge",
            ["model"] = "Fleet API-forbrug",
            ["sw_version"] = TeslaBridge.Versioning.BuildInfo.Display,
        };
    }

    internal static List<(string Topic, JsonObject Payload)> Build(VehicleOptions v, string bridgeBase, string prefix, string availabilityTopic)
    {
        var vin = v.Vin;
        var id = $"tesla_bridge_{vin.ToLowerInvariant()}";
        var state = (string key) => $"{bridgeBase}/{vin}/state/{key}";
        var cmd = (string name) => $"{bridgeBase}/{vin}/cmd/{name}";
        var list = new List<(string, JsonObject)>();

        JsonObject Base(string key, string name)
        {
            var o = new JsonObject
            {
                ["name"] = name,
                ["unique_id"] = $"{id}_{key}",
                ["availability_topic"] = availabilityTopic,
                ["device"] = new JsonObject
                {
                    ["identifiers"] = new JsonArray(id),
                    ["name"] = v.Name,
                    ["manufacturer"] = "Tesla",
                    ["model"] = "Tesla Bridge",
                    ["serial_number"] = vin,
                    ["sw_version"] = TeslaBridge.Versioning.BuildInfo.Display,
                },
            };
            return o;
        }

        // default_entity_id (HA 2025.10+; object_id stopped working in 2026.4): e.g. "switch.model_y_climate".
        void Add(string component, string key, JsonObject cfg)
        {
            cfg["default_entity_id"] = $"{component}.{Slug(v.Name)}_{key}";
            list.Add(($"{prefix}/{component}/{id}/{key}/config", cfg));
        }

        foreach (var s in Sensors)
        {
            var c = Base(s.Key, s.Name);
            c["state_topic"] = state(StateKey(s.Key));
            c["value_template"] = s.Key == "time_to_arrival"
                ? "{{ (now() + timedelta(minutes=value_json | float)).isoformat() if value_json is not none else None }}"
                : Num;
            if (s.Unit is not null) c["unit_of_measurement"] = s.Unit;
            if (s.DeviceClass is not null) c["device_class"] = s.DeviceClass;
            if (s.StateClass is not null) c["state_class"] = s.StateClass;
            if (s.Icon is not null) c["icon"] = s.Icon;
            if (s.Category is not null) c["entity_category"] = s.Category;
            if (!s.Enabled) c["enabled_by_default"] = false;
            if (s.Key == "sentry_mode") c["unique_id"] = $"{id}_sentry_state";   // the switch is called sentry_mode too
            Add("sensor", s.Key, c);
        }

        {
            var c = Base("last_seen", "Last seen");
            c["state_topic"] = state("last_seen");
            c["value_template"] = Num;
            c["device_class"] = "timestamp";
            c["entity_category"] = "diagnostic";
            Add("sensor", "last_seen", c);
        }

        {
            var c = Base("command_status", "Command status");
            c["state_topic"] = $"{bridgeBase}/{vin}/command/status";
            c["value_template"] = "{{ value_json.status }}";
            c["json_attributes_topic"] = $"{bridgeBase}/{vin}/command/status";
            c["icon"] = "mdi:console";
            c["entity_category"] = "diagnostic";
            Add("sensor", "command_status", c);
        }

        foreach (var b in Binaries)
        {
            var c = Base(b.Key, b.Name);
            c["state_topic"] = state(StateKey(b.Key));
            c["value_template"] = BoolOnOff;
            if (b.DeviceClass is not null) c["device_class"] = b.DeviceClass;
            if (b.Icon is not null) c["icon"] = b.Icon;
            if (b.Category is not null) c["entity_category"] = b.Category;
            Add("binary_sensor", b.Key, c);
        }

        // ---------- Controls (the same as Teslemetry) ----------

        // While a command waits for the car (being woken, or not confirmed yet), the entity gets the attributes
        // pending: true and waiting_for: "car to wake up" / "car to confirm" – HA has no "loading" state for
        // switches, numbers, selects or climate, so this is how dashboards can show it.
        void Waiting(JsonObject c, params string[] keys)
        {
            var list = string.Join(", ", keys.Select(k => $"'{k}'"));
            c["json_attributes_topic"] = state(TeslaBridge.State.PendingTracker.Key);
            c["json_attributes_template"] =
                "{% set p = value_json if value_json is mapping else {} %}" +
                "{% set on = (p.get('keys') or []) | select('in', [" + list + "]) | list | count > 0 %}" +
                "{{ {'pending': on, 'waiting_for': (('car to wake up' if p.get('waking') else 'car to confirm') if on else none)} | tojson }}";
        }

        void Switch(string key, string name, string stateKey, string icon, string? template = null)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = template ?? BoolOnOff;
            c["command_topic"] = cmd(key);
            c["payload_on"] = "on";
            c["payload_off"] = "off";
            c["state_on"] = "ON";
            c["state_off"] = "OFF";
            c["icon"] = icon;
            Waiting(c, stateKey);
            Add("switch", key, c);
        }

        Switch("auto_seat_climate_left", "Auto seat climate left", "auto_seat_climate_left", "mdi:car-seat-heater");
        Switch("auto_seat_climate_right", "Auto seat climate right", "auto_seat_climate_right", "mdi:car-seat-heater");
        Switch("auto_steering_wheel_heater", "Auto steering wheel heater", "auto_steering_wheel_heat", "mdi:steering");
        Switch("charge", "Charge", "charging_state", "mdi:ev-station",
            "{{ 'ON' if value_json in ['charging', 'starting'] else 'OFF' }}");
        Switch("defrost", "Defrost", "defrost", "mdi:snowflake-melt");
        Switch("guest_mode", "Guest mode", "guest_mode", "mdi:account-group");
        Switch("sentry_mode", "Sentry mode", "sentry_mode", "mdi:shield-car");
        Switch("valet_mode", "Valet mode", "valet_mode", "mdi:speedometer-slow");

        void Lock(string key, string name, string stateKey, string? icon)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey + "_motion");          // LOCKED/UNLOCKED/LOCKING/UNLOCKING
            c["value_template"] = "{{ value_json if value_json is not none else None }}";
            c["command_topic"] = cmd(key);
            c["payload_lock"] = "LOCK";
            c["payload_unlock"] = "UNLOCK";
            if (icon is not null) c["icon"] = icon;
            Add("lock", key, c);
        }

        Lock("lock", "Lock", "locked", null);
        Lock("charge_cable_lock", "Charge cable lock", "charge_cable_locked", "mdi:ev-plug-type2");

        void Number(string key, string name, string stateKey, double min, double max, string unit, string icon)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = Num;
            c["command_topic"] = cmd(key);
            c["min"] = min;
            c["max"] = max;
            c["step"] = 1;
            c["unit_of_measurement"] = unit;
            c["mode"] = "slider";
            c["icon"] = icon;
            Waiting(c, stateKey);
            Add("number", key, c);
        }

        Number("charge_current", "Charge current", "charge_current_request", 5, 32, "A", "mdi:current-ac");
        Number("charge_limit", "Charge limit", "charge_limit", 50, 100, "%", "mdi:battery-charging-high");

        // cover: state_topic gives true/false → open/closed
        void Cover(string key, string name, string stateKey, string deviceClass, bool canClose, string? icon = null)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey + "_motion");          // open/closed/opening/closing
            c["value_template"] = "{{ value_json if value_json is not none else None }}";
            c["state_open"] = "open";
            c["state_closed"] = "closed";
            c["state_opening"] = "opening";
            c["state_closing"] = "closing";
            c["command_topic"] = cmd(key);
            c["payload_open"] = "OPEN";
            c["payload_close"] = canClose ? "CLOSE" : null;
            c["payload_stop"] = null;
            c["device_class"] = deviceClass;
            if (icon is not null) c["icon"] = icon;
            Add("cover", key, c);
        }

        Cover("charge_port_door", "Charge port door", "charge_port_open", "door", canClose: true);
        Cover("frunk", "Frunk", "frunk_open", "door", canClose: false);
        Cover("trunk", "Trunk", "trunk_open", "door", canClose: true);
        Cover("windows", "Windows", "windows_open", "window", canClose: true);

        // The climate card: on/off, temperature, cabin temperature and climate modes (Normal/Keep/Dog/Camp).
        {
            var c = Base("climate", "Climate");
            c["unique_id"] = $"{id}_climate_entity";
            c["modes"] = new JsonArray("heat_cool", "off");
            c["mode_state_topic"] = state("climate_on");
            c["mode_state_template"] = "{{ 'heat_cool' if value_json else 'off' }}";
            c["mode_command_topic"] = cmd("climate");
            c["mode_command_template"] = "{{ 'off' if value == 'off' else 'on' }}";
            c["temperature_state_topic"] = state("driver_temp_setting");
            c["temperature_state_template"] = Num;
            c["temperature_command_topic"] = cmd("driver_temp");
            c["current_temperature_topic"] = state("inside_temp");
            c["current_temperature_template"] = Num;
            c["min_temp"] = 15;
            c["max_temp"] = 28;
            c["temp_step"] = 0.5;
            c["precision"] = 0.5;
            c["temperature_unit"] = "C";
            c["preset_modes"] = new JsonArray(CommandParser.KeeperPresets.Select(k => (JsonNode?)JsonValue.Create(k.Preset)).ToArray());
            c["preset_mode_state_topic"] = state("climate_keeper_mode");
            c["preset_mode_value_template"] =
                "{% set m = {" + string.Join(", ", CommandParser.KeeperPresets.Select(k => $"'{k.Key}': '{k.Preset}'")) + "} %}{{ m.get(value_json, 'none') }}";
            c["preset_mode_command_topic"] = cmd("climate_keeper");
            Waiting(c, "climate_on", "driver_temp_setting", "climate_keeper_mode");
            Add("climate", "climate", c);
        }

        // Cabin overheat protection as a climate card: off / cool / fan only + limit 30/35/40 °C.
        {
            var c = Base("cabin_overheat_protection", "Cabin overheat protection");
            c["modes"] = new JsonArray("off", "cool", "fan_only");
            c["mode_state_topic"] = state("cabin_overheat_protection");
            c["mode_state_template"] = "{{ {'on': 'cool', 'fan_only': 'fan_only'}.get(value_json, 'off') }}";
            c["mode_command_topic"] = cmd("cabin_overheat_protection");
            c["mode_command_template"] = "{{ {'cool': 'on', 'fan_only': 'fan_only'}.get(value, 'off') }}";
            c["temperature_state_topic"] = state("cop_temp_limit");
            c["temperature_state_template"] = Num;
            c["temperature_command_topic"] = cmd("cop_temp");
            c["current_temperature_topic"] = state("inside_temp");
            c["current_temperature_template"] = Num;
            c["min_temp"] = 30;
            c["max_temp"] = 40;
            c["temp_step"] = 5;
            c["precision"] = 1.0;
            c["temperature_unit"] = "C";
            c["icon"] = "mdi:sun-thermometer";
            Waiting(c, "cabin_overheat_protection", "cop_temp_limit");
            Add("climate", "cabin_overheat_protection", c);
        }

        // Seat and steering wheel heat
        void Select(string key, string name, string stateKey, string[] options, string template, string icon)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = template;
            c["command_topic"] = cmd(key);
            c["options"] = new JsonArray(options.Select(o => (JsonNode?)JsonValue.Create(o)).ToArray());
            c["icon"] = icon;
            Waiting(c, stateKey);
            Add("select", key, c);
        }

        const string SeatTemplate = "{% set l = ['off','low','medium','high'] %}{{ l[value_json | int(0)] if value_json is not none else None }}";
        foreach (var (key, name, stateKey) in new[]
                 {
                     ("seat_heater_front_left", "Seat heater front left", "seat_heater_left"),
                     ("seat_heater_front_right", "Seat heater front right", "seat_heater_right"),
                     ("seat_heater_rear_left", "Seat heater rear left", "seat_heater_rear_left"),
                     ("seat_heater_rear_center", "Seat heater rear center", "seat_heater_rear_center"),
                     ("seat_heater_rear_right", "Seat heater rear right", "seat_heater_rear_right"),
                 })
            Select(key, name, stateKey, CommandParser.SeatLevels, SeatTemplate, "mdi:car-seat-heater");

        Select("steering_wheel_heater", "Steering wheel heater", "steering_wheel_heat_level", ["off", "low", "high"],
            "{% if value_json is none %}None{% elif value_json | int == 0 %}off{% elif value_json | int >= 2 %}high{% else %}low{% endif %}",
            "mdi:steering");

        void Button(string key, string name, string icon)
        {
            var c = Base(key, name);
            c["command_topic"] = cmd(key);
            c["payload_press"] = "PRESS";
            c["icon"] = icon;
            Add("button", key, c);
        }

        Button("wake", "Wake", "mdi:sleep-off");
        Button("flash_lights", "Flash lights", "mdi:flashlight");
        Button("honk_horn", "Honk horn", "mdi:bullhorn");
        Button("keyless_driving", "Keyless driving", "mdi:car-key");
        Button("play_fart", "Play fart", "mdi:volume-high");
        Button("homelink", "Homelink", "mdi:garage");

        void Tracker(string key, string name, string stateKey, string icon)
        {
            var c = Base(key, name);
            c["json_attributes_topic"] = state(stateKey);
            c["json_attributes_template"] = "{{ (value_json if value_json is mapping else {}) | tojson }}";
            c["source_type"] = "gps";
            c["icon"] = icon;
            Add("device_tracker", key, c);
        }

        Tracker("location", "Location", "location", "mdi:car");
        Tracker("route", "Route", "destination_location", "mdi:routes");

        return list;
    }
}
