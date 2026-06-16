using Microsoft.Extensions.Logging;

namespace PiscineController.Hardware;

public sealed class Ds18b20
{
    private readonly ILogger<Ds18b20> _logger;
    private string? _devicePath;

    public bool IsAvailable => _devicePath != null;

    public Ds18b20(ILogger<Ds18b20> logger, string? sensorId = null)
    {
        _logger = logger;
        _devicePath = sensorId != null
            ? ResolveById(sensorId)
            : FindDevice();

        if (_devicePath == null)
            _logger.LogWarning("DS18B20: aucune sonde 1-Wire disponible, " +
                               "les lectures de température seront ignorées");
        else
            _logger.LogInformation("DS18B20: sonde active → {Path}", _devicePath);
    }

    // Tente de résoudre l'ID fourni ; si le fichier n'existe pas, repli sur auto-détection
    private string? ResolveById(string sensorId)
    {
        string path = $"/sys/bus/w1/devices/{sensorId}/w1_slave";
        if (File.Exists(path)) return path;

        _logger.LogWarning(
            "DS18B20: sonde '{Id}' introuvable dans /sys/bus/w1/devices, " +
            "tentative d'auto-détection...", sensorId);

        return FindDevice();
    }

    private string? FindDevice()
    {
        const string base_ = "/sys/bus/w1/devices";
        if (!Directory.Exists(base_)) return null;

        var dir = Directory.GetDirectories(base_, "28-*").FirstOrDefault();
        if (dir == null) return null;

        string path = Path.Combine(dir, "w1_slave");
        if (!File.Exists(path)) return null;

        _logger.LogInformation("DS18B20: capteur auto-détecté → {Dir}", dir);
        return path;
    }

    public double? Read()
    {
        if (_devicePath == null) return null;   // sonde absente → silencieux

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
        catch (IOException ex)
        {
            // La sonde était là au démarrage mais a disparu (débranchée, etc.)
            _logger.LogWarning(ex, "DS18B20: sonde déconnectée ({Path}), " +
                                   "nouvelle tentative au prochain cycle", _devicePath);
            _devicePath = FindDevice();   // essaie de retrouver une sonde
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DS18B20: erreur lecture {Path}", _devicePath);
            return null;
        }
    }
}
