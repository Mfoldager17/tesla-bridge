using System.Net;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using TeslaBridge.Options;

namespace TeslaBridge.Mqtt;

/// <summary>
/// Valgfri indbygget MQTT-broker (Bridge__Mqtt__EmbeddedBroker=true), så man ikke behøver Mosquitto.
/// fleet-telemetry sender til den over docker-netværket; bridgen selv forbinder via 127.0.0.1.
/// Porten publiceres ikke i docker-compose som standard. Gør man det (fx så HA kan bruge den),
/// gælder brugerne fra .env: Bridge__Mqtt__Username/Password og Bridge__Telemetry__MqttUsername/MqttPassword.
/// </summary>
public sealed class EmbeddedBroker(IOptions<BridgeOptions> options, ILogger<EmbeddedBroker> log) : IHostedService, IDisposable
{
    private MqttServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Mqtt.EmbeddedBroker) return;

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(o.Mqtt.Port)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.None)   // kun IPv4 – virker også uden IPv6
            .Build();
        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        var users = Users(o);
        _server.ValidatingConnectionAsync += e =>
        {
            if (!Allowed(users, e.UserName, e.Password, e.RemoteEndPoint))
            {
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                log.LogWarning("Afviste MQTT-login fra {Endpoint} (bruger '{User}')", e.RemoteEndPoint, e.UserName);
            }
            return Task.CompletedTask;
        };
        await _server.StartAsync();
        log.LogInformation("Indbygget MQTT-broker kører på port {Port} ({Auth})", o.Mqtt.Port,
            users.Count == 0 ? "ingen kodeord sat – kun til det interne docker-netværk" : $"{users.Count} bruger(e)");
    }

    internal static Dictionary<string, string> Users(BridgeOptions o)
    {
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(o.Mqtt.Username) && !string.IsNullOrEmpty(o.Mqtt.Password))
            users[o.Mqtt.Username] = o.Mqtt.Password;
        if (!string.IsNullOrEmpty(o.Telemetry.MqttUsername) && !string.IsNullOrEmpty(o.Telemetry.MqttPassword))
            users[o.Telemetry.MqttUsername] = o.Telemetry.MqttPassword;
        return users;
    }

    /// <summary>Ingen kodeord sat → alle må. Ellers skal bruger/kodeord passe; bridgen selv (loopback) må altid.</summary>
    internal static bool Allowed(Dictionary<string, string> users, string? user, string? password, EndPoint? remote)
    {
        if (users.Count == 0) return true;
        if (remote is IPEndPoint ip && IPAddress.IsLoopback(ip.Address)) return true;
        return user is not null && users.TryGetValue(user, out var expected) &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(password ?? ""));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync();
    }

    public void Dispose() => _server?.Dispose();
}
