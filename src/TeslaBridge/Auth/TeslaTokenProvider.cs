using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Auth;

public sealed record StoredTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

/// <summary>
/// Holder et gyldigt Tesla access token. Refresh tokens roterer ved hver fornyelse,
/// så det nye gemmes altid på disk med det samme.
/// </summary>
public sealed class TeslaTokenProvider(
    HttpClient http,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<TeslaTokenProvider> log)
{
    private readonly TeslaOptions _opts = options.Value.Tesla;
    private readonly string _redirectUri = options.Value.RedirectUri;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoredTokens? _tokens;
    private bool _unsaved;   // renewed tokens that couldn't be written yet (e.g. disk full)

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _tokens ??= Load() ?? throw new InvalidOperationException(
                $"Ikke logget ind hos Tesla. Brug 'Log ind med Tesla' under Opsætning i dashboardet.");

            if (_unsaved) TrySave(_tokens);
            if (_tokens.ExpiresAt - time.GetUtcNow() > TimeSpan.FromMinutes(5))
                return _tokens.AccessToken;

            log.LogInformation("Fornyer Tesla access token");
            _tokens = await RequestAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _opts.ClientId,
                ["refresh_token"] = _tokens.RefreshToken,
            }, ct);
            // The old refresh token is now spent, so never let a failed write lose the new one: keep it in
            // memory, use it, and write it as soon as the disk allows.
            TrySave(_tokens);
            return _tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Første login: byt authorization code til tokens.</summary>
    public async Task ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var tokens = await RequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _opts.ClientId,
            ["client_secret"] = _opts.ClientSecret,
            ["code"] = code,
            ["audience"] = _opts.FleetApiAudience,
            ["redirect_uri"] = _redirectUri,
        }, ct);
        Save(tokens);
        _tokens = tokens;
    }

    public bool HasTokens => _tokens is not null || File.Exists(_opts.TokenFile);

    public string BuildAuthorizeUrl(string state)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _opts.ClientId,
            ["redirect_uri"] = _redirectUri,
            ["scope"] = _opts.Scopes,
            ["state"] = state,
            ["prompt_missing_scopes"] = "true",
        };
        return _opts.AuthorizeUrl + "?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private async Task<StoredTokens> RequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await http.PostAsync(_opts.TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tesla token-endpoint svarede {(int)resp.StatusCode}: {body}");

        var json = JsonDocument.Parse(body).RootElement;
        var access = json.GetProperty("access_token").GetString()!;
        var refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString()! : _tokens?.RefreshToken
            ?? throw new InvalidOperationException("Intet refresh_token i svaret – mangler scope 'offline_access'?");
        var expiresIn = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 28800;
        return new StoredTokens(access, refresh, time.GetUtcNow().AddSeconds(expiresIn));
    }

    private StoredTokens? Load() =>
        File.Exists(_opts.TokenFile)
            ? JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(_opts.TokenFile), JsonOpts)
            : null;

    private void TrySave(StoredTokens tokens)
    {
        try
        {
            Save(tokens);
            if (_unsaved) log.LogInformation("Tesla tokens saved to {File} again", _opts.TokenFile);
            _unsaved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unsaved = true;
            log.LogError("Can't save the renewed Tesla tokens to {File}: {Error}. They are kept in memory and saved as soon as possible – " +
                         "free up disk space before the bridge restarts, or you'll have to sign in with Tesla again.", _opts.TokenFile, ex.Message);
            try { File.Delete(_opts.TokenFile + ".tmp"); } catch { /* best effort */ }
        }
    }

    private void Save(StoredTokens tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_opts.TokenFile))!);
        var tmp = _opts.TokenFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(tokens, JsonOpts));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, _opts.TokenFile, overwrite: true);
    }
}

