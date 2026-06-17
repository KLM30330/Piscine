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
    // Pas de commande d'activation séparée pour l'électrolyseur : son
    // fonctionnement dépend uniquement de l'engagement du relais PCF8574,
    // lui-même verrouillé sur l'état réel de la pompe (cf. Apply()). On
    // active donc la consigne par défaut pour que le relais s'engage dès
    // que la pompe tourne. SetElectrolyzer() reste disponible si vous
    // voulez ajouter plus tard une coupure manuelle (ex. hivernage).
    private bool _electrolyzerOn = true;
    private bool? _lastActive;   // null = jamais publié, force le premier envoi
    private bool? _lastEnabled;  // idem, suivi indépendant de "enabled"

    public ElectrolyzerService(PoolState state, Pcf8574 relay,
        MqttService mqtt, PiscineController.Config.PoolConfig cfg,
        ILogger<ElectrolyzerService> logger)
    {
        _state = state; _relay = relay;
        _mqtt = mqtt; _mqttPrefix = cfg.MqttPrefix; _logger = logger;
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
        bool enabled = _electrolyzerOn;

        // _lastActive/_lastEnabled valent null avant le tout premier appel :
        // la comparaison bool? vs bool échoue alors systématiquement (null
        // n'égale jamais false ni true), donc le premier publish a TOUJOURS
        // lieu — contrairement à l'ancien bool initialisé à false, qui
        // coïncidait avec le premier état "false" et bloquait silencieusement
        // toute publication dès le départ.
        if (_lastActive == active && _lastEnabled == enabled) return;
        _lastActive = active;
        _lastEnabled = enabled;

        // État réel du relais (ON/OFF pour HA)
        await _mqtt.PublishAsync(
            $"{_mqttPrefix}/electrolyzer/state",
            active ? "ON" : "OFF",
            retain: true, ct: ct);

        // Consigne (ce que l'utilisateur a demandé, indépendant de la pompe)
        await _mqtt.PublishAsync(
            $"{_mqttPrefix}/electrolyzer/enabled",
            enabled ? "ON" : "OFF",
            retain: true, ct: ct);

        _logger.LogInformation("Électrolyseur: {State} (consigne={Enabled}, pompe={Pump})",
            active ? "ON" : "OFF", enabled, _state.PumpRunning);
    }
}
