using System.Device.Gpio;
using Microsoft.Extensions.Logging;
using PiscineController.Config;

namespace PiscineController.Hardware;

public sealed class GpioButtons : IDisposable
{
    private readonly GpioController _gpio;
    private readonly ILogger<GpioButtons> _logger;

    public event Action? LcdDisplayPressed;
    public event Action? PrimePumpPressed;
    public event Action? PauseFilterPressed;
    public event Action? ResumeFilterPressed;

    public GpioButtons(PoolConfig cfg, ILogger<GpioButtons> logger)
    {
        _logger = logger;
        _gpio = new GpioController(PinNumberingScheme.Logical);

        Register(cfg.BtnLcdDisplay,   () => LcdDisplayPressed?.Invoke());
        Register(cfg.BtnPrimePump,    () => PrimePumpPressed?.Invoke());
        Register(cfg.BtnPauseFilter,  () => PauseFilterPressed?.Invoke());
        Register(cfg.BtnResumeFilter, () => ResumeFilterPressed?.Invoke());
    }

    private void Register(int pin, Action handler)
    {
        _gpio.OpenPin(pin, PinMode.InputPullUp);
        _gpio.RegisterCallbackForPinValueChangedEvent(
            pin, PinEventTypes.Falling,
            (_, args) =>
            {
                _logger.LogDebug("Bouton GPIO {Pin} appuyé", args.PinNumber);
                handler();
            });
    }

    public void Dispose()
    {
        _gpio.Dispose();
    }
}
