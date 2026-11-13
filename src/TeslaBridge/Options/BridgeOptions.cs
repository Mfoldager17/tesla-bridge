namespace TeslaBridge.Options;

public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>Reads the Bridge section (appsettings.json, then environment variables such as Bridge__Web__Token).</summary>
    public static BridgeOptions From(IConfiguration config)
    {
        var o = new BridgeOptions();
        config.GetSection(Section).Bind(o);
        o.Normalize();
        return o;
    }

    /// <summary>Clean-ups after binding: an empty VIN (an empty Bridge__Vehicles__0__Vin in .env) means no car.</summary>
    public void Normalize()
    {
        Vehicles.RemoveAll(v => string.IsNullOrWhiteSpace(v.Vin));
        // The embedded broker never runs without passwords: if .env has none for fleet-telemetry, the init container
        // made one (Bootstrap) and wrote it into fleet-telemetry's config.json too.
        if (Mqtt.EmbeddedBroker && string.IsNullOrEmpty(Telemetry.MqttPassword))
        {
            try
            {
                if (File.Exists(GeneratedTelemetryPasswordFile))
                    Telemetry.MqttPassword = File.ReadAllText(GeneratedTelemetryPasswordFile).Trim();
            }
            catch (IOException) { /* Bootstrap makes a new one */ }
        }
    }

    public MqttOptions Mqtt { get; set; } = new();
    public TeslaOptions Tesla { get; set; } = new();
    public List<VehicleOptions> Vehicles { get; set; } = [];
    public TelemetryOptions Telemetry { get; set; } = new();
    public CommandOptions Commands { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public UpdatesOptions Updates { get; set; } = new();
    public MapOptions Map { get; set; } = new();
    public string StateFile { get; set; } = "/data/state.json";

    /// <summary>
    /// Make missing keys, the internal TLS and fleet-telemetry's config.json at start. In Docker the init container
    /// does that, and docker-compose.yml turns it off for the bridge, which can't write those files.
    /// </summary>
    public bool RunBootstrap { get; set; } = true;

    /// <summary>The MQTT password fleet-telemetry uses with the embedded broker, when .env doesn't set one.</summary>
    public string GeneratedTelemetryPasswordFile => Path.Combine(Path.GetDirectoryName(StateFile) ?? "/data", "mqtt-telemetry-password");

    /// <summary>The domain the app is registered with at Tesla (Allowed Origin). Empty = the same as the telemetry host name.</summary>
    public string PartnerDomain => !string.IsNullOrWhiteSpace(Tesla.PartnerDomain) ? Tesla.PartnerDomain.Trim() : Telemetry.Hostname.Trim();

    /// <summary>Where Tesla sends the browser after sign-in. Empty = https://&lt;partner domain&gt;/auth/callback.</summary>
    public string RedirectUri => !string.IsNullOrWhiteSpace(Tesla.RedirectUri) ? Tesla.RedirectUri.Trim() : $"https://{PartnerDomain}/auth/callback";

    public string PublicKeyUrl => $"https://{PartnerDomain}{Web.PublicKeyPath}";

    public string PairingUrl => $"https://www.tesla.com/_ak/{PartnerDomain}";
    public string UsageFile { get; set; } = "/data/usage.json";
}

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string ClientId { get; set; } = "tesla-bridge";

    /// <summary>
    /// Run an MQTT broker inside the bridge (on Port) instead of using an external one (Mosquitto). fleet-telemetry
    /// then sends to the bridge directly, and Host is ignored (the bridge connects to itself). For those without a
    /// broker of their own – e.g. together with the HA integration in custom_components/tesla_bridge.
    /// </summary>
    public bool EmbeddedBroker { get; set; }

    /// <summary>The name fleet-telemetry reaches the bridge by (the docker service name) with EmbeddedBroker.</summary>
    public string EmbeddedHostname { get; set; } = "tesla-bridge";

    /// <summary>Where the bridge itself connects: to itself with the embedded broker, otherwise Host.</summary>
    public string ConnectHost => EmbeddedBroker ? "127.0.0.1" : Host;

    /// <summary>Where fleet-telemetry sends raw data (written into its config.json).</summary>
    public string BrokerForTelemetry => EmbeddedBroker ? $"{EmbeddedHostname}:{Port}" : $"{Host}:{Port}";

    /// <summary>The topic_base fleet-telemetry publishes raw data under.</summary>
    public string TelemetryTopicBase { get; set; } = "telemetry";

    /// <summary>The root of the bridge's own, tidy topics (tesla/&lt;vin&gt;/state/...).</summary>
    public string BridgeTopicBase { get; set; } = "tesla";

    /// <summary>Home Assistant MQTT discovery prefix. Empty = no discovery.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>
    /// Take commands on tesla/&lt;vin&gt;/cmd/… (what Home Assistant's MQTT entities use). Off: only the HTTP API
    /// (with its token) can send commands.
    /// </summary>
    public bool Commands { get; set; } = true;
}

