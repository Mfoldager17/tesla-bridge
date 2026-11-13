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
    public void Every_command_belongs_to_the_catalogue()
    {
        // Each VehicleCommand type must map to an entry, or an assistant could never be allowed (or refused) to send it.
        var types = typeof(Cmd).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(VehicleCommand)));
        var ids = McpCommands.Catalogue.Select(e => e.Id).ToHashSet();
        foreach (var t in types)
        {
            var sample = (VehicleCommand)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            var id = McpCommands.IdOf(sample);
            Assert.True(id is not null && ids.Contains(id), t.Name);
        }
    }

    [Theory]
    [InlineData("lock", "unlock", "unlock", true)]
    [InlineData("frunk", "", "frunk", true)]
    [InlineData("windows", "vent", "windows_vent", true)]
    [InlineData("windows", "close", "windows_close", false)]
    [InlineData("sentry", "off", "sentry_off", true)]
    [InlineData("sentry", "on", "sentry_on", false)]
    [InlineData("charge_port_door", "open", "charge_port_open", true)]
    [InlineData("charge_port_door", "close", "charge_port_close", false)]
    [InlineData("homelink", "", "homelink", true)]
    [InlineData("climate", "on", "climate", false)]
    [InlineData("seat_heater_rear_left", "high", "seat_heater", false)]
    [InlineData("auto_steering_wheel_heater", "on", "steering_wheel_heat", false)]
    public void Commands_map_to_their_entry(string name, string value, string id, bool security)
    {
        var parked = new TeslaBridge.State.FieldValue(System.Text.Json.Nodes.JsonNode.Parse("""{"latitude":56.153,"longitude":10.203}"""),
            DateTimeOffset.UtcNow, TeslaBridge.State.ValueSource.Stream);
        var c = CommandParser.Parse(name, value.Length == 0 ? "PRESS" : value, k => k == "location" ? parked : null)!;
        Assert.Equal(id, McpCommands.IdOf(c));
        Assert.Equal(security, McpCommands.IsSecurity(id));
    }

    [Fact]
    public void The_policy_decides()
    {
        var safe = new McpSetting();
        Assert.Equal("safe", safe.Policy);
        Assert.True(McpCommands.IsAllowed(safe, "climate"));
        Assert.False(McpCommands.IsAllowed(safe, "unlock"));
        Assert.False(McpCommands.IsAllowed(safe, null));

        Assert.False(McpCommands.IsAllowed(safe with { Policy = "read_only" }, "climate"));
        Assert.True(McpCommands.IsAllowed(safe with { Policy = "all" }, "unlock"));

        var custom = safe with { Policy = "custom", AllowedCommands = McpCommands.Clean(["climate", "unlock", "nonsense"]) };
        Assert.Equal(["climate", "unlock"], custom.AllowedCommands);
        Assert.True(McpCommands.IsAllowed(custom, "unlock"));
        Assert.False(McpCommands.IsAllowed(custom, "charge_limit"));
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
