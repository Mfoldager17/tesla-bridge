using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeslaBridge.Auth;
using TeslaBridge.Options;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Cli;

/// <summary>Engangs-værktøjer: docker compose exec tesla-bridge dotnet TeslaBridge.dll &lt;kommando&gt;</summary>
public static class CliCommands
{
    public static readonly string[] Names = ["init", "auth", "register", "public-key", "push-config", "telemetry-status", "telemetry-errors", "help"];

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string command, IServiceProvider sp)
    {
        var opts = sp.GetRequiredService<IOptions<BridgeOptions>>().Value;
        var ct = CancellationToken.None;
        try
        {
            switch (command)
            {
                case "init":
                {
                    var r = TeslaBridge.Setup.Bootstrap.Run(opts);
                    foreach (var c in r.Created) Console.WriteLine($"✓ Lavede {c}");
                    Console.WriteLine($"✓ Skrev {opts.Telemetry.ServerConfigFile} (broker {opts.Mqtt.Host}:{opts.Mqtt.Port})");
                    foreach (var w in r.Warnings) Console.WriteLine($"⚠ {w}");
                    return 0;
                }

                case "auth":
                    return await AuthAsync(sp.GetRequiredService<TeslaTokenProvider>(), opts, ct);

                case "public-key":
                {
                    var key = sp.GetRequiredService<TeslaKey>().Public;
                    if (key is null) { Console.Error.WriteLine($"Ingen nøgle i {opts.Tesla.FleetKeyFile}. Kør: docker compose run --rm tesla-init"); return 1; }
                    Console.Write(key.Pem);
                    Console.WriteLine($"\nFingeraftryk: {key.Fingerprint}\nSkal kunne hentes på: {opts.PublicKeyUrl}");
                    return 0;
                }

                case "register":
                {
                    var resp = await sp.GetRequiredService<TeslaPartnerClient>().RegisterAsync(ct);
                    Console.WriteLine(resp?.ToJsonString(Pretty));
                    Console.WriteLine($"\n✓ {opts.PartnerDomain} er registreret. Par nu nøglen med bilen: {opts.PairingUrl}");
                    return 0;
                }

                case "push-config":
                {
                    var svc = sp.GetRequiredService<TelemetryConfigService>();
                    var resp = await svc.PushAsync(opts.Vehicles.Select(v => v.Vin).ToArray(), ct);
                    Console.WriteLine(resp?.ToJsonString(Pretty));
                    Console.WriteLine("\nTjek om bilen har modtaget den med: telemetry-status (vent på \"synced\": true)");
                    return 0;
                }

                case "telemetry-status":
                {
                    var svc = sp.GetRequiredService<TelemetryConfigService>();
                    foreach (var v in opts.Vehicles)
                        Console.WriteLine($"{v.Name} ({v.Vin}):\n{(await svc.GetStatusAsync(v.Vin, ct))?.ToJsonString(Pretty)}\n");
                    return 0;
                }

                case "telemetry-errors":
                {
                    var svc = sp.GetRequiredService<TelemetryConfigService>();
                    foreach (var v in opts.Vehicles)
                        Console.WriteLine($"{v.Name} ({v.Vin}):\n{(await svc.GetErrorsAsync(v.Vin, ct))?.ToJsonString(Pretty)}\n");
                    return 0;
                }

                default:
                    Console.WriteLine("""
                        Brug: tesla-bridge [kommando]

                          (ingen)            Kør bridgen
                          init               Lav nøgler, intern TLS og fleet-telemetrys config (sker også automatisk)
                          auth               Log ind hos Tesla i terminalen (alternativ til dashboardets knap)
                          public-key         Vis den offentlige nøgle og hvor den skal kunne hentes
                          register           Registrér partnerdomænet hos Tesla
                          push-config        Send telemetri-konfiguration til bilen(e) nu
                          telemetry-status   Vis bilens aktuelle telemetri-konfiguration (synced?)
                          telemetry-errors   Vis fejl bilen har rapporteret om forbindelsen til din server
                        """);
                    return command == "help" ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fejl: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> AuthAsync(TeslaTokenProvider tokens, BridgeOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.Tesla.ClientId) || string.IsNullOrWhiteSpace(opts.PartnerDomain))
        {
            Console.Error.WriteLine("Sæt Bridge__Tesla__ClientId, Bridge__Tesla__ClientSecret og Bridge__Telemetry__Hostname i .env først.");
            return 1;
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Console.WriteLine($"""

            1. Åbn denne adresse og log ind med din Tesla-konto:

               {tokens.BuildAuthorizeUrl(state)}

            2. Godkend adgang. Browseren sendes videre til {opts.RedirectUri}
               (det er fint, hvis siden ikke findes).
            3. Kopiér HELE adressen fra browserens adresselinje og indsæt den her:

            """);
        Console.Write("> ");
        var input = Console.ReadLine()?.Trim() ?? "";

        var code = input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var q = HttpUtility.ParseQueryString(uri.Query);
            if (q["state"] is { } s && s != state)
            {
                Console.Error.WriteLine("state matcher ikke – brug linket ovenfor og prøv igen.");
                return 1;
            }
            code = q["code"] ?? "";
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.Error.WriteLine("Fandt ingen 'code' i adressen.");
            return 1;
        }

        await tokens.ExchangeCodeAsync(code, ct);
        Console.WriteLine($"\n✓ Tokens gemt i {opts.Tesla.TokenFile}. Start nu bridgen: docker compose up -d");
        return 0;
    }
}
