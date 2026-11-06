using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeslaBridge.Usage;

public enum ApiCallKind { Command, Wake, Data }

/// <summary>Forbrug for én kalendermåned (UTC), som Tesla afregner det.</summary>
public sealed class MonthUsage
{
    [JsonPropertyName("month")] public string Month { get; set; } = "";
    [JsonPropertyName("signals")] public Dictionary<string, long> Signals { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("commands")] public Dictionary<string, long> Commands { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("wakes")] public long Wakes { get; set; }
    [JsonPropertyName("data")] public Dictionary<string, long> Data { get; set; } = new(StringComparer.Ordinal);
}

public sealed record UsageCategory(string Key, string Name, long Count, double CostUsd, string Rate);

public sealed record UsageSnapshot(
    string Month,
    DateTimeOffset MonthStart,
    DateTimeOffset MonthEnd,
    IReadOnlyList<UsageCategory> Categories,
    double TotalUsd,
    double? ProjectedUsd,
    double CreditUsd,
    IReadOnlyDictionary<string, long> SignalsByField,
    IReadOnlyDictionary<string, long> CommandsByName,
    IReadOnlyDictionary<string, long> DataByName,
    IReadOnlyList<(string Month, double TotalUsd)> History);

/// <summary>
/// Tæller alt, bridgen sender til og modtager fra Tesla, pr. kalendermåned, efter Teslas Fleet API-takster.
/// Overlever genstart (gemmes af UsageService). Det, der går uden om bridgen (fx tesla_fleet i HA), ses ikke.
/// </summary>
public sealed class UsageTracker(TimeProvider time)
{
    public const double SignalsPerDollar = 150_000;
    public const double CommandsPerDollar = 1_000;
    public const double WakesPerDollar = 50;
    public const double DataPerDollar = 500;
    public const double MonthlyCreditUsd = 10;
    private const int HistoryMonths = 12;

    private readonly Lock _lock = new();
    private MonthUsage _current = new();
    private List<MonthUsage> _history = [];
    private volatile bool _dirty;

    public bool IsDirty => _dirty;

    public void CountSignal(string field)
    {
        lock (_lock)
        {
            Roll();
            Inc(_current.Signals, field);
        }
        _dirty = true;
    }

    public void CountApiCall(ApiCallKind kind, string name)
    {
        lock (_lock)
        {
            Roll();
            switch (kind)
            {
                case ApiCallKind.Command: Inc(_current.Commands, name); break;
                case ApiCallKind.Wake: _current.Wakes++; break;
                default: Inc(_current.Data, name); break;
            }
        }
        _dirty = true;
    }

    /// <summary>Klassificerer et Fleet API-kald ud fra stien, som Tesla afregner det.</summary>
    public static (ApiCallKind Kind, string Name) Classify(string path)
    {
        var clean = path.Split('?')[0].TrimEnd('/');
        var name = clean[(clean.LastIndexOf('/') + 1)..];
        if (clean.Contains("/command/", StringComparison.Ordinal)) return (ApiCallKind.Command, name);
        if (name == "wake_up") return (ApiCallKind.Wake, name);
        return (ApiCallKind.Data, name);
    }

    public UsageSnapshot Snapshot()
    {
        lock (_lock)
        {
            Roll();
            var now = time.GetUtcNow();
            var start = MonthStart(now);
            var end = start.AddMonths(1);

            var c = _current;
            var signals = c.Signals.Values.Sum();
            var commands = c.Commands.Values.Sum();
            var data = c.Data.Values.Sum();
            var categories = new List<UsageCategory>
            {
                new("signals", "Signaler (stream)", signals, signals / SignalsPerDollar, "$1 / 150.000"),
                new("commands", "Kommandoer", commands, commands / CommandsPerDollar, "$1 / 1.000"),
                new("wakes", "Wakes", c.Wakes, c.Wakes / WakesPerDollar, "$1 / 50"),
                new("data", "Data-forespørgsler", data, data / DataPerDollar, "$1 / 500"),
            };
            var total = categories.Sum(x => x.CostUsd);

            // Fremskrivning giver først mening efter et døgn.
            var elapsed = now - start;
            double? projected = elapsed >= TimeSpan.FromDays(1)
                ? total * ((end - start).TotalSeconds / elapsed.TotalSeconds)
                : null;

            return new UsageSnapshot(
                c.Month, start, end, categories, total, projected, MonthlyCreditUsd,
                Sorted(c.Signals), Sorted(c.Commands), Sorted(c.Data),
                _history.Select(h => (h.Month, Cost(h))).ToList());
        }
    }

    // ---------- persistens ----------

    private sealed class Dto
    {
        [JsonPropertyName("current")] public MonthUsage Current { get; set; } = new();
        [JsonPropertyName("history")] public List<MonthUsage> History { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string Serialize()
    {
        lock (_lock)
        {
            _dirty = false;
            return JsonSerializer.Serialize(new Dto { Current = _current, History = _history }, JsonOpts);
        }
    }

    public void Restore(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, JsonOpts);
        if (dto is null) return;
        lock (_lock)
        {
            _current = dto.Current;
            _history = dto.History;
            Roll();
        }
    }

    public void MarkDirty() => _dirty = true;

    // ---------- hjælpere ----------

    private void Roll()
    {
        var month = MonthKey(time.GetUtcNow());
        if (_current.Month == month) return;
        if (!string.IsNullOrEmpty(_current.Month))
        {
            _history.Insert(0, _current);
            if (_history.Count > HistoryMonths) _history.RemoveRange(HistoryMonths, _history.Count - HistoryMonths);
        }
        _current = new MonthUsage { Month = month };
        _dirty = true;
    }

    private static double Cost(MonthUsage m) =>
        m.Signals.Values.Sum() / SignalsPerDollar + m.Commands.Values.Sum() / CommandsPerDollar +
        m.Wakes / WakesPerDollar + m.Data.Values.Sum() / DataPerDollar;

    private static void Inc(Dictionary<string, long> d, string key) =>
        d[key] = d.TryGetValue(key, out var n) ? n + 1 : 1;

    private static Dictionary<string, long> Sorted(Dictionary<string, long> d) =>
        d.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static string MonthKey(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM");

    private static DateTimeOffset MonthStart(DateTimeOffset t) => new(t.UtcDateTime.Year, t.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
}
