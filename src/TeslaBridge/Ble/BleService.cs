using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;
using TeslaBridge.Settings;
using TeslaBridge.State;

namespace TeslaBridge.Ble;

/// <summary>What the dashboard shows about the ESP32 for one car.</summary>
public sealed record BleStatus(
    string Vin,
    bool Configured,
    string? Url,
    string? Source,
    bool EspConnected,
    bool CarInRange,
    bool? CarAsleep,
    double? Rssi,
    string? EspVersion,
    int Entities,
    bool LooksLikeTeslaBle,
    string? LastError,
    DateTimeOffset? LastEventAt);

/// <summary>
/// Keeps one <see cref="EspHomeDevice"/> per car that has an ESP32 (from .env or the dashboard), follows
/// whether the car is within Bluetooth range, and writes what the ESP32 reads from the car into the state
/// store – for free, while the car is near. Commands go through <see cref="BleTransport"/>.
/// </summary>
public sealed class BleService(
    IOptions<BridgeOptions> options,
    SettingsStore settings,
    VehicleStateStore store,
    ILoggerFactory logs,
    TimeProvider time) : IHostedService, IAsyncDisposable
{
    private sealed record Config(string Url, string? Username, string? Password, string Source);

    private sealed class Slot(Config config, EspHomeDevice device)
    {
        public Config Config { get; } = config;
        public EspHomeDevice Device { get; } = device;
        public bool? LastInRange;
    }

    private readonly ILogger _log = logs.CreateLogger<BleService>();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>For tests: build devices on a fake HTTP handler.</summary>
    internal Func<Uri, string?, string?, HttpMessageHandler?>? HandlerFactory { get; set; }

    public Task StartAsync(CancellationToken ct)
    {
        settings.Changed += Reconcile;
        Reconcile();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        settings.Changed -= Reconcile;
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Slot[] all;
        lock (_lock) { all = _slots.Values.ToArray(); _slots.Clear(); }
        foreach (var s in all) await s.Device.DisposeAsync();
    }

    public EspHomeDevice? Device(string vin)
    {
        lock (_lock) return _slots.TryGetValue(vin, out var s) ? s.Device : null;
    }

    /// <summary>Can a command reach the car over Bluetooth right now?</summary>
    public bool InRange(string vin) => Device(vin) is { Connected: true } d && Present(d);

    private static bool Present(EspHomeDevice d) =>
        EspHomeDevice.Bool(d.Find("binary_sensor", TeslaBleMap.Presence)) == true;

    public BleStatus Status(string vin)
    {
        Slot? s;
        lock (_lock) _slots.TryGetValue(vin, out s);
        if (s is null) return new BleStatus(vin, false, null, null, false, false, null, null, null, 0, false, null, null);
        var d = s.Device;
        var entities = d.Entities();
        return new BleStatus(vin, true, s.Config.Url, s.Config.Source, d.Connected, d.Connected && Present(d),
            EspHomeDevice.Bool(d.Find("binary_sensor", TeslaBleMap.Asleep)),
            EspHomeDevice.Number(d.Find("sensor", TeslaBleMap.Signal)),
            EspHomeDevice.Text(d.Find("text_sensor", TeslaBleMap.Version)),
            entities.Count,
            d.Find("button", TeslaBleMap.WakeButton) is not null && d.Find("lock", "Lock car") is not null,
            d.LastError, d.LastEventAt);
    }

    /// <summary>(Re)creates devices so they match .env + the dashboard's settings.</summary>
    public void Reconcile()
    {
        var wanted = new Dictionary<string, Config>(StringComparer.OrdinalIgnoreCase);
        var dash = settings.Current.Ble;
        foreach (var v in options.Value.Vehicles)
        {
            if (string.IsNullOrWhiteSpace(v.Vin)) continue;
            Config? c = dash.TryGetValue(v.Vin, out var d)
                ? (string.IsNullOrWhiteSpace(d.Url) ? null : new Config(d.Url.Trim(), d.Username, d.Password, "dashboard"))
                : string.IsNullOrWhiteSpace(v.Ble.Url) ? null : new Config(v.Ble.Url.Trim(), v.Ble.Username, v.Ble.Password, "env");
            if (c is not null) wanted[v.Vin] = c;
        }

        var stale = new List<EspHomeDevice>();
        lock (_lock)
        {
            foreach (var vin in _slots.Keys.ToArray())
                if (!wanted.TryGetValue(vin, out var c) || c != _slots[vin].Config)
                {
                    stale.Add(_slots[vin].Device);
                    _slots.Remove(vin);
                }
            foreach (var (vin, c) in wanted)
            {
                if (_slots.ContainsKey(vin)) continue;
                if (!Uri.TryCreate(c.Url, UriKind.Absolute, out var uri)) { _log.LogWarning("ESP32 address for {Vin} isn't a URL: {Url}", vin, c.Url); continue; }
                var device = new EspHomeDevice(uri, c.Username, c.Password, logs.CreateLogger<EspHomeDevice>(), time,
                    HandlerFactory?.Invoke(uri, c.Username, c.Password));
                var slot = new Slot(c, device);
                var v = vin;
                device.StateChanged += e => OnState(v, slot, e);
                device.ConnectionChanged += on =>
                {
                    _log.LogInformation("ESP32 {Url} for {Vin} {State}", c.Url, v, on ? "connected" : "disconnected");
                    PublishInRange(v, slot);
                };
                _slots[vin] = slot;
                device.Start();
                _log.LogInformation("Bluetooth for {Vin} via ESP32 at {Url} ({Source})", vin, c.Url, c.Source);
            }
        }
        foreach (var d in stale) _ = d.DisposeAsync().AsTask();
        foreach (var v in options.Value.Vehicles)
            if (!wanted.ContainsKey(v.Vin) && store.GetConfirmed(v.Vin, "ble_connected") is { Value: not null })
                store.Apply(v.Vin, "ble_connected", null, time.GetUtcNow(), ValueSource.Bridge);
    }

    private void OnState(string vin, Slot slot, EspEntity e)
    {
        PublishInRange(vin, slot);
        // Only trust the ESP32's car values while it can actually see the car – otherwise they may be old.
        if (!Present(slot.Device)) return;
        var now = time.GetUtcNow();
        foreach (var (key, value) in TeslaBleMap.StateFor(e))
            store.Apply(vin, key, value, now, ValueSource.Ble);
    }

    private void PublishInRange(string vin, Slot slot)
    {
        var inRange = slot.Device.Connected && Present(slot.Device);
        if (slot.LastInRange == inRange) return;
        slot.LastInRange = inRange;
        store.Apply(vin, "ble_connected", JsonValue.Create(inRange), time.GetUtcNow(), ValueSource.Bridge);
    }
}
