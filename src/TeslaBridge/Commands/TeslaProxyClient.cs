using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TeslaBridge.Auth;
using TeslaBridge.Usage;

namespace TeslaBridge.Commands;

/// <summary>
/// HTTP client for Tesla's tesla-http-proxy (vehicle-command), which signs commands and the telemetry configuration
/// with the app's private key and forwards them to the Fleet API.
/// </summary>
public sealed class TeslaProxyClient(HttpClient http, TeslaTokenProvider tokens, UsageTracker usage)
{
    public async Task<(HttpStatusCode Status, JsonNode? Body, string Raw)> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(ct));
        if (body is not null) req.Content = JsonContent.Create(body);
        else if (method == HttpMethod.Post) req.Content = JsonContent.Create(new JsonObject());

        using var resp = await http.SendAsync(req, ct);

        // Everything that reached Tesla is counted, the way Tesla bills it (also refused tries while the car sleeps).
        var (kind, name) = UsageTracker.Classify(path);
        usage.CountApiCall(kind, name);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? parsed = null;
        try { parsed = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw); } catch { /* not JSON */ }
        return (resp.StatusCode, parsed, raw);
    }

    /// <summary>The proxy's certificate is self-signed, so exactly that one is pinned.</summary>
    public static HttpMessageHandler CreateHandler(string proxyCertFile)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        if (File.Exists(proxyCertFile))
        {
            var pinned = X509Certificate2.CreateFromPem(File.ReadAllText(proxyCertFile));
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && cert.GetCertHashString() == pinned.GetCertHashString();
        }
        return handler;
    }
}
