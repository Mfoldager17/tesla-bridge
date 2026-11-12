using System.Text.Json.Nodes;
using TeslaBridge.Ble;
using TeslaBridge.Options;
using TeslaBridge.Settings;

namespace TeslaBridge.Web;

/// <summary>
/// The dashboard's Bluetooth section: where the ESP32 is, whether it sees the car, and buttons to pair the
/// key and test a wake-up.
///   GET    /api/ble                 status per car
///   PUT    /api/ble/{vin}           { "url": "http://192.168.1.50", "username": "", "password": "" } – password omitted = keep
///   DELETE /api/ble/{vin}           forget the dashboard setting (.env applies again)
///   POST   /api/ble/{vin}/pair      presses "Pair BLE key" – then tap your key card in the car
///   POST   /api/ble/{vin}/wake      wakes the car over Bluetooth
/// </summary>
public static class BleApi
{
    public static void MapBleApi(this RouteGroupBuilder api, BridgeOptions opts)
    {
        api.MapGet("/ble", (BleService ble, SettingsStore settings) => Results.Json(Describe(opts, ble, settings)));

        api.MapPut("/ble/{vin}", async (string vin, HttpRequest req, BleService ble, SettingsStore settings, CancellationToken ct) =>
        {
            if (!Known(opts, vin)) return Results.Json(new { error = $"Unknown VIN {vin}" }, statusCode: 404);
            try
            {
                var body = await JsonNode.ParseAsync(req.Body, cancellationToken: ct) as JsonObject
                    ?? throw new ArgumentException("Expected a JSON object");
                var url = Str(body, "url") ?? "";
                var user = Str(body, "username");
                // The password is never sent back to the browser, so "not in the body" means "keep it".
                var pass = body.ContainsKey("password") ? Str(body, "password")
                    : settings.Current.Ble.TryGetValue(vin, out var cur) ? cur.Password : null;
                settings.SetBle(vin, new BleSetting(url.Trim().TrimEnd('/'), user, pass));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
            return Results.Json(Describe(opts, ble, settings));
        });

        api.MapDelete("/ble/{vin}", (string vin, BleService ble, SettingsStore settings) =>
        {
            settings.SetBle(vin, null);
            return Results.Json(Describe(opts, ble, settings));
        });

        api.MapPost("/ble/{vin}/pair", (string vin, BleService ble, CancellationToken ct) =>
            Press(ble, vin, TeslaBleMap.PairButton, ct));

        api.MapPost("/ble/{vin}/wake", (string vin, BleService ble, CancellationToken ct) =>
            Press(ble, vin, TeslaBleMap.WakeButton, ct));
    }

    private static async Task<IResult> Press(BleService ble, string vin, string button, CancellationToken ct)
    {
        if (ble.Device(vin) is not { } d) return Results.Json(new { error = "No ESP32 set up for this car" }, statusCode: 404);
        var error = await d.ActAsync("button", button, "press", null, ct);
        return error is null ? Results.Json(new { ok = true }) : Results.Json(new { error }, statusCode: 502);
    }

    /// <summary>
    /// The name the car advertises over Bluetooth: "S" + the first 8 bytes of SHA-1(VIN) in hex + "C".
    /// Helps find its MAC address in a Bluetooth scanner app.
    /// </summary>
    internal static string CarBleName(string vin) =>
        "S" + Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.ASCII.GetBytes(vin)))[..16] + "C";

    private static bool Known(BridgeOptions o, string vin) => o.Vehicles.Any(v => string.Equals(v.Vin, vin, StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static JsonArray Describe(BridgeOptions opts, BleService ble, SettingsStore settings)
    {
        var dash = settings.Current.Ble;
        var list = new JsonArray();
        foreach (var v in opts.Vehicles)
        {
            var s = ble.Status(v.Vin);
            var d = dash.TryGetValue(v.Vin, out var x) ? x : null;
            list.Add(new JsonObject
            {
                ["vin"] = v.Vin,
                ["name"] = v.Name,
                ["configured"] = s.Configured,
                ["url"] = s.Url ?? d?.Url ?? (string.IsNullOrWhiteSpace(v.Ble.Url) ? null : v.Ble.Url),
                ["username"] = d?.Username ?? (string.IsNullOrWhiteSpace(v.Ble.Username) ? null : v.Ble.Username),
                ["has_password"] = !string.IsNullOrEmpty(d?.Password ?? v.Ble.Password),
                ["source"] = s.Source ?? (d is not null ? "dashboard" : null),
                ["env_url"] = string.IsNullOrWhiteSpace(v.Ble.Url) ? null : v.Ble.Url,
                ["esp_connected"] = s.EspConnected,
                ["car_in_range"] = s.CarInRange,
                ["car_asleep"] = s.CarAsleep,
                ["rssi"] = s.Rssi,
                ["esp_version"] = s.EspVersion,
                ["entities"] = s.Entities,
                ["looks_like_tesla_ble"] = s.LooksLikeTeslaBle,
                ["last_error"] = s.LastError,
                ["last_event_at"] = s.LastEventAt?.ToString("O"),
                ["car_ble_name"] = CarBleName(v.Vin),
            });
        }
        return list;
    }
}
