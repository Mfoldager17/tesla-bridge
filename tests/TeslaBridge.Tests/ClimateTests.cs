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
    [InlineData("Hund", 2)]
    [InlineData("dog", 2)]
    [InlineData("Hold", 1)]
    [InlineData("Camp", 3)]
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
        Assert.True(c.FleetBody["on"]!.GetValue<bool>());
        Assert.True(c.FleetBody["fan_only"]!.GetValue<bool>());
    }

    [Fact]
    public void Discovery_has_a_thermostat_entity_like_teslemetry()
    {
        var configs = HomeAssistantDiscovery.Build(new VehicleOptions { Vin = "VIN1", Name = "Model Y" },
            "tesla", "homeassistant", "tesla/bridge/status");
        var (topic, c) = configs.Single(x => x.Topic.StartsWith("homeassistant/climate/"));

        Assert.Equal("climate.model_y_climate", c["default_entity_id"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/cmd/climate", c["mode_command_topic"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/cmd/driver_temp", c["temperature_command_topic"]!.GetValue<string>());
        Assert.Equal("tesla/VIN1/state/inside_temp", c["current_temperature_topic"]!.GetValue<string>());
        Assert.Equal(["Hold", "Hund", "Camp"], c["preset_modes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("'dog': 'Hund'", c["preset_mode_value_template"]!.GetValue<string>());

        // Den gamle kontakt findes stadig og har ikke samme unique_id
        var sw = configs.Single(x => x.Topic.Contains("/switch/") && x.Topic.EndsWith("/climate/config")).Payload;
        Assert.NotEqual(sw["unique_id"]!.GetValue<string>(), c["unique_id"]!.GetValue<string>());
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
