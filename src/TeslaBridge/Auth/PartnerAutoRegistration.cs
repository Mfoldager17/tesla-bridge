using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Auth;

/// <summary>
/// Does the "Register domain with Tesla" step by itself: once the app's details are in .env and the public
/// key can be fetched from the internet, the domain is registered. Retries every 10 minutes for the first
/// hour (while you sort out DNS, certificate and reverse proxy), then every 6 hours. Never replaces a
/// different key that is already registered – that stays a manual decision on the setup page.
/// </summary>
public sealed class PartnerAutoRegistration(
    TeslaPartnerClient partner, TeslaKey key, IOptions<BridgeOptions> options, ILogger<PartnerAutoRegistration> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Tesla.AutoRegister || !TeslaBridge.Web.SetupApi.HasCredentials(o)) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }

        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            if (await TryOnceAsync(o, ct)) return;
            try { await Task.Delay(attempt < 6 ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <returns>true when nothing more is to be done.</returns>
    private async Task<bool> TryOnceAsync(BridgeOptions o, CancellationToken ct)
    {
        try
        {
            var pub = key.Public;
            if (pub is null) return false;
            var registered = await partner.GetRegisteredPublicKeyAsync(ct);
            if (registered == pub.UncompressedHex) { log.LogInformation("Partner domain {Domain} is registered with Tesla", o.PartnerDomain); return true; }
            if (registered is not null)
            {
                log.LogWarning("Tesla has a DIFFERENT key registered for {Domain}. Press Register on the setup page if this bridge's key should replace it", o.PartnerDomain);
                return true;
            }
            await partner.RegisterAsync(ct);
            log.LogInformation("Registered partner domain {Domain} with Tesla", o.PartnerDomain);
            return true;
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception ex)
        {
            log.LogWarning("Couldn't register {Domain} with Tesla yet ({Error}). Is {Url} reachable from the internet? Trying again later",
                o.PartnerDomain, ex.Message, o.PublicKeyUrl);
            return false;
        }
    }
}
