using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Versioning;

/// <summary>What the newest release is, compared to the running version.</summary>
public sealed record UpdateInfo(
    string Installed,
    string Channel,
    string? Latest,
    bool UpdateAvailable,
    string? ReleaseUrl,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? CheckedAt,
    string? Error);

/// <summary>
/// Looks up the newest release on GitHub every few hours (one anonymous request – nothing about you is sent)
/// and publishes it for Home Assistant's update entity and the dashboard. Turn off with
/// Bridge__Updates__Check=false. The bridge never updates itself: that stays `docker compose pull`.
/// </summary>
public sealed class UpdateCheckService(
    IOptions<BridgeOptions> options,
    MqttConnection mqtt,
    IHttpClientFactory http,
    TimeProvider time,
    ILogger<UpdateCheckService> log) : BackgroundService
{
    private readonly UpdatesOptions _opts = options.Value.Updates;
    private UpdateInfo _info = new(BuildInfo.Version, BuildInfo.Channel, null, false, null, null, null, null, null);

    public UpdateInfo Current => _info;

    /// <summary>{bridge}/bridge/update – the state of HA's update entity.</summary>
    public string Topic { get; } = $"{options.Value.Mqtt.BridgeTopicBase}/bridge/update";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.Connected += PublishAsync;
        if (!_opts.Check) { await PublishAsync(stoppingToken); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(20), time, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromHours(Math.Max(1, _opts.IntervalHours)), time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient("github");
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"tesla-bridge/{BuildInfo.Version}");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var res = await client.GetAsync($"https://api.github.com/repos/{_opts.Repository}/releases/latest", ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _info = _info with { CheckedAt = time.GetUtcNow(), Error = null };   // no releases yet
            }
            else
            {
                res.EnsureSuccessStatusCode();
                var o = await res.Content.ReadFromJsonAsync<JsonObject>(ct) ?? [];
                var tag = o["tag_name"]?.GetValue<string>();
                var latest = tag?.TrimStart('v');
                var notes = o["body"]?.GetValue<string>();
                _info = _info with
                {
                    Latest = latest,
                    UpdateAvailable = latest is not null && BuildInfo.Parse(latest) > BuildInfo.Parse(BuildInfo.Version),
                    ReleaseUrl = o["html_url"]?.GetValue<string>(),
                    ReleaseNotes = notes is { Length: > 6000 } ? notes[..6000] + "\n…" : notes,
                    PublishedAt = o["published_at"] is JsonValue p && p.TryGetValue(out DateTimeOffset at) ? at : null,
                    CheckedAt = time.GetUtcNow(),
                    Error = null,
                };
                if (_info.UpdateAvailable) log.LogInformation("Tesla Bridge {Latest} is available (running {Installed}): {Url}", latest, BuildInfo.Display, _info.ReleaseUrl);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            _info = _info with { CheckedAt = time.GetUtcNow(), Error = ex.Message };
            log.LogDebug("Update check failed: {Error}", ex.Message);
        }
        await PublishAsync(ct);
        return _info;
    }

    /// <summary>The JSON HA's MQTT update entity reads (installed_version, latest_version …).</summary>
    public JsonObject StatePayload() => new()
    {
        ["installed_version"] = BuildInfo.Version,
        // No known release (yet, or checking is off): the same as installed, so HA shows "up to date".
        ["latest_version"] = _info.Latest ?? BuildInfo.Version,
        ["title"] = "Tesla Bridge (server)",
        ["release_url"] = _info.ReleaseUrl,
        ["release_summary"] = Summary(_info.ReleaseNotes),
    };

    /// <summary>The bridge runs in Docker, so HA can't install it – the summary says where it's updated.</summary>
    public const string HowToUpdate = "Updated on the server, not in Home Assistant: docker compose pull && docker compose up -d. ";

    private static string Summary(string? notes)
    {
        var text = HowToUpdate + (notes?.Trim() ?? "");
        return text.Length <= 250 ? text.TrimEnd() : text[..247] + "…";   // HA allows 255 characters
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        if (!mqtt.IsConnected) return;
        try { await mqtt.PublishAsync(Topic, StatePayload().ToJsonString(), retain: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "Could not publish the update state"); }
    }
}
