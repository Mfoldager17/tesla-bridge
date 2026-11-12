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

var cliCommand = args.FirstOrDefault(a => !a.StartsWith('-'));

var builder = WebApplication.CreateBuilder(args.Where(a => a != cliCommand).ToArray());
builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.Section));
// Tomme VIN'er (fx en tom Bridge__Vehicles__0__Vin i .env) ignoreres.
builder.Services.PostConfigure<BridgeOptions>(o => o.Vehicles.RemoveAll(v => string.IsNullOrWhiteSpace(v.Vin)));
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

// Tesla: auth + proxy. Singletons – refresh tokens roterer, så der må kun være én der fornyer.
services.AddHttpClient("tesla-auth");
services.AddHttpClient("tesla-proxy", (sp, http) =>
    {
        var o = sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Tesla;
        http.BaseAddress = new Uri(o.ProxyUrl);
        http.Timeout = TimeSpan.FromSeconds(60);
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
        TeslaProxyClient.CreateHandler(sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Tesla.ProxyCertFile));
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
services.AddSingleton<CommandRouter>();
services.AddSingleton<CommandService>();

// Telemetri-konfiguration (også brugt fra CLI)
services.AddSingleton<TelemetryConfigService>();

if (cliCommand is not null)
{
    builder.Services.AddSingleton<LiveEvents>();
    using var cliHost = builder.Build();
    return await CliCommands.RunAsync(cliCommand, cliHost.Services);
}

// Nøgler, intern TLS og fleet-telemetrys config laves, hvis de mangler (i Docker gør init-containeren det først).
{
    var bootOpts = new BridgeOptions();
    builder.Configuration.GetSection(BridgeOptions.Section).Bind(bootOpts);
    bootOpts.Vehicles.RemoveAll(v => string.IsNullOrWhiteSpace(v.Vin));
    try
    {
        var r = TeslaBridge.Setup.Bootstrap.Run(bootOpts);
        foreach (var c in r.Created) Console.WriteLine($"✓ Lavede {c}");
        foreach (var w in r.Warnings) Console.WriteLine($"⚠ {w}");
    }
    catch (Exception ex) { Console.WriteLine($"⚠ Kunne ikke forberede filer: {ex.Message}"); }
}

// Kørende services. Rækkefølgen betyder noget: state indlæses og evt. broker starter før MQTT forbinder.
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
services.AddHostedService<OptimisticExpiryService>();
services.AddHostedService<UsageService>();
services.AddHostedService(sp => sp.GetRequiredService<TelemetryConfigService>());

// Test-dashboard
services.AddSingleton<LiveEvents>();
services.AddHostedService<LiveEventsBridge>();
services.AddHostedService<TeslaBridge.Auth.PartnerAutoRegistration>();
var web = builder.Configuration.GetSection("Bridge:Web").Get<WebOptions>() ?? new WebOptions();
builder.WebHost.UseUrls($"http://0.0.0.0:{web.Port}", $"http://0.0.0.0:{web.PublicPort}");

var app = builder.Build();
app.UsePublicListenerFilter();   // offentlig port: kun nøgle + login-callback
app.MapPublicEndpoints();
if (web.Enabled) app.MapBridgeApi();
await app.RunAsync();
return 0;
