using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

public enum CommandPhase { Pending, Waking, Succeeded, Failed }

public sealed record CommandStatus(string Vin, string Command, CommandPhase Phase, string? Transport, string? Error, DateTimeOffset At);

/// <summary>
/// Udfører en kommando:
///  1. sætter forventede værdier med det samme (optimistisk – UI'et skifter straks)
///  2. sender via første transport der kan nå bilen
///  3. sover bilen: vækker den og prøver igen indtil WakeTimeout
///  4. fejler det: ruller de forventede værdier tilbage
/// Streamen bekræfter bagefter den rigtige tilstand.
/// </summary>
public sealed class CommandService(
    CommandRouter router,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<CommandService> log)
{
    private readonly CommandOptions _opts = options.Value.Commands;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perVin = new(StringComparer.OrdinalIgnoreCase);

    public event Action<CommandStatus>? StatusChanged;

    /// <summary>Tid mellem forsøg mens vi venter på at bilen vågner. Internal så tests kan skrue ned.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(4);

    public async Task<CommandResult> ExecuteAsync(string vin, VehicleCommand command, CancellationToken ct = default)
    {
        var name = command.Name;
        var gate = _perVin.GetOrAdd(vin, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            Report(vin, name, CommandPhase.Pending);
            var timeout = TimeSpan.FromSeconds(_opts.OptimisticTimeoutSeconds + _opts.WakeTimeoutSeconds);
            foreach (var e in command.Expectations)
                store.SetPending(vin, e.Key, e.Value, timeout, e.ConfirmedBy);

            CommandResult result;
            string? transport;
            try
            {
                (result, transport) = await SendWithWakeAsync(vin, command, name, ct);
                if (NeedsClimate(command, result))
                    (result, transport) = await WithClimateOnAsync(vin, command, name, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fx manglende login eller proxy nede – skal stadig vises som fejl og rulles tilbage.
                (result, transport) = (CommandResult.Failed(ex.Message), null);
            }

            if (result.IsSuccess)
            {
                // Forventningen gælder nu kun OptimisticTimeout mere – så må streamen have bekræftet.
                // (Not if the car already confirmed it while we waited – over Bluetooth that is often instant.)
                foreach (var e in command.Expectations)
                    if (store.HasPending(vin, e.Key))
                        store.SetPending(vin, e.Key, e.Value, TimeSpan.FromSeconds(_opts.OptimisticTimeoutSeconds), e.ConfirmedBy);
                Report(vin, name, CommandPhase.Succeeded, transport);
                log.LogInformation("{Command} til {Vin} lykkedes via {Transport}", name, vin, transport);
            }
            else
            {
                foreach (var e in command.Expectations)
                    store.ClearPending(vin, e.Key);
                Report(vin, name, CommandPhase.Failed, transport, result.Reason ?? result.Outcome.ToString());
                log.LogWarning("{Command} til {Vin} fejlede: {Outcome} {Reason}", name, vin, result.Outcome, result.Reason);
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Seat and steering-wheel heating only work while the climate is on – otherwise the car answers
    /// "cabin comfort remote settings not enabled". Do what the Tesla app does: turn the climate on first.
    /// </summary>
    internal static bool NeedsClimate(VehicleCommand command, CommandResult result) =>
        !result.IsSuccess
        && command is Cmd.SetSeatHeater or Cmd.SetAutoSeatClimate or Cmd.SetSteeringWheelHeat
            or Cmd.SetAutoSteeringWheelHeat or Cmd.SetSteeringWheelHeatLevel
        && result.Reason?.Contains("cabin comfort remote settings not enabled", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<(CommandResult, string?)> WithClimateOnAsync(string vin, VehicleCommand command, string name, CancellationToken ct)
    {
        log.LogInformation("{Command}: the climate is off – turning it on first", name);
        var climate = new Cmd.ClimateOn();
        foreach (var e in climate.Expectations)
            store.SetPending(vin, e.Key, e.Value, TimeSpan.FromSeconds(_opts.OptimisticTimeoutSeconds), e.ConfirmedBy);
        var (on, onVia) = await SendWithWakeAsync(vin, climate, name, ct);
        if (!on.IsSuccess)
        {
            foreach (var e in climate.Expectations) store.ClearPending(vin, e.Key);
            return (CommandResult.Failed($"The climate is off, and turning it on failed: {on.Reason ?? on.Outcome.ToString()}"), onVia);
        }
        Report(vin, "ClimateOn", CommandPhase.Succeeded, onVia);

        // The car needs a moment before it accepts cabin comfort commands after the climate starts.
        CommandResult last = on; string? via = onVia;
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(RetryDelay, time, ct);
            (last, via) = await SendWithWakeAsync(vin, command, name, ct);
            if (!NeedsClimate(command, last)) break;
        }
        return (last, via);
    }

    private async Task<(CommandResult, string?)> SendWithWakeAsync(string vin, VehicleCommand command, string name, CancellationToken ct)
    {
        var candidates = await router.CandidatesAsync(vin, ct);
        if (candidates.Count == 0) return (CommandResult.Failed("Ingen transport kan nå bilen"), null);

        CommandResult last = CommandResult.Failed("ikke forsøgt");
        foreach (var transport in candidates)
        {
            if (command is Cmd.Wake)
            {
                last = await WakeAndWaitAsync(vin, transport, name, ct);
                if (last.IsSuccess) return (last, transport.Name);
                continue;
            }

            last = await transport.SendAsync(vin, command, ct);
            if (last.Outcome == CommandOutcome.VehicleAsleep)
            {
                // Wake the cheapest way that can reach the car: over Bluetooth it's free, over the Fleet API a wake costs.
                foreach (var waker in candidates)
                {
                    Report(vin, name, CommandPhase.Waking, waker.Name);
                    if ((await waker.WakeAsync(vin, ct)).IsSuccess) break;
                }

                var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(_opts.WakeTimeoutSeconds);
                while (last.Outcome == CommandOutcome.VehicleAsleep && time.GetUtcNow() < deadline)
                {
                    await WaitForOnlineOrDelayAsync(vin, ct);
                    last = await transport.SendAsync(vin, command, ct);
                }
                if (last.Outcome == CommandOutcome.VehicleAsleep)
                    last = CommandResult.Failed($"Bilen vågnede ikke inden for {_opts.WakeTimeoutSeconds} s");
            }

            if (last.IsSuccess || last.Outcome == CommandOutcome.Refused) return (last, transport.Name);
            // Failed: prøv næste transport (fx cloud hvis BLE fejlede).
        }
        return (last, candidates[^1].Name);
    }

    private async Task<CommandResult> WakeAndWaitAsync(string vin, ICommandTransport transport, string name, CancellationToken ct)
    {
        Report(vin, name, CommandPhase.Waking, transport.Name);
        var r = await transport.WakeAsync(vin, ct);
        if (!r.IsSuccess) return r;

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(_opts.WakeTimeoutSeconds);
        while (time.GetUtcNow() < deadline)
        {
            if (IsOnline(vin)) return CommandResult.Ok();
            await WaitForOnlineOrDelayAsync(vin, ct);
        }
        // Wake-kaldet lykkedes; streamen har bare ikke meldt online endnu.
        return CommandResult.Ok();
    }

    private bool IsOnline(string vin) =>
        store.GetConfirmed(vin, "online")?.Value is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>Vent til streamen melder bilen online, eller RetryDelay – hvad der kommer først.</summary>
    private async Task WaitForOnlineOrDelayAsync(string vin, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(StateChange c)
        {
            if (c.Key == "online" && string.Equals(c.Vin, vin, StringComparison.OrdinalIgnoreCase) &&
                c.Effective?.Value is JsonValue v && v.TryGetValue(out bool b) && b)
                tcs.TrySetResult();
        }
        store.Changed += Handler;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(RetryDelay, time, ct));
            // Når bilen lige er kommet online, giv den et øjeblik før næste forsøg.
            if (tcs.Task.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        }
        finally
        {
            store.Changed -= Handler;
        }
        ct.ThrowIfCancellationRequested();
    }

    private void Report(string vin, string command, CommandPhase phase, string? transport = null, string? error = null) =>
        StatusChanged?.Invoke(new CommandStatus(vin, command, phase, transport, error, time.GetUtcNow()));
}
