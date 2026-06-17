using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Hardware;
using PiscineController.Services;

namespace PiscineController.Services;

public sealed class ElectrolyzerService : BackgroundService
{
    private readonly PoolState _state;
    private readonly Pcf8574 _relay;
    private readonly MqttService _mqtt;
    private readonly string _mqttPrefix;
    private readonly ILogger<ElectrolyzerService> _logger;
    private bool _electrolyzerOn;
    private bool _lastPublished;   // évite les publications inutiles

    public ElectrolyzerService(PoolState state, Pcf8574 relay,
        MqttService mqtt, PiscineController.Config.PoolConfig cfg,
        ILogger<ElectrolyzerService> logger)
    {
        _state = state; _relay = relay;
        _mqtt = mqtt; _mqttPrefix = cfg.MqttPrefix; _logger = logger;
        _lastPublished = false;
    }

    public void SetElectrolyzer(bool on)
    {
        _electrolyzerOn = on;
        Apply();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Apply();
                await PublishStateAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { _logger.LogError(ex, "ElectrolyzerService: erreur"); }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        try { _relay.SetPin(0, false); } catch { }
    }

    private void Apply()
    {
        bool active = _electrolyzerOn && _state.PumpRunning;
        _relay.SetPin(0, active);
        _state.ElectrolyzerRunning = active;
    }

    private async Task PublishStateAsync(CancellationToken ct)
    {
        bool active = _state.ElectrolyzerRunning;
        if (active == _lastPublished) return;   // pas de changement, rien à publier
        _lastPublished = active;

        // État réel du relais (ON/OFF pour HA)
        await _mqtt.PublishAsync(
            $"{_mqttPrefix}/electrolyzer/state",
            active ? "ON" : "OFF",
            retain: true, ct: ct);

        // Consigne (ce que l'utilisateur a demandé, indépendant de la pompe)
        await _mqtt.PublishAsync(
            $"{_mqttPrefix}/electrolyzer/enabled",
            _electrolyzerOn ? "ON" : "OFF",
            retain: true, ct: ct);

        _logger.LogInformation("Électrolyseur: {State} (consigne={Enabled}, pompe={Pump})",
            active ? "ON" : "OFF", _electrolyzerOn, _state.PumpRunning);
    }
}
