using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TeslaBridge.Auth;

/// <summary>
/// Sign-in in the browser: the dashboard starts it (on the LAN), and Tesla sends the browser back to
/// https://&lt;partner domain&gt;/auth/callback. The callback is only accepted with a state that a sign-in started here
/// within 10 minutes handed out – so nobody from outside can use it.
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
            throw new InvalidOperationException("Unknown or expired sign-in. Start it again from the dashboard.");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Tesla sendte ingen kode tilbage.");
        await tokens.ExchangeCodeAsync(code, ct);
    }
}
