using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Commands;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Publishing;

/// <summary>
/// Udgiver MQTT discovery, så Home Assistant selv opretter én enhed pr. bil med alle entiteter.
/// Genudsendes når HA melder "online" på {discovery}/status. Andre klienter kan ignorere dette
/// og læse {bridge}/{vin}/state/* direkte.
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
            foreach (var (topic, payload) in configs)
                await mqtt.PublishAsync(topic, payload.ToJsonString(Json.Relaxed), retain: true, ct);
            log.LogInformation("Udgav {Count} HA-entiteter for {Name} ({Vin})", configs.Count, v.Name, v.Vin);
        }
    }

    // ---------- entitetsdefinitioner ----------

    private const string Num = "{{ value_json }}";
    private const string BoolOnOff = "{% if value_json is none %}None{% elif value_json %}ON{% else %}OFF{% endif %}";

    private sealed record Sensor(string Key, string Name, string? Unit = null, string? DeviceClass = null,
        string? StateClass = null, string? Icon = null, string? Category = null, bool Enabled = true);

    private sealed record Binary(string Key, string Name, string? DeviceClass = null, string? Icon = null, string? Category = null);

    private static readonly Sensor[] Sensors =
    [
        new("battery_level", "Batteri", "%", "battery", "measurement"),
        new("est_range", "Rækkevidde", "km", "distance", "measurement", "mdi:map-marker-distance"),
        new("ideal_range", "Ideel rækkevidde", "km", "distance", "measurement", "mdi:map-marker-distance", Enabled: false),
        new("inside_temp", "Indetemperatur", "°C", "temperature", "measurement"),
        new("outside_temp", "Udetemperatur", "°C", "temperature", "measurement"),
        new("driver_temp_setting", "Indstillet temperatur", "°C", "temperature", null, Category: "diagnostic"),
        new("odometer", "Kilometertæller", "km", "distance", "total_increasing", "mdi:counter"),
        new("speed", "Hastighed", "km/h", "speed", "measurement"),
        new("heading", "Kurs", "°", null, "measurement", "mdi:compass", Enabled: false),
        new("gear", "Gear", Icon: "mdi:car-shift-pattern"),
        new("charging_state", "Ladestatus", Icon: "mdi:ev-station"),
        new("charge_limit", "Ladegrænse", "%", null, null, "mdi:battery-charging-high", "diagnostic"),
        new("ac_charging_power", "Ladeeffekt AC", "kW", "power", "measurement"),
        new("dc_charging_power", "Ladeeffekt DC", "kW", "power", "measurement"),
        new("charger_current", "Ladestrøm", "A", "current", "measurement"),
        new("charger_voltage", "Ladespænding", "V", "voltage", "measurement"),
        new("ac_charging_energy_in", "Energi tilført (AC)", "kWh", "energy", null),
        new("dc_charging_energy_in", "Energi tilført (DC)", "kWh", "energy", null, Enabled: false),
        new("time_to_full_charge", "Tid til fuld", "h", "duration", null),
        new("destination", "Destination", Icon: "mdi:map-marker"),
        new("minutes_to_arrival", "Tid til ankomst", "min", "duration", null),
        new("km_to_arrival", "Afstand til ankomst", "km", "distance", null),
        new("software_version", "Softwareversion", Icon: "mdi:update", Category: "diagnostic"),
        new("hvac_state", "Klimastatus", Icon: "mdi:fan", Category: "diagnostic"),
        new("sentry_state", "Sentry-status", Icon: "mdi:cctv", Category: "diagnostic"),
    ];

    private static readonly Binary[] Binaries =
    [
        new("online", "Online", "connectivity", Category: "diagnostic"),
        new("charge_cable_connected", "Ladekabel", "plug"),
        new("charge_port_open", "Ladeport", "opening"),
        new("user_present", "Person i bilen", "occupancy"),
        new("door_driver_front_open", "Fordør fører", "door"),
        new("door_passenger_front_open", "Fordør passager", "door"),
        new("door_driver_rear_open", "Bagdør fører", "door"),
        new("door_passenger_rear_open", "Bagdør passager", "door"),
        new("frunk_open", "Frunk", "opening", "mdi:car-select"),
        new("trunk_open", "Bagagerum", "opening", "mdi:car-back"),
        new("window_driver_front_open", "Rude fører for", "window"),
        new("window_passenger_front_open", "Rude passager for", "window"),
        new("window_driver_rear_open", "Rude fører bag", "window"),
        new("window_passenger_rear_open", "Rude passager bag", "window"),
    ];

    /// <summary>"Model Y" → "model_y". Kun a-z, 0-9 og _ (gyldigt i et entity id).</summary>
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

    /// <summary>Én enhed "Tesla Bridge" med Fleet API-forbruget for måneden (fra {bridge}/bridge/usage).</summary>
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
                ["device"] = new JsonObject
                {
                    ["identifiers"] = new JsonArray(id),
                    ["name"] = "Tesla Bridge",
                    ["manufacturer"] = "Tesla Bridge",
                    ["model"] = "Fleet API-forbrug",
                },
            };
            if (unit is not null) c["unit_of_measurement"] = unit;
            if (deviceClass is not null) c["device_class"] = deviceClass;
            if (stateClass is not null) c["state_class"] = stateClass;
            if (attributes) c["json_attributes_topic"] = topic;
            // Tælleren nulstilles den 1.; last_reset fortæller HA's statistik det.
            if (stateClass == "total") c["last_reset_value_template"] = "{{ value_json.month_start }}";
            list.Add(($"{prefix}/sensor/{id}/{key}/config", c));
        }

        Add("cost_month", "Forbrug denne måned", "{{ value_json.total_usd }}", "USD", "monetary", "total", "mdi:currency-usd", attributes: true);
        Add("cost_projected", "Forventet forbrug for måneden", "{{ value_json.projected_usd if value_json.projected_usd is not none else None }}", "USD", "monetary", null, "mdi:chart-line");
        Add("credit_left", "Tilbage af gratis rabat", "{{ [value_json.credit_usd - value_json.total_usd, 0] | max | round(2) }}", "USD", "monetary", null, "mdi:gift-outline");
        Add("signals_month", "Signaler denne måned", "{{ value_json.categories.signals.count }}", null, null, "total_increasing", "mdi:access-point");
        Add("commands_month", "Kommandoer denne måned", "{{ value_json.categories.commands.count }}", null, null, "total_increasing", "mdi:console");
        Add("wakes_month", "Wakes denne måned", "{{ value_json.categories.wakes.count }}", null, null, "total_increasing", "mdi:sleep-off");
        Add("data_month", "Data-forespørgsler denne måned", "{{ value_json.categories.data.count }}", null, null, "total_increasing", "mdi:database-search");
        return list;
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
                },
            };
            return o;
        }

        // default_entity_id (HA 2025.10+; object_id virker ikke fra 2026.4): fx "switch.model_y_climate".
        void Add(string component, string key, JsonObject cfg)
        {
            cfg["default_entity_id"] = $"{component}.{Slug(v.Name)}_{key}";
            list.Add(($"{prefix}/{component}/{id}/{key}/config", cfg));
        }

        foreach (var s in Sensors)
        {
            var c = Base(s.Key, s.Name);
            c["state_topic"] = state(s.Key);
            c["value_template"] = Num;
            if (s.Unit is not null) c["unit_of_measurement"] = s.Unit;
            if (s.DeviceClass is not null) c["device_class"] = s.DeviceClass;
            if (s.StateClass is not null) c["state_class"] = s.StateClass;
            if (s.Icon is not null) c["icon"] = s.Icon;
            if (s.Category is not null) c["entity_category"] = s.Category;
            if (!s.Enabled) c["enabled_by_default"] = false;
            Add("sensor", s.Key, c);
        }

        {
            var c = Base("last_seen", "Sidst set");
            c["state_topic"] = state("last_seen");
            c["value_template"] = Num;
            c["device_class"] = "timestamp";
            c["entity_category"] = "diagnostic";
            Add("sensor", "last_seen", c);
        }

        {
            var c = Base("command_status", "Kommandostatus");
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
            c["state_topic"] = state(b.Key);
            c["value_template"] = BoolOnOff;
            if (b.DeviceClass is not null) c["device_class"] = b.DeviceClass;
            if (b.Icon is not null) c["icon"] = b.Icon;
            if (b.Category is not null) c["entity_category"] = b.Category;
            Add("binary_sensor", b.Key, c);
        }

        // Lås
        {
            var c = Base("lock", "Lås");
            c["state_topic"] = state("locked");
            c["value_template"] = "{% if value_json is none %}None{% elif value_json %}LOCKED{% else %}UNLOCKED{% endif %}";
            c["command_topic"] = cmd("lock");
            c["payload_lock"] = "LOCK";
            c["payload_unlock"] = "UNLOCK";
            Add("lock", "lock", c);
        }

        // Kontakter
        void Switch(string key, string name, string stateKey, string cmdName, string icon, string? template = null)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = template ?? BoolOnOff;
            c["command_topic"] = cmd(cmdName);
            c["payload_on"] = "on";
            c["payload_off"] = "off";
            c["state_on"] = "ON";
            c["state_off"] = "OFF";
            c["icon"] = icon;
            Add("switch", key, c);
        }

        Switch("climate", "Klima", "climate_on", "climate", "mdi:fan");
        Switch("defrost", "Afrimning", "defrost", "defrost", "mdi:car-defrost-front");
        Switch("steering_wheel_heat", "Rattvarme", "steering_wheel_heat", "steering_wheel_heat", "mdi:steering");
        Switch("sentry", "Sentry", "sentry_mode", "sentry", "mdi:cctv");
        Switch("charging", "Opladning", "charging_state", "charging", "mdi:ev-station",
            "{{ 'ON' if value_json in ['charging', 'starting'] else 'OFF' }}");

        // Tal
        void Number(string key, string name, string stateKey, string cmdName, double min, double max, double step, string unit, string icon)
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = Num;
            c["command_topic"] = cmd(cmdName);
            c["min"] = min;
            c["max"] = max;
            c["step"] = step;
            c["unit_of_measurement"] = unit;
            c["mode"] = "slider";
            c["icon"] = icon;
            Add("number", key, c);
        }

        Number("charge_limit_set", "Ladegrænse", "charge_limit", "charge_limit", 50, 100, 1, "%", "mdi:battery-charging-high");
        Number("charging_amps_set", "Ladestrøm (ønsket)", "charge_current_request", "charging_amps", 5, 32, 1, "A", "mdi:current-ac");
        Number("driver_temp_set", "Temperatur", "driver_temp_setting", "driver_temp", 15, 28, 0.5, "°C", "mdi:thermometer");

        // Sædevarme
        foreach (var (key, name, stateKey, cmdName) in new[]
                 {
                     ("seat_heater_left_set", "Sædevarme fører", "seat_heater_left", "seat_heater_left"),
                     ("seat_heater_right_set", "Sædevarme passager", "seat_heater_right", "seat_heater_right"),
                 })
        {
            var c = Base(key, name);
            c["state_topic"] = state(stateKey);
            c["value_template"] = "{% set l = ['off','low','medium','high'] %}{{ l[value_json | int(0)] if value_json is not none else None }}";
            c["command_topic"] = cmd(cmdName);
            c["options"] = new JsonArray(CommandParser.SeatLevels.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            c["icon"] = "mdi:car-seat-heater";
            Add("select", key, c);
        }

        // Knapper
        void Button(string key, string name, string cmdName, string icon)
        {
            var c = Base(key, name);
            c["command_topic"] = cmd(cmdName);
            c["payload_press"] = "PRESS";
            c["icon"] = icon;
            Add("button", key, c);
        }

        Button("wake", "Væk bilen", "wake", "mdi:sleep-off");
        Button("flash_lights", "Blink lys", "flash_lights", "mdi:car-light-high");
        Button("honk_horn", "Dyt", "honk_horn", "mdi:bullhorn");
        Button("open_frunk", "Åbn frunk", "frunk", "mdi:car-select");
        Button("toggle_trunk", "Bagagerum", "trunk", "mdi:car-back");
        Button("vent_windows", "Luft ud (ruder)", "vent_windows", "mdi:car-door");
        Button("close_windows", "Luk ruder", "close_windows", "mdi:car-door-lock");
        Button("open_charge_port", "Åbn ladeport", "charge_port_open", "mdi:ev-plug-type2");
        Button("close_charge_port", "Luk ladeport", "charge_port_close", "mdi:ev-plug-type2");

        // Position
        {
            var c = Base("location", "Position");
            c["json_attributes_topic"] = state("location");
            c["source_type"] = "gps";
            c["icon"] = "mdi:car";
            Add("device_tracker", "location", c);
        }

        return list;
    }
}
