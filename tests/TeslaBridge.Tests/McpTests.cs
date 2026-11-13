using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeslaBridge.Commands;
using TeslaBridge.Mcp;
using TeslaBridge.Settings;

namespace TeslaBridge.Tests;

public sealed class McpTests : IAsyncDisposable
{
    private WebApplication? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    [Fact]
    public void Commands_that_open_the_car_are_security_commands()
    {
        var parked = new TeslaBridge.State.FieldValue(System.Text.Json.Nodes.JsonNode.Parse("""{"latitude":56.153,"longitude":10.203}"""),
            DateTimeOffset.UtcNow, TeslaBridge.State.ValueSource.Stream);
        var parse = (string name, string value) => CommandParser.Parse(name, value, k => k == "location" ? parked : null) ?? throw new Xunit.Sdk.XunitException($"{name} {value} doesn't parse");
        foreach (var (name, value, _) in CarTools.SecurityCommands)
            foreach (var v in value.Length == 0 ? ["PRESS"] : value.Split(" | "))
            {
                var c = parse(name, v);
                if (name == "trunk" && v == "close") continue;   // closing the trunk is harmless but goes through the same toggle
                if (name is "guest_mode" or "valet_mode" && v == "off") continue;
                Assert.True(CarTools.IsSecurityCommand(c), $"{name} {v}");
            }

        foreach (var (name, value, _) in CarTools.Commands)
            foreach (var v in value.Length == 0 ? ["PRESS"] : value.Split(" | "))
            {
                var c = parse(name, v.Contains('–') ? v.Split('–')[0] : v);
                Assert.False(CarTools.IsSecurityCommand(c), $"{name} {v}");
            }
    }

    [Fact]
    public void Only_the_hash_of_the_token_is_compared()
    {
        var hash = McpEndpoint.Hash("tbm_secret");
        Assert.True(McpEndpoint.Matches("tbm_secret", hash));
        Assert.False(McpEndpoint.Matches("tbm_secreT", hash));
        Assert.False(McpEndpoint.Matches("", hash));
        Assert.False(McpEndpoint.Matches("tbm_secret", null));
    }

    private async Task<(HttpClient Http, SettingsStore Settings)> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var settings = new SettingsStore(null, "unused.json");
        builder.Services.AddSingleton(settings);
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools([]);
        _app = builder.Build();
        _app.MapMcpServer();
        await _app.StartAsync();
        return (_app.GetTestClient(), settings);
    }

    private static HttpRequestMessage Initialize(string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) req.Headers.Authorization = new("Bearer", token);
        return req;
    }

    [Fact]
    public async Task Mcp_is_off_until_turned_on_and_then_needs_its_own_token()
    {
        var (http, settings) = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Initialize(null))).StatusCode);

        settings.SetMcp(m => m with { Enabled = true });
        var noToken = await http.SendAsync(Initialize(null));
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Contains("Bearer", noToken.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        settings.SetMcp(m => m with { TokenHash = McpEndpoint.Hash("tbm_good") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize("tbm_bad"))).StatusCode);
        var ok = await http.SendAsync(Initialize("tbm_good"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("protocolVersion", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        settings.SetMcp(m => m with { TokenHash = null });   // revoked
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Initialize("tbm_good"))).StatusCode);
    }
}
