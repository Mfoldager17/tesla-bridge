using TeslaBridge.Options;

namespace TeslaBridge.Web;

/// <summary>The dashboard's HTTP protections: token check, cross-site requests, host names and browser headers.</summary>
public static class WebSecurity
{
    /// <summary>API paths that answer without a token (they decide themselves what they show).</summary>
    private static readonly string[] Open = ["/api/info", "/api/session"];

    public static void UseBridgeSecurity(this WebApplication app, WebAccess access, BridgeOptions opts)
    {
        var csp = ContentSecurityPolicy(opts);
        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";   // OpenStreetMap's tiles need the origin
            h.ContentSecurityPolicy = csp;

            // The public listener is reached through the partner domain – any name is fine there.
            if (ctx.Connection.LocalPort != opts.Web.PublicPort && !access.IsAllowedHost(req.Host))
            {
                await Deny(ctx, StatusCodes.Status403Forbidden,
                    $"Unknown host name '{req.Host.Host}'. Open the dashboard by its IP address, or add the name to Bridge__Web__AllowedHosts.");
                return;
            }

            if (req.Path.StartsWithSegments("/api"))
            {
                // Another web site must never be able to make the browser change something here.
                if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && IsCrossSite(req))
                {
                    await Deny(ctx, StatusCodes.Status403Forbidden, "Cross-site requests are not allowed");
                    return;
                }
                if (!Open.Any(p => req.Path.Equals(p, StringComparison.OrdinalIgnoreCase)) && !access.IsAuthenticated(req))
                {
                    await Deny(ctx, StatusCodes.Status401Unauthorized, "A valid token is required");
                    return;
                }
            }
            await next();
        });
    }

    /// <summary>A browser request started by another site (Sec-Fetch-Site), or with an Origin that isn't this host.</summary>
    internal static bool IsCrossSite(HttpRequest req)
    {
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (site is "cross-site") return true;
        var origin = req.Headers.Origin.ToString();
        if (origin.Length == 0 || origin == "null") return origin == "null";
        return !Uri.TryCreate(origin, UriKind.Absolute, out var o)
               || !string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ContentSecurityPolicy(BridgeOptions opts)
    {
        // The map's tiles are the only thing loaded from elsewhere.
        var tiles = "https://tile.openstreetmap.org";
        if (!string.IsNullOrWhiteSpace(opts.Map.TileUrl)
            && Uri.TryCreate(opts.Map.TileUrl.Replace("{s}.", "s.").Replace("{s}", "s"), UriKind.Absolute, out var u))
            tiles = opts.Map.TileUrl.Contains("{s}") ? $"{u.Scheme}://*.{string.Join('.', u.Host.Split('.').Skip(1))}" : $"{u.Scheme}://{u.Authority}";
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self' 'wasm-unsafe-eval'",       // the 3D model's mesh decoder is WebAssembly
            "style-src 'self' 'unsafe-inline'",              // Solid and Leaflet set element styles
            $"img-src 'self' data: blob: {tiles}",
            "connect-src 'self' data: blob:",                // three.js reads textures inside the .glb through blob: URLs
            "font-src 'self' data:",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    /// <summary>POST /api/session</summary>
    public sealed record SessionRequest(string? Token);

    /// <summary>
    /// POST /api/session { token } → an HttpOnly, SameSite=Strict session cookie for the dashboard, so it never has to
    /// keep or send the token itself. DELETE /api/session signs out.
    /// </summary>
    public static void MapSessionApi(this RouteGroupBuilder api, WebAccess access)
    {
        api.MapPost("/session", async (HttpContext ctx, SessionRequest? body) =>
        {
            if (access.Anonymous) return Results.NoContent();
            if (!access.IsValidToken(body?.Token?.Trim()))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));   // slows down guessing
                return ApiErrors.Problem(StatusCodes.Status401Unauthorized, "Wrong token");
            }
            ctx.Response.Cookies.Append(WebAccess.CookieName, access.SessionValue, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = ctx.Request.IsHttps,
                Path = "/",
                MaxAge = TimeSpan.FromDays(400),
            });
            return Results.NoContent();
        });
        api.MapDelete("/session", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(WebAccess.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });
    }

    /// <summary>Only cars the bridge knows: a typo or a guess gets a 404 instead of a call to Tesla.</summary>
    public static RouteGroupBuilder OnlyKnownVehicles(this RouteGroupBuilder group, BridgeOptions opts)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var vin = ctx.HttpContext.Request.RouteValues["vin"] as string;
            return opts.Vehicles.Any(v => string.Equals(v.Vin, vin, StringComparison.OrdinalIgnoreCase))
                ? await next(ctx)
                : ApiErrors.NotFound($"Unknown VIN '{vin}'");
        });
        return group;
    }

    private static Task Deny(HttpContext ctx, int status, string message) =>
        ApiErrors.Problem(status, message).ExecuteAsync(ctx);
}
