using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Auth;

/// <summary>
/// Appens nøglepar. Den private nøgle ligger i en fil, som tesla-proxy også signerer med;
/// bridgen læser den kun for at kunne vise den offentlige nøgle og sammenligne med Teslas registrering.
/// </summary>
public sealed class TeslaKey(IOptions<BridgeOptions> options)
{
    private readonly string _file = options.Value.Tesla.FleetKeyFile;
    private readonly Lock _lock = new();
    private DateTime _loadedMtime;
    private PublicKeyInfo? _cached;

    public sealed record PublicKeyInfo(string Pem, string UncompressedHex, string Fingerprint);

    public bool Exists => File.Exists(_file);

    /// <summary>Den offentlige nøgle, eller null hvis den private nøgle mangler. Genindlæses hvis filen ændres.</summary>
    public PublicKeyInfo? Public
    {
        get
        {
            if (!File.Exists(_file)) return null;
            var mtime = File.GetLastWriteTimeUtc(_file);
            lock (_lock)
            {
                if (_cached is null || mtime != _loadedMtime)
                {
                    _cached = FromPrivatePem(File.ReadAllText(_file));
                    _loadedMtime = mtime;
                }
                return _cached;
            }
        }
    }

    public static PublicKeyInfo FromPrivatePem(string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);   // SEC1 ("EC PRIVATE KEY") og PKCS#8 – samme formater som tesla-proxy accepterer
        var p = ec.ExportParameters(false);
        if (p.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Tesla kræver en prime256v1 (P-256) nøgle");

        var spki = ec.ExportSubjectPublicKeyInfo();
        var pem = PemEncoding.WriteString("PUBLIC KEY", spki) + "\n";
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 33);
        var fp = Convert.ToHexString(SHA256.HashData(spki))[..16].ToLowerInvariant();
        return new PublicKeyInfo(pem, Convert.ToHexString(point).ToLowerInvariant(), string.Join(':', fp.Chunk(4).Select(c => new string(c))));
    }
}
