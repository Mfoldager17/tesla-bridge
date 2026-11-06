using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TeslaBridge.Commands;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.State;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Tests;

public class ParsingAndDiscoveryTests
{
    private static FieldValue? NoState(string _) => null;

    [Theory]
    [InlineData("climate", "on", typeof(Cmd.ClimateOn))]
    [InlineData("climate", "OFF", typeof(Cmd.ClimateOff))]
    [InlineData("lock", "UNLOCK", typeof(Cmd.Unlock))]
    [InlineData("charging", "on", typeof(Cmd.StartCharging))]
    [InlineData("wake", "PRESS", typeof(Cmd.Wake))]
    [InlineData("frunk", "PRESS", typeof(Cmd.OpenFrunk))]
    public void Parses_simple_commands(string name, string payload, Type expected) =>
        Assert.IsType(expected, CommandParser.Parse(name, payload, NoState));

    [Fact]
    public void Parses_and_clamps_numbers()
    {
        Assert.Equal(new Cmd.SetChargeLimit(100), CommandParser.Parse("charge_limit", "120", NoState));
        Assert.Equal(new Cmd.SetDriverTemp(21.5), CommandParser.Parse("driver_temp", "21.5", NoState));
    }

    [Theory]
    [InlineData("medium", 2)]
    [InlineData("3", 3)]
    public void Parses_seat_heater_levels(string payload, int level) =>
        Assert.Equal(new Cmd.SetSeatHeater(Seat.FrontLeft, level), CommandParser.Parse("seat_heater_left", payload, NoState));

    [Fact]
    public void Window_commands_use_car_location()
    {
        var loc = new FieldValue(new JsonObject { ["latitude"] = 56.1, ["longitude"] = 10.2 }, DateTimeOffset.UtcNow, ValueSource.Stream);
        var cmd = CommandParser.Parse("close_windows", "PRESS", k => k == "location" ? loc : null);
        Assert.Equal(new Cmd.CloseWindows(56.1, 10.2), cmd);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.Null(CommandParser.Parse("climate", "maybe", NoState));
        Assert.Null(CommandParser.Parse("self_destruct", "on", NoState));
    }

    [Theory]
    [InlineData("telemetry/+/v/+", "telemetry/VIN/v/Soc", true)]
    [InlineData("telemetry/+/v/+", "telemetry/VIN/connectivity", false)]
    [InlineData("tesla/#", "tesla/VIN/cmd/lock", true)]
    [InlineData("homeassistant/status", "homeassistant/status", true)]
    public void Topic_matching(string filter, string topic, bool expected) =>
        Assert.Equal(expected, MqttConnection.TopicMatches(filter, topic));

    [Fact]
    public void Discovery_ids_are_unique_and_command_topics_parse()
    {
        var configs = HomeAssistantDiscovery.Build(new VehicleOptions { Vin = "VIN1", Name = "Tesla" },
            "tesla", "homeassistant", "tesla/bridge/status");

        var ids = configs.Select(c => c.Payload["unique_id"]!.GetValue<string>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(configs, c => Assert.StartsWith("homeassistant/", c.Topic));

        // HA 2026.4+ kræver default_entity_id med domæne; object_id må ikke bruges.
        Assert.All(configs, c =>
        {
            var component = c.Topic.Split('/')[1];
            Assert.StartsWith($"{component}.tesla_", c.Payload["default_entity_id"]!.GetValue<string>());
            Assert.Null(c.Payload["object_id"]);
        });

        // Hver kommando HA kan sende skal kunne parses af bridgen.
        foreach (var (_, p) in configs)
        {
            if (p["command_topic"]?.GetValue<string>() is not { } topic) continue;
            var name = topic.Split('/')[^1];
            var payload = p["payload_press"]?.GetValue<string>() ?? p["payload_on"]?.GetValue<string>()
                ?? p["payload_lock"]?.GetValue<string>() ?? (p["options"] is JsonArray o ? o[1]!.GetValue<string>() : "60");
            Assert.NotNull(CommandParser.Parse(name, payload, NoState));
        }
    }

    [Theory]
    [InlineData("Model Y", "model_y")]
    [InlineData("Min Tesla", "min_tesla")]
    [InlineData("Bil 2 – grå", "bil_2_gra")]
    [InlineData("Søs", "soes")]
    public void Entity_slug(string name, string slug) => Assert.Equal(slug, HomeAssistantDiscovery.Slug(name));

    [Fact]
    public void Telemetry_config_request_has_expected_shape()
    {
        var dir = Directory.CreateTempSubdirectory();
        var fields = Path.Combine(dir.FullName, "fields.json");
        var ca = Path.Combine(dir.FullName, "ca.pem");
        File.WriteAllText(fields, """{ "fields": { "Locked": { "interval_seconds": 1 } } }""");
        File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----\nX\n-----END CERTIFICATE-----\n");

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-05T12:00:00Z"));
        var opts = Microsoft.Extensions.Options.Options.Create(new BridgeOptions
        {
            Telemetry = new TelemetryOptions { Hostname = "tesla.example.dk", Port = 4443, FieldsFile = fields, CaFile = ca, ConfigLifetimeDays = 30 },
        });
        var svc = new TelemetryConfigService(null!, opts, time, NullLogger<TelemetryConfigService>.Instance);

        var req = svc.BuildRequest(["VIN1"]);
        var cfg = req["config"]!;
        Assert.Equal("VIN1", req["vins"]![0]!.GetValue<string>());
        Assert.Equal("tesla.example.dk", cfg["hostname"]!.GetValue<string>());
        Assert.Equal(1, cfg["fields"]!["Locked"]!["interval_seconds"]!.GetValue<int>());
        Assert.True(cfg["prefer_typed"]!.GetValue<bool>());
        Assert.Equal(time.GetUtcNow().AddDays(30).ToUnixTimeSeconds(), cfg["exp"]!.GetValue<long>());
    }

    [Fact]
    public void Shipped_field_config_is_valid_and_only_uses_known_names()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../../config/bridge/telemetry-fields.json");
        var fields = JsonNode.Parse(File.ReadAllText(path))!["fields"]!.AsObject();
        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.Value!["interval_seconds"]!.GetValue<int>() >= 1));
    }
}
