using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeslaBridge.State;

public sealed record StateChange(string Vin, string Key, FieldValue? Effective);

/// <summary>
/// Al kendt tilstand pr. bil og felt. Holder adskilt hvad bilen har bekræftet,
/// og hvad vi forventer efter en kommando. "Effektiv" værdi = forventet (hvis aktiv) ellers bekræftet.
/// Trådsikker.
/// </summary>
public sealed class VehicleStateStore
{
    private sealed class Entry
    {
        public FieldValue? Confirmed;
        public PendingValue? Pending;
    }

    private readonly Dictionary<string, Dictionary<string, Entry>> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    public VehicleStateStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Kaldes (uden lås) hver gang en effektiv værdi ændres.</summary>
    public event Action<StateChange>? Changed;

    /// <summary>Kaldes når en bekræftet værdi ændres (bruges til at gemme på disk).</summary>
    public event Action? ConfirmedChanged;

    /// <summary>A field started or stopped waiting for the car to confirm a command (argument: VIN).</summary>
    public event Action<string>? PendingChanged;

    /// <summary>The fields of one car that are waiting for the car to confirm a command.</summary>
    public IReadOnlyList<string> PendingKeys(string vin)
    {
        lock (_lock)
            return _vehicles.TryGetValue(vin, out var f) ? f.Where(kv => kv.Value.Pending is not null).Select(kv => kv.Key).Order().ToList() : [];
    }

    public IReadOnlyCollection<string> Vins
    {
        get { lock (_lock) return _vehicles.Keys.ToArray(); }
    }

    /// <summary>Ny værdi fra bilen.</summary>
    public void Apply(string vin, string key, JsonNode? value, DateTimeOffset at, ValueSource source = ValueSource.Stream)
    {
        StateChange? change;
        var confirmed = false;
        lock (_lock)
        {
            var entry = GetEntry(vin, key);
            var before = EffectiveOf(entry);

            entry.Confirmed = new FieldValue(value?.DeepClone(), at, source);

            // Bekræfter streamen det vi forventede? Så er vi færdige med at vente.
            // Hvis ikke (fx gammelt snapshot lige efter opvågning) holder vi den forventede værdi til den udløber.
            if (entry.Pending is { } p && p.IsConfirmedBy(value))
            {
                entry.Pending = null;
                confirmed = true;
            }

            change = Diff(vin, key, before, EffectiveOf(entry));
        }

        ConfirmedChanged?.Invoke();
        if (change is not null) Changed?.Invoke(change);
        if (confirmed) PendingChanged?.Invoke(vin);
    }

    /// <summary>Forvent en værdi efter en kommando.</summary>
    public void SetPending(string vin, string key, JsonNode? value, TimeSpan timeout, Func<JsonNode?, bool>? confirmedBy = null)
    {
        StateChange? change;
        lock (_lock)
        {
            var entry = GetEntry(vin, key);
            var before = EffectiveOf(entry);
            var now = _time.GetUtcNow();
            var expected = value?.DeepClone();
            entry.Pending = new PendingValue(expected, now, now + timeout,
                confirmedBy ?? (v => JsonNode.DeepEquals(v, expected)));
            change = Diff(vin, key, before, EffectiveOf(entry));
        }

        if (change is not null) Changed?.Invoke(change);
        PendingChanged?.Invoke(vin);
    }

    /// <summary>Opgiv en forventet værdi (fx fordi kommandoen fejlede) og vis den bekræftede igen.</summary>
    public void ClearPending(string vin, string key)
    {
        StateChange? change = null;
        var cleared = false;
        lock (_lock)
        {
            if (_vehicles.TryGetValue(vin, out var fields) && fields.TryGetValue(key, out var entry) && entry.Pending is not null)
            {
                var before = EffectiveOf(entry);
                entry.Pending = null;
                change = Diff(vin, key, before, EffectiveOf(entry));
                cleared = true;
            }
        }

        if (change is not null) Changed?.Invoke(change);
        if (cleared) PendingChanged?.Invoke(vin);
    }

