using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TeslaBridge.Auth;

/// <summary>
/// Login i browseren: dashboardet starter (på LAN), Tesla sender browseren tilbage til
/// https://&lt;partnerdomæne&gt;/auth/callback. Callback accepteres kun med en state, som et
/// login startet her inden for 10 minutter har udstedt – ellers kan ingen udefra bruge den.
/// </summary>
public sealed class LoginFlow(TeslaTokenProvider tokens, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new();

    public string Start()
    {
        foreach (var (k, exp) in _pending)
            if (exp < time.GetUtcNow()) _pending.TryRemove(k, out _);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _pending[state] = time.GetUtcNow() + Lifetime;
        return tokens.BuildAuthorizeUrl(state);
    }

    public bool IsValidState(string? state) =>
        state is not null && _pending.TryGetValue(state, out var exp) && exp >= time.GetUtcNow();

    public async Task CompleteAsync(string? code, string? state, CancellationToken ct)
    {
        if (!IsValidState(state) || !_pending.TryRemove(state!, out _))
            throw new InvalidOperationException("Ukendt eller udløbet login. Start det igen fra dashboardet.");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Tesla sendte ingen kode tilbage.");
        await tokens.ExchangeCodeAsync(code, ct);
    }
}
