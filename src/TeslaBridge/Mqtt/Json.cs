using System.Text.Encodings.Web;
using System.Text.Json;

namespace TeslaBridge.Mqtt;

internal static class Json
{
    /// <summary>Læsbar JSON på MQTT: ingen ° for ° eller + for +.</summary>
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
