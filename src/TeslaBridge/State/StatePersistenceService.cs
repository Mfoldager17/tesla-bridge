using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeslaBridge.Options;

namespace TeslaBridge.State;

/// <summary>
/// Gemmer sidst kendte værdier på disk og indlæser dem ved opstart,
/// så intet står "unknown" efter en genstart mens bilen sover.
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
                log.LogInformation("Indlæste sidst kendte tilstand fra {File}", _file);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Kunne ikke læse {File} – starter uden gemt tilstand", _file);
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
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, store.Serialize());
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Kunne ikke gemme tilstand til {File}", _file);
            _dirty = true;
        }
    }
}
