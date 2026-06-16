using System.Device.I2c;

namespace PiscineController.Hardware;

public sealed class Pcf8574 : IDisposable
{
    private readonly I2cDevice _device;
    private byte _state;

    public Pcf8574(int busId, int address)
    {
        _device = I2cDevice.Create(
            new I2cConnectionSettings(busId, address));

        // Toutes les sorties à 1 (relais désactivés si actifs-bas)
        _state = 0xFF;
        Write();
    }

    public void SetPin(int pin, bool value)
    {
        if (value)
            _state |= (byte)(1 << pin);
        else
            _state &= (byte)~(1 << pin);

        Write();
    }

    public bool GetPin(int pin) => (_state & (1 << pin)) != 0;

    public void SetAll(bool value)
    {
        _state = value ? (byte)0xFF : (byte)0x00;
        Write();
    }

    private void Write()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _device.WriteByte(_state);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _device.Dispose();
}
