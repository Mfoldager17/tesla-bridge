using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeslaBridge.Auth;
using TeslaBridge.Options;
using TeslaBridge.Storage;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Setup;

/// <summary>
/// The dashboard's setup page: what is in place (key, Tesla app, login) and what Tesla says (partner registration,
/// pairing and telemetry per car). Tesla is asked at most every <see cref="CheckMaxAge"/> unless the page asks for a
/// fresh check, and the last answer is kept in /data/setup-status.json so it survives restarts.
/// </summary>
public sealed class SetupStatusService(
    IOptions<BridgeOptions> options,
    TeslaKey key,
    TeslaTokenProvider tokens,
    TeslaPartnerClient partner,
    TelemetryConfigService telemetry,
    TeslaBridge.Mqtt.BrokerSecurityCheck broker,
    TimeProvider time,
    ILogger<SetupStatusService> log) : IDisposable
{
    /// <summary>How long a check at Tesla is trusted before the setup page refreshes it on its own.</summary>
    public static readonly TimeSpan CheckMaxAge = TimeSpan.FromHours(6);

    private readonly BridgeOptions _opts = options.Value;
    private readonly string _file = Path.Combine(Path.GetDirectoryName(options.Value.StateFile) ?? "/data", "setup-status.json");
    private readonly SemaphoreSlim _checking = new(1, 1);   // one round of calls to Tesla at a time
    private readonly Lock _lock = new();                     // guards the two fields below
    private RegisteredCheck? _registration;
    private Dictionary<string, JsonObject>? _vehicles;

    internal sealed record RegisteredCheck(bool Registered, bool Matches, string? Error, DateTimeOffset At);

    /// <summary>The status for the setup page. <paramref name="check"/>: true = ask Tesla now, false = never, null = when stale.</summary>
    public async Task<JsonObject> GetAsync(bool? check, CancellationToken ct)
    {
        EnsureLoaded();
        var pub = SafeKey(out var keyError);
        var canCheck = pub is not null && HasCredentials(_opts);
        if (check == true || (check is null && canCheck && IsStale()))
        {
            await _checking.WaitAsync(ct);
            try
            {
                // Someone else may have checked while we waited.
                if (check == true || IsStale()) await CheckAsync(pub, ct);
            }
            finally { _checking.Release(); }
        }

        lock (_lock)
        {
            var vehicles = new JsonArray();
            foreach (var v in _opts.Vehicles)
            {
                var o = new JsonObject { ["vin"] = v.Vin, ["name"] = v.Name };
                if (_vehicles!.TryGetValue(v.Vin, out var cached))
                    foreach (var (k, n) in cached) o[k] = n?.DeepClone();
                vehicles.Add(o);
            }
            return new JsonObject
            {
                ["partner_domain"] = _opts.PartnerDomain,
                ["public_key_url"] = _opts.PublicKeyUrl,
                ["redirect_uri"] = _opts.RedirectUri,
                ["pairing_url"] = _opts.PairingUrl,
                ["credentials_set"] = HasCredentials(_opts),
                ["key"] = new JsonObject
                {
                    ["present"] = pub is not null,
                    ["fingerprint"] = pub?.Fingerprint,
                    ["error"] = keyError,
                    ["file"] = _opts.Tesla.FleetKeyFile,
                },
                ["registration"] = Describe(_registration),
                ["logged_in"] = tokens.HasTokens,
                ["vehicles"] = vehicles,
                ["mqtt"] = new JsonObject
                {
                    ["embedded"] = broker.Embedded,
                    ["commands"] = broker.CommandsEnabled,
                    ["anonymous_allowed"] = broker.Current.AnonymousAllowed,
                    ["checked_at"] = broker.Current.CheckedAt?.ToString("O"),
                    ["error"] = broker.Current.Error,
                },
            };
        }
    }

    /// <summary>Registers the partner domain at Tesla and checks the result. Returns Tesla's answer.</summary>
    public async Task<JsonNode?> RegisterAsync(CancellationToken ct)
    {
        EnsureLoaded();
        var resp = await partner.RegisterAsync(ct);
        var registered = await partner.GetRegisteredPublicKeyAsync(ct);
        var pub = key.Public;
        lock (_lock)
        {
            _registration = new RegisteredCheck(registered is not null, pub is not null && registered == pub.UncompressedHex, null, time.GetUtcNow());
            Save();
        }
        return resp;
    }

    private bool IsStale()
    {
        lock (_lock)
            return _registration is null || time.GetUtcNow() - _registration.At > CheckMaxAge
                   || (tokens.HasTokens && _opts.Vehicles.Any(v => !_vehicles!.ContainsKey(v.Vin)));
    }

    private async Task CheckAsync(TeslaKey.PublicKeyInfo? pub, CancellationToken ct)
    {
        RegisteredCheck? registration = null;
        if (pub is not null && HasCredentials(_opts))
        {
            try
            {
                var registered = await partner.GetRegisteredPublicKeyAsync(ct);
                registration = new RegisteredCheck(registered is not null, registered == pub.UncompressedHex, null, time.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                registration = new RegisteredCheck(false, false, ex.Message, time.GetUtcNow());
            }
        }

        var vehicles = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (tokens.HasTokens)
        {
            foreach (var v in _opts.Vehicles)
            {
                var st = new JsonObject { ["checked_at"] = time.GetUtcNow().ToString("O") };
                try
                {
                    var r = (await telemetry.GetStatusAsync(v.Vin, ct))?["response"];
                    st["synced"] = r?["synced"]?.DeepClone();
                    st["key_paired"] = r?["key_paired"]?.DeepClone();
                    st["hostname"] = r?["config"]?["hostname"]?.DeepClone();
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { st["error"] = ex.Message; }
                vehicles[v.Vin] = st;
            }
        }

        lock (_lock)
        {
            if (registration is not null) _registration = registration;
            foreach (var (vin, st) in vehicles) _vehicles![vin] = st;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_vehicles is not null) return;
            (_registration, _vehicles) = Load(_file);
        }
    }

    internal static (RegisteredCheck?, Dictionary<string, JsonObject>) Load(string file)
    {
        var vehicles = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
            {
                RegisteredCheck? reg = null;
                if (o["registration"] is JsonObject r && DateTimeOffset.TryParse((string?)r["checked_at"], out var at))
                    reg = new RegisteredCheck((bool?)r["registered"] ?? false, (bool?)r["matches"] ?? false, (string?)r["error"], at);
                if (o["vehicles"] is JsonObject vs)
                    foreach (var (vin, n) in vs) if (n is JsonObject vo) vehicles[vin] = (JsonObject)vo.DeepClone();
                return (reg, vehicles);
            }
        }
        catch (Exception) { /* a broken file just means "not checked yet" */ }
        return (null, vehicles);
    }

    // Called with _lock held.
    private void Save()
    {
        try
        {
            var vs = new JsonObject();
            foreach (var (vin, o) in _vehicles!) vs[vin] = o.DeepClone();
            var doc = new JsonObject { ["registration"] = Describe(_registration), ["vehicles"] = vs };
            AtomicFile.WriteAllText(_file, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Can't save the setup status to {File}: {Error}", _file, ex.Message);
        }
    }

    private static JsonObject? Describe(RegisteredCheck? r) => r is null ? null : new JsonObject
    {
        ["registered"] = r.Registered,
        ["matches"] = r.Matches,
        ["error"] = r.Error,
        ["checked_at"] = r.At.ToString("O"),
    };

    internal static bool HasCredentials(BridgeOptions o) =>
        !string.IsNullOrWhiteSpace(o.Tesla.ClientId) && !string.IsNullOrWhiteSpace(o.Tesla.ClientSecret) && !string.IsNullOrWhiteSpace(o.PartnerDomain);

    private TeslaKey.PublicKeyInfo? SafeKey(out string? error)
    {
        error = null;
        try { return key.Public; }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public void Dispose() => _checking.Dispose();
}
