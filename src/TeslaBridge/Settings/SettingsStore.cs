using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Settings;

/// <summary>One telemetry field as the user wants it: on/off and the minimum interval between two signals.</summary>
public sealed record FieldSetting(bool Enabled, int IntervalSeconds);

/// <summary>Settings changed from the dashboard. Saved in /data/settings.json and survive updates.</summary>
public sealed class BridgeSettings
{
    /// <summary>Only fields the user changed. Everything else follows the defaults shipped with the image.</summary>
    public Dictionary<string, FieldSetting> TelemetryFields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Minutes between snapshots from the Fleet API when the car wakes up. 0 = off.</summary>
    public int SnapshotIntervalMinutes { get; set; } = SettingsStore.DefaultSnapshotMinutes;

    /// <summary>What the dashboard's Car page shows: "builtin" (the bundled Model Y), "custom" (your upload) or "drawing".</summary>
    public string CarView { get; set; } = SettingsStore.DefaultCarView;

    /// <summary>ESP32 per VIN, set on the dashboard. Wins over Bridge__Vehicles__N__Ble__* from .env; an empty URL turns it off.</summary>
    public Dictionary<string, BleSetting> Ble { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Where the ESP32 for one car is. Password is kept as entered (the file is only readable by the bridge's user).</summary>
public sealed record BleSetting(string Url, string? Username = null, string? Password = null);

/// <summary>Reads and writes <see cref="BridgeSettings"/>; thread-safe. Invalid values are rejected on update.</summary>
public sealed class SettingsStore
{
    public const int DefaultSnapshotMinutes = 60;
    public const string DefaultCarView = "builtin";
    public static readonly string[] CarViews = ["builtin", "custom", "drawing"];
    public const int MinInterval = 1;
    public const int MaxInterval = 86_400;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string? _file;
    private readonly string _defaultsFile;
    private readonly Lock _lock = new();
    private BridgeSettings _current;
    private Dictionary<string, int>? _defaults;

    public SettingsStore(IOptions<BridgeOptions> options)
        : this(Path.Combine(Path.GetDirectoryName(options.Value.StateFile) ?? "/data", "settings.json"), options.Value.Telemetry.FieldsFile) { }

    /// <summary>file = null: in-memory only (defaults, nothing saved).</summary>
    internal SettingsStore(string? file, string defaultsFile)
    {
        _file = file;
        _defaultsFile = defaultsFile;
        _current = Load(file);
    }

    public event Action? Changed;

    public BridgeSettings Current { get { lock (_lock) return Clone(_current); } }

    /// <summary>The fields and intervals shipped with the image (telemetry-fields.json).</summary>
    public IReadOnlyDictionary<string, int> Defaults
    {
        get
        {
            lock (_lock)
            {
                if (_defaults is not null) return _defaults;
                var doc = JsonNode.Parse(File.ReadAllText(_defaultsFile))?["fields"] as JsonObject
                    ?? throw new InvalidOperationException($"{_defaultsFile} has no 'fields'");
                return _defaults = doc.ToDictionary(kv => kv.Key,
                    kv => kv.Value?["interval_seconds"] is JsonValue v && v.TryGetValue(out int i) ? i : 60, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>What is sent to the car: defaults with the user's changes applied, disabled fields left out.</summary>
    public JsonObject EffectiveFields()
    {
        var s = Current;
        var result = new JsonObject();
        foreach (var (field, interval) in Defaults)
        {
            var f = s.TelemetryFields.TryGetValue(field, out var o) ? o : new FieldSetting(true, interval);
            if (f.Enabled) result[field] = new JsonObject { ["interval_seconds"] = f.IntervalSeconds };
        }
        return result;
    }

    /// <summary>Applies changes. Fields set back to their default are removed from the file.</summary>
    public BridgeSettings Update(IReadOnlyDictionary<string, FieldSetting>? fields, int? snapshotMinutes, string? carView = null)
    {
        if (carView is not null && !CarViews.Contains(carView)) throw new ArgumentException($"car_view must be one of {string.Join(", ", CarViews)}");
        var defaults = Defaults;
        if (fields is not null)
            foreach (var (name, f) in fields)
            {
                if (!defaults.ContainsKey(name)) throw new ArgumentException($"Unknown telemetry field '{name}'");
                if (f.IntervalSeconds is < MinInterval or > MaxInterval)
                    throw new ArgumentException($"{name}: interval must be between {MinInterval} and {MaxInterval} seconds");
            }
        if (snapshotMinutes is < 0 or > 24 * 60) throw new ArgumentException("Snapshot interval must be between 0 and 1440 minutes");

        BridgeSettings saved;
        lock (_lock)
        {
            var next = Clone(_current);
            if (fields is not null)
                foreach (var (name, f) in fields)
                {
                    if (f.Enabled && f.IntervalSeconds == defaults[name]) next.TelemetryFields.Remove(name);
                    else next.TelemetryFields[name] = f;
                }
            if (snapshotMinutes is { } m) next.SnapshotIntervalMinutes = m;
            if (carView is not null) next.CarView = carView;
            Save(_file, next);
            _current = next;
            saved = Clone(next);
        }
        Changed?.Invoke();
        return saved;
    }

    /// <summary>Back to the default telemetry fields and snapshot interval. The car view is kept – it isn't about the car's data.</summary>
    public BridgeSettings Reset()
    {
        lock (_lock)
        {
            _current = new BridgeSettings { CarView = _current.CarView, Ble = Clone(_current).Ble };
            Save(_file, _current);
        }
        Changed?.Invoke();
        return Current;
    }

    private static BridgeSettings Load(string? file)
    {
        try
        {
            if (file is not null && File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(file), Json) ?? new BridgeSettings();
                s.TelemetryFields = new Dictionary<string, FieldSetting>(s.TelemetryFields ?? [], StringComparer.Ordinal);
                s.Ble = new Dictionary<string, BleSetting>(s.Ble ?? [], StringComparer.OrdinalIgnoreCase);
                return s;
            }
        }
        catch (Exception) { /* a broken file must not stop the bridge – fall back to defaults */ }
        return new BridgeSettings();
    }

    private static void Save(string? file, BridgeSettings s)
    {
        if (file is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json));
        File.Move(tmp, file, overwrite: true);
    }

    private static BridgeSettings Clone(BridgeSettings s) => new()
    {
        TelemetryFields = new Dictionary<string, FieldSetting>(s.TelemetryFields, StringComparer.Ordinal),
        SnapshotIntervalMinutes = s.SnapshotIntervalMinutes,
        CarView = CarViews.Contains(s.CarView) ? s.CarView : DefaultCarView,
        Ble = new Dictionary<string, BleSetting>(s.Ble, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Sets (or with null: forgets, so .env applies again) the ESP32 for one car.</summary>
    public BridgeSettings SetBle(string vin, BleSetting? ble)
    {
        if (ble is { Url.Length: > 0 } && !(Uri.TryCreate(ble.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
            throw new ArgumentException("The ESP32 address must look like http://192.168.1.50");
        lock (_lock)
        {
            var next = Clone(_current);
            if (ble is null) next.Ble.Remove(vin); else next.Ble[vin] = ble;
            Save(_file, next);
            _current = next;
        }
        Changed?.Invoke();
        return Current;
    }
}
