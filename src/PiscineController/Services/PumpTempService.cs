using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class PumpTempService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Ds18b20 _sensor;
    private readonly MqttService _mqtt;
    private readonly ILogger<PumpTempService> _logger;

    // null = jamais publié, force le premier envoi (cf. le même bug déjà
    // rencontré et corrigé sur ElectrolyzerService avec un bool non-nullable).
    private bool? _lastFaultPublished;

    // Valeurs de repli classiques du DS18B20 en cas de défaut matériel :
    // 85°C = registre de reset au power-on avant toute vraie conversion ;
    // 0°C = lecture "garbage" fréquente sur pull-up manquante/mal câblée,
    // masse de mauvaise qualité ou alimentation marginale. Un CRC valide
    // ("YES" dans w1_slave) n'exclut PAS ces deux cas.
    private const double FaultValueLow = 0.0;
    private const double FaultValueHigh = 85.0;
    private const double FaultTolerance = 0.05;

    public PumpTempService(PoolConfig cfg, PoolState state,
        Ds18b20 sensor, MqttService mqtt, ILogger<PumpTempService> logger)
    {
        _cfg = cfg; _state = state; _sensor = sensor; _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                double? tempC = _sensor.Read();

                if (tempC.HasValue)
                {
                    bool suspect = Math.Abs(tempC.Value - FaultValueLow) < FaultTolerance
                                || Math.Abs(tempC.Value - FaultValueHigh) < FaultTolerance;
                    await ReportFaultAsync(suspect,
                        suspect ? $"lecture suspecte ({tempC.Value:F1}°C, valeur typique d'un défaut de câblage one-wire)" : null,
                        ct);

                    if (tempC >= _cfg.PumpTempCriticalC)
                    {
                        _logger.LogCritical(
                            "Température pompe CRITIQUE: {T}°C — arrêt d'urgence de la filtration", tempC);
                        // Arrêt de sécurité : on passe en mode Stop pour protéger
                        // la pompe. L'opérateur doit relancer manuellement depuis HA.
                        _state.FilterMode = PiscineController.Filtration.FilterMode.Stop;
                    }
                    else if (tempC >= _cfg.PumpTempAlertC)
                        _logger.LogWarning("Température pompe élevée: {T}°C", tempC);

                    // On publie quand même la valeur brute (même suspecte) : on
                    // ne masque pas la donnée, on la signale en parallèle via
                    // pump_temp_fault plutôt que de la cacher silencieusement.
                    await _mqtt.PublishAsync(
                        $"{_cfg.MqttPrefix}/pump_temp",
                        tempC.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct: ct);
                }
                else
                {
                    // Sonde absente, déconnectée ou illisible : défaut aussi.
                    await ReportFaultAsync(true, "sonde absente ou illisible", ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "PumpTempService: erreur lecture DS18B20"); }

            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
    }

    // Log + publication MQTT uniquement sur un changement d'état (apparition
    // ou disparition du défaut) — pas à chaque cycle de 30s, pour ne pas
    // spammer les logs si le défaut persiste pendant des heures.
    private async Task ReportFaultAsync(bool fault, string? reason, CancellationToken ct)
    {
        if (_lastFaultPublished == fault) return;
        _lastFaultPublished = fault;

        if (fault)
            _logger.LogWarning("DS18B20 pompe: défaut détecté — {Reason}", reason);
        else
            _logger.LogInformation("DS18B20 pompe: défaut résolu, lecture redevenue normale");

        await _mqtt.PublishAsync(
            $"{_cfg.MqttPrefix}/pump_temp_fault",
            fault ? "ON" : "OFF",
            retain: true, ct: ct);
    }
}
