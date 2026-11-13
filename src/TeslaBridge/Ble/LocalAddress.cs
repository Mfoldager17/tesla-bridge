using System.Net;
using System.Net.Sockets;

namespace TeslaBridge.Ble;

/// <summary>
/// The ESP32 is always on your own network, and the bridge sends it the web password. So its address must be a
/// private IP address or a local name – never something on the internet.
/// </summary>
public static class LocalAddress
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain"];

    public static bool IsLocal(Uri url)
    {
        var host = url.IdnHost.Trim('[', ']');
        if (IPAddress.TryParse(host, out var ip)) return IsPrivate(ip);
        if (!host.Contains('.') || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        try
        {
            // Any other name must point into the local network too.
            var addresses = Dns.GetHostAddresses(host);
            return addresses.Length > 0 && addresses.All(IsPrivate);
        }
        catch (SocketException) { return false; }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] is >= 64 and <= 127);   // CGNAT, used by Tailscale
    }
}
