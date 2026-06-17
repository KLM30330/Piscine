using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;
using System.Text.Json;

namespace PiscineController.Services;

public sealed class SensorService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly EzoPh _ph;
    private readonly EzoOrp _orp;
    private readonly EzoRtd _rtd;
    private readonly EzoPmp _pmp;
    private readonly FiltrationManager _filtration;
    private readonly PhPidController _pid;
    private readonly MqttService _mqtt;
    private readonly ILogger<SensorService> _logger;

    public SensorService(PoolConfig cfg, PoolState state,
        EzoPh ph, EzoOrp orp, EzoRtd rtd, EzoPmp pmp,
        FiltrationManager filtration, PhPidController pid,
        MqttService mqtt, ILogger<SensorService> logger)
    {
        _cfg = cfg; _state = state;
        _ph = ph; _orp = orp; _rtd = rtd; _pmp = pmp;
        _filtration = filtration; _pid = pid;
        _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SensorService: démarrage de la boucle de lecture");
        while (!ct.IsCancellationRequested)
        {
            try { await ReadAndPublish(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "SensorService: erreur lecture capteurs"); }
            await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
    }

    private async Task ReadAndPublish(CancellationToken ct)
    {
        double? tempC = _rtd.Read();
        if (tempC.HasValue)
        {
            _state.WaterTempC = tempC.Value;
            _ph.SetTemperatureCompensation(tempC.Value);
        }

        double? phVal = _ph.Read();
        double? orpMv = _orp.Read();
        _logger.LogInformation("SensorService cycle: temp={Temp} ph={Ph} orp={Orp} pumpRunning={Pump}",
            tempC, phVal, orpMv, _state.PumpRunning);
        if (phVal.HasValue) _state.PhValue = phVal.Value;
        if (orpMv.HasValue) _state.OrpMv = orpMv.Value;

        // ── Alarme pH bas ────────────────────────────────────────────────────
        // Le PID ne corrige que les pH trop hauts (dosage acide uniquement).
        // Un pH trop bas n'est jamais corrigé automatiquement : on le signale.
        bool phAlarmLow = phVal.HasValue
            && (_cfg.PhTarget - phVal.Value) > _cfg.PhDeadband;

        if (phAlarmLow)
            _logger.LogWarning("Alarme pH bas: {Ph:F2} (cible {Target:F2})",
                phVal!.Value, _cfg.PhTarget);

        if (phVal.HasValue)
        {
            double dose = _pid.Compute(phVal.Value, _state.PumpRunning);
            if (dose > 0)
            {
                _logger.LogInformation("Dosage pH: {Dose} mL (pH={Ph:F2})", dose, phVal.Value);
                _pmp.Dose(dose);
            }
        }

        if (orpMv.HasValue)
        {
            var (waterState, targetFreq, alarm) = _filtration.UpdateOrp(orpMv.Value);
            _filtration.SetTargetFreq(targetFreq);
            _filtration.CheckBoostExit(orpMv.Value);
            if (alarm) _logger.LogWarning("Alarme ORP: {Orp} mV — état {State}", orpMv.Value, waterState);
        }

        var payload = new SensorPayload(
            PhValue: phVal ?? _state.PhValue,
            OrpMv: orpMv ?? _state.OrpMv,
            WaterTempC: tempC ?? _state.WaterTempC,
            WaterState: _filtration.WaterState.ToString(),
            TargetFreqHz: _filtration.TargetFreqHz,
            OrpAlarm: _filtration.WaterState is WaterState.CriticalLow or WaterState.Overdose,
            PhDoseTotalMl: _pid.TotalMl,
            PhAlarmLow: phAlarmLow);

        await _mqtt.PublishAsync(
            $"{_cfg.MqttPrefix}/sensors",
            JsonSerializer.Serialize(payload, AppJsonContext.Default.SensorPayload),
            ct: ct);
    }
}
