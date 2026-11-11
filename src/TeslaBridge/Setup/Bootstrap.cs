using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Setup;

/// <summary>
/// Laver alt det, der før krævede scripts og repo-filer, så serveren kun skal have docker-compose.yml + .env:
/// <list type="bullet">
/// <item>appens nøglepar (P-256), hvis det mangler</item>
/// <item>intern TLS mellem bridge og tesla-proxy, hvis den mangler</item>
/// <item>fleet-telemetrys config.json ud fra MQTT-indstillingerne (skrives hver gang)</item>
/// </list>
/// Køres som "init" (root) før de andre containere starter, og igen ved almindelig opstart (ufarligt).
/// Som root gives filerne til PUID:PGID, så tesla-proxy, fleet-telemetry og bridgen kan læse dem.
/// </summary>
public static class Bootstrap
{
    public sealed record Result(List<string> Created, List<string> Warnings);

    public static Result Run(BridgeOptions o)
    {
        var created = new List<string>();
        var warnings = new List<string>();

        // Appens nøgle: SEC1 "EC PRIVATE KEY", P-256 – samme format som openssl ecparam og tesla-proxy forventer.
        if (!File.Exists(o.Tesla.FleetKeyFile))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WriteSecret(o.Tesla.FleetKeyFile, key.ExportECPrivateKeyPem() + "\n");
            created.Add($"{o.Tesla.FleetKeyFile} (appens nøgle – tag den med i din backup)");
        }

        if (!File.Exists(o.Tesla.ProxyCertFile) || !File.Exists(o.Tesla.ProxyKeyFile))
        {
            var (cert, key) = CreateProxyTls();
            WriteSecret(o.Tesla.ProxyKeyFile, key);
            WriteFile(o.Tesla.ProxyCertFile, cert);
            created.Add($"{o.Tesla.ProxyCertFile} + {Path.GetFileName(o.Tesla.ProxyKeyFile)} (intern TLS)");
        }

        WriteSecret(o.Telemetry.ServerConfigFile, BuildServerConfig(o).ToJsonString(Json.Pretty) + "\n");

        var certDir = Path.Combine(Path.GetDirectoryName(o.Telemetry.ServerConfigFile)!, "certs");
        Directory.CreateDirectory(certDir);
        if (!File.Exists(Path.Combine(certDir, "fullchain.pem")) || !File.Exists(Path.Combine(certDir, "privkey.pem")))
            warnings.Add($"Mangler fullchain.pem + privkey.pem i {certDir} – fleet-telemetry kan ikke starte uden certifikatet til dit domæne.");
        if (string.IsNullOrWhiteSpace(o.Telemetry.Hostname))
            warnings.Add("Bridge__Telemetry__Hostname er ikke sat i .env.");
        if (o.Vehicles.Count == 0)
            warnings.Add("Ingen bil sat op – sæt Bridge__Vehicles__0__Vin i .env.");
        if (string.IsNullOrWhiteSpace(o.Tesla.ClientId))
            warnings.Add("Bridge__Tesla__ClientId er ikke sat i .env.");

        GiveToOwner(o);
        return new Result(created, warnings);
    }

    /// <summary>fleet-telemetrys config: bilen → (mTLS :4443) → MQTT-brokeren.</summary>
    public static JsonObject BuildServerConfig(BridgeOptions o)
    {
        var dir = "/etc/fleet-telemetry/certs";
        return new JsonObject
        {
            ["host"] = "0.0.0.0",
            ["port"] = 4443,
            ["log_level"] = "info",
            ["json_log_enable"] = true,
            ["namespace"] = "tesla_telemetry",
            ["reliable_ack"] = false,
            ["rate_limit"] = new JsonObject { ["enabled"] = true, ["message_interval_time"] = 30, ["message_limit"] = 1000 },
            ["mqtt"] = new JsonObject
            {
                ["broker"] = o.Mqtt.BrokerForTelemetry,
                ["client_id"] = "fleet-telemetry",
                ["username"] = o.Telemetry.MqttUsername,
                ["password"] = o.Telemetry.MqttPassword,
                ["topic_base"] = o.Mqtt.TelemetryTopicBase,
                ["qos"] = 1,
                ["retained"] = false,
            },
            ["records"] = new JsonObject
            {
                ["V"] = new JsonArray("mqtt"),
                ["connectivity"] = new JsonArray("mqtt"),
                ["alerts"] = new JsonArray("mqtt"),
                ["errors"] = new JsonArray("mqtt"),
            },
            ["tls"] = new JsonObject
            {
                ["server_cert"] = $"{dir}/fullchain.pem",
                ["server_key"] = $"{dir}/privkey.pem",
            },
        };
    }

    public static (string CertPem, string KeyPem) CreateProxyTls()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var req = new CertificateRequest("CN=tesla-proxy", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("tesla-proxy");
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return (cert.ExportCertificatePem() + "\n", key.ExportECPrivateKeyPem() + "\n");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteSecret(string path, string content)
    {
        WriteFile(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---- ejerskab: kun når vi kører som root (init-containeren) ----

    [DllImport("libc", SetLastError = true)] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint owner, uint group);

    private static void GiveToOwner(BridgeOptions o)
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0) return;
        if (!uint.TryParse(Environment.GetEnvironmentVariable("PUID"), out var uid)) return;
        var gid = uint.TryParse(Environment.GetEnvironmentVariable("PGID"), out var g) ? g : uid;

        var roots = new[]
        {
            Path.GetDirectoryName(o.Tesla.FleetKeyFile)!,
            Path.GetDirectoryName(o.Telemetry.ServerConfigFile)!,
            Path.GetDirectoryName(o.StateFile)!,
        }.Distinct();

        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            chown(root, uid, gid);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                chown(entry, uid, gid);
        }
    }
}
