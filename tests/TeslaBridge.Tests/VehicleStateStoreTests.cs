using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using TeslaBridge.State;

namespace TeslaBridge.Tests;

public class VehicleStateStoreTests
{
    private const string Vin = "VIN1";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-05T12:00:00Z"));

    private static bool? Bool(FieldValue? f) => f?.Value?.GetValue<bool>();

    [Fact]
    public void Stale_value_right_after_wake_does_not_override_expected_value()
    {
        // Det oprindelige problem: tryk "klima on" → loader → viser "off" → 5-10 s senere "on".
        var store = new VehicleStateStore(_time);
        store.Apply(Vin, "climate_on", JsonValue.Create(false), _time.GetUtcNow());

        store.SetPending(Vin, "climate_on", JsonValue.Create(true), TimeSpan.FromSeconds(45));
        Assert.True(Bool(store.Get(Vin, "climate_on")));

        // Bilen vågner og sender sit gamle snapshot: klima stadig off.
        store.Apply(Vin, "climate_on", JsonValue.Create(false), _time.GetUtcNow());
        Assert.True(Bool(store.Get(Vin, "climate_on")));
        Assert.Equal(ValueSource.Optimistic, store.Get(Vin, "climate_on")!.Source);

        // Lidt efter bekræfter bilen.
        store.Apply(Vin, "climate_on", JsonValue.Create(true), _time.GetUtcNow());
        Assert.True(Bool(store.Get(Vin, "climate_on")));
        Assert.Equal(ValueSource.Stream, store.Get(Vin, "climate_on")!.Source);
        Assert.False(store.HasPending(Vin, "climate_on"));
    }

    [Fact]
    public void Unconfirmed_expectation_expires_back_to_vehicle_state()
    {
        var store = new VehicleStateStore(_time);
        store.Apply(Vin, "locked", JsonValue.Create(true), _time.GetUtcNow());
        store.SetPending(Vin, "locked", JsonValue.Create(false), TimeSpan.FromSeconds(30));
        Assert.False(Bool(store.Get(Vin, "locked")));

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, store.ExpirePending());
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, store.ExpirePending());
        Assert.True(Bool(store.Get(Vin, "locked")));
    }

    [Fact]
    public void Clear_pending_reverts_and_raises_change()
    {
        var store = new VehicleStateStore(_time);
        store.Apply(Vin, "sentry_mode", JsonValue.Create(false), _time.GetUtcNow());
        store.SetPending(Vin, "sentry_mode", JsonValue.Create(true), TimeSpan.FromSeconds(30));

        var changes = new List<StateChange>();
        store.Changed += changes.Add;
        store.ClearPending(Vin, "sentry_mode");

        Assert.False(Bool(store.Get(Vin, "sentry_mode")));
        Assert.Single(changes);
    }

    [Fact]
    public void Custom_confirmation_predicate_is_used()
    {
        var store = new VehicleStateStore(_time);
        store.SetPending(Vin, "charging_state", JsonValue.Create("starting"), TimeSpan.FromSeconds(30),
            v => v?.GetValue<string>() is "starting" or "charging");

        store.Apply(Vin, "charging_state", JsonValue.Create("stopped"), _time.GetUtcNow());
        Assert.True(store.HasPending(Vin, "charging_state"));

        store.Apply(Vin, "charging_state", JsonValue.Create("charging"), _time.GetUtcNow());
        Assert.False(store.HasPending(Vin, "charging_state"));
        Assert.Equal("charging", store.Get(Vin, "charging_state")!.Value!.GetValue<string>());
    }

    [Fact]
    public void Serialize_and_restore_keeps_confirmed_values_only()
    {
        var store = new VehicleStateStore(_time);
        store.Apply(Vin, "battery_level", JsonValue.Create(72.5), _time.GetUtcNow());
        store.SetPending(Vin, "climate_on", JsonValue.Create(true), TimeSpan.FromSeconds(30));

        var restored = new VehicleStateStore(_time);
        restored.Restore(store.Serialize());

        Assert.Equal(72.5, restored.Get(Vin, "battery_level")!.Value!.GetValue<double>());
        Assert.Equal(ValueSource.Restored, restored.Get(Vin, "battery_level")!.Source);
        Assert.Null(restored.Get(Vin, "climate_on"));
    }

    [Fact]
    public void Online_is_not_restored_from_disk_but_last_seen_is()
    {
        var store = new VehicleStateStore(_time);
        store.Apply(Vin, "online", JsonValue.Create(true), _time.GetUtcNow(), ValueSource.Bridge);
        store.Apply(Vin, "last_seen", JsonValue.Create("2026-11-05T12:00:00Z"), _time.GetUtcNow(), ValueSource.Bridge);

        var restored = new VehicleStateStore(_time);
        restored.Restore(store.Serialize());

        Assert.Null(restored.Get(Vin, "online"));
        Assert.NotNull(restored.Get(Vin, "last_seen"));
    }
}
