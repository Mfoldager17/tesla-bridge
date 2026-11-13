using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TeslaBridge.Options;
using TeslaBridge.Web;

namespace TeslaBridge.Tests;

/// <summary>The dashboard's HTTP protections, through the real ASP.NET Core pipeline (in memory).</summary>
public sealed class WebSecurityTests : IAsyncDisposable
{
    private const string Token = "a-very-secret-token-for-tests";
    private const string Vin = "LRWYGCEK0SB000001";
    private readonly string _dir = Directory.CreateTempSubdirectory("websec").FullName;
    private WebApplication? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<HttpClient> StartAsync(Action<BridgeOptions>? configure = null)
    {
        var opts = new BridgeOptions { StateFile = Path.Combine(_dir, "state.json") };
        opts.Web.Token = Token;
        opts.Vehicles.Add(new VehicleOptions { Vin = Vin });
        configure?.Invoke(opts);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var access = new WebAccess(Microsoft.Extensions.Options.Options.Create(opts), NullLogger<WebAccess>.Instance);
        _app.UseBridgeSecurity(access, opts);
        var api = _app.MapGroup("/api");
        api.MapSessionApi(access);
        api.MapGet("/state", () => "state");
        api.MapGet("/info", () => "info");
        api.MapGroup("/vehicles/{vin}").OnlyKnownVehicles(opts).MapPost("/cmd/{name}", (string name) => name);
        await _app.StartAsync();
        var client = _app.GetTestClient();
        client.BaseAddress = new Uri("http://192.168.1.10:8090");
        return client;
    }

    private static HttpRequestMessage Post(string path, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("UNLOCK") };
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
        return req;
    }

    [Fact]
    public async Task The_api_needs_the_token()
    {
        var http = await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post($"/api/vehicles/{Vin}/cmd/lock"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Post($"/api/vehicles/{Vin}/cmd/lock", "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post($"/api/vehicles/{Vin}/cmd/lock", Token))).StatusCode);
        // The token in the address is not accepted (it would end up in browser history and logs).
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/state?token={Token}")).StatusCode);
        // /api/info answers without it (the sign-in page needs the version).
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/info")).StatusCode);
    }

    [Fact]
    public async Task Errors_are_problem_details_with_an_error_member()
    {
        var http = await StartAsync();
        var res = await http.GetAsync("/api/state");
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("A valid token is required", body!["error"].ToString());
        Assert.Equal(body["detail"].ToString(), body["error"].ToString());
    }

    [Fact]
    public async Task The_dashboard_signs_in_once_and_then_uses_an_HttpOnly_cookie()
    {
        var http = await StartAsync();
        var wrong = await http.PostAsJsonAsync("/api/session", new { token = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var ok = await http.PostAsJsonAsync("/api/session", new { token = Token });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        var cookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Token, cookie, StringComparison.Ordinal);   // a derived value, not the token itself

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        req.Headers.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);

        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        forged.Headers.Add("Cookie", $"{WebAccess.CookieName}=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(forged)).StatusCode);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Origin", "https://evil.example.com")]
    [InlineData("Origin", "null")]
    public async Task Another_web_site_cannot_send_commands(string header, string value)
    {
        var http = await StartAsync();
        var req = Post($"/api/vehicles/{Vin}/cmd/lock", Token);
        req.Headers.Add(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(req)).StatusCode);

        var sameSite = Post($"/api/vehicles/{Vin}/cmd/lock", Token);
        sameSite.Headers.Add("Origin", "http://192.168.1.10:8090");
        sameSite.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(sameSite)).StatusCode);
    }

    [Fact]
    public async Task Unknown_cars_get_a_404()
    {
        var http = await StartAsync();
        var res = await http.SendAsync(Post("/api/vehicles/NOTAVIN123/cmd/lock", Token));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Browser_protections_are_sent()
    {
        var http = await StartAsync(o => o.Map.TileUrl = "https://{s}.tiles.example.com/{z}/{x}/{y}.png");
        var res = await http.GetAsync("/api/info");
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data: blob: https://*.tiles.example.com", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_token_only_known_host_names_are_answered()
    {
        var http = await StartAsync(o => { o.Web.Token = null; o.Web.AllowAnonymous = true; o.Web.AllowedHosts = "pi.lan"; });
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/state")).StatusCode);   // by IP address

        foreach (var (host, expected) in new[] { ("attacker.example.com", HttpStatusCode.Forbidden), ("pi.lan", HttpStatusCode.OK), ("tesla.local", HttpStatusCode.OK) })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            req.Headers.Host = host;
            Assert.Equal(expected, (await http.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public void Without_a_token_in_env_the_bridge_makes_one_and_keeps_it()
    {
        var opts = new BridgeOptions { StateFile = Path.Combine(_dir, "state.json") };
        var first = new WebAccess(Microsoft.Extensions.Options.Options.Create(opts), NullLogger<WebAccess>.Instance);
        var file = Path.Combine(_dir, "web-token");
        var token = File.ReadAllText(file).Trim();
        Assert.True(token.Length >= 32);
        Assert.False(first.Anonymous);
        Assert.True(first.IsValidToken(token));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var again = new WebAccess(Microsoft.Extensions.Options.Options.Create(opts), NullLogger<WebAccess>.Instance);
        Assert.True(again.IsValidToken(token));   // the same one after a restart
    }
}
