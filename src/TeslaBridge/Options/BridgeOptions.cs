namespace TeslaBridge.Options;

public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    public MqttOptions Mqtt { get; set; } = new();
    public TeslaOptions Tesla { get; set; } = new();
    public List<VehicleOptions> Vehicles { get; set; } = [];
    public TelemetryOptions Telemetry { get; set; } = new();
    public CommandOptions Commands { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public string StateFile { get; set; } = "/data/state.json";

    /// <summary>Domænet appen er registreret med hos Tesla (Allowed Origin). Tom = samme som telemetri-hostname.</summary>
    public string PartnerDomain => !string.IsNullOrWhiteSpace(Tesla.PartnerDomain) ? Tesla.PartnerDomain.Trim() : Telemetry.Hostname.Trim();

    /// <summary>Hvor Tesla sender browseren hen efter login. Tom = https://&lt;partnerdomæne&gt;/auth/callback.</summary>
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
    /// Start en MQTT-broker inde i bridgen (på Port) i stedet for at bruge en ekstern (Mosquitto).
    /// fleet-telemetry sender så til bridgen direkte, og Host ignoreres (bridgen forbinder til sig selv).
    /// Til dem uden egen broker – fx sammen med HA-integrationen i custom_components/tesla_bridge.
    /// </summary>
    public bool EmbeddedBroker { get; set; }

    /// <summary>Navnet fleet-telemetry når bridgen på (docker-servicens navn), når EmbeddedBroker er slået til.</summary>
    public string EmbeddedHostname { get; set; } = "tesla-bridge";

    /// <summary>Hvor bridgen selv forbinder til: sig selv med den indbyggede broker, ellers Host.</summary>
    public string ConnectHost => EmbeddedBroker ? "127.0.0.1" : Host;

    /// <summary>Hvor fleet-telemetry skal sende rå data hen (skrives i dens config.json).</summary>
    public string BrokerForTelemetry => EmbeddedBroker ? $"{EmbeddedHostname}:{Port}" : $"{Host}:{Port}";

    /// <summary>topic_base fleet-telemetry publicerer rå data på.</summary>
    public string TelemetryTopicBase { get; set; } = "telemetry";

    /// <summary>Roden for bridgens egne, pæne topics (tesla/&lt;vin&gt;/state/...).</summary>
    public string BridgeTopicBase { get; set; } = "tesla";

    /// <summary>Home Assistant MQTT discovery-prefix. Tom = slå discovery fra.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";
}

public sealed class TeslaOptions
{
    public string Region { get; set; } = "eu";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string ProxyUrl { get; set; } = "https://tesla-proxy:4443";
    public string ProxyCertFile { get; set; } = "/config/tesla-proxy/tls-cert.pem";

    /// <summary>Privatnøgle til den interne TLS mellem bridge og tesla-proxy. Laves automatisk.</summary>
    public string ProxyKeyFile { get; set; } = "/config/tesla-proxy/tls-key.pem";

    /// <summary>Appens private nøgle (samme fil som tesla-proxy signerer med). Laves automatisk, hvis den mangler.</summary>
    public string FleetKeyFile { get; set; } = "/config/tesla-proxy/fleet-key.pem";

    /// <summary>Tom = samme som Telemetry:Hostname. Sæt kun hvis appen er registreret på et andet domæne.</summary>
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
        var r => throw new InvalidOperationException($"Ukendt region '{r}' (brug eu, na eller cn)"),
    };
}

public sealed class VehicleOptions
{
    public string Vin { get; set; } = "";
    public string Name { get; set; } = "Tesla";
}

public sealed class TelemetryOptions
{
    /// <summary>Offentligt værtsnavn bilen forbinder til (skal matche certifikatet).</summary>
    public string Hostname { get; set; } = "";
    public int Port { get; set; } = 4443;

    /// <summary>PEM-bundle bilen validerer serverens certifikat mod (fx Let's Encrypt-rødder).</summary>
    public string CaFile { get; set; } = "/app/defaults/vehicle-trust-ca.pem";

    /// <summary>Felter + intervaller. Standard følger med imaget; peg på din egen fil for at ændre dem.</summary>
    public string FieldsFile { get; set; } = "/app/defaults/telemetry-fields.json";

    /// <summary>fleet-telemetrys egen config.json. Skrives af bridgen ud fra MQTT-indstillingerne.</summary>
    public string ServerConfigFile { get; set; } = "/config/fleet-telemetry/config.json";

    /// <summary>MQTT-bruger som fleet-telemetry logger ind på brokeren med.</summary>
    public string MqttUsername { get; set; } = "fleet-telemetry";
    public string MqttPassword { get; set; } = "";
    public int ConfigLifetimeDays { get; set; } = 30;
    public int RenewWhenDaysLeft { get; set; } = 23;
    public bool AutoPush { get; set; } = true;
}

public sealed class CommandOptions
{
    /// <summary>Hvor længe en optimistisk værdi holdes, mens vi venter på at streamen bekræfter.</summary>
    public int OptimisticTimeoutSeconds { get; set; } = 45;
    public int WakeTimeoutSeconds { get; set; } = 60;
}

public sealed class WebOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Lytter med KUN to offentlige stier: den offentlige nøgle og login-callback.
    /// NPM sender https://&lt;partnerdomæne&gt; hertil. Alt andet giver 404.
    /// </summary>
    public int PublicPort { get; set; } = 8091;

    public string PublicKeyPath { get; set; } = "/.well-known/appspecific/com.tesla.3p.public-key.pem";
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>Hvis sat, kræver API'et dette token (UI'et spørger om det første gang). Anbefalet: kan låse bilen op.</summary>
    public string? Token { get; set; }

    /// <summary>Viser en knap der sender falske telemetri-beskeder via MQTT – til at teste kæden før bilen er sat op.</summary>
    public bool Simulator { get; set; }
}
