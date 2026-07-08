using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Hardware;
using PiscineController.Services;

namespace PiscineController.Services;

public sealed class ElectrolyzerService : BackgroundService
{
    private readonly PoolState _state;
    private readonly Pcf8574 _relay;
    private readonly MqttService _mqtt;
    private readonly PoolConfig _cfg;
    private readonly ILogger<ElectrolyzerService> _logger;
    private bool? _lastActive;
    private bool? _lastEnabled;
    // Mémoire d'état ORP : true = électrolyseur bloqué par ORP élevé.
    // Reste true jusqu'à ce que l'ORP repasse sous (OrpMax - Hysteresis),
    // évitant ainsi les cycles rapides si l'ORP oscille au seuil.
    private bool _orpBlocked;

    public ElectrolyzerService(PoolState state, Pcf8574 relay,
        MqttService mqtt, PoolConfig cfg,
        ILogger<ElectrolyzerService> logger)
    {
        _state = state; _relay = relay;
        _mqtt = mqtt; _cfg = cfg; _logger = logger;

        _state.ElectrolyzerEnabledChanged += enabled => { _ = ApplyAndPublishNowAsync(); };
        _state.PumpRunningChanged         += running => { _ = ApplyAndPublishNowAsync(); };
        // Réévaluation immédiate à chaque variation ORP significative (≥5 mV)
        // pour couper/réactiver l'électrolyseur sans attendre le cycle de 5s.
        _state.OrpMvChanged               += orp    => { _ = ApplyAndPublishNowAsync(); };
    }

    private async Task ApplyAndPublishNowAsync()
    {
        try
        {
            Apply();
            await PublishStateAsync(CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "Électrolyseur: erreur publication immédiate"); }
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

            // Réévaluation toutes les 5s ET à chaque changement d'état
            // (via événements PumpRunningChanged/ElectrolyzerEnabledChanged).
            // On calcule aussi le délai jusqu'à la prochaine transition
            // horaire (StartH ou StopH) pour couper/activer exactement
            // à l'heure sans attendre un cycle complet.
            int h = DateTime.Now.Hour;
            int minToNext = h < _cfg.ElectrolyzerStartH
                ? (_cfg.ElectrolyzerStartH - h) * 60 - DateTime.Now.Minute
                : h < _cfg.ElectrolyzerStopH
                    ? (_cfg.ElectrolyzerStopH - h) * 60 - DateTime.Now.Minute
                    : (24 - h + _cfg.ElectrolyzerStartH) * 60 - DateTime.Now.Minute;
            int delaySec = Math.Min(5, Math.Max(1, minToNext * 60));

            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
        }
        try { _relay.SetPin(0, true); } catch { }
    }

    private void Apply()
    {
        int h = DateTime.Now.Hour;
        bool inTimeWindow = h >= _cfg.ElectrolyzerStartH && h < _cfg.ElectrolyzerStopH;

        // ── Hystérésis ORP ────────────────────────────────────────────────────
        // Coupe à OrpMax, ne réactive qu'à (OrpMax - Hysteresis).
        // _orpBlocked persiste entre les appels pour mémoriser l'état.
        double orp = _state.OrpMv;
        if (!_orpBlocked && orp >= _cfg.ElectrolyzerOrpMax)
            _orpBlocked = true;
        else if (_orpBlocked && orp < _cfg.ElectrolyzerOrpMax - _cfg.ElectrolyzerOrpHysteresis)
            _orpBlocked = false;

        bool orpOk = !_orpBlocked;

        bool active = _state.ElectrolyzerEnabled
                   && _state.PumpRunning
                   && inTimeWindow
                   && orpOk;

        if (active != _state.ElectrolyzerRunning)
        {
            if (!active)
            {
                string reason =
                    !_state.PumpRunning         ? "pompe arrêtée" :
                    !_state.ElectrolyzerEnabled ? "désactivé manuellement" :
                    !inTimeWindow               ? $"hors plage {_cfg.ElectrolyzerStartH}h–{_cfg.ElectrolyzerStopH}h" :
                    _orpBlocked                 ? $"ORP élevé ({orp:F0} mV ≥ {_cfg.ElectrolyzerOrpMax} mV, réactivation < {_cfg.ElectrolyzerOrpMax - _cfg.ElectrolyzerOrpHysteresis} mV)" :
                                                  "inconnu";
                _logger.LogInformation("Électrolyseur OFF — {Reason}", reason);
            }
            else
            {
                _logger.LogInformation(
                    "Électrolyseur ON (ORP={Orp:F0} mV, pompe={Pump}, plage={Ok})",
                    orp, _state.PumpRunning, inTimeWindow);
            }
        }

        _relay.SetPin(0, !active);
        _state.ElectrolyzerRunning = active;

        // Log Debug à chaque cycle pour faciliter le diagnostic — visible
        // uniquement si le niveau minimum est baissé à Debug dans la config.
        _logger.LogDebug(
            "Électrolyseur Apply: active={A} enabled={E} pump={P} window={W} orpOk={O} orp={Orp:F0}",
            active, _state.ElectrolyzerEnabled, _state.PumpRunning, inTimeWindow, orpOk, orp);
    }

    private async Task PublishStateAsync(CancellationToken ct)
    {
        bool active = _state.ElectrolyzerRunning;
        bool enabled = _state.ElectrolyzerEnabled;

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
            $"{_cfg.MqttPrefix}/electrolyzer/state",
            active ? "ON" : "OFF",
            retain: true, ct: ct);

        // Consigne (ce que l'utilisateur a demandé, indépendant de la pompe)
        await _mqtt.PublishAsync(
            $"{_cfg.MqttPrefix}/electrolyzer/enabled",
            enabled ? "ON" : "OFF",
            retain: true, ct: ct);

        _logger.LogInformation("Électrolyseur: {State} (consigne={Enabled}, pompe={Pump})",
            active ? "ON" : "OFF", enabled, _state.PumpRunning);
    }
}
