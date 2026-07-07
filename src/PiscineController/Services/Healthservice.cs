using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController;

namespace PiscineController.Services;

// Publie périodiquement l'état de santé des 2 bus matériels (I2C,
// RS485) sur MQTT, à partir des rapports remontés par chaque driver matériel
// via EquipmentHealth. Les entités Home Assistant correspondantes sont
// enregistrées dans MqttService.PublishHaDiscovery.
public sealed class HealthService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly EquipmentHealth _health;
    private readonly MqttService _mqtt;
    private readonly ILogger<HealthService> _logger;

    public HealthService(PoolConfig cfg, EquipmentHealth health,
        MqttService mqtt, ILogger<HealthService> logger)
    {
        _cfg = cfg; _health = health; _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = new HealthPayload(
                    I2cProblem:    !_health.IsOk(EquipmentBus.I2C),
                    Rs485Problem:  !_health.IsOk(EquipmentBus.Rs485),
                    I2cLastError:   _health.LastErrorText(EquipmentBus.I2C),
                    Rs485LastError: _health.LastErrorText(EquipmentBus.Rs485));

                await _mqtt.PublishAsync(
                    $"{_cfg.MqttPrefix}/health",
                    JsonSerializer.Serialize(payload, AppJsonContext.Default.HealthPayload),
                    ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "HealthService: erreur publication"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
