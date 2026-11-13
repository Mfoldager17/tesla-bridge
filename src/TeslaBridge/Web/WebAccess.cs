using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;
using TeslaBridge.Storage;

namespace TeslaBridge.Web;

/// <summary>
/// Who may use the dashboard and the API. The API can unlock the car, so it always needs a token unless
/// Bridge__Web__AllowAnonymous=true: with no Bridge__Web__Token set, the bridge makes one on first start and keeps it
/// in /data/web-token.
///
/// Home Assistant and scripts send it as <c>Authorization: Bearer …</c>. The dashboard trades it once for an
/// HttpOnly, SameSite=Strict session cookie (POST /api/session), so the token isn't kept in the browser's storage
/// or put in URLs, and other web pages can't make the browser use it.
/// </summary>
public sealed class WebAccess
{
    public const string CookieName = "tb_session";

    private readonly byte[]? _token;
    private readonly byte[]? _session;
    private readonly HashSet<string> _allowedHosts;

    public WebAccess(IOptions<BridgeOptions> options, ILogger<WebAccess> log)
    {
        var web = options.Value.Web;
        _allowedHosts = new HashSet<string>(
            (web.AllowedHosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var token = web.Token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            if (web.AllowAnonymous)
            {
                log.LogWarning("Bridge__Web__AllowAnonymous=true: the dashboard and the API need no token. Anyone who can " +
                               "reach port {Port} can unlock the car – only use this on a network you fully trust.", web.Port);
                return;
            }
            var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.StateFile)) ?? "/data", "web-token");
            token = LoadOrCreate(file, log);
        }
        _token = Encoding.UTF8.GetBytes(token);
        _session = HMACSHA256.HashData(_token, "tesla-bridge dashboard session"u8);
    }

    /// <summary>True when Bridge__Web__AllowAnonymous=true and no token is set.</summary>
    public bool Anonymous => _token is null;

    public bool IsValidToken(string? given) =>
        _token is not null && given is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);

    /// <summary>A bearer token or the dashboard's session cookie.</summary>
    public bool IsAuthenticated(HttpRequest req)
    {
        if (_token is null) return true;
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.Ordinal)) return IsValidToken(auth[7..].Trim());
        return req.Cookies.TryGetValue(CookieName, out var c) && IsValidSession(c);
    }

    public string SessionValue => _session is null ? "" : Convert.ToBase64String(_session);

    private bool IsValidSession(string value)
    {
        Span<byte> buf = stackalloc byte[64];
        return _session is not null && Convert.TryFromBase64String(value, buf, out var n)
               && CryptographicOperations.FixedTimeEquals(buf[..n], _session);
    }

    /// <summary>
    /// Without a token, a web page could reach the API through a host name it controls (DNS rebinding). So then
    /// only IP addresses, localhost, *.local and Bridge__Web__AllowedHosts are answered.
    /// </summary>
    public bool IsAllowedHost(HostString host)
    {
        if (_token is not null || !host.HasValue) return true;
        var name = host.Host.Trim('[', ']');
        return IPAddress.TryParse(name, out _)
               || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
               || _allowedHosts.Contains(name);
    }

    private static string LoadOrCreate(string file, ILogger log)
    {
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: >= 20 } existing)
            {
                log.LogInformation("Dashboard and API token: in {File} (show it with: docker compose exec tesla-bridge cat {File}). " +
                                   "Set Bridge__Web__Token in .env to choose your own.", file, file);
                return existing;
            }
        }
        catch (IOException ex) { log.LogWarning("Can't read {File}: {Error} – making a new token", file, ex.Message); }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20));
        AtomicFile.WriteAllText(file, token + "\n", secret: true);
        log.LogWarning("No Bridge__Web__Token was set, so the bridge made one and saved it in {File}:\n\n    {Token}\n\n" +
                       "Use it to sign in on the dashboard and in the Home Assistant integration, or set your own in .env.", file, token);
        return token;
    }
}
