using System.Globalization;
using System.Text.Json.Nodes;
using TeslaBridge.Options;

namespace TeslaBridge.Map;

/// <summary>A street address for a position, ready to show: "Skanderborgvej 12" / "8361 Hasselager".</summary>
public sealed record StreetAddress(string Line1, string? Line2, string? Place, string? Country, string? CountryCode, string Full);

/// <summary>
/// Turns the car's position into a street address with a Nominatim server (OpenStreetMap by default). Answers are
/// cached per ~10 m, and the server is asked at most once a second, as Nominatim's usage policy asks.
/// </summary>
public sealed class ReverseGeocoder : IDisposable
{
    private const int CacheSize = 1000;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Dictionary<string, StreetAddress?> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _cacheLock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Time between two requests to the server.</summary>
    internal TimeSpan Spacing { get; init; } = TimeSpan.FromSeconds(1.1);

    public ReverseGeocoder(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<BridgeOptions> options)
        : this(http.CreateClient("geocoder"), options.Value.Map.GeocoderUrl) { }

    internal ReverseGeocoder(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Requests sent to the server so far (for tests and the log).</summary>
    public int Requests { get; private set; }

    /// <summary>The address, or null where there is none (at sea, in a forest). Throws when the server can't be reached.</summary>
    public async Task<StreetAddress?> LookupAsync(double lat, double lon, string lang, CancellationToken ct)
    {
        lang = lang is "da" or "en" ? lang : "en";
        var key = string.Create(CultureInfo.InvariantCulture, $"{Math.Round(lat, 4)},{Math.Round(lon, 4)},{lang}");
        lock (_cacheLock)
            if (_cache.TryGetValue(key, out var hit)) return hit;

        await _gate.WaitAsync(ct);
        try
        {
            lock (_cacheLock)   // someone else may have fetched it while we waited
                if (_cache.TryGetValue(key, out var hit)) return hit;

            var wait = _last + Spacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            var url = string.Create(CultureInfo.InvariantCulture,
                $"{_baseUrl}/reverse?format=jsonv2&addressdetails=1&zoom=18&lat={lat:0.######}&lon={lon:0.######}&accept-language={lang}");
            Requests++;
            _last = DateTimeOffset.UtcNow;
            using var res = await _http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            var address = Format(JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)));

            lock (_cacheLock)
            {
                if (_cache.TryAdd(key, address)) _order.Enqueue(key);
                while (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
            }
            return address;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Builds the two lines from Nominatim's jsonv2 answer.</summary>
    internal static StreetAddress? Format(JsonNode? doc)
    {
        if (doc?["address"] is not JsonObject a) return null;
        string? S(params string[] keys) => keys.Select(k => a[k]?.GetValue<string>()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var road = S("road", "pedestrian", "footway", "cycleway", "path", "square", "service");
        var number = S("house_number");
        var street = road is null ? null : number is null ? road : $"{road} {number}";
        var city = S("city", "town", "village", "municipality", "suburb", "hamlet", "county");
        var postcode = S("postcode");
        var area = postcode is null ? city : city is null ? postcode : $"{postcode} {city}";

        // A named place – a shop, a car park, a charging station – when it isn't just the street's name.
        var name = doc["name"]?.GetValue<string>();
        var place = !string.IsNullOrWhiteSpace(name) && name != road && name != city ? name : null;

        var line1 = street ?? place ?? S("neighbourhood", "suburb", "village", "town", "city") ?? doc["display_name"]?.GetValue<string>();
        if (line1 is null) return null;
        var line2 = area == line1 ? null : area;
        if (street is null && place == line1) place = null;
        var full = doc["display_name"]?.GetValue<string>() ?? string.Join(", ", new[] { line1, line2 }.Where(x => x is not null));
        return new StreetAddress(line1, line2, place, S("country"), S("country_code")?.ToLowerInvariant(), full);
    }

    public void Dispose() => _gate.Dispose();
}
