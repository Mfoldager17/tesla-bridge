using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using QRCoder;
using TeslaBridge.Auth;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Web;

/// <summary>
/// Opsætning uden HA: offentlig nøgle + login-callback (offentlig lytter) og
/// status/registrering/parring (dashboardet på LAN).
/// </summary>
public static class SetupApi
{
    /// <summary>De eneste stier, den offentlige lytter svarer på.</summary>
    public static bool IsPublicPath(WebOptions web, PathString path) =>
        path.Equals(web.PublicKeyPath, StringComparison.Ordinal) ||
        path.Equals(web.CallbackPath, StringComparison.Ordinal);

    public static void UsePublicListenerFilter(this WebApplication app)
    {
        var web = app.Services.GetRequiredService<IOptions<BridgeOptions>>().Value.Web;
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort == web.PublicPort && !IsPublicPath(web, ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
    }

    public static void MapPublicEndpoints(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<BridgeOptions>>().Value;

        app.MapGet(opts.Web.PublicKeyPath, (TeslaKey key) =>
            key.Public is { } k ? Results.Text(k.Pem, "application/x-pem-file") : Results.NotFound());

        app.MapGet(opts.Web.CallbackPath, async (string? code, string? state, string? error, LoginFlow login,
            TelemetryConfigService telemetry, IHostApplicationLifetime life, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Login");
            if (!string.IsNullOrEmpty(error))
                return Page("Login afbrudt", $"Tesla svarede: {error}. Start login igen fra dashboardet.", ok: false);
            try
            {
                await login.CompleteAsync(code, state, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning("Login fejlede: {Error}", ex.Message);
                return Page("Login fejlede", ex.Message, ok: false);
            }

            log.LogInformation("Logget ind hos Tesla via browser");
            // Nu kan konfigurationen sendes til bilen.
            _ = Task.Run(async () =>
            {
                foreach (var v in opts.Vehicles)
                {
                    try { await telemetry.EnsureFreshAsync(v.Vin, life.ApplicationStopping); }
                    catch (Exception ex) { log.LogWarning("Telemetri-konfiguration efter login fejlede: {Error}", ex.Message); }
                }
            });
            return Page("Logget ind hos Tesla", "Bridgen har nu adgang. Du kan lukke dette vindue og gå tilbage til dashboardet.", ok: true);
        });
    }

    public static void MapSetupApi(this RouteGroupBuilder api, BridgeOptions opts)
    {
        RegisteredCheck? lastCheck = null;

        api.MapGet("/auth/start", (LoginFlow login) => Results.Redirect(login.Start()));

        api.MapGet("/setup", async (bool? check, TeslaKey key, TeslaTokenProvider tokens, TeslaPartnerClient partner,
            TelemetryConfigService telemetry, CancellationToken ct) =>
        {
            var pub = SafeKey(key, out var keyError);
            if (check == true && pub is not null && HasCredentials(opts))
            {
                try
                {
                    var registered = await partner.GetRegisteredPublicKeyAsync(ct);
                    lastCheck = new RegisteredCheck(registered is not null, registered == pub.UncompressedHex, null, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    lastCheck = new RegisteredCheck(false, false, ex.Message, DateTimeOffset.UtcNow);
                }
            }

            var vehicles = new JsonArray();
            foreach (var v in opts.Vehicles)
            {
                var o = new JsonObject { ["vin"] = v.Vin, ["name"] = v.Name };
                if (check == true && tokens.HasTokens)
                {
                    try
                    {
                        var r = (await telemetry.GetStatusAsync(v.Vin, ct))?["response"];
                        o["synced"] = r?["synced"]?.DeepClone();
                        o["key_paired"] = r?["key_paired"]?.DeepClone();
                        o["hostname"] = r?["config"]?["hostname"]?.DeepClone();
                    }
                    catch (Exception ex) { o["error"] = ex.Message; }
                }
                vehicles.Add(o);
            }

            var result = new JsonObject
            {
                ["partner_domain"] = opts.PartnerDomain,
                ["public_key_url"] = opts.PublicKeyUrl,
                ["redirect_uri"] = opts.RedirectUri,
                ["pairing_url"] = opts.PairingUrl,
                ["credentials_set"] = HasCredentials(opts),
                ["key"] = new JsonObject
                {
                    ["present"] = pub is not null,
                    ["fingerprint"] = pub?.Fingerprint,
                    ["error"] = keyError,
                    ["file"] = opts.Tesla.FleetKeyFile,
                },
                ["registration"] = lastCheck is null ? null : new JsonObject
                {
                    ["registered"] = lastCheck.Registered,
                    ["matches"] = lastCheck.Matches,
                    ["error"] = lastCheck.Error,
                    ["checked_at"] = lastCheck.At.ToString("O"),
                },
                ["logged_in"] = tokens.HasTokens,
                ["vehicles"] = vehicles,
            };
            return Results.Content(result.ToJsonString(Json.Relaxed), "application/json");
        });

        api.MapPost("/setup/register", async (TeslaPartnerClient partner, TeslaKey key, CancellationToken ct) =>
        {
            try
            {
                var resp = await partner.RegisterAsync(ct);
                var pub = key.Public;
                var registered = await partner.GetRegisteredPublicKeyAsync(ct);
                lastCheck = new RegisteredCheck(registered is not null, pub is not null && registered == pub.UncompressedHex, null, DateTimeOffset.UtcNow);
                return Results.Content(resp?.ToJsonString(Json.Relaxed) ?? "{}", "application/json");
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapGet("/setup/pair-qr.svg", () =>
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(opts.PairingUrl, QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(6, "#000000", "#ffffff", drawQuietZones: true);
            return Results.Content(svg, "image/svg+xml");
        });
    }

    private sealed record RegisteredCheck(bool Registered, bool Matches, string? Error, DateTimeOffset At);

    private static bool HasCredentials(BridgeOptions o) =>
        !string.IsNullOrWhiteSpace(o.Tesla.ClientId) && !string.IsNullOrWhiteSpace(o.Tesla.ClientSecret) && !string.IsNullOrWhiteSpace(o.PartnerDomain);

    private static TeslaKey.PublicKeyInfo? SafeKey(TeslaKey key, out string? error)
    {
        error = null;
        try { return key.Public; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static IResult Page(string title, string text, bool ok)
    {
        var color = ok ? "#2fbf71" : "#ef5350";
        var html = $$"""
            <!doctype html><html lang="da"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0d10;color:#e7eaee;font:16px/1.5 -apple-system,BlinkMacSystemFont,sans-serif}
            main{max-width:420px;padding:32px;text-align:center}h1{font-size:20px;margin:16px 0 8px}p{color:#8a93a0;margin:0}
            .dot{width:48px;height:48px;border-radius:50%;margin:0 auto;background:{{color}};opacity:.9}</style></head>
            <body><main><div class="dot"></div><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: ok ? 200 : 400);
    }
}
