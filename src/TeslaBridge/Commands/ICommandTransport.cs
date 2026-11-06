namespace TeslaBridge.Commands;

public enum CommandOutcome
{
    Success,
    /// <summary>Bilen sover eller er offline – væk den og prøv igen.</summary>
    VehicleAsleep,
    /// <summary>Bilen modtog kommandoen men sagde nej (fx "is_charging").</summary>
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
/// En vej til bilen. I dag kun Fleet API; senere fx BLE.
/// Resten af bridgen kender kun dette interface.
/// </summary>
public interface ICommandTransport
{
    string Name { get; }

    /// <summary>Lavere tal vælges først (BLE får fx 0, cloud 100).</summary>
    int Priority { get; }

    /// <summary>Kan transporten nå bilen lige nu? (BLE: er bilen inden for rækkevidde.)</summary>
    ValueTask<bool> CanReachAsync(string vin, CancellationToken ct);

    Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct);

    Task<CommandResult> WakeAsync(string vin, CancellationToken ct);
}

/// <summary>Vælger transport pr. kommando. Med én transport er den triviel – det er pointen.</summary>
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
