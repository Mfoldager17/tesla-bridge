using System.Buffers;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using TeslaBridge.Options;

namespace TeslaBridge.Mqtt;

public delegate Task MqttMessageHandler(string topic, string payload, CancellationToken ct);

/// <summary>
/// One shared MQTT connection that reconnects by itself. Other services register topic filters and handlers;
/// subscriptions are restored on every reconnect. Sets a Last Will on {bridge}/bridge/status, so HA shows the
/// bridge as unavailable if it dies.
/// </summary>
public sealed class MqttConnection : BackgroundService
{
    private readonly ILogger<MqttConnection> _log;
    private readonly MqttOptions _opts;
    private readonly IMqttClient _client;
    private readonly List<(string Filter, MqttMessageHandler Handler)> _handlers = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MqttConnection(IOptions<BridgeOptions> options, ILogger<MqttConnection> log)
    {
        _log = log;
        _opts = options.Value.Mqtt;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
    }

    public string StatusTopic => $"{_opts.BridgeTopicBase}/bridge/status";

    /// <summary>Raised after every (re)connect – e.g. to send HA discovery again.</summary>
    public event Func<CancellationToken, Task>? Connected;

    public bool IsConnected => _client.IsConnected;

    public Task WaitForConnectionAsync(CancellationToken ct) => _firstConnect.Task.WaitAsync(ct);

    public void Subscribe(string filter, MqttMessageHandler handler)
    {
        lock (_lock) _handlers.Add((filter, handler));
        if (_client.IsConnected)
            _ = SubscribeFiltersAsync([filter], CancellationToken.None).ContinueWith(
                t => _log.LogWarning(t.Exception?.GetBaseException(), "Couldn't subscribe to {Filter}", filter),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default)
    {
        if (!_client.IsConnected) return;
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        try { await _client.PublishAsync(msg, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Couldn't publish to {Topic}", topic);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_client.IsConnected)
            {
                try
                {
                    await ConnectAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("The MQTT connection to {Host}:{Port} failed: {Error}. Trying again in 5 s.",
                        _opts.ConnectHost, _opts.Port, ex.Message);
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_opts.ConnectHost, _opts.Port)
            .WithClientId(_opts.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithWillTopic(StatusTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(_opts.Username))
            builder = builder.WithCredentials(_opts.Username, _opts.Password);

        await _client.ConnectAsync(builder.Build(), ct);
        _log.LogInformation("Connected to MQTT {Host}:{Port}", _opts.ConnectHost, _opts.Port);

        await PublishAsync(StatusTopic, "online", retain: true, ct);

        string[] filters;
        lock (_lock) filters = _handlers.Select(h => h.Filter).Distinct().ToArray();
        await SubscribeFiltersAsync(filters, ct);

        _firstConnect.TrySetResult();
        if (Connected is { } connected)
        {
            foreach (var h in connected.GetInvocationList().Cast<Func<CancellationToken, Task>>())
            {
                try { await h(ct); }
                catch (Exception ex) { _log.LogError(ex, "Error in a Connected handler"); }
            }
        }
    }

    private async Task SubscribeFiltersAsync(IReadOnlyCollection<string> filters, CancellationToken ct)
    {
        if (filters.Count == 0) return;
        var b = new MqttClientSubscribeOptionsBuilder();
        foreach (var f in filters)
            b.WithTopicFilter(f, MqttQualityOfServiceLevel.AtLeastOnce);
        await _client.SubscribeAsync(b.Build(), ct);
        _log.LogDebug("Subscribing to {Filters}", string.Join(", ", filters));
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());

        MqttMessageHandler[] matches;
        lock (_lock) matches = _handlers.Where(h => TopicMatches(h.Filter, topic)).Select(h => h.Handler).ToArray();

        foreach (var handler in matches)
        {
            try { await handler(topic, payload, CancellationToken.None); }
            catch (Exception ex) { _log.LogError(ex, "Error while handling {Topic}", topic); }
        }
    }

    /// <summary>MQTT wildcard match (+ and #).</summary>
    public static bool TopicMatches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] != "+" && f[i] != t[i]) return false;
        }
        return f.Length == t.Length;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            await PublishAsync(StatusTopic, "offline", retain: true, cancellationToken);
            await _client.DisconnectAsync(cancellationToken: cancellationToken);
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
