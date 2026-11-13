using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeslaBridge.Auth;
using TeslaBridge.Options;
using TeslaBridge.Telemetry;

namespace TeslaBridge.Cli;

/// <summary>One-off tools: docker compose exec tesla-bridge dotnet TeslaBridge.dll &lt;command&gt;</summary>
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
                    foreach (var c in r.Created) Console.WriteLine($"✓ Created {c}");
                    Console.WriteLine($"✓ Wrote {opts.Telemetry.ServerConfigFile} (broker {opts.Mqtt.BrokerForTelemetry})");
                    foreach (var w in r.Warnings) Console.WriteLine($"⚠ {w}");
                    return 0;
                }

                case "auth":
                    return await AuthAsync(sp.GetRequiredService<TeslaTokenProvider>(), opts, ct);

                case "public-key":
                {
                    var key = sp.GetRequiredService<TeslaKey>().Public;
                    if (key is null) { Console.Error.WriteLine($"No key in {opts.Tesla.FleetKeyFile}. Run: docker compose run --rm tesla-init"); return 1; }
                    Console.Write(key.Pem);
                    Console.WriteLine($"\nFingerprint: {key.Fingerprint}\nMust be reachable at: {opts.PublicKeyUrl}");
                    return 0;
                }

                case "register":
                {
                    var resp = await sp.GetRequiredService<TeslaPartnerClient>().RegisterAsync(ct);
                    Console.WriteLine(resp?.ToJsonString(Pretty));
                    Console.WriteLine($"\n✓ {opts.PartnerDomain} is registered. Now pair the key with the car: {opts.PairingUrl}");
                    return 0;
                }

                case "push-config":
                {
                    var svc = sp.GetRequiredService<TelemetryConfigService>();
                    var resp = await svc.PushAsync(opts.Vehicles.Select(v => v.Vin).ToArray(), ct);
                    Console.WriteLine(resp?.ToJsonString(Pretty));
                    Console.WriteLine("\nCheck that the car has it with: telemetry-status (wait for \"synced\": true)");
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
                        Usage: tesla-bridge [command]

                          (none)             Run the bridge
                          init               Make keys, internal TLS and fleet-telemetry's config (also happens by itself)
                          auth               Sign in with Tesla in the terminal (instead of the dashboard's button)
                          public-key         Show the public key and where it must be reachable
                          register           Register the partner domain with Tesla
                          push-config        Send the telemetry configuration to the car(s) now
                          telemetry-status   Show the car's current telemetry configuration (synced?)
                          telemetry-errors   Show errors the car has reported about the connection to your server
                        """);
                    return command == "help" ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> AuthAsync(TeslaTokenProvider tokens, BridgeOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.Tesla.ClientId) || string.IsNullOrWhiteSpace(opts.PartnerDomain))
        {
            Console.Error.WriteLine("Set Bridge__Tesla__ClientId, Bridge__Tesla__ClientSecret and Bridge__Telemetry__Hostname in .env first.");
            return 1;
        }

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        Console.WriteLine($"""

            1. Open this address and sign in with your Tesla account:

               {tokens.BuildAuthorizeUrl(state)}

            2. Allow access. The browser is sent on to {opts.RedirectUri}
               (it's fine if that page doesn't exist).
            3. Copy the WHOLE address from the browser's address bar and paste it here:

            """);
        Console.Write("> ");
        var input = Console.ReadLine()?.Trim() ?? "";

        var code = input;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var q = HttpUtility.ParseQueryString(uri.Query);
            if (q["state"] != state)
            {
                Console.Error.WriteLine("The state in the address doesn't match – use the link above and try again.");
                return 1;
            }
            code = q["code"] ?? "";
        }

        if (string.IsNullOrEmpty(code))
        {
            Console.Error.WriteLine("No 'code' in the address.");
            return 1;
        }

        await tokens.ExchangeCodeAsync(code, ct);
        Console.WriteLine($"\n✓ Tokens saved in {opts.Tesla.TokenFile}. Now start the bridge: docker compose up -d");
        return 0;
    }
}
