using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;
using TeslaBridge.Storage;

namespace TeslaBridge.State;

/// <summary>
/// Saves the last known values to disk and loads them at start, so nothing shows "unknown" after a restart while
/// the car sleeps.
/// </summary>
public sealed class StatePersistenceService(
    VehicleStateStore store,
    IOptions<BridgeOptions> options,
    ILogger<StatePersistenceService> log) : BackgroundService
{
    private readonly string _file = options.Value.StateFile;
    private volatile bool _dirty;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_file))
        {
            try
            {
                store.Restore(File.ReadAllText(_file));
                log.LogInformation("Loaded the last known state from {File}", _file);
            }
            catch (Exception ex)
            {
                var moved = AtomicFile.Quarantine(_file);
                log.LogWarning(ex, "Can't read {File} – starting without saved state (the file was moved to {Moved})", _file, moved);
            }
        }
        store.ConfirmedChanged += MarkDirty;
        return base.StartAsync(cancellationToken);
    }

    private void MarkDirty() => _dirty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Save();
        }
        catch (OperationCanceledException) { }
        finally
        {
            store.ConfirmedChanged -= MarkDirty;
            Save();
        }
    }

    private void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            AtomicFile.WriteAllText(_file, store.Serialize());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Can't save the state to {File}", _file);
            _dirty = true;
        }
    }
}
