using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Auth;

/// <summary>
/// Partner-kald med appens egne credentials (client_credentials) – bruges til at registrere
/// partnerdomænet hos Tesla og tjekke hvilken offentlig nøgle Tesla har på det.
/// </summary>
public sealed class TeslaPartnerClient(HttpClient http, IOptions<BridgeOptions> options)
{
    private readonly BridgeOptions _opts = options.Value;

    public async Task<JsonNode?> RegisterAsync(CancellationToken ct)
    {
        // Tesla henter nu https://<domæne>/.well-known/... og gemmer nøglen.
        var (status, body, raw) = await SendAsync(HttpMethod.Post, "/api/1/partner_accounts",
            new JsonObject { ["domain"] = _opts.PartnerDomain }, ct);
        if ((int)status >= 300) throw new InvalidOperationException($"Registrering svarede {(int)status}: {raw}");
        return body;
    }

    /// <summary>Den nøgle Tesla har registreret på partnerdomænet (hex, uncompressed), eller null.</summary>
    public async Task<string?> GetRegisteredPublicKeyAsync(CancellationToken ct)
    {
        var (status, body, raw) = await SendAsync(HttpMethod.Get,
            $"/api/1/partner_accounts/public_key?domain={Uri.EscapeDataString(_opts.PartnerDomain)}", null, ct);
        if (status == System.Net.HttpStatusCode.NotFound) return null;
        if ((int)status >= 300) throw new InvalidOperationException($"{(int)status}: {raw}");
        return body?["response"]?["public_key"]?.ToString()?.ToLowerInvariant();
    }

    private async Task<(System.Net.HttpStatusCode, JsonNode?, string)> SendAsync(HttpMethod method, string path, JsonNode? json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _opts.Tesla.FleetApiAudience + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetPartnerTokenAsync(ct));
        if (json is not null) req.Content = JsonContent.Create(json);
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? parsed = null;
        try { parsed = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw); } catch { }
        return (resp.StatusCode, parsed, raw);
    }

    private async Task<string> GetPartnerTokenAsync(CancellationToken ct)
    {
        var t = _opts.Tesla;
        if (string.IsNullOrWhiteSpace(t.ClientId) || string.IsNullOrWhiteSpace(t.ClientSecret))
            throw new InvalidOperationException("Bridge__Tesla__ClientId og Bridge__Tesla__ClientSecret skal være sat i .env");

        using var resp = await http.PostAsync(t.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = t.ClientId,
            ["client_secret"] = t.ClientSecret,
            ["scope"] = "openid vehicle_device_data vehicle_cmds vehicle_charging_cmds",
            ["audience"] = t.FleetApiAudience,
        }), ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Partner-token afvist ({(int)resp.StatusCode}): {raw}");
        return JsonNode.Parse(raw)?["access_token"]?.ToString()
            ?? throw new InvalidOperationException("Intet access_token i svaret");
    }
}
