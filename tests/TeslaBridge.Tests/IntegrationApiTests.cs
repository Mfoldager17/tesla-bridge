using System.Net;
using System.Text.Json.Nodes;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.Setup;

namespace TeslaBridge.Tests;

/// <summary>GET /api/entities til HA-integrationen og den indbyggede broker.</summary>
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
        Assert.Equal(7, api["bridge"]!["entities"]!.AsArray().Count);
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
    public void Embedded_broker_checks_passwords_when_set()
    {
        var o = Opts();
        var remote = new IPEndPoint(IPAddress.Parse("172.18.0.4"), 50000);
        Assert.True(EmbeddedBroker.Allowed(EmbeddedBroker.Users(o), null, null, remote));   // intet sat: åben (kun docker-netværket)

        o.Telemetry.MqttPassword = "hemmelig";
        var users = EmbeddedBroker.Users(o);
        Assert.True(EmbeddedBroker.Allowed(users, "fleet-telemetry", "hemmelig", remote));
        Assert.False(EmbeddedBroker.Allowed(users, "fleet-telemetry", "forkert", remote));
        Assert.False(EmbeddedBroker.Allowed(users, null, null, remote));
        Assert.True(EmbeddedBroker.Allowed(users, null, null, new IPEndPoint(IPAddress.Loopback, 1)));   // bridgen selv
    }
}
