namespace TeslaBridge.Commands;

public enum CommandOutcome
{
    Success,
    /// <summary>The car sleeps or is offline – wake it and try again.</summary>
    VehicleAsleep,
    /// <summary>The car got the command but said no (e.g. "is_charging").</summary>
    Refused,
    Failed,
}

public sealed record CommandResult(CommandOutcome Outcome, string? Reason = null)
{
    public static CommandResult Ok() => new(CommandOutcome.Success);
    public static CommandResult Asleep(string? reason = null) => new(CommandOutcome.VehicleAsleep, reason);
    public static CommandResult Refused(string reason) => new(CommandOutcome.Refused, reason);
    public static CommandResult Failed(string reason) => new(CommandOutcome.Failed, reason);
    public bool IsSuccess => Outcome == CommandOutcome.Success;
}

/// <summary>
/// A way to the car: the Fleet API, Bluetooth through an ESP32, or the simulator. The rest of the bridge only knows
/// this interface.
/// </summary>
public interface ICommandTransport
{
    string Name { get; }

    /// <summary>Lower numbers are tried first (Bluetooth 0, the simulator 50, the Fleet API 100).</summary>
    int Priority { get; }

    /// <summary>Can the transport reach the car right now? (Bluetooth: is the car in range.)</summary>
    ValueTask<bool> CanReachAsync(string vin, CancellationToken ct);

    Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct);

    Task<CommandResult> WakeAsync(string vin, CancellationToken ct);
}

/// <summary>Picks the transports for each command, in order of priority.</summary>
public sealed class CommandRouter(IEnumerable<ICommandTransport> transports)
{
    private readonly ICommandTransport[] _transports = transports.OrderBy(t => t.Priority).ToArray();

    public async Task<IReadOnlyList<ICommandTransport>> CandidatesAsync(string vin, CancellationToken ct)
    {
        var list = new List<ICommandTransport>();
        foreach (var t in _transports)
            if (await t.CanReachAsync(vin, ct)) list.Add(t);
        return list;
    }
}
