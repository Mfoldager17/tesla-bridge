using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TeslaBridge.Options;

/// <summary>
/// Checks the configuration when the bridge starts, so a typo in .env stops it with a clear message instead of
/// failing later, the first time the value is used.
/// </summary>
public sealed partial class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
    [GeneratedRegex("^[A-HJ-NPR-Z0-9]{17}$", RegexOptions.IgnoreCase)]
    private static partial Regex VinPattern();

    public ValidateOptionsResult Validate(string? name, BridgeOptions o)
    {
        var errors = new List<string>();

        if (o.Tesla.Region.ToLowerInvariant() is not ("eu" or "na" or "cn"))
            errors.Add($"Bridge__Tesla__Region is '{o.Tesla.Region}' – use eu, na or cn");

        for (var i = 0; i < o.Vehicles.Count; i++)
        {
            var v = o.Vehicles[i];
            if (!VinPattern().IsMatch(v.Vin))
                errors.Add($"Bridge__Vehicles__{i}__Vin '{v.Vin}' isn't a VIN (17 letters and digits, without I, O and Q)");
            if (!string.IsNullOrWhiteSpace(v.Ble.Url) && !IsHttpUrl(v.Ble.Url))
                errors.Add($"Bridge__Vehicles__{i}__Ble__Url must be an http:// address, e.g. http://192.168.1.50");
        }
        if (o.Vehicles.GroupBy(v => v.Vin, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            errors.Add("The same VIN is set up twice under Bridge__Vehicles");

        if (!o.Mqtt.EmbeddedBroker && string.IsNullOrWhiteSpace(o.Mqtt.Host))
            errors.Add("Bridge__Mqtt__Host is empty – set your broker, or Bridge__Mqtt__EmbeddedBroker=true");
        foreach (var (key, port) in new[] { ("Mqtt__Port", o.Mqtt.Port), ("Web__Port", o.Web.Port), ("Web__PublicPort", o.Web.PublicPort), ("Telemetry__Port", o.Telemetry.Port) })
            if (port is < 1 or > 65535) errors.Add($"Bridge__{key} {port} isn't a port number");
        if (o.Web.Port == o.Web.PublicPort)
            errors.Add("Bridge__Web__Port and Bridge__Web__PublicPort must differ – only the public port is reachable from the internet");

        if (!IsHttpUrl(o.Tesla.ProxyUrl)) errors.Add("Bridge__Tesla__ProxyUrl must be an http(s) address");
        if (!IsHttpUrl(o.Map.GeocoderUrl)) errors.Add("Bridge__Map__GeocoderUrl must be an http(s) address");
        if (!string.IsNullOrWhiteSpace(o.Map.TileUrl) && !o.Map.TileUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            errors.Add("Bridge__Map__TileUrl must be an https:// address with {z}/{x}/{y}");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
