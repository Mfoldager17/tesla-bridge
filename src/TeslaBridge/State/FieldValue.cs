using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TeslaBridge.State;

[JsonConverter(typeof(JsonStringEnumConverter<ValueSource>))]
public enum ValueSource
{
    /// <summary>Kom fra bilens telemetri-stream.</summary>
    Stream,
    /// <summary>Sat af os efter en kommando; venter på bekræftelse fra streamen.</summary>
    Optimistic,
    /// <summary>Indlæst fra disk ved opstart (sidst kendte værdi).</summary>
    Restored,
    /// <summary>Afledt eller sat af bridgen selv (fx online/last_seen).</summary>
    Bridge,
    /// <summary>Hentet med vehicle_data, fordi streamen endnu ikke havde sendt feltet.</summary>
    Snapshot,
    // Senere: Ble
}

/// <summary>Én værdi for ét felt, med hvornår og hvorfra den kom.</summary>
public sealed record FieldValue(JsonNode? Value, DateTimeOffset UpdatedAt, ValueSource Source);

/// <summary>
/// En forventet værdi efter en kommando. Holdes indtil streamen bekræfter den,
/// eller til den udløber – så en gammel "off" lige efter opvågning ikke overskriver "on".
/// </summary>
public sealed record PendingValue(
    JsonNode? Value,
    DateTimeOffset SetAt,
    DateTimeOffset ExpiresAt,
    Func<JsonNode?, bool> IsConfirmedBy);
