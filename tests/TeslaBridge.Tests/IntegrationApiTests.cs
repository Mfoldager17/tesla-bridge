using System.Net;
using System.Text.Json.Nodes;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.Setup;

namespace TeslaBridge.Tests;

/// <summary>GET /api/entities for the HA integration, and the embedded broker.</summary>
public class IntegrationApiTests
{
    private static BridgeOptions Opts()
    {
        var o = new BridgeOptions();
        o.Vehicles.Add(new VehicleOptions { Vin = "VIN1", Name = "Model Y" });
        return o;
    }

    private static JsonObject Entity(string platform, string key) =>
        HomeAssistantDiscovery.ApiDefinitions(Opts())["vehicles"]![0]!["entities"]!.AsArray()
            .Single(e => (string?)e!["platform"] == platform && (string?)e["key"] == key)!["config"]!.AsObject();

    [Fact]
    public void Same_entities_as_mqtt_discovery()
    {
        var api = HomeAssistantDiscovery.ApiDefinitions(Opts());
        var mqtt = HomeAssistantDiscovery.Build(Opts().Vehicles[0], "tesla", "homeassistant", "x");
        Assert.Equal(mqtt.Count, api["vehicles"]![0]!["entities"]!.AsArray().Count);
        Assert.Equal("model_y", (string?)api["vehicles"]![0]!["slug"]);
        Assert.Equal(8, api["bridge"]!["entities"]!.AsArray().Count);   // 7 usage sensors + the update entity
        var update = api["bridge"]!["entities"]!.AsArray().Single(e => (string?)e!["platform"] == "update")!;
        Assert.Equal("update", (string?)update["config"]!["state_topic"]);
    }

    [Fact]
    public void Topics_become_state_and_cmd_keys()
    {
        var c = Entity("climate", "climate");
        Assert.Equal("state:climate_on", (string?)c["mode_state_topic"]);
        Assert.Equal("cmd:climate", (string?)c["mode_command_topic"]);
        Assert.Equal("climate.model_y_climate", (string?)c["default_entity_id"]);
        Assert.Null(c["availability_topic"]);
        Assert.Null(c["device"]);

        Assert.Equal("state:locked_motion", (string?)Entity("lock", "lock")["state_topic"]);
        Assert.Equal("state:command_status", (string?)Entity("sensor", "command_status")["state_topic"]);
        Assert.Equal("state:battery_level", (string?)Entity("sensor", "battery_level")["state_topic"]);
        Assert.Equal("usage", (string?)HomeAssistantDiscovery.ApiDefinitions(Opts())["bridge"]!["entities"]![0]!["config"]!["state_topic"]);
    }

    [Fact]
    public void Embedded_broker_tells_fleet_telemetry_to_use_the_bridge()
    {
        var o = Opts();
        o.Mqtt.Host = "192.168.0.20";
        Assert.Equal("192.168.0.20:1883", o.Mqtt.BrokerForTelemetry);
        Assert.Equal("192.168.0.20", o.Mqtt.ConnectHost);
        o.Mqtt.EmbeddedBroker = true;
        Assert.Equal("tesla-bridge:1883", (string?)Bootstrap.BuildServerConfig(o)["mqtt"]!["broker"]);
        Assert.Equal("127.0.0.1", o.Mqtt.ConnectHost);
    }

    [Fact]
    public void Embedded_broker_never_lets_anyone_in_without_a_password()
    {
        var o = Opts();
        var remote = new IPEndPoint(IPAddress.Parse("172.18.0.4"), 50000);
        Assert.Null(EmbeddedBroker.Authenticate(EmbeddedBroker.Users(o), null, null, remote));   // nothing set: nobody

        o.Telemetry.MqttPassword = "secret";
        o.Mqtt.Username = "homeassistant";
        o.Mqtt.Password = "ha-secret";
        var users = EmbeddedBroker.Users(o);
        Assert.Equal(EmbeddedBroker.Role.Telemetry, EmbeddedBroker.Authenticate(users, "fleet-telemetry", "secret", remote));
        Assert.Equal(EmbeddedBroker.Role.Full, EmbeddedBroker.Authenticate(users, "homeassistant", "ha-secret", remote));
        Assert.Null(EmbeddedBroker.Authenticate(users, "fleet-telemetry", "wrong", remote));
        Assert.Null(EmbeddedBroker.Authenticate(users, null, null, remote));
        Assert.Equal(EmbeddedBroker.Role.Full, EmbeddedBroker.Authenticate(users, "tesla-bridge", "", new IPEndPoint(IPAddress.Loopback, 1)));   // the bridge itself
    }

