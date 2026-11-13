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
        { "name": "", "display_name": "2, Rådhuspladsen, Aarhus C, Aarhus Kommune, 8000, Danmark",
          "address": { "house_number": "2", "road": "Rådhuspladsen", "suburb": "Aarhus C", "city": "Aarhus",
                       "municipality": "Aarhus Kommune", "postcode": "8000", "country": "Danmark", "country_code": "dk" } }
        """;

    [Fact]
    public void A_house_becomes_street_and_number_over_postcode_and_city()
    {
        var a = ReverseGeocoder.Format(JsonNode.Parse(House))!;
        Assert.Equal("Rådhuspladsen 2", a.Line1);
        Assert.Equal("8000 Aarhus", a.Line2);
        Assert.Null(a.Place);
        Assert.Equal("dk", a.CountryCode);
    }

    [Fact]
    public void A_named_place_is_kept_next_to_the_street()
    {
        var a = ReverseGeocoder.Format(JsonNode.Parse("""
            { "name": "Tesla Supercharger", "display_name": "Tesla Supercharger, Agro Food Park, 8200 Aarhus N",
              "address": { "amenity": "Tesla Supercharger", "road": "Agro Food Park", "town": "Aarhus N", "postcode": "8200" } }
            """))!;
        Assert.Equal("Agro Food Park", a.Line1);
        Assert.Equal("8200 Aarhus N", a.Line2);
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

        var a = await g.LookupAsync(56.153001, 10.203299, "da", default);
        var b = await g.LookupAsync(56.153004, 10.203302, "da", default);   // < 1 m away
        await g.LookupAsync(56.153001, 10.203299, "en", default);          // another language is another answer

        Assert.Same(a, b);
        Assert.Equal(2, g.Requests);
        Assert.StartsWith("https://geo.example.com/reverse?format=jsonv2", fake.Urls[0]);
        Assert.Contains("lat=56.153001&lon=10.203299&accept-language=da", fake.Urls[0]);
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
