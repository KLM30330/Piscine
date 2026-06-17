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
        // Arrêt du service : désactiver le relais (HIGH=true en actif-bas),
        // pour ne jamais laisser l'électrolyseur engagé après extinction.
        try { _relay.SetPin(0, true); } catch { }
    }

    private void Apply()
    {
        // Verrou de sécurité : l'électrolyseur ne peut JAMAIS être actif si la
        // pompe ne tourne pas, quelle que soit la consigne utilisateur — vrai
        // dans tous les modes de filtration (Auto/Forced/Boost/Pause/Stop),
        // puisque _state.PumpRunning reflète l'état réel matériel du variateur.
        bool active = _electrolyzerOn && _state.PumpRunning;

        // Pcf8574 : relais actif-bas (cf. constructeur, état par défaut 0xFF =
        // "relais désactivés"). Pour ACTIVER le relais il faut donc mettre la
        // broche à LOW (false), et à HIGH (true) pour le désactiver — c'est
        // l'inverse de "active". L'ancien code faisait SetPin(0, active), ce
        // qui activait le relais quand on le croyait inactif (et inversement) :
        // avec _electrolyzerOn resté à false par défaut, le relais était donc
        // en réalité activé en PERMANENCE, indépendamment de l'état de la pompe.
        _relay.SetPin(0, !active);
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
