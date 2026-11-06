using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using TeslaBridge.Options;

namespace TeslaBridge.Mqtt;

/// <summary>Valgfri indbygget MQTT-broker til lokal test (Bridge:Mqtt:EmbeddedBroker=true).</summary>
public sealed class EmbeddedBroker(IOptions<BridgeOptions> options, ILogger<EmbeddedBroker> log) : IHostedService, IDisposable
{
    private MqttServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value.Mqtt;
        if (!o.EmbeddedBroker) return;

        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(o.Port)
            .WithDefaultEndpointBoundIPV6Address(System.Net.IPAddress.None)   // kun IPv4 – virker også uden IPv6
            .Build();
        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        await _server.StartAsync();
        log.LogInformation("Indbygget MQTT-broker kører på port {Port} (kun til test – ingen adgangskontrol)", o.Port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.StopAsync();
    }

    public void Dispose() => _server?.Dispose();
}
