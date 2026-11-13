using System.Net;
using System.Net.Sockets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using TeslaBridge.Mqtt;

namespace TeslaBridge.Tests;

/// <summary>The warning about a broker that lets anyone in, against a real (in-process) MQTT broker.</summary>
public class BrokerSecurityCheckTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<MqttServer> BrokerAsync(int port, bool requireLogin)
    {
        var server = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPV6Address(IPAddress.None).Build());
        server.ValidatingConnectionAsync += e =>
        {
            if (requireLogin && string.IsNullOrEmpty(e.UserName)) e.ReasonCode = MqttConnectReasonCode.NotAuthorized;
            return Task.CompletedTask;
        };
        await server.StartAsync();
        return server;
    }

    [Fact]
    public async Task An_open_broker_is_reported()
    {
        var port = FreePort();
        using var broker = await BrokerAsync(port, requireLogin: false);
        var r = await BrokerSecurityCheck.TryAnonymousLoginAsync("127.0.0.1", port, default);
        Assert.True(r.AnonymousAllowed);
        await broker.StopAsync();
    }

    [Fact]
    public async Task A_broker_that_requires_a_login_is_fine()
    {
        var port = FreePort();
        using var broker = await BrokerAsync(port, requireLogin: true);
        var r = await BrokerSecurityCheck.TryAnonymousLoginAsync("127.0.0.1", port, default);
        Assert.False(r.AnonymousAllowed);
        Assert.Null(r.Error);
        await broker.StopAsync();
    }

    [Fact]
    public async Task No_broker_means_unknown_not_safe()
    {
        var r = await BrokerSecurityCheck.TryAnonymousLoginAsync("127.0.0.1", FreePort(), default);
        Assert.Null(r.AnonymousAllowed);
        Assert.NotNull(r.Error);
    }
}
