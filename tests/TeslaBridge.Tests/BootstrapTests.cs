using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TeslaBridge.Auth;
using TeslaBridge.Options;
using TeslaBridge.Setup;

namespace TeslaBridge.Tests;

public class BootstrapTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bootstrap").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private BridgeOptions Options()
    {
        var o = new BridgeOptions
        {
            StateFile = Path.Combine(_dir, "data", "state.json"),
            Mqtt = { Host = "192.168.0.20", Port = 1883 },
            Telemetry =
            {
                Hostname = "tesla.example.com",
                ServerConfigFile = Path.Combine(_dir, "config", "fleet-telemetry", "config.json"),
                MqttPassword = "p\"w&d",
            },
            Tesla =
            {
                ClientId = "abc",
                FleetKeyFile = Path.Combine(_dir, "config", "tesla-proxy", "fleet-key.pem"),
                ProxyCertFile = Path.Combine(_dir, "config", "tesla-proxy", "tls-cert.pem"),
                ProxyKeyFile = Path.Combine(_dir, "config", "tesla-proxy", "tls-key.pem"),
            },
        };
        o.Vehicles.Add(new VehicleOptions { Vin = "VIN1" });
        return o;
    }

    [Fact]
    public void Creates_missing_files_from_nothing()
    {
        var o = Options();
        var r = Bootstrap.Run(o);

        Assert.Equal(2, r.Created.Count);
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", File.ReadAllText(o.Tesla.FleetKeyFile));
        Assert.NotNull(TeslaKey.FromPrivatePem(File.ReadAllText(o.Tesla.FleetKeyFile)));   // P-256, ellers kaster den

        // Bridgen pinner proxy-certifikatet sådan her
        using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(o.Tesla.ProxyCertFile), File.ReadAllText(o.Tesla.ProxyKeyFile));
        Assert.Contains("tesla-proxy", cert.Subject);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(o.Tesla.FleetKeyFile));

        Assert.Contains(r.Warnings, w => w.Contains("fullchain.pem"));
    }

    [Fact]
    public void Never_replaces_an_existing_key()
    {
        var o = Options();
        Bootstrap.Run(o);
        var key = File.ReadAllText(o.Tesla.FleetKeyFile);
        var cert = File.ReadAllText(o.Tesla.ProxyCertFile);

        var r = Bootstrap.Run(o);

        Assert.Empty(r.Created);
        Assert.Equal(key, File.ReadAllText(o.Tesla.FleetKeyFile));
        Assert.Equal(cert, File.ReadAllText(o.Tesla.ProxyCertFile));
    }

    [Fact]
    public void Server_config_follows_mqtt_settings()
    {
        var o = Options();
        Bootstrap.Run(o);
        var mqtt = JsonNode.Parse(File.ReadAllText(o.Telemetry.ServerConfigFile))!["mqtt"]!;

        Assert.Equal("192.168.0.20:1883", (string?)mqtt["broker"]);
        Assert.Equal("fleet-telemetry", (string?)mqtt["username"]);
        Assert.Equal("p\"w&d", (string?)mqtt["password"]);
        Assert.Equal("telemetry", (string?)mqtt["topic_base"]);

        o.Mqtt.Host = "10.0.0.5";
        Bootstrap.Run(o);
        Assert.Equal("10.0.0.5:1883", (string?)JsonNode.Parse(File.ReadAllText(o.Telemetry.ServerConfigFile))!["mqtt"]!["broker"]);
    }

    [Fact]
    public void Warns_about_missing_settings()
    {
        var o = Options();
        o.Vehicles.Clear();
        o.Telemetry.Hostname = "";
        var r = Bootstrap.Run(o);
        Assert.Contains(r.Warnings, w => w.Contains("Bridge__Vehicles__0__Vin"));
        Assert.Contains(r.Warnings, w => w.Contains("Hostname"));
    }
}
