using Microsoft.Extensions.Logging;

namespace PiscineController.Hardware;

public sealed class Ds18b20
{
    private readonly ILogger<Ds18b20> _logger;
    private string? _devicePath;

    public Ds18b20(ILogger<Ds18b20> logger, string? sensorId = null)
    {
        _logger = logger;
        _devicePath = sensorId != null
            ? $"/sys/bus/w1/devices/{sensorId}/w1_slave"
            : FindDevice();
    }

    private string? FindDevice()
    {
        const string base_ = "/sys/bus/w1/devices";
        if (!Directory.Exists(base_)) return null;
        var dir = Directory.GetDirectories(base_, "28-*").FirstOrDefault();
        if (dir == null) { _logger.LogWarning("DS18B20: aucun capteur 1-Wire trouvé"); return null; }
        _logger.LogInformation("DS18B20: capteur détecté {Dir}", dir);
        return Path.Combine(dir, "w1_slave");
    }

    public double? Read()
    {
        if (_devicePath == null) return null;
        try
        {
            string content = File.ReadAllText(_devicePath);
            if (!content.Contains("YES")) return null;
            int idx = content.IndexOf("t=", StringComparison.Ordinal);
            if (idx < 0) return null;
            string raw = content[(idx + 2)..].Trim();
            if (int.TryParse(raw, out int milliC))
                return milliC / 1000.0;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DS18B20: erreur lecture {Path}", _devicePath);
            return null;
        }
    }
}
