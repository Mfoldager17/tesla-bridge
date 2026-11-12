using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TeslaBridge.Ble;

/// <summary>One entity on the ESP32 as its web server reports it.</summary>
public sealed record EspEntity(string Domain, string UrlName, string State, JsonNode? Value, DateTimeOffset At);

/// <summary>
/// Talks to one ESPHome device over its built-in web server (the <c>web_server:</c> component):
/// live states from <c>/events</c> (Server-Sent Events) and actions as <c>POST /{domain}/{name}/{action}</c>.
/// Works with both of ESPHome's id formats: the current <c>"switch/Climate"</c> (name based) and the older
/// <c>"switch-climate"</c> (object id). Entities are looked up by name, case and punctuation ignored.
/// </summary>
public sealed class EspHomeDevice : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, EspEntity> _entities = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public EspHomeDevice(Uri baseUrl, string? username, string? password, ILogger log, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        BaseUrl = baseUrl;
        _log = log;
        _time = time ?? TimeProvider.System;
        handler ??= new SocketsHttpHandler
        {
            // ESPHome's web_server uses digest (ESP32) or basic auth; NetworkCredential answers either challenge.
            Credentials = string.IsNullOrEmpty(username) ? null : new NetworkCredential(username, password ?? ""),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = Timeout.InfiniteTimeSpan };
    }

    public Uri BaseUrl { get; }

    /// <summary>True while the event stream from the ESP32 is open.</summary>
    public bool Connected { get; private set; }

    public string? LastError { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>A state changed (or arrived for the first time). Called without locks held.</summary>
    public event Action<EspEntity>? StateChanged;

    /// <summary>The event stream opened or closed.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>How long without any event (ESPHome pings every ~10 s) before the stream is considered dead.</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    public EspEntity? Find(string domain, string name)
    {
        lock (_lock) return _entities.GetValueOrDefault(Key(domain, name));
    }

    public IReadOnlyList<EspEntity> Entities()
    {
        lock (_lock) return _entities.Values.ToList();
    }

    /// <summary>
    /// Runs an action, e.g. ("switch","Climate","turn_on") or ("number","Charging limit","set", "value=80").
    /// Returns null on success, otherwise why it failed. The entity must have been seen on the event stream.
    /// </summary>
    public async Task<string?> ActAsync(string domain, string name, string action, string? query, CancellationToken ct)
    {
        var e = Find(domain, name);
        if (e is null) return $"the ESP32 has no {domain} called \"{name}\"";
        var path = $"/{domain}/{string.Join('/', e.UrlName.Split('/').Select(Uri.EscapeDataString))}/{action}";
        if (!string.IsNullOrEmpty(query)) path += "?" + query;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var res = await _http.PostAsync(path, null, cts.Token);
            if (res.IsSuccessStatusCode) return null;
            return $"ESP32 answered {(int)res.StatusCode} for {path}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"ESP32 not reachable: {ex.Message}";
        }
    }

    // ---------- event stream ----------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReadStreamAsync(ct);
                LastError = "the ESP32 closed the event stream";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex is TimeoutException ? "no events from the ESP32 for a minute" : ex.Message;
                _log.LogDebug("ESP32 {Url}: {Error}", BaseUrl, LastError);
            }
            SetConnected(false);
            try { await Task.Delay(RetryDelay, _time, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReadStreamAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Accept.ParseAdd("text/event-stream");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("the ESP32 wants a username and password (web_server auth)");
        if (res.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("no /events on the ESP32 – is web_server: in its YAML?");
        res.EnsureSuccessStatusCode();

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        SetConnected(true);
        LastError = null;

        string evt = "message";
        var data = new StringBuilder();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(); }
            if (line is null) return;

            if (line.Length == 0)
            {
                if (data.Length > 0) Handle(evt, data.ToString());
                evt = "message"; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');
            if (field == "event") evt = value;
            else if (field == "data") { if (data.Length > 0) data.Append('\n'); data.Append(value); }
        }
    }

    internal void Handle(string evt, string data)
    {
        LastEventAt = _time.GetUtcNow();
        if (evt != "state") return;
        JsonObject? o;
        try { o = JsonNode.Parse(data) as JsonObject; } catch (JsonException) { return; }
        if (o is null) return;

        // name_id (ESPHome 2026.1–2026.7) or an id with a "/" (2026.8+) is "domain/Entity Name"; older ids are "domain-object_id".
        var id = o["name_id"]?.GetValue<string>() ?? o["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id)) return;
        string domain, urlName;
        var slash = id.IndexOf('/');
        if (slash > 0) { domain = id[..slash]; urlName = id[(slash + 1)..]; }
        else
        {
            var dash = id.IndexOf('-');
            if (dash <= 0) return;
            domain = id[..dash]; urlName = id[(dash + 1)..];
        }
        var displayName = urlName.Contains('/') ? urlName[(urlName.LastIndexOf('/') + 1)..] : urlName;

        var entity = new EspEntity(domain, urlName, o["state"]?.ToString() ?? "", o["value"]?.DeepClone(), _time.GetUtcNow());
        lock (_lock) _entities[Key(domain, displayName)] = entity;
        StateChanged?.Invoke(entity);
    }

    private void SetConnected(bool on)
    {
        if (Connected == on) return;
        Connected = on;
        ConnectionChanged?.Invoke(on);
    }

    /// <summary>"Charging limit" and "charging_limit" are the same entity.</summary>
    internal static string Key(string domain, string name)
    {
        var sb = new StringBuilder(domain.ToLowerInvariant()).Append(':');
        foreach (var c in name.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    // ---------- reading values ----------

    /// <summary>A number from a state like {"value": 80, "state": "80 %"}; null for NaN/unknown.</summary>
    public static double? Number(EspEntity? e)
    {
        if (e is null) return null;
        if (e.Value is JsonValue v)
        {
            if (v.TryGetValue(out double d)) return double.IsFinite(d) ? d : null;
            if (v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        }
        var first = e.State.Split(' ', 2)[0];
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p) ? p : null;
    }

    /// <summary>The unit after the number in "state" ("12345.6 mi" → "mi").</summary>
    public static string Unit(EspEntity? e) => e?.State.Split(' ', 2) is [_, var u] ? u.Trim() : "";

    /// <summary>ON/OFF, true/false, LOCKED/OPEN …; null when unknown.</summary>
    public static bool? Bool(EspEntity? e)
    {
        if (e is null) return null;
        if (e.Value is JsonValue v && v.TryGetValue(out bool b)) return b;
        return e.State.ToUpperInvariant() switch
        {
            "ON" or "OPEN" or "LOCKED" or "TRUE" => true,
            "OFF" or "CLOSED" or "UNLOCKED" or "FALSE" => false,
            _ => null,
        };
    }

    /// <summary>Text state; null for empty or unknown.</summary>
    public static string? Text(EspEntity? e)
    {
        var s = e?.Value is JsonValue v && v.TryGetValue(out string? t) ? t : e?.State;
        return string.IsNullOrWhiteSpace(s) || s is "unknown" or "Unknown" or "NA" or "nan" ? null : s.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) try { await _loop; } catch { /* stopping */ }
        _http.Dispose();
        _stop.Dispose();
    }
}
