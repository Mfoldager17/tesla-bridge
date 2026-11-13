using Microsoft.Extensions.Hosting;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>Lets expected values the stream never confirmed fall back to the car's state.</summary>
public sealed class OptimisticExpiryService(VehicleStateStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                store.ExpirePending();
        }
        catch (OperationCanceledException) { }
    }
}
