using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.Auth;

/// <summary>
/// The app's key pair. tesla-proxy signs commands with the private key; the bridge only needs the public key, to
/// publish it on the partner domain and compare it with Tesla's registration. It reads the public key file the init
/// container writes, and only falls back to the private key outside Docker (dotnet run).
/// </summary>
public sealed class TeslaKey(IOptions<BridgeOptions> options)
{
    private readonly string _public = options.Value.Tesla.FleetPublicKeyFile;
    private readonly string _private = options.Value.Tesla.FleetKeyFile;
    private string File_ => File.Exists(_public) ? _public : _private;
    private readonly Lock _lock = new();
    private DateTime _loadedMtime;
    private PublicKeyInfo? _cached;

    public sealed record PublicKeyInfo(string Pem, string UncompressedHex, string Fingerprint);

    public bool Exists => File.Exists(File_);

    /// <summary>The public key, or null when there is no key yet. Read again when the file changes.</summary>
    public PublicKeyInfo? Public
    {
        get
        {
            var file = File_;
            if (!File.Exists(file)) return null;
            var mtime = File.GetLastWriteTimeUtc(file);
            lock (_lock)
            {
                if (_cached is null || mtime != _loadedMtime)
                {
                    _cached = FromPem(File.ReadAllText(file));
                    _loadedMtime = mtime;
                }
                return _cached;
            }
        }
    }

    /// <summary>The public key from a private key (SEC1 "EC PRIVATE KEY" or PKCS#8) or a public key ("PUBLIC KEY") PEM.</summary>
    public static PublicKeyInfo FromPem(string keyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(keyPem);
        var p = ec.ExportParameters(false);
        if (p.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException("Tesla needs a prime256v1 (P-256) key");

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
