using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

public enum CommandPhase { Pending, Waking, Succeeded, Failed }

/// <param name="Note">Something the bridge did on its own to make the command work, e.g. turned the climate on.</param>
public sealed record CommandStatus(string Vin, string Command, CommandPhase Phase, string? Transport, string? Error, DateTimeOffset At, string? Note = null);

/// <summary>
/// Runs a command:
///  1. sets the expected values at once (optimistic – the UI changes straight away)
///  2. sends it through the first transport that can reach the car
///  3. if the car sleeps: wakes it and tries again until WakeTimeout
///  4. if it fails: rolls the expected values back
/// The stream confirms the real state afterwards.
/// </summary>
public sealed class CommandService(
    CommandRouter router,
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    TimeProvider time,
    ILogger<CommandService> log,
    TeslaBridge.Settings.SettingsStore? settings = null)
{
    private readonly CommandOptions _opts = options.Value.Commands;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perVin = new(StringComparer.OrdinalIgnoreCase);

    public event Action<CommandStatus>? StatusChanged;

    /// <summary>Time between tries while waiting for the car to wake up. Internal, so tests can turn it down.</summary>
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
            string? transport, note = null;
            try
            {
                (result, transport) = await SendWithWakeAsync(vin, command, name, ct);
                if (NeedsClimate(command, result))
                {
                    if (settings?.Current.ClimateForSeatHeat ?? true)
                    {
                        (result, transport) = await WithClimateOnAsync(vin, command, name, ct);
                        if (result.IsSuccess) note = ClimateNeededNote;
                    }
                    else result = CommandResult.Refused(ClimateNeededError);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // E.g. not signed in, or the proxy is down – still shown as a failure and rolled back.
                (result, transport) = (CommandResult.Failed(ex.Message), null);
            }

            if (result.IsSuccess)
            {
                // The expectation now only holds for OptimisticTimeout more – by then the stream must have confirmed it.
                // (Not if the car already confirmed it while we waited – over Bluetooth that is often instant.)
                foreach (var e in command.Expectations)
                    if (store.HasPending(vin, e.Key))
                        store.SetPending(vin, e.Key, e.Value, TimeSpan.FromSeconds(_opts.OptimisticTimeoutSeconds), e.ConfirmedBy);
                Report(vin, name, CommandPhase.Succeeded, transport, note: note);
                log.LogInformation("{Command} to {Vin} succeeded via {Transport}", name, vin, transport);
            }
            else
            {
                foreach (var e in command.Expectations)
                    store.ClearPending(vin, e.Key);
                Report(vin, name, CommandPhase.Failed, transport, result.Reason ?? result.Outcome.ToString());
                log.LogWarning("{Command} to {Vin} failed: {Outcome} {Reason}", name, vin, result.Outcome, result.Reason);
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
        Report(vin, "ClimateOn", CommandPhase.Succeeded, onVia, note: $"Turned on for {name}");

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
        if (candidates.Count == 0) return (CommandResult.Failed("No transport can reach the car"), null);

        CommandResult last = CommandResult.Failed("not tried");
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
                    last = CommandResult.Failed($"The car didn't wake up within {_opts.WakeTimeoutSeconds} s");
            }

            if (last.IsSuccess || last.Outcome == CommandOutcome.Refused) return (last, transport.Name);
            // Failed: try the next transport (e.g. the Fleet API when Bluetooth failed).
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
        // The wake call succeeded; the stream just hasn't said online yet.
        return CommandResult.Ok();
    }

    private bool IsOnline(string vin) =>
        store.GetConfirmed(vin, StateKeys.Online)?.Value is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>Waits until the stream says the car is online, or RetryDelay – whichever comes first.</summary>
    private async Task WaitForOnlineOrDelayAsync(string vin, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(StateChange c)
        {
            if (c.Key == StateKeys.Online && string.Equals(c.Vin, vin, StringComparison.OrdinalIgnoreCase) &&
                c.Effective?.Value is JsonValue v && v.TryGetValue(out bool b) && b)
                tcs.TrySetResult();
        }
        store.Changed += Handler;
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(RetryDelay, time, ct));
            // When the car has just come online, give it a moment before the next try.
            if (tcs.Task.IsCompleted) await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        }
        finally
        {
            store.Changed -= Handler;
        }
        ct.ThrowIfCancellationRequested();
    }

    private void Report(string vin, string command, CommandPhase phase, string? transport = null, string? error = null, string? note = null) =>
        StatusChanged?.Invoke(new CommandStatus(vin, command, phase, transport, error, time.GetUtcNow(), note));

    public const string ClimateNeededNote = "The climate was off, so it was turned on first – the car only heats seats and the steering wheel while it is on";
    public const string ClimateNeededError = "The car only heats seats and the steering wheel while the climate is on. Turn the climate on first, or let the bridge do it (Settings → Seat heat).";
}
