using System.Device.I2c;
using Microsoft.Extensions.Logging;
using PiscineController;

namespace PiscineController.Hardware;

public sealed class Pcf8574 : IDisposable
{
    private readonly I2cDevice _device;
    private readonly EquipmentHealth _health;
    private readonly ILogger<Pcf8574> _logger;
    private byte _state;
    // Verrou sur l'ensemble read-modify-write de _state + Write() pour éviter
    // la race condition entre ElectrolyzerService (pin 0) et RescueService
    // (pin 1) qui tournent sur des threads différents. Sans ce verrou, une
    // écriture concurrente peut écraser la modification de l'autre et laisser
    // un relais coincé dans le mauvais état malgré un _state interne "correct".
    private readonly object _lock = new();

    public Pcf8574(int busId, int address, EquipmentHealth health, ILogger<Pcf8574> logger)
    {
        _health = health;
        _logger = logger;
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
        lock (_lock)
        {
            _state = 0xFF;
            Write();
        }
    }

    public void SetPin(int pin, bool value)
    {
        lock (_lock)
        {
            if (value)
                _state |= (byte)(1 << pin);
            else
                _state &= (byte)~(1 << pin);
            Write();
        }
    }

    public bool GetPin(int pin) { lock (_lock) return (_state & (1 << pin)) != 0; }

    public void SetAll(bool value)
    {
        lock (_lock)
        {
            _state = value ? (byte)0xFF : (byte)0x00;
            Write();
        }
    }

    // Appelé uniquement depuis l'intérieur du lock
    private void Write()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _device.WriteByte(_state);
                _health.ReportSuccess(EquipmentBus.I2C, "PCF8574");
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "PCF8574: écriture I2C en échec après 3 tentatives (state=0x{State:X2})", _state);
                _health.ReportFailure(EquipmentBus.I2C, "PCF8574", ex);
            }
        }
    }

    public void Dispose() { lock (_lock) _device.Dispose(); }
}