public sealed class TeslaOptions
{
    public string Region { get; set; } = "eu";

    /// <summary>
    /// Register the partner domain with Tesla by itself when it isn't registered yet (the "Register" step on
    /// the setup page). Never replaces a different key that is already registered.
    /// </summary>
    public bool AutoRegister { get; set; } = true;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string ProxyUrl { get; set; } = "https://tesla-proxy:4443";
    /// <summary>tesla-proxy's TLS certificate (the bridge only trusts this one when it talks to the proxy).</summary>
    public string ProxyCertFile { get; set; } = "/config/tesla-proxy/tls-cert.pem";

    /// <summary>The private key for the internal TLS between the bridge and tesla-proxy. Made automatically.</summary>
    public string ProxyKeyFile { get; set; } = "/config/tesla-proxy/tls-key.pem";

    /// <summary>The app's private key (the file tesla-proxy signs commands with). Made automatically if missing.</summary>
    public string FleetKeyFile { get; set; } = "/config/tesla-proxy/fleet-key.pem";

    /// <summary>
    /// The public halves the bridge needs – the app's public key and a copy of tesla-proxy's certificate – written
    /// by the init container to /config/public, so the bridge doesn't have to be able to read the private key.
    /// </summary>
    public string FleetPublicKeyFile { get; set; } = "/config/public/fleet-key.pub.pem";
    public string PublicProxyCertFile { get; set; } = "/config/public/tesla-proxy-cert.pem";

    /// <summary>The certificate the bridge pins for tesla-proxy: the public copy when there is one.</summary>
    public string PinnedProxyCertFile => File.Exists(PublicProxyCertFile) ? PublicProxyCertFile : ProxyCertFile;

    /// <summary>Empty = the same as Telemetry:Hostname. Only set it if the app is registered with another domain.</summary>
    public string PartnerDomain { get; set; } = "";
    public string TokenFile { get; set; } = "/data/tokens.json";

    public string AuthorizeUrl { get; set; } = "https://auth.tesla.com/oauth2/v3/authorize";
    public string TokenUrl { get; set; } = "https://fleet-auth.prd.vn.cloud.tesla.com/oauth2/v3/token";

    public string Scopes { get; set; } =
        "openid offline_access vehicle_device_data vehicle_location vehicle_cmds vehicle_charging_cmds";

    public string FleetApiAudience => Region.ToLowerInvariant() switch
    {
        "na" => "https://fleet-api.prd.na.vn.cloud.tesla.com",
        "eu" => "https://fleet-api.prd.eu.vn.cloud.tesla.com",
        "cn" => "https://fleet-api.prd.cn.vn.cloud.tesla.cn",
        var r => throw new InvalidOperationException($"Unknown region '{r}' (use eu, na or cn)"),
    };
}

public sealed class VehicleOptions
{
    public string Vin { get; set; } = "";
    public string Name { get; set; } = "Tesla";

    /// <summary>Optional ESP32 next to the car (Bridge__Vehicles__0__Ble__Url). Can also be set on the dashboard.</summary>
    public BleOptions Ble { get; set; } = new();
}

