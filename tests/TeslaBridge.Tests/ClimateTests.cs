using System.Text.Json.Nodes;
using TeslaBridge.Commands;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.State;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Tests;

public class ClimateTests
{
    private static FieldValue? NoState(string _) => null;

    private static Dictionary<string, JsonNode?> Map(string field, string json) =>
        SignalMapper.Map(field, JsonNode.Parse(json)).ToDictionary(x => x.Key, x => x.Value);

    [Theory]
    [InlineData("ClimateKeeperModeStateOff", "off")]
    [InlineData("ClimateKeeperModeStateOn", "keep")]
    [InlineData("ClimateKeeperModeStateDog", "dog")]
    [InlineData("ClimateKeeperModeStateParty", "camp")]
    public void Climate_keeper_mode_is_mapped(string raw, string expected) =>
        Assert.Equal(expected, Map("ClimateKeeperMode", $"\"{raw}\"")["climate_keeper_mode"]!.GetValue<string>());

    [Fact]
    public void Rear_seats_and_overheat_protection_are_mapped()
    {
        Assert.Equal(2, Map("SeatHeaterRearCenter", "2")["seat_heater_rear_center"]!.GetValue<double>());
        Assert.Equal("fan_only", Map("CabinOverheatProtectionMode", "\"CabinOverheatProtectionModeStateFanOnly\"")["cabin_overheat_protection"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Dog mode", 2)]
    [InlineData("dog", 2)]
    [InlineData("Keep mode", 1)]
    [InlineData("Camp mode", 3)]
    [InlineData("Normal", 0)]
    [InlineData("none", 0)]
    public void Climate_keeper_presets_are_parsed(string payload, int mode) =>
        Assert.Equal(new Cmd.SetClimateKeeper(mode), CommandParser.Parse("climate_keeper", payload, NoState));

    [Fact]
    public void Climate_keeper_expects_climate_on()
    {
        var c = new Cmd.SetClimateKeeper(2);
        Assert.Equal(2, c.FleetBody["climate_keeper_mode"]!.GetValue<int>());
        Assert.Contains(c.Expectations, e => e.Key == "climate_on");
        Assert.Contains(c.Expectations, e => e.Key == "climate_keeper_mode" && e.Value!.GetValue<string>() == "dog");
    }

    [Fact]
    public void Rear_seat_uses_teslas_seat_position()
    {
        var c = (Cmd.SetSeatHeater)CommandParser.Parse("seat_heater_rear_right", "high", NoState)!;
        Assert.Equal(5, c.FleetBody["seat_position"]!.GetValue<int>());
        Assert.Equal(3, c.FleetBody["level"]!.GetValue<int>());
        Assert.Equal("seat_heater_rear_right", c.Expectations.Single().Key);
    }

    [Fact]
    public void Overheat_protection_body()
    {
        var c = (Cmd.SetCabinOverheatProtection)CommandParser.Parse("cabin_overheat_protection", "fan_only", NoState)!;
        Assert.Equal(1, new Cmd.SetCopTemp(35).FleetBody["cop_temp"]!.GetValue<int>());
        Assert.True(c.FleetBody["on"]!.GetValue<bool>());
        Assert.True(c.FleetBody["fan_only"]!.GetValue<bool>());
    }

    private static List<(string Topic, JsonObject Payload)> Discovery() =>
        HomeAssistantDiscovery.Build(new VehicleOptions { Vin = "VIN1", Name = "Model Y" }, "tesla", "homeassistant", "tesla/bridge/status");

    [Fact]
    public void Discovery_has_a_thermostat_entity_like_teslemetry()
    {
        var c = Discovery().Single(x => x.Topic == "homeassistant/climate/tesla_bridge_vin1/climate/config").Payload;
        Assert.Equal("climate.model_y_climate", c["default_entity_id"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/cmd/climate", c["mode_command_topic"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/cmd/driver_temp", c["temperature_command_topic"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/state/inside_temp", c["current_temperature_topic"]!.GetValue<string>());
        Assert.Equal(["off", "keep", "dog", "camp"], c["preset_modes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("'dog': 'dog'", c["preset_mode_value_template"]!.GetValue<string>());
    }

    /// <summary>The same entities (domain + name) as Teslemetry's "Controls".</summary>
    [Theory]
    [InlineData("switch.model_y_auto_seat_climate_left", "Auto seat climate left")]
    [InlineData("switch.model_y_auto_seat_climate_right", "Auto seat climate right")]
    [InlineData("switch.model_y_auto_steering_wheel_heater", "Auto steering wheel heater")]
    [InlineData("switch.model_y_charge", "Charge")]
    [InlineData("lock.model_y_charge_cable_lock", "Charge cable lock")]
    [InlineData("number.model_y_charge_current", "Charge current")]
    [InlineData("number.model_y_charge_limit", "Charge limit")]
    [InlineData("cover.model_y_charge_port_door", "Charge port door")]
    [InlineData("climate.model_y_climate", "Climate")]
    [InlineData("climate.model_y_cabin_overheat_protection", "Cabin overheat protection")]
    [InlineData("switch.model_y_defrost", "Defrost")]
    [InlineData("button.model_y_flash_lights", "Flash lights")]
    [InlineData("cover.model_y_frunk", "Frunk")]
    [InlineData("switch.model_y_guest_mode", "Guest mode")]
    [InlineData("button.model_y_homelink", "Homelink")]
    [InlineData("button.model_y_honk_horn", "Honk horn")]
    [InlineData("button.model_y_keyless_driving", "Keyless driving")]
    [InlineData("lock.model_y_lock", "Lock")]
    [InlineData("button.model_y_play_fart", "Play fart")]
    [InlineData("select.model_y_seat_heater_front_left", "Seat heater front left")]
    [InlineData("select.model_y_seat_heater_front_right", "Seat heater front right")]
    [InlineData("switch.model_y_sentry_mode", "Sentry mode")]
    [InlineData("select.model_y_steering_wheel_heater", "Steering wheel heater")]
    [InlineData("cover.model_y_trunk", "Trunk")]
    [InlineData("switch.model_y_valet_mode", "Valet mode")]
    [InlineData("button.model_y_wake", "Wake")]
    [InlineData("cover.model_y_windows", "Windows")]
    [InlineData("sensor.model_y_battery_level", "Battery level")]
    [InlineData("binary_sensor.model_y_status", "Status")]
    public void Controls_match_teslemetry(string entityId, string name)
    {
        var c = Discovery().Single(x => x.Payload["default_entity_id"]!.GetValue<string>() == entityId).Payload;
        Assert.Equal(name, c["name"]!.GetValue<string>());
    }

    [Fact]
    public void All_unique_ids_are_unique_and_old_entities_are_cleaned_up()
    {
        var configs = Discovery();
        var ids = configs.Select(c => c.Payload["unique_id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var legacy = HomeAssistantDiscovery.LegacyTopics(new VehicleOptions { Vin = "VIN1", Name = "Model Y" }, "homeassistant", configs).ToList();
        Assert.Contains("homeassistant/switch/tesla_bridge_vin1/sentry/config", legacy);
        Assert.Contains("homeassistant/sensor/tesla_bridge_vin1/est_range/config", legacy);
        Assert.DoesNotContain(legacy, t => configs.Any(c => c.Topic == t));   // rydder aldrig noget, der er i brug
    }

    [Fact]
    public void Frunk_cannot_be_closed_and_windows_aggregate()
    {
        var frunk = Discovery().Single(x => x.Topic.EndsWith("/frunk/config")).Payload;
        Assert.Null(frunk["payload_close"]);
        Assert.True(SignalMapper.WindowsOpen(k => k == "window_driver_rear_open" ? JsonValue.Create(true) : JsonValue.Create(false))!.GetValue<bool>());
        Assert.Null(SignalMapper.WindowsOpen(_ => null));
        Assert.IsType<Cmd.VentWindows>(CommandParser.Parse("windows", "OPEN", NoState));
        Assert.IsType<Cmd.OpenChargePort>(CommandParser.Parse("charge_cable_lock", "UNLOCK", NoState));
        Assert.Equal(new Cmd.SetSteeringWheelHeatLevel(3), CommandParser.Parse("steering_wheel_heater", "high", NoState));
    }
}

public class FieldsDifferTests
{
    private static System.Text.Json.Nodes.JsonObject F(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Detects_new_fields_and_changed_intervals()
    {
        var car = F("""{ "Locked": { "interval_seconds": 1 }, "Odometer": { "interval_seconds": 60 } }""");
        Assert.Null(TeslaBridge.Telemetry.TelemetryConfigService.FieldsDiffer(car, F("""{ "Locked": { "interval_seconds": 1 }, "Odometer": { "interval_seconds": 60 } }""")));
        Assert.Contains("ClimateKeeperMode", TeslaBridge.Telemetry.TelemetryConfigService.FieldsDiffer(car,
            F("""{ "Locked": { "interval_seconds": 1 }, "Odometer": { "interval_seconds": 60 }, "ClimateKeeperMode": { "interval_seconds": 1 } }""")));
        Assert.Contains("Odometer", TeslaBridge.Telemetry.TelemetryConfigService.FieldsDiffer(car,
            F("""{ "Locked": { "interval_seconds": 1 }, "Odometer": { "interval_seconds": 30 } }""")));
        Assert.Null(TeslaBridge.Telemetry.TelemetryConfigService.FieldsDiffer(null, car));
    }
}