    [Fact]
    public void Fleet_telemetry_may_only_publish_raw_telemetry()
    {
        Assert.True(EmbeddedBroker.MayPublish(EmbeddedBroker.Role.Telemetry, "telemetry/VIN/v/Locked", "telemetry/"));
        Assert.False(EmbeddedBroker.MayPublish(EmbeddedBroker.Role.Telemetry, "tesla/VIN/cmd/lock", "telemetry/"));
        Assert.False(EmbeddedBroker.MayPublish(EmbeddedBroker.Role.Telemetry, "telemetryx/VIN", "telemetry/"));
        Assert.True(EmbeddedBroker.MayPublish(EmbeddedBroker.Role.Full, "tesla/VIN/cmd/lock", "telemetry/"));
    }
}

public class ParkedCarTests
{
    private static Dictionary<string, System.Text.Json.Nodes.JsonNode?> Map(string field, string json) =>
        TeslaBridge.Telemetry.SignalMapper.Map(field, System.Text.Json.Nodes.JsonNode.Parse(json)).ToDictionary(x => x.Key, x => x.Value);

    [Theory]
    [InlineData("\"ShiftStateD\"", "D")]
    [InlineData("\"ShiftStateR\"", "R")]
    [InlineData("\"ShiftStateP\"", "P")]
    [InlineData("\"ShiftStateInvalid\"", "P")]
    [InlineData("\"ShiftStateSNA\"", "P")]
    [InlineData("null", "P")]
    public void Invalid_gear_means_parked(string raw, string expected) =>
        Assert.Equal(expected, Map("Gear", raw)["gear"]!.GetValue<string>());

    [Fact]
    public void Invalid_speed_is_zero_and_invalid_range_is_ignored()
    {
        Assert.Equal(0, Map("VehicleSpeed", "null")["speed"]!.GetValue<double>());
        Assert.Empty(Map("EstBatteryRange", "null"));
        Assert.Equal(161, Map("EstBatteryRange", "100")["est_range"]!.GetValue<double>());
    }
}

public class CarModelTests
{
    [Fact]
    public void Only_glb_files_are_accepted()
    {
        Assert.True(TeslaBridge.Web.CarModelApi.LooksLikeGlb("glTF\u0002\0\0\0"u8));
        Assert.False(TeslaBridge.Web.CarModelApi.LooksLikeGlb("glTF\u0001\0\0\0"u8));
        Assert.False(TeslaBridge.Web.CarModelApi.LooksLikeGlb("{\"asset\":"u8));
        Assert.False(TeslaBridge.Web.CarModelApi.LooksLikeGlb("gl"u8));
    }

    [Fact]
    public void Uploaded_model_wins_over_the_config_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var o = new TeslaBridge.Options.BridgeOptions { StateFile = Path.Combine(dir, "state.json") };
        o.Web.CarModelFile = Path.Combine(dir, "car.glb");
        Assert.Null(TeslaBridge.Web.CarModelApi.Find(o));
        File.WriteAllText(o.Web.CarModelFile, "x");
        Assert.Equal("file", TeslaBridge.Web.CarModelApi.Find(o)!.Value.Source);
        File.WriteAllText(TeslaBridge.Web.CarModelApi.UploadedPath(o), "y");
        Assert.Equal("uploaded", TeslaBridge.Web.CarModelApi.Find(o)!.Value.Source);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Version_numbers_compare_as_numbers()
    {
        Assert.True(TeslaBridge.Versioning.BuildInfo.Parse("v1.10.0") > TeslaBridge.Versioning.BuildInfo.Parse("1.9.3"));
        Assert.Equal(new Version(1, 2, 0), TeslaBridge.Versioning.BuildInfo.Parse("1.2.0-edge+abc"));
        Assert.Equal(new Version(0, 0, 0), TeslaBridge.Versioning.BuildInfo.Parse("nonsense"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", TeslaBridge.Versioning.BuildInfo.Version);
    }
}
