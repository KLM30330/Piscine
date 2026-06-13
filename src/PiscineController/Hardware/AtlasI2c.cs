using System.Device.I2c;
using Microsoft.Extensions.Logging;

namespace PiscineController.Hardware;

public abstract class AtlasEzoBase : IDisposable
{
    private readonly I2cDevice _device;
    protected readonly ILogger _logger;
    private bool _disposed;

    protected AtlasEzoBase(int busId, int address, ILogger logger)
    {
        _logger = logger;
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
    }

    protected string? SendCommand(string command, int delayMs = 900)
    {
        Span<byte> tx = stackalloc byte[command.Length];
        for (int i = 0; i < command.Length; i++) tx[i] = (byte)command[i];
        _device.Write(tx);

        Thread.Sleep(delayMs);

        Span<byte> rx = stackalloc byte[32];
        _device.Read(rx);

        if (rx[0] != 1)
        {
            _logger.LogWarning("Atlas EZO réponse code {Code} pour commande '{Cmd}'", rx[0], command);
            return null;
        }
        int len = rx[1..].IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(rx[1..(len < 0 ? 32 : len + 1)]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _device.Dispose();
    }
}

public sealed class EzoPh : AtlasEzoBase
{
    public EzoPh(int busId, int address, ILogger<EzoPh> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 900);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public void SetTemperatureCompensation(double tempC) =>
        SendCommand($"T,{tempC.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 300);

    public void CalibrateMid(double ph) => SendCommand($"Cal,mid,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
    public void CalibrateLow(double ph) => SendCommand($"Cal,low,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
    public void CalibrateHigh(double ph) => SendCommand($"Cal,high,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1400);
}

public sealed class EzoOrp : AtlasEzoBase
{
    public EzoOrp(int busId, int address, ILogger<EzoOrp> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 900);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public void SetTemperatureCompensation(double tempC) =>
        SendCommand($"T,{tempC.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 300);
}

public sealed class EzoRtd : AtlasEzoBase
{
    public EzoRtd(int busId, int address, ILogger<EzoRtd> logger)
        : base(busId, address, logger) { }

    public double? Read()
    {
        var raw = SendCommand("R", 600);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

public sealed class EzoPmp : AtlasEzoBase
{
    public EzoPmp(int busId, int address, ILogger<EzoPmp> logger)
        : base(busId, address, logger) { }

    public void Dose(double volumeMl)
    {
        int delayMs = (int)(volumeMl / 20.0 * 1000) + 2000;
        SendCommand($"D,{volumeMl.ToString(System.Globalization.CultureInfo.InvariantCulture)}", delayMs);
    }

    public void Stop() => SendCommand("X", 300);
}
