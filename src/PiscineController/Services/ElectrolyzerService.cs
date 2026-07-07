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

    public ElectrolyzerService(PoolState state, Pcf8574 relay,
        MqttService mqtt, PoolConfig cfg,
        ILogger<ElectrolyzerService> logger)
    {
        _state = state; _relay = relay;
        _mqtt = mqtt; _cfg = cfg; _logger = logger;

        _state.ElectrolyzerEnabledChanged += enabled => { _ = ApplyAndPublishNowAsync(); };
        _state.PumpRunningChanged         += running => { _ = ApplyAndPublishNowAsync(); };
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
        // Verrou de sécurité 1 : pompe doit tourner
        // Verrou de sécurité 2 : plage horaire autorisée
        int h = DateTime.Now.Hour;
        bool inTimeWindow = h >= _cfg.ElectrolyzerStartH && h < _cfg.ElectrolyzerStopH;

        if (!inTimeWindow && _state.ElectrolyzerRunning)
            _logger.LogInformation(
                "Électrolyseur coupé (hors plage {Start}h–{Stop}h)",
                _cfg.ElectrolyzerStartH, _cfg.ElectrolyzerStopH);

        bool active = _state.ElectrolyzerEnabled && _state.PumpRunning && inTimeWindow;

        // Pcf8574 : relais actif-bas (cf. constructeur, état par défaut 0xFF =
        // "relais désactivés"). Pour ACTIVER le relais il faut donc mettre la
        // broche à LOW (false), et à HIGH (true) pour le désactiver — c'est
        // l'inverse de "active".
        _relay.SetPin(0, !active);
        _state.ElectrolyzerRunning = active;
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
