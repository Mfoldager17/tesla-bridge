using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TeslaBridge.Auth;
using TeslaBridge.Options;

namespace TeslaBridge.Tests;

/// <summary>Tesla's refresh tokens rotate: a renewed one that gets lost means signing in again.</summary>
public sealed class TokenProviderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tokens").FullName;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-11-13T12:00:00Z"));
    private readonly FakeTesla _tesla = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Tesla's token endpoint: every renewal hands out access token A{n} and refresh token R{n}.</summary>
    private sealed class FakeTesla : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Requests.Add(form);
            var n = Requests.Count + 1;
            var body = JsonSerializer.Serialize(new { access_token = $"A{n}", refresh_token = $"R{n}", expires_in = 3600 });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private string TokenDir => Path.Combine(_dir, "data");
    private string TokenFile => Path.Combine(TokenDir, "tokens.json");

    private TeslaTokenProvider Provider()
    {
        var opts = new BridgeOptions();
        opts.Tesla.TokenFile = TokenFile;
        opts.Tesla.ClientId = "client";
        opts.Tesla.TokenUrl = "https://auth.example.com/token";
        return new TeslaTokenProvider(new HttpClient(_tesla), Microsoft.Extensions.Options.Options.Create(opts), _time,
            NullLogger<TeslaTokenProvider>.Instance);
    }

    private void SignedIn(string access, string refresh, TimeSpan validFor)
    {
        Directory.CreateDirectory(TokenDir);
        File.WriteAllText(TokenFile, JsonSerializer.Serialize(new StoredTokens(access, refresh, _time.GetUtcNow() + validFor)));
    }

    private StoredTokens OnDisk() => JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(TokenFile))!;

    [Fact]
    public async Task Without_tokens_it_says_to_sign_in()
    {
        await Assert.ThrowsAsync<TeslaNotSignedInException>(() => Provider().GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task A_fresh_token_is_used_and_a_nearly_expired_one_renewed_and_saved()
    {
        SignedIn("A1", "R1", TimeSpan.FromHours(1));
        var p = Provider();
        Assert.Equal("A1", await p.GetAccessTokenAsync(default));
        Assert.Empty(_tesla.Requests);

        _time.Advance(TimeSpan.FromMinutes(56));   // less than 5 minutes left
        Assert.Equal("A2", await p.GetAccessTokenAsync(default));
        Assert.Equal("refresh_token", _tesla.Requests[0]["grant_type"]);
        Assert.Equal("R1", _tesla.Requests[0]["refresh_token"]);
        Assert.Equal("R2", OnDisk().RefreshToken);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(TokenFile));
    }

    [Fact]
    public async Task A_renewed_token_survives_a_full_disk()
    {
        SignedIn("A1", "R1", TimeSpan.FromHours(1));
        var p = Provider();
        await p.GetAccessTokenAsync(default);   // loaded

        // The disk can't be written (here: the folder is replaced by a file).
        Directory.Delete(TokenDir, recursive: true);
        File.WriteAllText(TokenDir, "");
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("A2", await p.GetAccessTokenAsync(default));   // renewed, but not saved

        // The next renewal uses the rotated refresh token kept in memory, not the spent one.
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal("A3", await p.GetAccessTokenAsync(default));
        Assert.Equal("R2", _tesla.Requests[1]["refresh_token"]);

        // Room again: it's saved on the next call, without asking Tesla.
        File.Delete(TokenDir);
        Assert.Equal("A3", await p.GetAccessTokenAsync(default));
        Assert.Equal(2, _tesla.Requests.Count);
        Assert.Equal("R3", OnDisk().RefreshToken);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_renewal()
    {
        SignedIn("A1", "R1", TimeSpan.FromMinutes(1));
        var p = Provider();
        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => p.GetAccessTokenAsync(default)));
        Assert.All(tokens, t => Assert.Equal("A2", t));
        Assert.Single(_tesla.Requests);
    }
}
