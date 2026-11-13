using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using TeslaBridge.Options;

namespace TeslaBridge.Mqtt;

/// <summary>
/// Optional MQTT broker inside the bridge (Bridge__Mqtt__EmbeddedBroker=true), so no Mosquitto is needed.
/// fleet-telemetry sends to it over the docker network; the bridge itself connects through 127.0.0.1.
///
/// Nobody logs in without a password, and each login may only do its own job:
/// <list type="bullet">
/// <item>fleet-telemetry (Bridge__Telemetry__MqttUsername) may only publish raw data under telemetry/…</item>
/// <item>Bridge__Mqtt__Username (e.g. Home Assistant, if you publish the port) may do everything, commands included</item>
/// <item>the bridge itself, from inside the container, may do everything</item>
/// </list>
/// </summary>
public sealed class EmbeddedBroker(IOptions<BridgeOptions> options, ILogger<EmbeddedBroker> log) : IHostedService, IDisposable
{
    public enum Role { Telemetry, Full }

    private MqttServer? _server;
    private readonly ConcurrentDictionary<string, Role> _clients = new(StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Mqtt.EmbeddedBroker) return;

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(o.Mqtt.Port)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)   // IPv4 only – also works without IPv6
            .Build();
        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        var users = Users(o);
        var telemetryPrefix = o.Mqtt.TelemetryTopicBase.TrimEnd('/') + "/";

        _server.ValidatingConnectionAsync += e =>
        {
            if (Authenticate(users, e.UserName, e.Password, e.RemoteEndPoint) is { } role)
                _clients[e.ClientId] = role;
            else
            {
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                log.LogWarning("Refused an MQTT login from {Endpoint} (user '{User}')", e.RemoteEndPoint, e.UserName);
            }
            return Task.CompletedTask;
        };
        _server.ClientDisconnectedAsync += e =>
        {
            _clients.TryRemove(e.ClientId, out _);
            return Task.CompletedTask;
        };
        _server.InterceptingPublishAsync += e =>
        {
            if (!MayPublish(RoleOf(e.ClientId), e.ApplicationMessage.Topic, telemetryPrefix))
            {
                e.ProcessPublish = false;
                log.LogWarning("Dropped a message from MQTT client {Client} to {Topic}: it may only publish under {Prefix}",
                    e.ClientId, e.ApplicationMessage.Topic, telemetryPrefix);
            }
            return Task.CompletedTask;
        };
        _server.InterceptingSubscriptionAsync += e =>
        {
            if (RoleOf(e.ClientId) == Role.Telemetry)
            {
                e.ProcessSubscription = false;
                e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
            }
            return Task.CompletedTask;
        };

        await _server.StartAsync();
        log.LogInformation("Embedded MQTT broker running on port {Port} ({Count} login(s); fleet-telemetry may only publish telemetry)",
            o.Mqtt.Port, users.Count);
    }

    // The bridge's own in-process messages have no client id; they may do everything.
    private Role RoleOf(string? clientId) =>
        clientId is not null && _clients.TryGetValue(clientId, out var r) ? r : Role.Full;

    internal static Dictionary<string, (string Password, Role Role)> Users(BridgeOptions o)
    {
        var users = new Dictionary<string, (string, Role)>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(o.Telemetry.MqttUsername) && !string.IsNullOrEmpty(o.Telemetry.MqttPassword))
            users[o.Telemetry.MqttUsername] = (o.Telemetry.MqttPassword, Role.Telemetry);
        if (!string.IsNullOrEmpty(o.Mqtt.Username) && !string.IsNullOrEmpty(o.Mqtt.Password))
            users[o.Mqtt.Username] = (o.Mqtt.Password, Role.Full);
        return users;
    }

    /// <summary>
    /// The role for a login, or null when it's refused. The bridge itself (loopback) needs no password; everyone
    /// else needs a user and password from .env – there are no anonymous logins.
    /// </summary>
    internal static Role? Authenticate(Dictionary<string, (string Password, Role Role)> users, string? user, string? password, EndPoint? remote)
    {
        if (remote is IPEndPoint ip && IPAddress.IsLoopback(ip.Address)) return Role.Full;   // the bridge itself
        if (user is null || !users.TryGetValue(user, out var u)) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(u.Password), Encoding.UTF8.GetBytes(password ?? ""))
            ? u.Role : null;
    }

    internal static bool MayPublish(Role role, string topic, string telemetryPrefix) =>
        role == Role.Full || topic.StartsWith(telemetryPrefix, StringComparison.Ordinal);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync();
    }

    public void Dispose() => _server?.Dispose();
}
