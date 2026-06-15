using System.Device.Gpio;
using Microsoft.Extensions.Logging;
using PiscineController.Config;

namespace PiscineController.Hardware;

public sealed class GpioButtons : IDisposable
{
    private readonly GpioController _gpio;
    private readonly ILogger<GpioButtons> _logger;
    private readonly Dictionary<int, long> _lastTrigger = new();
    private const long DebounceTicks = 200 * TimeSpan.TicksPerMillisecond;

    public event Action? LcdDisplayPressed;
    public event Action? PrimePumpPressed;
    public event Action? PauseFilterPressed;
    public event Action? ResumeFilterPressed;

    public GpioButtons(PoolConfig cfg, ILogger<GpioButtons> logger)
    {
        _logger = logger;
        _gpio = new GpioController();

        Register(cfg.BtnLcdDisplay,   () => LcdDisplayPressed?.Invoke());
        Register(cfg.BtnPrimePump,    () => PrimePumpPressed?.Invoke());
        Register(cfg.BtnPauseFilter,  () => PauseFilterPressed?.Invoke());
        Register(cfg.BtnResumeFilter, () => ResumeFilterPressed?.Invoke());
    }

    private void Register(int pin, Action handler)
    {
        _lastTrigger[pin] = 0;
        _gpio.OpenPin(pin, PinMode.InputPullUp);
        _gpio.RegisterCallbackForPinValueChangedEvent(
            pin, PinEventTypes.Falling,
            (_, args) =>
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - _lastTrigger[args.PinNumber] < DebounceTicks) return;
                Thread.Sleep(10);
                if (_gpio.Read(args.PinNumber) != PinValue.Low) return;
                _lastTrigger[args.PinNumber] = now;
                _logger.LogDebug("Bouton GPIO {Pin} appuyé", args.PinNumber);
                handler();
            });
    }

    public void Dispose()
    {
        _gpio.Dispose();
    }
}
