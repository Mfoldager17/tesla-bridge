using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;

namespace TeslaBridge.Setup;

/// <summary>
/// Makes everything that used to need scripts and repository files, so the server only needs docker-compose.yml + .env:
/// <list type="bullet">
/// <item>the app's key pair (P-256), if missing, and its public key in /config/public</item>
/// <item>the internal TLS between the bridge and tesla-proxy, if missing, and a copy of its certificate in /config/public</item>
/// <item>a password for fleet-telemetry's MQTT login when the embedded broker is used and .env has none</item>
/// <item>fleet-telemetry's config.json from the MQTT settings (written every time)</item>
/// </list>
/// Runs as "init" (root) before the other containers start. As root it hands the files to PUID:PGID, so tesla-proxy,
/// fleet-telemetry and the bridge can read them.
/// </summary>
public static class Bootstrap
{
    public sealed record Result(List<string> Created, List<string> Warnings);

    public static Result Run(BridgeOptions o)
    {
        var created = new List<string>();
        var warnings = new List<string>();

        // The app's key: SEC1 "EC PRIVATE KEY", P-256 – the format openssl ecparam makes and tesla-proxy expects.
        if (!File.Exists(o.Tesla.FleetKeyFile))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WriteSecret(o.Tesla.FleetKeyFile, key.ExportECPrivateKeyPem() + "\n");
            created.Add($"{o.Tesla.FleetKeyFile} (the app's key – keep it in your backup)");
        }
        WriteIfChanged(o.Tesla.FleetPublicKeyFile, TeslaBridge.Auth.TeslaKey.FromPem(File.ReadAllText(o.Tesla.FleetKeyFile)).Pem);

        if (!File.Exists(o.Tesla.ProxyCertFile) || !File.Exists(o.Tesla.ProxyKeyFile))
        {
            var (cert, key) = CreateProxyTls();
            WriteSecret(o.Tesla.ProxyKeyFile, key);
            WriteFile(o.Tesla.ProxyCertFile, cert);
            created.Add($"{o.Tesla.ProxyCertFile} + {Path.GetFileName(o.Tesla.ProxyKeyFile)} (internal TLS)");
        }
        WriteIfChanged(o.Tesla.PublicProxyCertFile, File.ReadAllText(o.Tesla.ProxyCertFile));

        // The embedded broker never accepts anonymous logins, so fleet-telemetry needs a password.
        if (o.Mqtt.EmbeddedBroker && string.IsNullOrEmpty(o.Telemetry.MqttPassword))
        {
            o.Telemetry.MqttPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            WriteSecret(o.GeneratedTelemetryPasswordFile, o.Telemetry.MqttPassword + "\n");
            created.Add($"{o.GeneratedTelemetryPasswordFile} (fleet-telemetry's MQTT password). If fleet-telemetry was " +
                        "already running, restart it once: docker compose restart fleet-telemetry");
        }

        WriteSecret(o.Telemetry.ServerConfigFile, BuildServerConfig(o).ToJsonString(Json.Pretty) + "\n");

        var certDir = Path.Combine(Path.GetDirectoryName(o.Telemetry.ServerConfigFile)!, "certs");
        Directory.CreateDirectory(certDir);
        if (!File.Exists(Path.Combine(certDir, "fullchain.pem")) || !File.Exists(Path.Combine(certDir, "privkey.pem")))
            warnings.Add($"fullchain.pem + privkey.pem are missing in {certDir} – fleet-telemetry can't start without the certificate for your domain.");
        if (string.IsNullOrWhiteSpace(o.Telemetry.Hostname))
            warnings.Add("Bridge__Telemetry__Hostname isn't set in .env.");
        if (o.Vehicles.Count == 0)
            warnings.Add("No car is set up – set Bridge__Vehicles__0__Vin in .env.");
        if (string.IsNullOrWhiteSpace(o.Tesla.ClientId))
            warnings.Add("Bridge__Tesla__ClientId isn't set in .env.");

        GiveToOwner(o);
        return new Result(created, warnings);
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        WriteFile(path, content);
    }

    /// <summary>fleet-telemetry's config: the car → (mTLS :4443) → the MQTT broker.</summary>
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

    private static void WriteFile(string path, string content) => TeslaBridge.Storage.AtomicFile.WriteAllText(path, content);

    private static void WriteSecret(string path, string content) => TeslaBridge.Storage.AtomicFile.WriteAllText(path, content, secret: true);

    // ---- ownership: only when running as root (the init container) ----

    [DllImport("libc", SetLastError = true)] private static extern uint geteuid();
    // lchown: a symlink someone put in /data or /config changes owner itself – the file it points to is left alone.
    [DllImport("libc", SetLastError = true)] private static extern int lchown(string path, uint owner, uint group);

    private static void GiveToOwner(BridgeOptions o)
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0) return;
        if (!uint.TryParse(Environment.GetEnvironmentVariable("PUID"), out var uid)) return;
        var gid = uint.TryParse(Environment.GetEnvironmentVariable("PGID"), out var g) ? g : uid;

        var roots = new[]
        {
            Path.GetDirectoryName(o.Tesla.FleetKeyFile)!,
            Path.GetDirectoryName(o.Tesla.FleetPublicKeyFile)!,
            Path.GetDirectoryName(o.Telemetry.ServerConfigFile)!,
            Path.GetDirectoryName(o.StateFile)!,
        }.Distinct();

        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            Own(root, uid, gid);
        }
    }

    /// <summary>chown -R without following symlinks: a link changes owner itself, and isn't descended into.</summary>
    private static void Own(string path, uint uid, uint gid)
    {
        if (lchown(path, uid, gid) != 0)
            Console.WriteLine($"⚠ Couldn't hand {path} to {uid}:{gid} (errno {Marshal.GetLastPInvokeError()})");
        if (new FileInfo(path).LinkTarget is not null || !Directory.Exists(path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            Own(entry, uid, gid);
    }
}
