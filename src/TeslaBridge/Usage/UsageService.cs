using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Usage;

/// <summary>Gemmer forbruget på disk og udgiver det på {bridge}/bridge/usage, så HA kan vise det.</summary>
public sealed class UsageService(
    UsageTracker tracker,
    MqttConnection mqtt,
    IOptions<BridgeOptions> options,
    ILogger<UsageService> log) : BackgroundService
{
    private readonly string _file = options.Value.UsageFile;
    private readonly string _topic = $"{options.Value.Mqtt.BridgeTopicBase}/bridge/usage";

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_file))
        {
            try
            {
                tracker.Restore(File.ReadAllText(_file));
                log.LogInformation("Indlæste forbrug fra {File}", _file);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Kunne ikke læse {File} – starter forfra", _file);
            }
        }
        mqtt.Connected += PublishAsync;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changed = tracker.IsDirty;
                Save();
                if (changed) await PublishAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            mqtt.Connected -= PublishAsync;
            Save();
        }
    }

    private void Save()
    {
        if (!tracker.IsDirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, tracker.Serialize());
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            tracker.MarkDirty();
            log.LogWarning(ex, "Kunne ikke gemme forbrug til {File}", _file);
        }
    }

    private Task PublishAsync(CancellationToken ct) =>
        mqtt.PublishAsync(_topic, ToJson(tracker.Snapshot()).ToJsonString(Json.Relaxed), retain: true, ct);

    public static JsonObject ToJson(UsageSnapshot s)
    {
        var cats = new JsonObject();
        foreach (var c in s.Categories)
            cats[c.Key] = new JsonObject { ["name"] = c.Name, ["count"] = c.Count, ["cost_usd"] = Math.Round(c.CostUsd, 4), ["rate"] = c.Rate };

        static JsonObject Map(IReadOnlyDictionary<string, long> d)
        {
            var o = new JsonObject();
            foreach (var (k, v) in d) o[k] = v;
            return o;
        }

        var history = new JsonArray();
        foreach (var (month, total) in s.History)
            history.Add(new JsonObject { ["month"] = month, ["total_usd"] = Math.Round(total, 4) });

        return new JsonObject
        {
            ["month"] = s.Month,
            ["month_start"] = s.MonthStart.ToString("O"),
            ["month_end"] = s.MonthEnd.ToString("O"),
            ["total_usd"] = Math.Round(s.TotalUsd, 4),
            ["projected_usd"] = s.ProjectedUsd is { } p ? Math.Round(p, 2) : null,
            ["credit_usd"] = s.CreditUsd,
            ["categories"] = cats,
            ["signals_by_field"] = Map(s.SignalsByField),
            ["commands_by_name"] = Map(s.CommandsByName),
            ["data_by_name"] = Map(s.DataByName),
            ["ble_by_name"] = Map(s.BleByName ?? new Dictionary<string, long>()),
            ["ble_saved_usd"] = Math.Round(s.BleSavedUsd, 4),
            ["history"] = history,
        };
    }
}
