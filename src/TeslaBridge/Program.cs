using Microsoft.Extensions.Options;
using TeslaBridge.Auth;
using TeslaBridge.Cli;
using TeslaBridge.Commands;
using TeslaBridge.Mqtt;
using TeslaBridge.Options;
using TeslaBridge.Publishing;
using TeslaBridge.State;
using TeslaBridge.Telemetry;
using TeslaBridge.Usage;
using TeslaBridge.Web;

// "dotnet TeslaBridge.dll auth" etc. runs a one-off command (see Cli/CliCommands.cs); anything else starts the bridge.
var cliCommand = args.Length > 0 && CliCommands.Names.Contains(args[0]) ? args[0] : null;

var builder = WebApplication.CreateBuilder(cliCommand is null ? args : args[1..]);
builder.Services.AddOptions<BridgeOptions>()
    .Bind(builder.Configuration.GetSection(BridgeOptions.Section))
    .PostConfigure(o => o.Normalize())
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BridgeOptions>, BridgeOptionsValidator>();
// The same values, for the few decisions made before the services exist.
var config = BridgeOptions.From(builder.Configuration);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

var services = builder.Services;
services.AddSingleton(TimeProvider.System);
services.AddSingleton<VehicleStateStore>(sp => new VehicleStateStore(sp.GetRequiredService<TimeProvider>()));
services.AddSingleton<UsageTracker>();
services.AddSingleton<TeslaBridge.Settings.SettingsStore>();

// Tesla: sign-in and the command proxy. Singletons – refresh tokens rotate, so only one may renew them.
services.AddHttpClient("tesla-auth");
services.AddHttpClient("tesla-proxy", (sp, http) =>
    {
        var o = sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Tesla;
        http.BaseAddress = new Uri(o.ProxyUrl);
        http.Timeout = TimeSpan.FromSeconds(60);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
        TeslaProxyClient.CreateHandler(sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Tesla.PinnedProxyCertFile));
services.AddSingleton(sp => new TeslaTokenProvider(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("tesla-auth"),
    sp.GetRequiredService<IOptions<BridgeOptions>>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TeslaTokenProvider>>()));
services.AddSingleton<TeslaKey>();
services.AddSingleton(sp => new TeslaPartnerClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("tesla-auth"),
    sp.GetRequiredService<IOptions<BridgeOptions>>()));
services.AddSingleton<LoginFlow>();
services.AddSingleton(sp => new TeslaProxyClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("tesla-proxy"),
    sp.GetRequiredService<TeslaTokenProvider>(),
    sp.GetRequiredService<UsageTracker>()));

// Commands: Bluetooth via an ESP32 first when the car is in range (free), otherwise the Fleet API.
services.AddSingleton<TeslaBridge.Ble.BleService>();
services.AddSingleton<ICommandTransport, TeslaBridge.Ble.BleTransport>();
services.AddSingleton<ICommandTransport, FleetApiTransport>();
if (config.Web.Simulator)
    services.AddSingleton<ICommandTransport, SimulatorTransport>();   // a pretend car, before the Fleet API
services.AddSingleton<CommandRouter>();
services.AddSingleton<CommandService>();

// The car's telemetry configuration (also used by the CLI)
services.AddSingleton<TelemetryConfigService>();

if (cliCommand is not null)
{
    builder.Services.AddSingleton<LiveEvents>();
    using var cliHost = builder.Build();
    return await CliCommands.RunAsync(cliCommand, cliHost.Services);
}

// Keys, internal TLS and fleet-telemetry's config are made if they are missing. In Docker the init container does it
// first, and the bridge (which can't write those files) skips it: Bridge__RunBootstrap=false in docker-compose.yml.
if (config.RunBootstrap)
{
    try
    {
        var r = TeslaBridge.Setup.Bootstrap.Run(config);
        foreach (var c in r.Created) Console.WriteLine($"✓ Created {c}");
        foreach (var w in r.Warnings) Console.WriteLine($"⚠ {w}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
    {
        Console.WriteLine($"⚠ Couldn't prepare the files: {ex.Message}");
    }
}

// Running services. The order matters: state is loaded and the embedded broker starts before MQTT connects.
services.AddHostedService<StatePersistenceService>();
services.AddHostedService<EmbeddedBroker>();
services.AddSingleton<MqttConnection>();
services.AddHostedService(sp => sp.GetRequiredService<MqttConnection>());
services.AddHostedService<TelemetryIngestService>();
services.AddHostedService<StatePublisher>();
services.AddHostedService<HomeAssistantDiscovery>();
services.AddHostedService<TeslaBridge.Telemetry.VehicleDataSeeder>();
services.AddHostedService(sp => sp.GetRequiredService<TeslaBridge.Ble.BleService>());
services.AddHostedService<CommandListener>();
services.AddHttpClient("github", c => c.Timeout = TimeSpan.FromSeconds(15));
services.AddHttpClient("geocoder", c =>
{
    // Nominatim asks every app to say who it is.
    c.Timeout = TimeSpan.FromSeconds(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"TeslaBridge/{TeslaBridge.Versioning.BuildInfo.Version} (+https://github.com/Mfoldager17/tesla-bridge)");
});
services.AddSingleton<TeslaBridge.Map.ReverseGeocoder>();
services.AddSingleton<TeslaBridge.Versioning.UpdateCheckService>();
services.AddHostedService(sp => sp.GetRequiredService<TeslaBridge.Versioning.UpdateCheckService>());
services.AddHostedService<TeslaBridge.State.PendingTracker>();
services.AddHostedService<OptimisticExpiryService>();
services.AddHostedService<UsageService>();
services.AddHostedService(sp => sp.GetRequiredService<TelemetryConfigService>());

// Dashboard and API
services.AddSingleton<WebAccess>();
services.AddSingleton<TeslaBridge.Setup.SetupStatusService>();
services.AddSingleton<LiveEvents>();
services.AddHostedService<LiveEventsBridge>();
services.AddHostedService<TeslaBridge.Auth.PartnerAutoRegistration>();
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Web.Port}", $"http://0.0.0.0:{config.Web.PublicPort}");

var app = builder.Build();
app.UsePublicListenerFilter();   // public port: only the key and the sign-in callback
app.MapPublicEndpoints();
if (config.Web.Enabled) app.MapBridgeApi();
await app.RunAsync();
return 0;
