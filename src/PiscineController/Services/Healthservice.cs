using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController;

namespace PiscineController.Services;

// Publie l'état de santé des bus I2C et RS485 sur pool/health.
//
// Stratégie de publication :
//  • Au démarrage : effacement du retained précédent (payload vide, retain=true)
//    puis publication immédiate de l'état actuel SANS retain.
//  • Sur changement d'état (BusStatusChanged) : publication immédiate SANS retain.
//  • Toutes les 30s : rafraîchissement périodique SANS retain.
//
// Sans retain, HA n'affiche l'alerte que pendant que le service tourne et
// la publie activement. Si le service redémarre après correction du problème,
// l'ancien message d'erreur disparaît immédiatement au lieu de persister 24h.
public sealed class HealthService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly EquipmentHealth _health;
    private readonly MqttService _mqtt;
    private readonly ILogger<HealthService> _logger;

    // Signale qu'un changement d'état vient d'arriver — permet au délai
    // de 30s d'être interrompu pour publier immédiatement.
    private readonly SemaphoreSlim _triggerPublish = new(0, 1);

    public HealthService(PoolConfig cfg, EquipmentHealth health,
        MqttService mqtt, ILogger<HealthService> logger)
    {
        _cfg = cfg; _health = health; _mqtt = mqtt; _logger = logger;

        // Abonnement à l'événement EquipmentHealth → publication immédiate
        _health.BusStatusChanged += () =>
        {
            if (_triggerPublish.CurrentCount == 0)
                _triggerPublish.Release();
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 1. Effacer le retained précédent dès le démarrage — évite que HA
        //    continue d'afficher une ancienne erreur après correction du problème.
        try
        {
            await _mqtt.PublishAsync(
                $"{_cfg.MqttPrefix}/health", "",
                retain: true, ct: ct);
        }
        catch { /* broker pas encore connecté, pas grave */ }

        // 2. Publication initiale immédiate (sans retain)
        await PublishHealthAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Attendre soit 30s soit un BusStatusChanged
                await Task.WhenAny(
                    Task.Delay(TimeSpan.FromSeconds(30), ct),
                    _triggerPublish.WaitAsync(ct));
            }
            catch (OperationCanceledException) { break; }

            await PublishHealthAsync(ct);
        }
    }

    private async Task PublishHealthAsync(CancellationToken ct)
    {
        try
        {
            var payload = new HealthPayload(
                I2cProblem:    !_health.IsOk(EquipmentBus.I2C),
                Rs485Problem:  !_health.IsOk(EquipmentBus.Rs485),
                I2cLastError:   _health.LastErrorText(EquipmentBus.I2C),
                Rs485LastError: _health.LastErrorText(EquipmentBus.Rs485));

            // retain: false — HA n'affiche l'alerte que pendant qu'elle est active.
            // Si le service redémarre en état sain, l'alerte disparaît immédiatement.
            await _mqtt.PublishAsync(
                $"{_cfg.MqttPrefix}/health",
                JsonSerializer.Serialize(payload, AppJsonContext.Default.HealthPayload),
                retain: false, ct: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        { _logger.LogError(ex, "HealthService: erreur publication"); }
    }
}
