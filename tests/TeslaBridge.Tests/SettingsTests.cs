using System.Text.Json.Nodes;
using TeslaBridge.Settings;
using TeslaBridge.Web;

namespace TeslaBridge.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("settings").FullName;
    private string File_ => Path.Combine(_dir, "settings.json");
    private string Defaults => Path.Combine(_dir, "fields.json");

    public SettingsTests() =>
        File.WriteAllText(Defaults, """{ "fields": { "Location": { "interval_seconds": 2 }, "Locked": { "interval_seconds": 1 }, "Odometer": { "interval_seconds": 30 } } }""");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_are_sent_when_nothing_is_changed()
    {
        var s = new SettingsStore(File_, Defaults);
        var f = s.EffectiveFields();
        Assert.Equal(3, f.Count);
        Assert.Equal(2, f["Location"]!["interval_seconds"]!.GetValue<int>());
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void Changes_are_applied_saved_and_survive_a_restart()
    {
        var s = new SettingsStore(File_, Defaults);
        s.Update(new Dictionary<string, FieldSetting>
        {
            ["Location"] = new(true, 30),
            ["Odometer"] = new(false, 30),
        }, snapshotMinutes: 0);

        var again = new SettingsStore(File_, Defaults);
        var f = again.EffectiveFields();
        Assert.Equal(30, f["Location"]!["interval_seconds"]!.GetValue<int>());
        Assert.Null(f["Odometer"]);                 // disabled → not sent to the car
        Assert.Equal(1, f["Locked"]!["interval_seconds"]!.GetValue<int>());
        Assert.Equal(0, again.Current.SnapshotIntervalMinutes);
    }

    [Fact]
    public void Setting_a_field_back_to_its_default_removes_the_override()
    {
        var s = new SettingsStore(File_, Defaults);
        s.Update(new Dictionary<string, FieldSetting> { ["Location"] = new(true, 30) }, null);
        s.Update(new Dictionary<string, FieldSetting> { ["Location"] = new(true, 2) }, null);
        Assert.Empty(s.Current.TelemetryFields);
        s.Update(new Dictionary<string, FieldSetting> { ["Locked"] = new(false, 1) }, null);
        Assert.Single(s.Current.TelemetryFields);
        s.Reset();
        Assert.Empty(s.Current.TelemetryFields);
        Assert.Equal(SettingsStore.DefaultSnapshotMinutes, s.Current.SnapshotIntervalMinutes);
    }

    [Theory]
    [InlineData("Unknown", 10)]
    [InlineData("Location", 0)]
    [InlineData("Location", 100_000)]
    public void Invalid_changes_are_rejected_and_nothing_is_saved(string field, int interval)
    {
        var s = new SettingsStore(File_, Defaults);
        Assert.Throws<ArgumentException>(() => s.Update(new Dictionary<string, FieldSetting> { [field] = new(true, interval) }, null));
        Assert.Throws<ArgumentException>(() => s.Update(null, -1));
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void A_broken_file_falls_back_to_defaults()
    {
        File.WriteAllText(File_, "{ not json");
        Assert.Equal(3, new SettingsStore(File_, Defaults).EffectiveFields().Count);
    }

    [Fact]
    public void Api_body_is_snake_case()
    {
        var (fields, snapshot, push, carView) = SettingsApi.Parse(JsonNode.Parse(
            """{ "fields": { "Location": { "enabled": false, "interval_seconds": 10 } }, "snapshot_interval_minutes": 15, "push": false }"""));
        Assert.Equal(new FieldSetting(false, 10), fields!["Location"]);
        Assert.Equal(15, snapshot);
        Assert.False(push);
        Assert.Null(carView);
        Assert.Equal("drawing", SettingsApi.Parse(JsonNode.Parse("""{ "car_view": "drawing" }""")).CarView);
        Assert.Throws<ArgumentException>(() => SettingsApi.Parse(JsonNode.Parse("""{ "fields": { "Location": { "enabled": true } } }""")));
    }

    [Fact]
    public void Every_default_field_has_a_description()
    {
        var real = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/bridge/telemetry-fields.json"));
        var fields = JsonNode.Parse(File.ReadAllText(real))!["fields"]!.AsObject();
        Assert.All(fields, kv => Assert.True(TelemetryFieldCatalog.Fields.ContainsKey(kv.Key), kv.Key));
        Assert.All(TelemetryFieldCatalog.Fields.Values, f => Assert.Contains(f.Group, TelemetryFieldCatalog.Groups));
    }
}

public class CarViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("carview").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Car_view_is_saved_validated_and_survives_a_reset()
    {
        var defaults = Path.Combine(_dir, "f.json");
        File.WriteAllText(defaults, """{ "fields": { "Locked": { "interval_seconds": 1 } } }""");
        var s = new SettingsStore(Path.Combine(_dir, "s.json"), defaults);
        Assert.Equal("builtin", s.Current.CarView);
        s.Update(null, null, "drawing");
        Assert.Equal("drawing", new SettingsStore(Path.Combine(_dir, "s.json"), defaults).Current.CarView);
        Assert.Throws<ArgumentException>(() => s.Update(null, null, "hologram"));
        s.Reset();
        Assert.Equal("drawing", s.Current.CarView);
    }

    [Fact]
    public void Builtin_model_ships_with_its_attribution()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/TeslaBridge/wwwroot/models"));
        Assert.True(TeslaBridge.Web.CarModelApi.LooksLikeGlb(File.ReadAllBytes(Path.Combine(root, "model-y.glb")).AsSpan(0, 8)));
        Assert.Contains("BloxBloger", File.ReadAllText(Path.Combine(root, "NOTICE.md")));
    }
}
