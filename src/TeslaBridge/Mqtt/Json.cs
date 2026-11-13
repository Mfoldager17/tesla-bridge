using System.Text.Encodings.Web;
using System.Text.Json;

namespace TeslaBridge.Mqtt;

internal static class Json
{
    /// <summary>Readable JSON on MQTT: no \u00b0 for ° or \u002b for +.</summary>
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly JsonSerializerOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
}
