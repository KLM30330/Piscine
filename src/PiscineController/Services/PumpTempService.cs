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
                    if (tempC >= _cfg.PumpTempCriticalC)
                        _logger.LogCritical("Température pompe CRITIQUE: {T}°C — arrêt requis", tempC);
                    else if (tempC >= _cfg.PumpTempAlertC)
                        _logger.LogWarning("Température pompe élevée: {T}°C", tempC);

                    await _mqtt.PublishAsync(
                        $"{_cfg.MqttPrefix}/pump_temp",
                        tempC.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct: ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "PumpTempService: erreur lecture DS18B20"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
