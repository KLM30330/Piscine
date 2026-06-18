using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiscineController.Config;
using PiscineController.Filtration;
using PiscineController.Hardware;

namespace PiscineController.Services;

public sealed class ButtonService : BackgroundService
{
    private readonly PoolConfig _cfg;
    private readonly PoolState _state;
    private readonly GpioButtons _buttons;
    private readonly FiltrationManager _filtration;
    private readonly PumpPrimingService _priming;
    private readonly DisplayService _display;
    private readonly ILogger<ButtonService> _logger;

    public ButtonService(PoolConfig cfg, PoolState state,
        GpioButtons buttons, FiltrationManager filtration,
        PumpPrimingService priming, DisplayService display, ILogger<ButtonService> logger)
    {
        _cfg = cfg; _state = state; _buttons = buttons;
        _filtration = filtration; _priming = priming; _display = display; _logger = logger;
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
        _logger.LogDebug("Bouton amorçage pompe");
        _priming.TryPrime(_cfg.PrimeVolumeMl);
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
