using System.Text.Json.Nodes;
using TeslaBridge.Options;
using TeslaBridge.Settings;
using TeslaBridge.Telemetry;
using TeslaBridge.Usage;

namespace TeslaBridge.Web;

/// <summary>
/// The dashboard's settings page: which telemetry fields the car sends and how often, and how often a
/// snapshot is fetched. Changes are saved in /data/settings.json and sent to the car straight away.
/// </summary>
public static class SettingsApi
{
    /// <summary>
    /// Body: { "fields": { "Location": { "enabled": true, "interval_seconds": 10 } },
    ///         "snapshot_interval_minutes": 60, "push": true } – every part optional.
    /// </summary>
    internal static (Dictionary<string, FieldSetting>? Fields, int? Snapshot, bool Push, string? CarView) Parse(JsonNode? body)
    {
        if (body is not JsonObject o) throw new ArgumentException("Expected a JSON object");
        Dictionary<string, FieldSetting>? fields = null;
        if (o["fields"] is JsonObject f)
        {
            fields = new(StringComparer.Ordinal);
            foreach (var (name, v) in f)
            {
                var enabled = v?["enabled"] is JsonValue e && e.TryGetValue(out bool b) ? b : true;
                var interval = v?["interval_seconds"] is JsonValue i && i.TryGetValue(out int n) ? n
                    : throw new ArgumentException($"{name}: interval_seconds is missing");
                fields[name] = new FieldSetting(enabled, interval);
            }
        }
        int? snapshot = o["snapshot_interval_minutes"] is JsonValue sv && sv.TryGetValue(out int m) ? m : null;
        var push = o["push"] is not JsonValue pv || !pv.TryGetValue(out bool p) || p;
        var carView = o["car_view"] is JsonValue cv && cv.TryGetValue(out string? c) ? c : null;
        return (fields, snapshot, push, carView);
    }

    public static void MapSettingsApi(this RouteGroupBuilder api, BridgeOptions opts)
    {
        api.MapGet("/settings", (SettingsStore store, UsageTracker usage) => Results.Json(Describe(store, usage)));

        api.MapPut("/settings", async (HttpRequest req, SettingsStore store, UsageTracker usage,
            TelemetryConfigService telemetry, CancellationToken ct) =>
        {
            Dictionary<string, FieldSetting>? fields;
            bool push;
            try
            {
                (fields, var snapshot, push, var carView) = Parse(await JsonNode.ParseAsync(req.Body, cancellationToken: ct));
                store.Update(fields, snapshot, carView);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }

            var result = Describe(store, usage);
            if (push && fields is { Count: > 0 }) result["push"] = await PushAsync(opts, telemetry, ct);
            return Results.Json(result);
        });

        api.MapPost("/settings/reset", async (SettingsStore store, UsageTracker usage, TelemetryConfigService telemetry, CancellationToken ct) =>
        {
            store.Reset();
            var result = Describe(store, usage);
            result["push"] = await PushAsync(opts, telemetry, ct);
            return Results.Json(result);
        });
    }

    private static async Task<JsonArray> PushAsync(BridgeOptions opts, TelemetryConfigService telemetry, CancellationToken ct)
    {
        var results = new JsonArray();
        foreach (var v in opts.Vehicles)
        {
            try
            {
                await telemetry.PushAsync([v.Vin], ct);
                results.Add(new JsonObject { ["vin"] = v.Vin, ["ok"] = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Saved anyway: the bridge checks the car every 12 hours and sends the new fields then.
                results.Add(new JsonObject { ["vin"] = v.Vin, ["ok"] = false, ["error"] = ex.Message });
            }
        }
        return results;
    }

    internal static JsonObject Describe(SettingsStore store, UsageTracker usage)
    {
        var s = store.Current;
        var u = usage.Snapshot();
        var fields = new JsonArray();
        var order = TelemetryFieldCatalog.Fields.Keys.ToList();
        foreach (var (field, def) in store.Defaults
                     .OrderBy(kv => Array.IndexOf(TelemetryFieldCatalog.Groups, TelemetryFieldCatalog.Describe(kv.Key).Group))
                     .ThenBy(kv => order.IndexOf(kv.Key) is var i && i < 0 ? int.MaxValue : i)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var info = TelemetryFieldCatalog.Describe(field);
            var set = s.TelemetryFields.TryGetValue(field, out var o) ? o : new FieldSetting(true, def);
            fields.Add(new JsonObject
            {
                ["field"] = field,
                ["group"] = info.Group,
                ["en"] = info.En,
                ["da"] = info.Da,
                ["continuous"] = info.Continuous,
                ["default_interval"] = def,
                ["enabled"] = set.Enabled,
                ["interval"] = set.IntervalSeconds,
                ["customized"] = s.TelemetryFields.ContainsKey(field),
                ["signals_month"] = u.SignalsByField.TryGetValue(field, out var n) ? n : 0,
            });
        }
        return new JsonObject
        {
            ["fields"] = fields,
            ["groups"] = new JsonArray(TelemetryFieldCatalog.Groups.Select(g => (JsonNode?)g).ToArray()),
            ["snapshot_interval_minutes"] = s.SnapshotIntervalMinutes,
            ["car_view"] = s.CarView,
            ["snapshot_default_minutes"] = SettingsStore.DefaultSnapshotMinutes,
            ["signals_per_dollar"] = UsageTracker.SignalsPerDollar,
            ["credit_usd"] = u.CreditUsd,
            ["month_start"] = u.MonthStart.ToString("O"),
            ["month_end"] = u.MonthEnd.ToString("O"),
            ["now"] = DateTimeOffset.UtcNow.ToString("O"),
            ["min_interval"] = SettingsStore.MinInterval,
            ["max_interval"] = SettingsStore.MaxInterval,
        };
    }
}
