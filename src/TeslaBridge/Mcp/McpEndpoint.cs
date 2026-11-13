using System.Security.Cryptography;
using System.Text;
using TeslaBridge.Settings;
using TeslaBridge.Web;

namespace TeslaBridge.Mcp;

/// <summary>
/// The MCP server for AI assistants at /mcp (Streamable HTTP). Off until it's turned on under Settings, and it only
/// answers with the MCP token – a token of its own, not the dashboard's, so access for an assistant can be revoked
/// without changing anything else. Only the token's SHA-256 is stored.
/// </summary>
public static class McpEndpoint
{
    public const string Path = "/mcp";

    public static void MapMcpServer(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<SettingsStore>();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(Path))
            {
                var mcp = settings.Current.Mcp;
                if (!mcp.Enabled)
                {
                    await ApiErrors.Problem(StatusCodes.Status404NotFound, "MCP is turned off. Turn it on under Settings → AI assistants (MCP).").ExecuteAsync(ctx);
                    return;
                }
                var auth = ctx.Request.Headers.Authorization.ToString();
                if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !Matches(auth[7..].Trim(), mcp.TokenHash))
                {
                    ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"tesla-bridge-mcp\"";
                    await ApiErrors.Problem(StatusCodes.Status401Unauthorized, "The MCP token is missing or wrong. Make one under Settings → AI assistants (MCP).").ExecuteAsync(ctx);
                    return;
                }
            }
            await next();
        });
        app.MapMcp(Path);
    }

    /// <summary>PUT /api/mcp-settings { enabled, allow_security_commands }</summary>
    public sealed record McpSettingsRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("enabled")] bool? Enabled,
        [property: System.Text.Json.Serialization.JsonPropertyName("allow_security_commands")] bool? AllowSecurityCommands);

    /// <summary>
    /// PUT /api/mcp-settings turns MCP on/off and allows security commands. POST /api/mcp-token makes a new MCP token
    /// (shown once; the old one stops working), DELETE /api/mcp-token revokes it.
    /// </summary>
    public static void MapMcpTokenApi(this RouteGroupBuilder api)
    {
        api.MapPut("/mcp-settings", (McpSettingsRequest body, SettingsStore settings) =>
        {
            var s = settings.SetMcp(m => m with
            {
                Enabled = body.Enabled ?? m.Enabled,
                AllowSecurityCommands = body.AllowSecurityCommands ?? m.AllowSecurityCommands,
            });
            return Results.Json(Describe(s.Mcp));
        });
        api.MapPost("/mcp-token", (SettingsStore settings) =>
        {
            var token = "tbm_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            settings.SetMcp(m => m with { TokenHash = Hash(token) });
            return Results.Json(new { token });
        });
        api.MapDelete("/mcp-token", (SettingsStore settings) =>
        {
            settings.SetMcp(m => m with { TokenHash = null });
            return Results.NoContent();
        });
    }

    internal static object Describe(McpSetting m) =>
        new { enabled = m.Enabled, allow_security_commands = m.AllowSecurityCommands, token_set = m.TokenHash is not null, path = Path };

    internal static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static bool Matches(string given, string? storedHash) =>
        storedHash is not null && given.Length > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(given)), Encoding.ASCII.GetBytes(storedHash));
}