/// <summary>
/// An ESP32 running ESPHome with the Tesla BLE component (esphome/tesla-ble.yaml in this repo), reached over
/// its built-in web server. When the car is within Bluetooth range, commands and wake-ups go this way –
/// free, and without the internet. Otherwise the bridge uses the Fleet API as usual.
/// </summary>
public sealed class BleOptions
{
    /// <summary>e.g. http://192.168.1.50 – empty = no ESP32.</summary>
    public string Url { get; set; } = "";
    /// <summary>The ESP32's web_server login (web_username / web_password in its secrets.yaml).</summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class TelemetryOptions
{
    /// <summary>The public host name the car connects to (must match the certificate).</summary>
    public string Hostname { get; set; } = "";
    public int Port { get; set; } = 4443;

    /// <summary>The PEM bundle the car checks the server's certificate against (e.g. Let's Encrypt's roots).</summary>
    public string CaFile { get; set; } = "/app/defaults/vehicle-trust-ca.pem";

    /// <summary>Fields and intervals. The default comes with the image; point to your own file to change them.</summary>
    public string FieldsFile { get; set; } = "/app/defaults/telemetry-fields.json";

    /// <summary>fleet-telemetry's own config.json. Written by the bridge from the MQTT settings.</summary>
    public string ServerConfigFile { get; set; } = "/config/fleet-telemetry/config.json";

    /// <summary>The MQTT user fleet-telemetry logs in to the broker with.</summary>
    public string MqttUsername { get; set; } = "fleet-telemetry";
    public string MqttPassword { get; set; } = "";
    public int ConfigLifetimeDays { get; set; } = 30;
    public int RenewWhenDaysLeft { get; set; } = 23;
    public bool AutoPush { get; set; } = true;
}

public sealed class CommandOptions
{
    /// <summary>How long an optimistic value is kept while waiting for the stream to confirm it.</summary>
    public int OptimisticTimeoutSeconds { get; set; } = 45;
    public int WakeTimeoutSeconds { get; set; } = 60;
}

public sealed class WebOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Listens with ONLY two public paths: the public key and the sign-in callback. The reverse proxy or tunnel sends
    /// https://&lt;partner domain&gt; here. Everything else is a 404.
    /// </summary>
    public int PublicPort { get; set; } = 8091;

    public string PublicKeyPath { get; set; } = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>
    /// The token for the dashboard and the API (which can unlock the car). Empty: the bridge makes one on first
    /// start and keeps it in /data/web-token.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// No token at all. Anyone who can reach the port can unlock the car, so only for a network you fully trust.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// With AllowAnonymous: host names (comma-separated) the dashboard may be opened by, besides IP addresses,
    /// localhost and *.local – e.g. "pi.lan,bridge.home".
    /// </summary>
    public string? AllowedHosts { get; set; }

    /// <summary>Shows a button that sends made-up telemetry through MQTT – for testing before the car is set up.</summary>
    public bool Simulator { get; set; }

    /// <summary>
    /// Your own 3D model of the car (glTF binary, .glb) shown on the dashboard. Put it here, or upload it
    /// under Settings (saved as car-model.glb next to the state file). Not shipped with the bridge.
    /// </summary>
    public string CarModelFile { get; set; } = "/config/bridge/car.glb";
}

/// <summary>Looking up new releases (Bridge__Updates__*). Only shown – the bridge never updates itself.</summary>
public sealed class UpdatesOptions
{
    /// <summary>Ask GitHub for the newest release (one anonymous request every IntervalHours).</summary>
    public bool Check { get; set; } = true;
    public string Repository { get; set; } = "Mfoldager17/tesla-bridge";
    public int IntervalHours { get; set; } = 6;
}

/// <summary>
/// The map and street address on the dashboard (Bridge__Map__*). Can be turned off under Settings, so the car's
/// position is never sent to a map service.
/// </summary>
public sealed class MapOptions
{
    /// <summary>Nominatim-compatible reverse geocoder. The bridge asks it (at most once a second, cached) for the address.</summary>
    public string GeocoderUrl { get; set; } = "https://nominatim.openstreetmap.org";

    /// <summary>
    /// Map tiles the dashboard loads in your browser, with {z}/{x}/{y} (and optionally {s}). Empty: OpenStreetMap's
    /// own map (tile.openstreetmap.org), darkened by the dashboard in the dark theme.
    /// </summary>
    public string? TileUrl { get; set; }

    /// <summary>Attribution shown on the map when TileUrl is set.</summary>
    public string? TileAttribution { get; set; }
}