    /// <summary>Lad udløbne forventninger falde tilbage til bilens bekræftede værdi.</summary>
    public int ExpirePending()
    {
        var now = _time.GetUtcNow();
        var changes = new List<StateChange>();
        var vins = new HashSet<string>();
        lock (_lock)
        {
            foreach (var (vin, fields) in _vehicles)
            foreach (var (key, entry) in fields)
            {
                if (entry.Pending is { } p && p.ExpiresAt <= now)
                {
                    var before = EffectiveOf(entry);
                    entry.Pending = null;
                    vins.Add(vin);
                    if (Diff(vin, key, before, EffectiveOf(entry)) is { } c) changes.Add(c);
                }
            }
        }

        foreach (var c in changes) Changed?.Invoke(c);
        foreach (var v in vins) PendingChanged?.Invoke(v);
        return changes.Count;
    }

    public FieldValue? Get(string vin, string key)
    {
        lock (_lock)
            return _vehicles.TryGetValue(vin, out var f) && f.TryGetValue(key, out var e) ? EffectiveOf(e) : null;
    }

    public FieldValue? GetConfirmed(string vin, string key)
    {
        lock (_lock)
            return _vehicles.TryGetValue(vin, out var f) && f.TryGetValue(key, out var e) ? e.Confirmed : null;
    }

    public bool HasPending(string vin, string key)
    {
        lock (_lock)
            return _vehicles.TryGetValue(vin, out var f) && f.TryGetValue(key, out var e) && e.Pending is not null;
    }

    /// <summary>Alle effektive værdier for én bil.</summary>
    public IReadOnlyDictionary<string, FieldValue> Snapshot(string vin)
    {
        lock (_lock)
        {
            if (!_vehicles.TryGetValue(vin, out var fields)) return new Dictionary<string, FieldValue>();
            return fields
                .Select(kv => (kv.Key, Value: EffectiveOf(kv.Value)))
                .Where(x => x.Value is not null)
                .ToDictionary(x => x.Key, x => x.Value!);
        }
    }

    // ---------- persistens (kun bekræftede værdier) ----------

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string Serialize()
    {
        lock (_lock)
        {
            var dto = _vehicles.ToDictionary(
                v => v.Key,
                v => v.Value.Where(f => f.Value.Confirmed is not null)
                            .ToDictionary(f => f.Key, f => f.Value.Confirmed!));
            return JsonSerializer.Serialize(dto, JsonOpts);
        }
    }

    /// <summary>Indlæs sidst kendte værdier, så intet står "unknown" efter genstart.</summary>
    public void Restore(string json)
    {
        var dto = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, FieldValue>>>(json, JsonOpts);
        if (dto is null) return;
        lock (_lock)
        {
            foreach (var (vin, fields) in dto)
            foreach (var (key, fv) in fields)
            {
                // "online" gælder kun nu. Fra disk ved vi ikke, om bilen stadig er vågen – den sættes,
                // når bilen sender næste gang. last_seen viser i mellemtiden, hvornår vi sidst hørte fra den.
                if (key == "online") continue;
                var entry = GetEntry(vin, key);
                entry.Confirmed ??= fv with { Source = ValueSource.Restored };
            }
        }
    }

    // ---------- hjælpere ----------

    private Entry GetEntry(string vin, string key)
    {
        if (!_vehicles.TryGetValue(vin, out var fields))
            _vehicles[vin] = fields = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!fields.TryGetValue(key, out var entry))
            fields[key] = entry = new Entry();
        return entry;
    }

    private static FieldValue? EffectiveOf(Entry e) =>
        e.Pending is { } p ? new FieldValue(p.Value, p.SetAt, ValueSource.Optimistic) : e.Confirmed;

    // Vi melder også ændring ved samme værdi, så updated_at i snapshot er frisk.
    // StatePublisher dropper selv identiske retained-værdier.
    private static StateChange? Diff(string vin, string key, FieldValue? before, FieldValue? after) =>
        before is null && after is null ? null : new StateChange(vin, key, after);
}
