using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;
using System.Text.Json;

namespace PiscineController.Services;

public sealed class DriveService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Wk600Drive _drive;
    private readonly MqttService _mqtt;
    private readonly ILogger<DriveService> _logger;

    public DriveService(PoolConfig cfg, PoolState state,
        Wk600Drive drive, MqttService mqtt, ILogger<DriveService> logger)
    {
        _cfg = cfg; _state = state; _drive = drive; _mqtt = mqtt; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = _drive.ReadStatus();
                _state.PumpRunning = snap.IsRunning;
                _state.PumpFreqHz = snap.OutFreqHz;

                if (snap.IsFault)
                    _logger.LogWarning("WK600-D défaut: [{Code}] {Label}", snap.FaultCode, snap.FaultLabel);

                var payload = new DriveStatusPayload(
                    snap.OutFreqHz, snap.OutCurrentA, snap.OutVoltageV,
                    snap.OutPowerKw, snap.RunTimeH,
                    snap.FaultCode, snap.FaultLabel,
                    snap.IsRunning, snap.IsFault, snap.AtSetpoint, snap.SetpointHz);

                await _mqtt.PublishAsync(
                    $"{_cfg.MqttPrefix}/drive",
                    JsonSerializer.Serialize(payload, AppJsonContext.Default.DriveStatusPayload),
                    ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "DriveService: erreur lecture variateur"); }

            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
    }
}
