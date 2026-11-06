using System.Text.Json.Nodes;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Tests;

public class VehicleDataTests
{
    private static Dictionary<string, JsonNode?> Map(string json) =>
        VehicleDataMapper.Map(JsonNode.Parse(json)!.AsObject()).Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value);

    [Fact]
    public void Parked_car_snapshot_fills_what_the_stream_never_sent()
    {
        var m = Map("""
        {
          "charge_state": { "battery_level": 25, "est_battery_range": 100, "charge_port_latch": "Engaged", "charging_state": "Disconnected" },
          "climate_state": { "inside_temp": 5.1, "climate_keeper_mode": "dog", "cabin_overheat_protection": "FanOnly",
                             "cop_activation_temperature": "Medium", "seat_heater_rear_left": 2, "auto_seat_climate_left": true },
          "vehicle_state": { "locked": true, "fd_window": 0, "fp_window": 0, "rd_window": 1, "rp_window": 0,
                             "odometer": 1000, "car_version": "2026.38.3.2 abc123", "tpms_pressure_fl": 2.9, "valet_mode": false },
          "drive_state": { "shift_state": null, "speed": null, "latitude": 56.1, "longitude": 10.1 }
        }
        """);

        Assert.Equal("P", m["gear"]!.GetValue<string>());
        Assert.Equal(0, m["speed"]!.GetValue<double>());
        Assert.Equal(161, m["est_range"]!.GetValue<double>());
        Assert.True(m["charge_cable_locked"]!.GetValue<bool>());
        Assert.False(m["charge_cable_connected"]!.GetValue<bool>());
        Assert.Equal("dog", m["climate_keeper_mode"]!.GetValue<string>());
        Assert.Equal("fan_only", m["cabin_overheat_protection"]!.GetValue<string>());
        Assert.Equal(35, m["cop_temp_limit"]!.GetValue<int>());
        Assert.True(m["windows_open"]!.GetValue<bool>());
        Assert.True(m["window_driver_rear_open"]!.GetValue<bool>());
        Assert.Equal("2026.38.3.2", m["software_version"]!.GetValue<string>());
        Assert.Equal(1609.3, m["odometer"]!.GetValue<double>());
        Assert.False(m["valet_mode"]!.GetValue<bool>());
        Assert.Equal(56.1, m["location"]!["latitude"]!.GetValue<double>());
    }

    [Fact]
    public void Missing_sections_give_nothing() => Assert.Empty(Map("{}"));
}
