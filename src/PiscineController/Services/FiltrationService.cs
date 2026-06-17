using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class FiltrationService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly FiltrationManager _filtration;
    private readonly Wk600Drive _drive;
    private readonly ILogger<FiltrationService> _logger;

    public FiltrationService(PoolConfig cfg, PoolState state,
        FiltrationManager filtration, Wk600Drive drive,
        ILogger<FiltrationService> logger)
    {
        _cfg = cfg; _state = state;
        _filtration = filtration; _drive = drive; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_filtration.Mode == FilterMode.Auto && _filtration.NeedsRebuild())
                {
                    // Le planning quotidien doit être basé sur la fréquence NOMINALE,
                    // pas sur la fréquence ORP instantanée (_filtration.TargetFreqHz).
                    // Sinon, un redémarrage du service en pleine journée — ou même
                    // un simple cycle SensorService qui modifie TargetFreqHz avant
                    // ce rebuild — fait varier le nombre d'heures total calculé,
                    // ce qui peut tronquer le créneau en cours et arrêter la pompe
                    // bien avant l'heure de fin prévue.
                    var schedule = _filtration.BuildSchedule(_state.WaterTempC, _cfg.FreqNominal);
                    _logger.LogInformation("Planning filtration reconstruit ({Count} créneau(x)): {Slots}",
                        schedule.Count,
                        string.Join(", ", schedule.Select(s => $"[{s.Start:F1}-{s.End:F1}]")));
                }

                bool shouldRun = _filtration.ShouldPumpRun();
                double targetFreq = _filtration.GetRunFreq();

                if (shouldRun && !_drive.IsRunning)
                {
                    _logger.LogInformation("Démarrage pompe @ {Freq} Hz", targetFreq);
                    await _drive.RampStartAsync(targetFreq, ct);
                    _state.PumpRunning = true;
                }
                else if (!shouldRun && _drive.IsRunning)
                {
                    _logger.LogInformation("Arrêt pompe");
                    await _drive.RampStopAsync(ct);
                    _state.PumpRunning = false;
                }
                else if (shouldRun && _drive.IsRunning
                      && Math.Abs(targetFreq - _drive.CurrentFreq) > 2.0
                      && _filtration.Mode == FilterMode.Forced)
                {
                    await _drive.RampToAsync(targetFreq, ct);
                }

                _state.PumpFreqHz = _drive.CurrentFreq;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "FiltrationService: erreur boucle");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        if (_drive.IsRunning)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _drive.RampStopAsync(cts.Token);
        }
    }
}
