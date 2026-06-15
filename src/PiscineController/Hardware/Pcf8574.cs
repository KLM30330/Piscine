using System.Device.I2c;

namespace PiscineController.Hardware;

public sealed class Pcf8574 : IDisposable
{
    private readonly I2cDevice _device;
    private byte _state;

    public Pcf8574(int busId, int address)
    {
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
        _state = 0x00;
    }

    public void SetPin(int pin, bool value)
    {
        if (value) _state |=  (byte)(1 << pin);
        else       _state &= (byte)~(1 << pin);
        Write();
    }

    public bool GetPin(int pin) => (_state & (1 << pin)) != 0;

    private void Write()
    {
        // Retry on EREMOTEIO: shared I2C bus contention with concurrent Atlas EZO operations
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { _device.WriteByte(_state); return; }
            catch (IOException) when (attempt < 2) { Thread.Sleep(50); }
        }
    }

    public void Dispose() => _device.Dispose();
}
