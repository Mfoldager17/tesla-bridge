using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using QRCoder;
using TeslaBridge.Auth;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Setup;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Web;

/// <summary>
/// Setup without Home Assistant: the public key and the sign-in callback (on the public listener), and
/// status, registration and pairing (on the dashboard, LAN only).
/// </summary>
public static class SetupApi
{
    /// <summary>The only paths the public listener answers.</summary>
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

        app.MapGet(opts.Web.CallbackPath, async (HttpRequest req, string? code, string? state, string? error, LoginFlow login,
            TelemetryConfigService telemetry, IHostApplicationLifetime life, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Login");
            var da = req.Headers.AcceptLanguage.ToString().StartsWith("da", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(error))
                return da
                    ? Page("da", "Login afbrudt", $"Tesla svarede: {error}. Start login igen fra dashboardet.", ok: false)
                    : Page("en", "Sign-in cancelled", $"Tesla answered: {error}. Start the sign-in again from the dashboard.", ok: false);
            try
            {
                await login.CompleteAsync(code, state, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Tesla sign-in failed: {Error}", ex.Message);
                return Page(da ? "da" : "en", da ? "Login fejlede" : "Sign-in failed", ex.Message, ok: false);
            }

            log.LogInformation("Signed in with Tesla through the browser");
            // Now the telemetry configuration can be sent to the car.
            _ = Task.Run(async () =>
            {
                foreach (var v in opts.Vehicles)
                {
                    try { await telemetry.EnsureFreshAsync(v.Vin, life.ApplicationStopping); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogWarning("Sending the telemetry configuration after sign-in failed: {Error}", ex.Message);
                    }
                }
            }, CancellationToken.None);   // runs on after the sign-in page has answered
            return da
                ? Page("da", "Logget ind hos Tesla", "Bridgen har nu adgang. Du kan lukke dette vindue og gå tilbage til dashboardet.", ok: true)
                : Page("en", "Signed in with Tesla", "The bridge has access now. You can close this window and go back to the dashboard.", ok: true);
        });
    }

    public static void MapSetupApi(this RouteGroupBuilder api, BridgeOptions opts)
    {
        api.MapGet("/auth/start", (LoginFlow login) => Results.Redirect(login.Start()));

        api.MapGet("/setup", async (bool? check, SetupStatusService status, CancellationToken ct) =>
            Results.Content((await status.GetAsync(check, ct)).ToJsonString(Json.Relaxed), "application/json"));

        api.MapPost("/setup/register", async (SetupStatusService status, CancellationToken ct) =>
        {
            try
            {
                var resp = await status.RegisterAsync(ct);
                return Results.Content(resp?.ToJsonString(Json.Relaxed) ?? "{}", "application/json");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ApiErrors.Upstream(ex);
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

    private static IResult Page(string lang, string title, string text, bool ok)
    {
        var color = ok ? "#2fbf71" : "#ef5350";
        var html = $$"""
            <!doctype html><html lang="{{lang}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0d10;color:#e7eaee;font:16px/1.5 -apple-system,BlinkMacSystemFont,sans-serif}
            main{max-width:420px;padding:32px;text-align:center}h1{font-size:20px;margin:16px 0 8px}p{color:#8a93a0;margin:0}
            .dot{width:48px;height:48px;border-radius:50%;margin:0 auto;background:{{color}};opacity:.9}</style></head>
            <body><main><div class="dot"></div><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(text)}}</p></main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: ok ? 200 : 400);
    }
}
