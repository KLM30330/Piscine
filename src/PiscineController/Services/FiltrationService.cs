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
    private readonly MqttService _mqtt;
    private readonly ILogger<FiltrationService> _logger;

    // ── Chrono de filtration journalier ──────────────────────────────────────
    // Horodatage précis (pas un compteur de cycles) pour éviter que les
    // variations de durée de cycle (retry Modbus, délai I2C) ne biaisent
    // l'accumulation. Réinitialisation automatique à minuit.
    private DateTimeOffset? _pumpOnSince;
    private double          _elapsedTodaySec;
    private int             _currentDay = -1;
    private DateTimeOffset  _lastElapsedPublish = DateTimeOffset.MinValue;

    public FiltrationService(PoolConfig cfg, PoolState state,
        FiltrationManager filtration, Wk600Drive drive,
        MqttService mqtt, ILogger<FiltrationService> logger)
    {
        _cfg = cfg; _state = state;
        _filtration = filtration; _drive = drive;
        _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // _state.WaterTempC vaut 0.0 (défaut du type double) jusqu'à ce que
                // SensorService ait effectué sa première lecture EZO RTD. Construire
                // le planning avec une température à 0°C produit un nombre d'heures
                // de filtration ridiculement bas (proche du minimum hydraulique),
                // ce qui tronque le créneau du jour. On attend une vraie lecture.
                bool tempReady = _state.WaterTempC > 0.0;

                if (!tempReady && _filtration.Mode == FilterMode.Auto && _filtration.NeedsRebuild())
                {
                    _logger.LogDebug("FiltrationService: en attente d'une lecture de " +
                                      "température valide avant de construire le planning");
                }

                if (_filtration.Mode == FilterMode.Auto && _filtration.NeedsRebuild() && tempReady)
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

                    // Conversion en format lisible hh:mm–hh:mm pour HA et panneau tactile
                    static string ToHM(double h)
                    {
                        int hh = (int)Math.Floor(h);   // pas de % 24 : 24h00 = fin de journée
                        int mm = (int)Math.Round((h - Math.Floor(h)) * 60);
                        if (mm == 60) { hh++; mm = 0; }
                        return $"{hh:D2}h{mm:D2}";
                    }

                    var slots = schedule.Select(s => new ScheduleSlot(
                        s.Start, s.End,
                        $"{ToHM(s.Start)}–{ToHM(s.End)}")).ToList();

                    var payload = new SchedulePayload(
                        RequiredHours: _filtration.CurrentRequiredHours,
                        WaterTempC:    _state.WaterTempC,
                        Slots:         slots,
                        SlotsLabel:    string.Join(", ", slots.Select(s => s.Label)));

                    try
                    {
                        await _mqtt.PublishAsync(
                            $"{_cfg.MqttPrefix}/schedule",
                            System.Text.Json.JsonSerializer.Serialize(payload,
                                PiscineController.Config.AppJsonContext.Default.SchedulePayload),
                            retain: true, ct: ct);
                    }
                    catch (Exception ex)
                    { _logger.LogError(ex, "FiltrationService: échec publication planning MQTT"); }
                }

                bool shouldRun = _filtration.ShouldPumpRun();
                double targetFreq = _filtration.GetRunFreq();

                // ── Chrono filtration journalier ──────────────────────────────
                var now   = DateTimeOffset.Now;
                int today = now.DayOfYear;

                if (today != _currentDay)
                {
                    // Minuit : fermer le créneau en cours avant reset
                    if (_pumpOnSince.HasValue)
                        _elapsedTodaySec += (now - _pumpOnSince.Value).TotalSeconds;
                    _elapsedTodaySec = 0;
                    _pumpOnSince     = (_drive.IsRunning || _filtration.Mode == FilterMode.Rescue) ? now : null;
                    _currentDay      = today;
                    _logger.LogInformation("Chrono filtration remis à zéro (nouveau jour)");
                }

                bool pumpIsOn = _drive.IsRunning || _filtration.Mode == FilterMode.Rescue;

                if (pumpIsOn && !_pumpOnSince.HasValue)
                    _pumpOnSince = now;
                else if (!pumpIsOn && _pumpOnSince.HasValue)
                {
                    _elapsedTodaySec += (now - _pumpOnSince.Value).TotalSeconds;
                    _pumpOnSince      = null;
                }

                double elapsedSec  = _elapsedTodaySec
                    + (_pumpOnSince.HasValue ? (now - _pumpOnSince.Value).TotalSeconds : 0);
                double elapsedH    = Math.Round(elapsedSec / 3600.0, 2);
                double requiredH   = _filtration.CurrentRequiredHours;
                double remainingH  = Math.Max(0, Math.Round(requiredH - elapsedH, 2));

                // Publication toutes les 30s (valeur qui change lentement)
                if ((now - _lastElapsedPublish).TotalSeconds >= 30)
                {
                    _lastElapsedPublish = now;
                    try
                    {
                        await _mqtt.PublishAsync(
                            $"{_cfg.MqttPrefix}/filtration/elapsed",
                            $"{{\"elapsed_h\":{elapsedH},\"remaining_h\":{remainingH}," +
                            $"\"required_h\":{requiredH:F1},\"pump_on\":{pumpIsOn.ToString().ToLower()}}}",
                            retain: true, ct: ct);
                    }
                    catch (Exception ex)
                    { _logger.LogError(ex, "FiltrationService: échec publication temps écoulé"); }
                }

                if (shouldRun && !_drive.IsRunning)
                {
                    _logger.LogInformation("Démarrage pompe @ {Freq} Hz", targetFreq);
                    await _drive.RampStartAsync(targetFreq, ct);
                    // Ne pas supposer le succès : si RampStartAsync a échoué (timeout
                    // Modbus), _drive.IsRunning reste false et on retentera au cycle
                    // suivant plutôt que de mentir sur l'état réel de la pompe.
                    _state.PumpRunning = _drive.IsRunning;
                }
                else if (!shouldRun && _drive.IsRunning)
                {
                    _logger.LogInformation("Arrêt pompe");
                    await _drive.RampStopAsync(ct);
                    _state.PumpRunning = _drive.IsRunning;
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
