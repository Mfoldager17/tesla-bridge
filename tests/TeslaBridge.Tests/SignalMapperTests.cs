using System.Text.Json.Nodes;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Tests;

public class SignalMapperTests
{
    private static Dictionary<string, JsonNode?> Map(string field, string json) =>
        SignalMapper.Map(field, JsonNode.Parse(json)).ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Charge_state_enum_is_normalised_and_derives_cable()
    {
        var m = Map("DetailedChargeState", "\"DetailedChargeStateNoPower\"");
        Assert.Equal("no_power", m["charging_state"]!.GetValue<string>());
        Assert.True(m["charge_cable_connected"]!.GetValue<bool>());

        var d = Map("DetailedChargeState", "\"DetailedChargeStateDisconnected\"");
        Assert.False(d["charge_cable_connected"]!.GetValue<bool>());
    }

    [Fact]
    public void Miles_are_converted_to_km_and_strings_are_tolerated()
    {
        Assert.Equal(161, Map("EstBatteryRange", "100").Single().Value!.GetValue<double>());
        Assert.Equal(161, Map("EstBatteryRange", "\"100.0\"").Single().Value!.GetValue<double>());
    }

    [Fact]
    public void Doors_are_split_into_separate_keys()
    {
        var m = Map("DoorState", """{"DriverFront":true,"PassengerFront":false,"DriverRear":false,"PassengerRear":false,"TrunkFront":true,"TrunkRear":false}""");
        Assert.True(m["door_driver_front_open"]!.GetValue<bool>());
        Assert.True(m["frunk_open"]!.GetValue<bool>());
        Assert.False(m["trunk_open"]!.GetValue<bool>());
        Assert.Equal(6, m.Count);
    }

    [Theory]
    [InlineData("\"WindowStateClosed\"", false)]
    [InlineData("\"WindowStatePartiallyOpen\"", true)]
    [InlineData("\"WindowStateOpened\"", true)]
    public void Window_state_becomes_open_bool(string raw, bool open) =>
        Assert.Equal(open, Map("FdWindow", raw)["window_driver_front_open"]!.GetValue<bool>());

    [Theory]
    [InlineData("\"HvacPowerStateOn\"", true)]
    [InlineData("\"HvacPowerStatePrecondition\"", true)]
    [InlineData("\"HvacPowerStateOff\"", false)]
    public void Hvac_power_maps_to_climate_on(string raw, bool on) =>
        Assert.Equal(on, Map("HvacPower", raw)["climate_on"]!.GetValue<bool>());

    [Fact]
    public void Location_keeps_lat_lon()
    {
        var loc = Map("Location", """{"latitude":56.1,"longitude":10.2}""")["location"]!;
        Assert.Equal(56.1, loc["latitude"]!.GetValue<double>());
        Assert.Equal(10.2, loc["longitude"]!.GetValue<double>());
    }

    [Fact]
    public void Gear_is_short_upper_case() =>
        Assert.Equal("D", Map("Gear", "\"ShiftStateD\"")["gear"]!.GetValue<string>());

    [Fact]
    public void Unknown_fields_pass_through_as_snake_case()
    {
        var m = Map("TpmsPressureFl", "2.9");
        Assert.Equal(2.9, m["tpms_pressure_fl"]!.GetValue<double>());
    }

    [Fact]
    public void Invalid_values_become_null() =>
        Assert.Null(Map("BatteryLevel", "null")["battery_level"]);

    [Theory]
    [InlineData("ACChargingPower", "ac_charging_power")]
    [InlineData("HvacLeftTemperatureRequest", "hvac_left_temperature_request")]
    [InlineData("SNA", "sna")]
    public void ToSnake(string input, string expected) => Assert.Equal(expected, SignalMapper.ToSnake(input));
}
