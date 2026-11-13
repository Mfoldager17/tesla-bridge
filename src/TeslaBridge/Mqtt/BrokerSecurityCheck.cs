using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Exceptions;
using MQTTnet.Protocol;
using TeslaBridge.Options;

namespace TeslaBridge.Mqtt;

/// <summary>
/// Anyone who can publish to tesla/&lt;vin&gt;/cmd/… on the broker can send commands to the car. The bridge can't
/// configure your own broker (Mosquitto), but it can find out whether it lets anyone in: shortly after starting,
/// and then every six hours, it tries to log in without a username or password. If that works, it warns in the log
/// and on the dashboard's setup page. The embedded broker never accepts anonymous logins, so it isn't checked.
/// </summary>
public sealed class BrokerSecurityCheck(
    MqttConnection mqtt,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<BrokerSecurityCheck> log) : BackgroundService
{
    /// <param name="AnonymousAllowed">true = anyone can log in; false = refused; null = not checked yet / couldn't tell.</param>
    public sealed record Result(bool? AnonymousAllowed, DateTimeOffset? CheckedAt, string? Error);

    private readonly MqttOptions _opts = options.Value.Mqtt;

    public Result Current { get; private set; } = new(null, null, null);

    /// <summary>The embedded broker refuses anonymous logins by design (see EmbeddedBroker).</summary>
    public bool Embedded => _opts.EmbeddedBroker;

    /// <summary>Whether the bridge takes commands over MQTT at all (Bridge__Mqtt__Commands).</summary>
    public bool CommandsEnabled => _opts.Commands;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opts.EmbeddedBroker)
        {
            Current = new Result(false, time.GetUtcNow(), null);
            return;
        }
        try
        {
            await mqtt.WaitForConnectionAsync(stoppingToken);   // the broker is up and our own login works
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(6), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task<Result> CheckAsync(CancellationToken ct)
    {
        var anonymous = await TryAnonymousLoginAsync(_opts.ConnectHost, _opts.Port, ct);
        Current = anonymous with { CheckedAt = time.GetUtcNow() };

        if (Current.AnonymousAllowed == true && _opts.Commands)
            log.LogWarning("The MQTT broker {Host}:{Port} accepts logins without a password. Then anyone on your network can " +
                           "send commands to the car over MQTT (tesla/<vin>/cmd/…), e.g. unlock it. Require logins in the broker " +
                           "(Mosquitto: allow_anonymous false + password_file), or set Bridge__Mqtt__Commands=false. See docs/security.md.",
                _opts.ConnectHost, _opts.Port);
        else if (Current.AnonymousAllowed == true)
            log.LogInformation("The MQTT broker {Host}:{Port} accepts logins without a password. MQTT commands are off " +
                               "(Bridge__Mqtt__Commands=false), so the car can't be controlled that way, but anyone can read its data there.",
                _opts.ConnectHost, _opts.Port);
        else if (Current.AnonymousAllowed == false)
            log.LogDebug("The MQTT broker refuses logins without a password – good");
        return Current;
    }

    /// <summary>Tries to log in to the broker with no username or password, with a client id of its own.</summary>
    internal static async Task<Result> TryAnonymousLoginAsync(string host, int port, CancellationToken ct)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var connect = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId("tesla-bridge-check-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)))
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(8))
            .Build();
        try
        {
            var result = await client.ConnectAsync(connect, ct);
            var allowed = result.ResultCode == MqttClientConnectResultCode.Success;
            if (allowed) await client.DisconnectAsync(cancellationToken: ct);
            return new Result(allowed, null, null);
        }
        catch (MqttConnectingFailedException)
        {
            // The broker answered the login and said no (not authorised / bad username or password).
            return new Result(false, null, null);
        }
        catch (Exception ex) when (ex is MqttCommunicationException or TimeoutException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new Result(null, null, ex.Message);
        }
    }
}
