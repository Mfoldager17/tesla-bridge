using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TeslaBridge.State;

[JsonConverter(typeof(JsonStringEnumConverter<ValueSource>))]
public enum ValueSource
{
    /// <summary>From the car's telemetry stream.</summary>
    Stream,
    /// <summary>Set by us after a command; waiting for the stream to confirm it.</summary>
    Optimistic,
    /// <summary>Loaded from disk at start (the last known value).</summary>
    Restored,
    /// <summary>Derived or set by the bridge itself (e.g. online/last_seen).</summary>
    Bridge,
    /// <summary>Fetched with vehicle_data, because the stream hadn't sent the field yet.</summary>
    Snapshot,
    /// <summary>Read over Bluetooth by the ESP32 next to the car (free, only when the car is in range).</summary>
    Ble,
}

/// <summary>One value for one field, with when and where it came from.</summary>
public sealed record FieldValue(JsonNode? Value, DateTimeOffset UpdatedAt, ValueSource Source);

/// <summary>
/// An expected value after a command. Kept until the stream confirms it, or until it expires – so an old "off" right
/// after waking up doesn't overwrite "on".
/// </summary>
public sealed record PendingValue(
    JsonNode? Value,
    DateTimeOffset SetAt,
    DateTimeOffset ExpiresAt,
    Func<JsonNode?, bool> IsConfirmedBy);
