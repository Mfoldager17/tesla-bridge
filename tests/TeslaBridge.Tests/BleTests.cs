using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TeslaBridge.Ble;
using TeslaBridge.Commands;
using TeslaBridge.Options;
using TeslaBridge.Settings;
using TeslaBridge.State;
using TeslaBridge.Usage;

namespace TeslaBridge.Tests;

/// <summary>A fake ESP32: /events streams whatever the test writes; POSTs are recorded and answered 200.</summary>
internal sealed class FakeEsp : HttpMessageHandler
{
    private readonly Pipe _pipe = new();
    public List<string> Posts { get; } = [];
    public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;

    public async Task SendStateAsync(string id, string state, object? value)
    {
        var data = new JsonObject { ["id"] = id, ["state"] = state, ["value"] = value is null ? null : JsonValue.Create(value) };
        await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes($"event: state\ndata: {data.ToJsonString()}\n\n"));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/events")
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_pipe.Reader.AsStream()) });
        lock (Posts) Posts.Add(Uri.UnescapeDataString(req.RequestUri!.PathAndQuery));
        return Task.FromResult(new HttpResponseMessage(PostStatus));
    }
}

public class BleTests
{
    private const string Vin = "VIN1";

    private static async Task Until(Func<bool> cond)
    {
        for (var i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    private static (BleService ble, VehicleStateStore store, FakeEsp esp, UsageTracker usage, SettingsStore settings) Create(string url = "http://192.168.1.50")
    {
        var o = new BridgeOptions();
        o.Vehicles.Add(new VehicleOptions { Vin = Vin, Name = "Model Y", Ble = new BleOptions { Url = url } });
        var store = new VehicleStateStore();
        var settings = new SettingsStore(null, Path.Combine(AppContext.BaseDirectory, "defaults", "telemetry-fields.json"));
        var esp = new FakeEsp();
        var ble = new BleService(Microsoft.Extensions.Options.Options.Create(o), settings, store, NullLoggerFactory.Instance, TimeProvider.System)
        {
            HandlerFactory = (_, _, _) => esp,
        };
        return (ble, store, esp, new UsageTracker(TimeProvider.System), settings);
    }

    [Fact]
    public async Task Reads_both_id_formats_and_finds_entities_by_name()
    {
        var (ble, _, esp, _, _) = Create();
        await ble.StartAsync(default);
        await esp.SendStateAsync("switch/Climate", "OFF", false);                 // ESPHome 2026.8+
        await esp.SendStateAsync("number-charging_limit", "80 %", 80);            // older ESPHome
        var d = ble.Device(Vin)!;
        await Until(() => d.Find("number", "Charging limit") is not null && d.Find("switch", "climate") is not null);

        Assert.Null(await d.ActAsync("switch", "Climate", "turn_on", null, default));
        Assert.Null(await d.ActAsync("number", "Charging limit", "set", "value=90", default));
        Assert.Equal(["/switch/Climate/turn_on", "/number/charging_limit/set?value=90"], esp.Posts);
        Assert.NotNull(await d.ActAsync("button", "Nope", "press", null, default));   // unknown entity: not posted
        await ble.DisposeAsync();
    }

    [Fact]
    public async Task Car_in_range_follows_presence_and_state_flows_into_the_store()
    {
        var (ble, store, esp, _, _) = Create();
        await ble.StartAsync(default);
        var d = ble.Device(Vin)!;
        await esp.SendStateAsync("sensor/Charge level", "64 %", 64);              // not in range yet: ignored
        await Until(() => d.Find("sensor", "Charge level") is not null);
        Assert.False(ble.InRange(Vin));
        Assert.Null(store.Get(Vin, "battery_level"));

        await esp.SendStateAsync("binary_sensor/BLE Status", "ON", true);
        await Until(() => ble.InRange(Vin));
        Assert.True(store.Get(Vin, "ble_connected")!.Value!.GetValue<bool>());

        await esp.SendStateAsync("sensor/Charge level", "65 %", 65);
        await esp.SendStateAsync("sensor/Odometer", "1000.00 mi", 1000.0);
        await esp.SendStateAsync("binary_sensor/Doors", "OFF", false);            // "Doors" on = unlocked
        await esp.SendStateAsync("text_sensor/Charging state", "Charging", "Charging");
        await esp.SendStateAsync("sensor/Range", "NA", double.NaN.ToString());    // unknown: must not overwrite
        await Until(() => store.Get(Vin, "charging_state") is not null);

        Assert.Equal(65, store.Get(Vin, "battery_level")!.Value!.GetValue<double>());
        Assert.Equal(ValueSource.Ble, store.Get(Vin, "battery_level")!.Source);
        Assert.Equal(1609.3, store.Get(Vin, "odometer")!.Value!.GetValue<double>());
        Assert.True(store.Get(Vin, "locked")!.Value!.GetValue<bool>());
        Assert.Equal("charging", store.Get(Vin, "charging_state")!.Value!.GetValue<string>());
        Assert.Null(store.Get(Vin, "est_range"));

        await esp.SendStateAsync("binary_sensor/BLE Status", "OFF", false);
        await Until(() => !ble.InRange(Vin));
        Assert.False(store.Get(Vin, "ble_connected")!.Value!.GetValue<bool>());
        await ble.DisposeAsync();
    }

    [Fact]
    public async Task Transport_sends_supported_commands_and_counts_them_as_free()
    {
        var (ble, _, esp, usage, _) = Create();
        await ble.StartAsync(default);
        var d = ble.Device(Vin)!;
        foreach (var (id, st) in new[] { ("binary_sensor/BLE Status", "ON"), ("lock/Lock car", "LOCKED"), ("button/Wake up", ""), ("cover/Boot", "CLOSED") })
            await esp.SendStateAsync(id, st, null);
        await Until(() => d.Find("cover", "Boot") is not null);

        var t = new BleTransport(ble, usage, NullLogger<BleTransport>.Instance);
        Assert.True(await t.CanReachAsync(Vin, default));
        Assert.True((await t.SendAsync(Vin, new Cmd.Unlock(), default)).IsSuccess);
        Assert.True((await t.SendAsync(Vin, new Cmd.ToggleTrunk(true), default)).IsSuccess);
        Assert.True((await t.WakeAsync(Vin, default)).IsSuccess);
        var seat = await t.SendAsync(Vin, new Cmd.SetSeatHeater(Seat.FrontLeft, 3), default);
        Assert.Equal(BleTransport.NotOverBle, seat.Reason);

        Assert.Equal(["/lock/Lock car/unlock", "/cover/Boot/open", "/button/Wake up/press"], esp.Posts);
        var snap = usage.Snapshot();
        Assert.Equal(3, snap.BleByName!.Values.Sum());
        Assert.Equal(0, snap.TotalUsd);
        Assert.True(snap.BleSavedUsd > 0.02);
        await ble.DisposeAsync();
    }

    [Fact]
    public async Task Dashboard_setting_wins_over_env_and_an_empty_url_turns_it_off()
    {
        var (ble, _, _, _, settings) = Create();
        await ble.StartAsync(default);
        Assert.Equal("env", ble.Status(Vin).Source);

        settings.SetBle(Vin, new BleSetting("http://10.0.0.9"));
        Assert.Equal("dashboard", ble.Status(Vin).Source);
        Assert.Equal("http://10.0.0.9", ble.Status(Vin).Url);

        settings.SetBle(Vin, new BleSetting(""));
        Assert.False(ble.Status(Vin).Configured);

        settings.SetBle(Vin, null);
        Assert.Equal("env", ble.Status(Vin).Source);
        Assert.Throws<ArgumentException>(() => settings.SetBle(Vin, new BleSetting("192.168.1.50")));
        await ble.DisposeAsync();
    }

    [Fact]
    public void Commands_map_to_esphome_entities()
    {
        Assert.Equal(new EspAction("number", "Charging amps", "set", "value=16"), TeslaBleMap.ActionFor(new Cmd.SetChargingAmps(16)));
        Assert.Equal(new EspAction("switch", "Charger", "turn_off"), TeslaBleMap.ActionFor(new Cmd.StopCharging()));
        Assert.Equal(new EspAction("select", "Cabin overheat protection", "set", "option=Fan"), TeslaBleMap.ActionFor(new Cmd.SetCabinOverheatProtection("fan_only")));
        Assert.Equal(new EspAction("cover", "Vent", "open"), TeslaBleMap.ActionFor(new Cmd.VentWindows(55, 10)));
        Assert.Null(TeslaBleMap.ActionFor(new Cmd.SetGuestMode(true)));
        Assert.Equal("no_power", TeslaBleMap.ChargingState("NoPower"));
        // "S" + 16 hex characters + "C", as Tesla's vehicle-command derives it from the VIN
        var name = TeslaBridge.Web.BleApi.CarBleName("5YJ3E1EA4JF000001");
        Assert.Matches("^S[0-9a-f]{16}C$", name);
    }
}

public class BleRoutingTests
{
    private sealed class Fake(string name, int priority, bool reachable, Func<VehicleCommand, CommandResult> send, Action? onWake = null) : ICommandTransport
    {
        public string Name => name;
        public int Priority => priority;
        public List<string> Log { get; } = [];
        public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(reachable);
        public Task<CommandResult> SendAsync(string vin, VehicleCommand c, CancellationToken ct) { Log.Add("send " + c.Name); return Task.FromResult(send(c)); }
        public Task<CommandResult> WakeAsync(string vin, CancellationToken ct) { Log.Add("wake"); onWake?.Invoke(); return Task.FromResult(CommandResult.Ok()); }
    }

    private static CommandService Svc(params ICommandTransport[] t) =>
        new(new CommandRouter(t), new VehicleStateStore(),
            Microsoft.Extensions.Options.Options.Create(new BridgeOptions { Commands = new CommandOptions { WakeTimeoutSeconds = 2 } }),
            TimeProvider.System, NullLogger<CommandService>.Instance) { RetryDelay = TimeSpan.FromMilliseconds(10) };

    [Fact]
    public async Task Bluetooth_first_then_fleet_for_what_it_cant_do()
    {
        var ble = new Fake("ble", 0, true, c => TeslaBleMap.ActionFor(c) is null ? CommandResult.Failed(BleTransport.NotOverBle) : CommandResult.Ok());
        var fleet = new Fake("fleet-api", 100, true, _ => CommandResult.Ok());
        var svc = Svc(ble, fleet);

        await svc.ExecuteAsync("V", new Cmd.ClimateOn());
        Assert.Equal(["send ClimateOn"], ble.Log);
        Assert.Empty(fleet.Log);

        await svc.ExecuteAsync("V", new Cmd.SetGuestMode(true));
        Assert.Equal(["send SetGuestMode"], fleet.Log);
    }

    [Fact]
    public async Task Car_out_of_range_uses_fleet_only()
    {
        var ble = new Fake("ble", 0, false, _ => CommandResult.Ok());
        var fleet = new Fake("fleet-api", 100, true, _ => CommandResult.Ok());
        await Svc(ble, fleet).ExecuteAsync("V", new Cmd.Lock());
        Assert.Empty(ble.Log);
        Assert.Equal(["send Lock"], fleet.Log);
    }

    [Fact]
    public async Task Sleeping_car_is_woken_over_bluetooth_for_a_fleet_only_command()
    {
        var asleep = true;
        var ble = new Fake("ble", 0, true, _ => CommandResult.Failed(BleTransport.NotOverBle), () => asleep = false);
        var fleet = new Fake("fleet-api", 100, true, _ => asleep ? CommandResult.Asleep() : CommandResult.Ok());

        Assert.True((await Svc(ble, fleet).ExecuteAsync("V", new Cmd.SetValetMode(true))).IsSuccess);
        Assert.Contains("wake", ble.Log);             // the free wake was used …
        Assert.DoesNotContain("wake", fleet.Log);     // … not the paid one
    }
}

public class CabinComfortTests
{
    private sealed class Car : ICommandTransport
    {
        public bool ClimateOn;
        public List<string> Sent { get; } = [];
        public string Name => "fleet-api";
        public int Priority => 100;
        public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(true);
        public Task<CommandResult> WakeAsync(string vin, CancellationToken ct) => Task.FromResult(CommandResult.Ok());
        public Task<CommandResult> SendAsync(string vin, VehicleCommand c, CancellationToken ct)
        {
            Sent.Add(c.Name);
            if (c is Cmd.ClimateOn) { ClimateOn = true; return Task.FromResult(CommandResult.Ok()); }
            return Task.FromResult(ClimateOn ? CommandResult.Ok()
                : CommandResult.Refused("car could not execute command: cabin comfort remote settings not enabled"));
        }
    }

    [Fact]
    public async Task Seat_heat_with_the_climate_off_turns_the_climate_on_first()
    {
        var car = new Car();
        var store = new VehicleStateStore();
        var svc = new CommandService(new CommandRouter([car]), store,
            Microsoft.Extensions.Options.Options.Create(new BridgeOptions()), TimeProvider.System, NullLogger<CommandService>.Instance)
            { RetryDelay = TimeSpan.FromMilliseconds(5) };

        var r = await svc.ExecuteAsync("V", new Cmd.SetAutoSeatClimate(1, true));
        Assert.True(r.IsSuccess);
        Assert.Equal(["SetAutoSeatClimate", "ClimateOn", "SetAutoSeatClimate"], car.Sent);
        Assert.True(store.Get("V", "climate_on")!.Value!.GetValue<bool>());
    }
}
