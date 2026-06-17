using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;
using PiscineController.Ph;

namespace PiscineController.Services;

public sealed class ButtonService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly GpioButtons _buttons;
    private readonly FiltrationManager _filtration;
    private readonly EzoPmp _pmp;
    private readonly DisplayService _display;
    private readonly ILogger<ButtonService> _logger;

    // Empêche deux amorçages concurrents si le bouton est pressé plusieurs
    // fois pendant le chrono (éviterait un sur-dosage cumulé).
    private int _priming;

    public ButtonService(PoolConfig cfg, PoolState state,
        GpioButtons buttons, FiltrationManager filtration,
        EzoPmp pmp, DisplayService display, ILogger<ButtonService> logger)
    {
        _cfg = cfg; _state = state; _buttons = buttons;
        _filtration = filtration; _pmp = pmp; _display = display; _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        _buttons.LcdDisplayPressed   += OnLcdDisplay;
        _buttons.PrimePumpPressed    += OnPrimePump;
        _buttons.PauseFilterPressed  += OnPauseFilter;
        _buttons.ResumeFilterPressed += OnResumeFilter;
        return Task.Delay(Timeout.Infinite, ct);
    }

    private void OnLcdDisplay()
    {
        _logger.LogDebug("Bouton LCD Display");
        _display.Show(
            $"pH{_state.PhValue:F2} ORP{_state.OrpMv:F0}",
            $"T{_state.WaterTempC:F1}C {_state.PumpFreqHz:F0}Hz",
            _cfg.LcdDisplayDuration * 1000);
    }

    private void OnPrimePump()
    {
        if (Interlocked.CompareExchange(ref _priming, 1, 0) != 0)
        {
            _logger.LogDebug("Amorçage déjà en cours, appui ignoré");
            return;
        }
        _logger.LogInformation("Amorçage pompe doseuse {Vol} mL", _cfg.PrimeVolumeMl);
        _ = RunPrimePumpAsync(_cfg.PrimeVolumeMl);
    }

    // Affiche un chrono pendant toute la durée réelle du dosage (au lieu
    // d'un texte statique de 3s), conformément au README : "affichage LCD
    // 'amorçage ph- ' + chrono".
    private async Task RunPrimePumpAsync(double volumeMl)
    {
        try
        {
            int totalMs = EzoPmp.EstimateDoseMs(volumeMl);
            int totalSeconds = Math.Max(1, (int)Math.Ceiling(totalMs / 1000.0));

            var doseTask = Task.Run(() => _pmp.Dose(volumeMl));

            for (int elapsed = 1; elapsed <= totalSeconds; elapsed++)
            {
                _display.Show("Amorcage ph-", $"{elapsed}/{totalSeconds}s...", 1100);
                await Task.Delay(1000);
            }

            await doseTask;
            _display.Show("Amorcage ph-", "Termine", 2000);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Amorçage pompe: échec du dosage {Vol} mL", volumeMl);
            _display.Show("Amorcage ph-", "ERREUR", 2000);
        }
        finally
        {
            Interlocked.Exchange(ref _priming, 0);
        }
    }

    private void OnPauseFilter()
    {
        _logger.LogInformation("Pause filtration (bouton)");
        _filtration.SetMode("pause");
        _state.FilterMode = FilterMode.Pause;
        // 30s comme annoncé dans le README (et non 3s) : même durée que le
        // bouton 1 (LcdDisplayDuration), désormais cohérente avec la doc.
        _display.Show("Filtration", "EN PAUSE", _cfg.LcdDisplayDuration * 1000);
    }

    private void OnResumeFilter()
    {
        _logger.LogInformation("Reprise filtration auto (bouton)");
        _filtration.ResumeAuto();
        _state.FilterMode = FilterMode.Auto;
        _display.Show("Filtration", "AUTO", _cfg.LcdDisplayDuration * 1000);
    }
}
