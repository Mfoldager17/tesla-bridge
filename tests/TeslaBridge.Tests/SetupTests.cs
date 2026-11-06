using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TeslaBridge.Auth;
using TeslaBridge.Options;
using TeslaBridge.Web;

namespace TeslaBridge.Tests;

public class SetupTests
{
    private static string NewKeyPem()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ec.ExportECPrivateKeyPem();   // samme format som "openssl ecparam -genkey" (EC PRIVATE KEY)
    }

    [Fact]
    public void Public_key_is_derived_from_private_key()
    {
        var pem = NewKeyPem();
        var info = TeslaKey.FromPrivatePem(pem);

        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", info.Pem);
        Assert.Equal(130, info.UncompressedHex.Length);   // 04 + X + Y = 65 bytes
        Assert.StartsWith("04", info.UncompressedHex);

        using var check = ECDsa.Create();
        check.ImportFromPem(info.Pem);
        using var priv = ECDsa.Create();
        priv.ImportFromPem(pem);
        var sig = priv.SignData("hej"u8, HashAlgorithmName.SHA256);
        Assert.True(check.VerifyData("hej"u8, sig, HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Wrong_curve_is_rejected()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() => TeslaKey.FromPrivatePem(ec.ExportECPrivateKeyPem()));
    }

    [Fact]
    public void Partner_domain_and_redirect_default_to_telemetry_hostname()
    {
        var o = new BridgeOptions { Telemetry = new TelemetryOptions { Hostname = "tesla.example.dk" } };
        Assert.Equal("tesla.example.dk", o.PartnerDomain);
        Assert.Equal("https://tesla.example.dk/auth/callback", o.RedirectUri);
        Assert.Equal("https://tesla.example.dk/.well-known/appspecific/com.tesla.3p.public-key.pem", o.PublicKeyUrl);
        Assert.Equal("https://www.tesla.com/_ak/tesla.example.dk", o.PairingUrl);

        o.Tesla.PartnerDomain = "assist.example.dk";
        Assert.Equal("https://assist.example.dk/auth/callback", o.RedirectUri);
    }

    [Theory]
    [InlineData("/.well-known/appspecific/com.tesla.3p.public-key.pem", true)]
    [InlineData("/auth/callback", true)]
    [InlineData("/", false)]
    [InlineData("/api/state", false)]
    [InlineData("/api/vehicles/VIN/cmd/lock", false)]
    [InlineData("/auth/callback/../../api/state", false)]
    public void Public_listener_only_exposes_two_paths(string path, bool allowed) =>
        Assert.Equal(allowed, SetupApi.IsPublicPath(new WebOptions(), new PathString(path)));

    [Fact]
    public async Task Callback_requires_a_state_issued_here_and_only_once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-06T12:00:00Z"));
        var opts = Microsoft.Extensions.Options.Options.Create(new BridgeOptions
        {
            Telemetry = new TelemetryOptions { Hostname = "tesla.example.dk" },
            Tesla = new TeslaOptions { ClientId = "id", TokenFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json") },
        });
        var tokens = new TeslaTokenProvider(new HttpClient(), opts, time, NullLogger<TeslaTokenProvider>.Instance);
        var login = new LoginFlow(tokens, time);

        var url = login.Start();
        Assert.Contains("redirect_uri=https%3A%2F%2Ftesla.example.dk%2Fauth%2Fcallback", url);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["state"];

        Assert.False(login.IsValidState("forfalsket"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => login.CompleteAsync("code", "forfalsket", default));
        Assert.True(login.IsValidState(state));

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(login.IsValidState(state));
    }
}
