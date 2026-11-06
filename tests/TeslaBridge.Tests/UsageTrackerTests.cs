using Microsoft.Extensions.Time.Testing;
using TeslaBridge.Publishing;
using TeslaBridge.Usage;

namespace TeslaBridge.Tests;

public class UsageTrackerTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-01T00:00:00Z"));

    [Theory]
    [InlineData("/api/1/vehicles/VIN/command/auto_conditioning_start", ApiCallKind.Command, "auto_conditioning_start")]
    [InlineData("/api/1/vehicles/VIN/wake_up", ApiCallKind.Wake, "wake_up")]
    [InlineData("/api/1/vehicles/VIN/fleet_telemetry_config", ApiCallKind.Data, "fleet_telemetry_config")]
    [InlineData("/api/1/vehicles/fleet_telemetry_config", ApiCallKind.Data, "fleet_telemetry_config")]
    [InlineData("/api/1/vehicles/VIN/fleet_telemetry_errors?x=1", ApiCallKind.Data, "fleet_telemetry_errors")]
    public void Classifies_calls_like_tesla_bills_them(string path, ApiCallKind kind, string name) =>
        Assert.Equal((kind, name), UsageTracker.Classify(path));

    [Fact]
    public void Costs_follow_tesla_rates()
    {
        var t = new UsageTracker(_time);
        for (var i = 0; i < 1500; i++) t.CountSignal("Location");
        for (var i = 0; i < 10; i++) t.CountApiCall(ApiCallKind.Command, "door_lock");
        t.CountApiCall(ApiCallKind.Wake, "wake_up");
        t.CountApiCall(ApiCallKind.Data, "fleet_telemetry_config");

        var s = t.Snapshot();
        Assert.Equal(1500 / 150_000.0 + 10 / 1000.0 + 1 / 50.0 + 1 / 500.0, s.TotalUsd, 6);
        Assert.Equal(1500, s.SignalsByField["Location"]);
        Assert.Equal(10, s.CommandsByName["door_lock"]);
        Assert.Equal(1, s.Categories.Single(c => c.Key == "wakes").Count);
    }

    [Fact]
    public void Projection_waits_a_day_then_extrapolates_to_month_end()
    {
        var t = new UsageTracker(_time);
        for (var i = 0; i < 100; i++) t.CountApiCall(ApiCallKind.Command, "x"); // $0.10

        _time.Advance(TimeSpan.FromHours(12));
        Assert.Null(t.Snapshot().ProjectedUsd);

        _time.Advance(TimeSpan.FromHours(108)); // 5 dage inde i november (30 dage)
        Assert.Equal(0.10 * 30 / 5, t.Snapshot().ProjectedUsd!.Value, 6);
    }

    [Fact]
    public void New_month_starts_from_zero_and_keeps_history()
    {
        var t = new UsageTracker(_time);
        for (var i = 0; i < 50; i++) t.CountApiCall(ApiCallKind.Wake, "wake_up"); // $1

        _time.SetUtcNow(DateTimeOffset.Parse("2026-12-01T00:00:01Z"));
        var s = t.Snapshot();

        Assert.Equal("2026-12", s.Month);
        Assert.Equal(0, s.TotalUsd);
        Assert.Equal(("2026-11", 1.0), s.History.Single());
    }

    [Fact]
    public void Survives_restart()
    {
        var t = new UsageTracker(_time);
        t.CountSignal("BatteryLevel");
        t.CountApiCall(ApiCallKind.Command, "door_lock");
        Assert.True(t.IsDirty);

        var json = t.Serialize();
        Assert.False(t.IsDirty);

        var restored = new UsageTracker(_time);
        restored.Restore(json);
        var s = restored.Snapshot();
        Assert.Equal(1, s.SignalsByField["BatteryLevel"]);
        Assert.Equal(1, s.CommandsByName["door_lock"]);
    }

    [Fact]
    public void Restored_old_month_rolls_into_history()
    {
        var t = new UsageTracker(_time);
        t.CountApiCall(ApiCallKind.Wake, "wake_up");
        var json = t.Serialize();

        _time.SetUtcNow(DateTimeOffset.Parse("2027-01-15T00:00:00Z"));
        var restored = new UsageTracker(_time);
        restored.Restore(json);

        Assert.Equal("2027-01", restored.Snapshot().Month);
        Assert.Equal("2026-11", restored.Snapshot().History.Single().Month);
    }

    [Fact]
    public void Usage_json_matches_what_ha_templates_read()
    {
        var t = new UsageTracker(_time);
        t.CountSignal("Locked");
        var json = UsageService.ToJson(t.Snapshot());

        Assert.NotNull(json["total_usd"]);
        Assert.NotNull(json["month_start"]);
        Assert.Equal(10, json["credit_usd"]!.GetValue<double>());
        foreach (var cat in new[] { "signals", "commands", "wakes", "data" })
            Assert.NotNull(json["categories"]![cat]!["count"]);

        var sensors = HomeAssistantDiscovery.BuildUsage("tesla", "homeassistant", "tesla/bridge/status");
        Assert.Equal(7, sensors.Count);
        Assert.All(sensors, s => Assert.Equal("tesla/bridge/usage", s.Payload["state_topic"]!.GetValue<string>()));
        Assert.All(sensors, s => Assert.StartsWith("sensor.tesla_bridge_", s.Payload["default_entity_id"]!.GetValue<string>()));
    }
}
