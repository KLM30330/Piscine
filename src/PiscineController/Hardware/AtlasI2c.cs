using System.Device.I2c;
using Microsoft.Extensions.Logging;
using PiscineController;

namespace PiscineController.Hardware;

public abstract class AtlasEzoBase : IDisposable
{
    private readonly I2cDevice _device;
    protected readonly ILogger _logger;
    private readonly EquipmentHealth _health;
    private readonly string _deviceName;
    private bool _disposed;

    // Délai minimum entre la fin d'une commande et le début de la suivante
    // sur le bus I2C partagé. Les sondes EZO maintiennent SDA bas pendant
    // le traitement — un délai inter-commande de 100 ms évite les collisions
    // quand plusieurs sondes sont interrogées séquentiellement.
    private const int InterCommandDelayMs = 100;

    protected AtlasEzoBase(int busId, int address, ILogger logger, EquipmentHealth health, string deviceName)
    {
        _logger = logger;
        _health = health;
        _deviceName = deviceName;
        _device = I2cDevice.Create(new I2cConnectionSettings(busId, address));
    }

    // delayMs : temps d'attente entre l'envoi de la commande et la lecture
    // de la réponse. Atlas Scientific recommande 900 ms pour une mesure ORP/pH
    // et 1 500 ms dans les environnements bruités. On utilise 1 500 ms par
    // défaut pour garantir que la sonde a fini de traiter avant la lecture,
    // ce qui évite le code réponse 63 (sonde occupée) observé à 900 ms.
    protected string? SendCommand(string command, int delayMs = 1500)
    {
        try
        {
            Span<byte> tx = stackalloc byte[command.Length];
            for (int i = 0; i < command.Length; i++) tx[i] = (byte)command[i];
            _device.Write(tx);

            Thread.Sleep(delayMs);

            Span<byte> rx = stackalloc byte[32];
            _device.Read(rx);

            // Délai inter-commande : laisser le bus se stabiliser avant que
            // la prochaine sonde prenne la main.
            Thread.Sleep(InterCommandDelayMs);

            if (rx[0] != 1)
            {
                _logger.LogWarning("Atlas EZO réponse code {Code} pour commande '{Cmd}' ({Device})",
                    rx[0], command, _deviceName);
                return null;
            }
            int len = rx[1..].IndexOf((byte)0);
            string result = System.Text.Encoding.ASCII.GetString(rx[1..(len < 0 ? 32 : len + 1)]);
            _health.ReportSuccess(EquipmentBus.I2C, _deviceName);
            return result;
        }
        catch (Exception ex)
        {
            _health.ReportFailure(EquipmentBus.I2C, _deviceName, ex);
            return null;
        }
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
    public EzoPh(int busId, int address, ILogger<EzoPh> logger, EquipmentHealth health)
        : base(busId, address, logger, health, "EZO-pH") { }

    public double? Read()
    {
        var raw = SendCommand("R", 1500);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    public void SetTemperatureCompensation(double tempC) =>
        SendCommand($"T,{tempC.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 300);

    public void CalibrateMid(double ph) => SendCommand($"Cal,mid,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1600);
    public void CalibrateLow(double ph) => SendCommand($"Cal,low,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1600);
    public void CalibrateHigh(double ph) => SendCommand($"Cal,high,{ph.ToString(System.Globalization.CultureInfo.InvariantCulture)}", 1600);
}

public sealed class EzoOrp : AtlasEzoBase
{
    public EzoOrp(int busId, int address, ILogger<EzoOrp> logger, EquipmentHealth health)
        : base(busId, address, logger, health, "EZO-ORP") { }

    public double? Read()
    {
        // 1 500 ms au lieu de 900 ms : l'EZO-ORP nécessite plus de temps
        // que sa datasheet ne l'indique en environnement bruité (variateur WK600-D).
        // Le code réponse 63 (sonde occupée) disparaît avec ce délai augmenté.
        var raw = SendCommand("R", 1500);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

public sealed class EzoRtd : AtlasEzoBase
{
    public EzoRtd(int busId, int address, ILogger<EzoRtd> logger, EquipmentHealth health)
        : base(busId, address, logger, health, "EZO-RTD") { }

    public double? Read()
    {
        // RTD : temps de conversion plus court (600 ms suffisent),
        // on augmente à 900 ms pour la même marge de sécurité.
        var raw = SendCommand("R", 900);
        return raw != null && double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}

public sealed class EzoPmp : AtlasEzoBase
{
    public EzoPmp(int busId, int address, ILogger<EzoPmp> logger, EquipmentHealth health)
        : base(busId, address, logger, health, "EZO-PMP") { }

    public static int EstimateDoseMs(double volumeMl) =>
        (int)(volumeMl / 20.0 * 1000) + 2000;

    public void Dose(double volumeMl)
    {
        int delayMs = EstimateDoseMs(volumeMl);
        SendCommand($"D,{volumeMl.ToString(System.Globalization.CultureInfo.InvariantCulture)}", delayMs);
    }

    public void Stop() => SendCommand("X", 300);
}
