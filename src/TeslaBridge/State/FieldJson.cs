using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeslaBridge.State;

/// <summary>
/// The one JSON shape of a field, used everywhere a client sees state – GET /api/state, the live events (SSE) and
/// MQTT's {bridge}/{vin}/json:
/// <code>{ "value": 72.5, "updated_at": "2026-11-13T20:01:02+00:00", "source": "stream" }</code>
/// Locks and covers also get a "{key}_motion" field with in-between steps while a command waits for the car
/// (LOCKING/UNLOCKING, opening/closing), the way Teslemetry shows them in Home Assistant.
/// </summary>
public static class FieldJson
{
    /// <summary>key → (state when true, when false, on the way to true, on the way to false).</summary>
    public static readonly IReadOnlyDictionary<string, (string True, string False, string ToTrue, string ToFalse)> Transitions =
        new Dictionary<string, (string, string, string, string)>
        {
            ["locked"] = ("LOCKED", "UNLOCKED", "LOCKING", "UNLOCKING"),
            ["charge_cable_locked"] = ("LOCKED", "UNLOCKED", "LOCKING", "UNLOCKING"),
            ["frunk_open"] = ("open", "closed", "opening", "closing"),
            ["trunk_open"] = ("open", "closed", "opening", "closing"),
            ["charge_port_open"] = ("open", "closed", "opening", "closing"),
            ["windows_open"] = ("open", "closed", "opening", "closing"),
        };

    public const string MotionSuffix = "_motion";

    public static string Source(ValueSource s) => JsonNamingPolicy.SnakeCaseLower.ConvertName(s.ToString());

    public static JsonObject Field(FieldValue? fv) => new()
    {
        ["value"] = fv?.Value?.DeepClone(),
        ["updated_at"] = fv?.UpdatedAt.ToString("O"),
        ["source"] = fv is null ? null : Source(fv.Source),
    };

    /// <summary>The in-between state of a lock or cover: e.g. "UNLOCKING" while unlocking waits for the car.</summary>
    public static string? Motion(string key, bool pending, JsonNode? effective, JsonNode? confirmed)
    {
        var t = Transitions[key];
        static bool? AsBool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) ? b : null;
        if (pending && AsBool(effective) is { } target && AsBool(confirmed) != target)
            return target ? t.ToTrue : t.ToFalse;
        return AsBool(effective) switch { true => t.True, false => t.False, _ => null };
    }

    /// <summary>The "{key}_motion" field for a lock or cover, as it is right now.</summary>
    public static FieldValue MotionValue(VehicleStateStore store, string vin, string key, DateTimeOffset at)
    {
        var motion = Motion(key, store.HasPending(vin, key), store.Get(vin, key)?.Value, store.GetConfirmed(vin, key)?.Value);
        return new FieldValue(motion is null ? null : JsonValue.Create(motion), at, ValueSource.Bridge);
    }

    /// <summary>All fields of one car, sorted by key, with the motion fields.</summary>
    public static JsonObject Fields(VehicleStateStore store, string vin)
    {
        var fields = new JsonObject();
        foreach (var (key, fv) in store.Snapshot(vin).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            fields[key] = Field(fv);
            if (Transitions.ContainsKey(key))
                fields[key + MotionSuffix] = Field(MotionValue(store, vin, key, fv.UpdatedAt));
        }
        return fields;
    }
}
