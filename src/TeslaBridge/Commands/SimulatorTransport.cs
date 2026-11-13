using System.Text.Json.Nodes;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>
/// Only with Bridge__Web__Simulator=true (development, screenshots, tests): a pretend car. It is asleep after
/// 5 minutes without commands, takes a few seconds to wake, and confirms each command over the "stream"
/// a moment later – so waking, pending and confirmation can be seen without a real car or a Tesla login.
/// </summary>
public sealed class SimulatorTransport(VehicleStateStore store, TimeProvider time) : ICommandTransport
{
    private readonly Dictionary<string, DateTimeOffset> _awakeUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _wakingDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    internal static TimeSpan StaysAwake { get; set; } = TimeSpan.FromMinutes(5);
    internal static TimeSpan WakeTime { get; set; } = TimeSpan.FromSeconds(6);
    internal static TimeSpan ConfirmAfter { get; set; } = TimeSpan.FromSeconds(1.5);

    public string Name => "simulator";
    public int Priority => 50;

    public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(true);

    public Task<CommandResult> WakeAsync(string vin, CancellationToken ct)
    {
        lock (_lock) if (!Awake(vin)) _wakingDone[vin] = time.GetUtcNow() + WakeTime;
        return Task.FromResult(CommandResult.Ok());
    }

    public Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!Awake(vin)) return Task.FromResult(CommandResult.Asleep());
            _awakeUntil[vin] = time.GetUtcNow() + StaysAwake;
        }
        store.Apply(vin, StateKeys.Online, JsonValue.Create(true), time.GetUtcNow(), ValueSource.Stream);
        var expected = command.Expectations;
        _ = Task.Run(async () =>
        {
            await Task.Delay(ConfirmAfter, time);
            foreach (var e in expected) store.Apply(vin, e.Key, e.Value?.DeepClone(), time.GetUtcNow(), ValueSource.Stream);
        }, CancellationToken.None);   // confirms even when the caller stops waiting
        return Task.FromResult(CommandResult.Ok());
    }

    public void Sleep(string vin)
    {
        lock (_lock) { _awakeUntil.Remove(vin); _wakingDone.Remove(vin); }
    }

    private bool Awake(string vin)
    {
        var now = time.GetUtcNow();
        if (_wakingDone.TryGetValue(vin, out var done) && done <= now)
        {
            _wakingDone.Remove(vin);
            _awakeUntil[vin] = now + StaysAwake;
        }
        return _awakeUntil.TryGetValue(vin, out var until) && until > now;
    }
}
