using Microsoft.Extensions.Hosting;
using TeslaBridge.State;

namespace TeslaBridge.Commands;

/// <summary>Lader forventede værdier, som streamen aldrig bekræftede, falde tilbage til bilens tilstand.</summary>
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
