using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;

namespace PiscineController.Services;

// Gestion du mode secours : alimentation directe de la pompe via le relais
// PCF8574 pin RescuePumpRelayPin (sortie n°2), en COURT-CIRCUITANT le variateur.
//
// Règles de sécurité absolues :
//   1. Le variateur doit être ARRÊTÉ avant d'engager le relais secours —
//      alimenter simultanément le moteur via le variateur ET un relais direct
//      est impossible électriquement (deux sources d'alimentation en parallèle).
//   2. Dès que le mode secours est désactivé, le relais est immédiatement
//      ouvert — le retour en mode auto/forcé est ensuite géré par FiltrationService.
//   3. PumpRunning est maintenu à true pendant le mode secours pour que
//      l'électrolyseur (ElectrolyzerService) continue de fonctionner.
public sealed class RescueService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly Pcf8574 _relay;
    private readonly Wk600Drive _drive;
    private readonly MqttService _mqtt;
    private readonly ILogger<RescueService> _logger;

    private bool _relayEngaged;
    private bool? _lastPublished;   // null = jamais publié, force le premier envoi

    public RescueService(PoolConfig cfg, PoolState state,
        Pcf8574 relay, Wk600Drive drive, MqttService mqtt,
        ILogger<RescueService> logger)
    {
        _cfg = cfg; _state = state;
        _relay = relay; _drive = drive;
        _mqtt = mqtt; _logger = logger;

        // Réaction immédiate sur changement de mode : pas d'attente du cycle
        _state.FilterModeChanged += OnFilterModeChanged;
    }

    private void OnFilterModeChanged(FilterMode mode)
        => _ = ApplyAsync(mode);

    private async Task ApplyAsync(FilterMode mode)
    {
        bool shouldEngage = mode == FilterMode.Rescue;

        if (shouldEngage && !_relayEngaged)
        {
            // ── Activation du mode secours ────────────────────────────────
            // 1. Arrêter le variateur s'il tourne encore (FiltrationService
            //    le fait aussi via ShouldPumpRun()=false, mais on ne peut pas
            //    attendre son prochain cycle de 5s pour une opération de sécurité).
            if (_drive.IsRunning)
            {
                _logger.LogWarning("Mode secours : arrêt forcé du variateur avant engagement relais");
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _drive.RampStopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Mode secours : échec arrêt variateur — relais NON engagé par sécurité");
                    return;   // on n'engage PAS le relais si l'arrêt a échoué
                }
            }

            // 2. Engager le relais (actif-bas : false = relais fermé)
            _relay.SetPin(_cfg.RescuePumpRelayPin, false);
            _relayEngaged = true;
            _state.PumpRunning = true;   // électrolyseur doit continuer à fonctionner

            _logger.LogWarning("Mode secours ACTIVÉ — pompe alimentée directement via relais PCF pin {Pin}",
                _cfg.RescuePumpRelayPin);
        }
        else if (!shouldEngage && _relayEngaged)
        {
            // ── Désactivation du mode secours ─────────────────────────────
            _relay.SetPin(_cfg.RescuePumpRelayPin, true);   // relais ouvert
            _relayEngaged = false;
            // PumpRunning sera remis à jour par FiltrationService au prochain cycle
            _state.PumpRunning = false;

            _logger.LogInformation("Mode secours DÉSACTIVÉ — relais PCF pin {Pin} ouvert",
                _cfg.RescuePumpRelayPin);
        }

        await PublishStateAsync(CancellationToken.None);
    }

    private async Task PublishStateAsync(CancellationToken ct)
    {
        bool active = _relayEngaged;
        if (_lastPublished == active) return;
        _lastPublished = active;
        try
        {
            await _mqtt.PublishAsync(
                $"{_cfg.MqttPrefix}/rescue/state",
                active ? "ON" : "OFF",
                retain: true, ct: ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "RescueService: échec publication état"); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // S'assurer que le relais est bien ouvert au démarrage
        try { _relay.SetPin(_cfg.RescuePumpRelayPin, true); }
        catch (Exception ex) { _logger.LogError(ex, "RescueService: impossible d'initialiser le relais secours"); }

        await PublishStateAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            // Re-publication périodique (retain: true suffit normalement,
            // mais on republié au cas où le broker aurait été redémarré)
            await PublishStateAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }

        // Sécurité à l'arrêt du service : ouvrir le relais
        try { _relay.SetPin(_cfg.RescuePumpRelayPin, true); }
        catch (Exception ex) { _logger.LogError(ex, "RescueService: impossible d'ouvrir le relais secours à l'arrêt"); }
    }
}
