using System.Globalization;
using TeslaBridge.Map;
using TeslaBridge.Settings;

namespace TeslaBridge.Web;

/// <summary>GET /api/geocode?lat=…&amp;lon=…&amp;lang=da – the street address for the dashboard's map card.</summary>
public static class MapApi
{
    public static void MapMapApi(this RouteGroupBuilder api)
    {
        api.MapGet("/geocode", async (HttpRequest req, ReverseGeocoder geocoder, SettingsStore settings,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            if (!settings.Current.MapEnabled)
                return ApiErrors.Problem(StatusCodes.Status403Forbidden, "The map is turned off under Settings");
            if (!TryCoord(req.Query["lat"], 90, out var lat) || !TryCoord(req.Query["lon"], 180, out var lon))
                return Results.BadRequest(new { error = "lat and lon are required" });
            try
            {
                var a = await geocoder.LookupAsync(lat, lon, req.Query["lang"].ToString(), ct);
                return Results.Json(new
                {
                    address = a is null ? null : new
                    {
                        line1 = a.Line1,
                        line2 = a.Line2,
                        place = a.Place,
                        country = a.Country,
                        country_code = a.CountryCode,
                        full = a.Full,
                    },
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       && !ct.IsCancellationRequested)
            {
                lf.CreateLogger("Map").LogInformation("Address lookup failed: {Error}", ex.Message);
                return ApiErrors.Problem(StatusCodes.Status502BadGateway, ex.Message);
            }
        });
    }

    private static bool TryCoord(string? s, double max, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(v) <= max;
}
