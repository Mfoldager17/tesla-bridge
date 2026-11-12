using Microsoft.Extensions.Logging;
using TeslaBridge.Commands;
using TeslaBridge.Usage;

namespace TeslaBridge.Ble;

/// <summary>
/// Commands over Bluetooth through the ESP32 next to the car. Tried first whenever the car is in range;
/// free and independent of the internet. The ESP32 wakes the car itself when needed. Commands it can't do
/// (e.g. seat heaters, guest mode) and any error fall through to the Fleet API.
/// </summary>
public sealed class BleTransport(BleService ble, UsageTracker usage, ILogger<BleTransport> log) : ICommandTransport
{
    public const string NotOverBle = "not available over Bluetooth";

    public string Name => "ble";
    public int Priority => 0;

    public ValueTask<bool> CanReachAsync(string vin, CancellationToken ct) => ValueTask.FromResult(ble.InRange(vin));

    public async Task<CommandResult> SendAsync(string vin, VehicleCommand command, CancellationToken ct)
    {
        if (TeslaBleMap.ActionFor(command) is not { } a) return CommandResult.Failed(NotOverBle);
        if (ble.Device(vin) is not { } device) return CommandResult.Failed("no ESP32");
        var error = await device.ActAsync(a.Domain, a.Name, a.Action, a.Query, ct);
        if (error is not null)
        {
            log.LogWarning("{Command} over Bluetooth failed: {Error} – trying the Fleet API", command, error);
            return CommandResult.Failed(error);
        }
        usage.CountBle(command is Cmd.Wake ? "wake_up" : command.FleetEndpoint);
        return CommandResult.Ok();
    }

    public async Task<CommandResult> WakeAsync(string vin, CancellationToken ct)
    {
        if (ble.Device(vin) is not { } device) return CommandResult.Failed("no ESP32");
        var error = await device.ActAsync("button", TeslaBleMap.WakeButton, "press", null, ct);
        if (error is not null) return CommandResult.Failed(error);
        usage.CountBle("wake_up");
        return CommandResult.Ok();
    }
}
