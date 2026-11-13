using System.Net;
using System.Text.Json.Nodes;
using TeslaBridge.Map;
using TeslaBridge.Settings;

namespace TeslaBridge.Tests;

public class MapTests
{
    private sealed class FakeNominatim(string body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private const string House = """
        { "name": "", "display_name": "12, Skanderborgvej, Hasselager, Aarhus Kommune, 8361, Danmark",
          "address": { "house_number": "12", "road": "Skanderborgvej", "suburb": "Hasselager", "city": "Aarhus",
                       "municipality": "Aarhus Kommune", "postcode": "8361", "country": "Danmark", "country_code": "dk" } }
        """;

    [Fact]
    public void A_house_becomes_street_and_number_over_postcode_and_city()
    {
        var a = ReverseGeocoder.Format(JsonNode.Parse(House))!;
        Assert.Equal("Skanderborgvej 12", a.Line1);
        Assert.Equal("8361 Aarhus", a.Line2);
        Assert.Null(a.Place);
        Assert.Equal("dk", a.CountryCode);
    }

    [Fact]
    public void A_named_place_is_kept_next_to_the_street()
    {
        var a = ReverseGeocoder.Format(JsonNode.Parse("""
            { "name": "Tesla Supercharger", "display_name": "Tesla Supercharger, Hasselager Allé, 8260 Viby J",
              "address": { "amenity": "Tesla Supercharger", "road": "Hasselager Allé", "town": "Viby J", "postcode": "8260" } }
            """))!;
        Assert.Equal("Hasselager Allé", a.Line1);
        Assert.Equal("8260 Viby J", a.Line2);
        Assert.Equal("Tesla Supercharger", a.Place);
    }

    [Fact]
    public void No_road_falls_back_to_the_place_or_the_town()
    {
        var a = ReverseGeocoder.Format(JsonNode.Parse("""{ "name": "", "address": { "village": "Tåstrup", "postcode": "8462" } }"""))!;
        Assert.Equal("Tåstrup", a.Line1);
        Assert.Equal("8462 Tåstrup", a.Line2);
        Assert.Null(ReverseGeocoder.Format(JsonNode.Parse("""{ "error": "Unable to geocode" }""")));
    }

    [Fact]
    public async Task Nearby_positions_are_answered_from_the_cache()
    {
        var fake = new FakeNominatim(House);
        var g = new ReverseGeocoder(new HttpClient(fake), "https://geo.example.com/") { Spacing = TimeSpan.Zero };

        var a = await g.LookupAsync(56.118501, 10.123499, "da", default);
        var b = await g.LookupAsync(56.118504, 10.123502, "da", default);   // < 1 m away
        await g.LookupAsync(56.118501, 10.123499, "en", default);          // another language is another answer

        Assert.Same(a, b);
        Assert.Equal(2, g.Requests);
        Assert.StartsWith("https://geo.example.com/reverse?format=jsonv2", fake.Urls[0]);
        Assert.Contains("lat=56.118501&lon=10.123499&accept-language=da", fake.Urls[0]);
    }

    [Fact]
    public async Task Requests_are_spaced_out()
    {
        var g = new ReverseGeocoder(new HttpClient(new FakeNominatim(House)), "https://geo.example.com") { Spacing = TimeSpan.FromMilliseconds(300) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(g.LookupAsync(56.1, 10.1, "da", default), g.LookupAsync(56.2, 10.2, "da", default));
        Assert.True(sw.ElapsedMilliseconds >= 280, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_map_setting_is_saved_and_kept_on_reset()
    {
        var dir = Directory.CreateTempSubdirectory("map").FullName;
        try
        {
            var defaults = Path.Combine(dir, "fields.json");
            File.WriteAllText(defaults, """{ "fields": { "Location": { "interval_seconds": 2 } } }""");
            var s = new SettingsStore(Path.Combine(dir, "settings.json"), defaults);
            Assert.True(s.Current.MapEnabled);
            s.Update(null, null, mapEnabled: false);
            s.Reset();
            Assert.False(new SettingsStore(Path.Combine(dir, "settings.json"), defaults).Current.MapEnabled);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
