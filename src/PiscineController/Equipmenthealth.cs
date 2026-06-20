using Microsoft.Extensions.Logging;

namespace PiscineController;

public enum EquipmentBus { I2C, OneWire, Rs485 }

// Suivi centralisé de l'état de communication par bus matériel. Chaque
// driver matériel (Atlas EZO, PCF8574, LCD1602 → I2C ; DS18B20 → one-wire ;
// WK600-D → RS485/Modbus) reporte ici ses succès/échecs de communication.
// Le log n'a lieu qu'à la TRANSITION d'état (santé → panne ou panne → santé),
// pas à chaque cycle, pour ne pas noyer les journaux si un équipement reste
// en panne pendant des heures.
public sealed class EquipmentHealth
{
    private sealed class BusState
    {
        public bool Ok = true;
        public string? LastError;
        public DateTimeOffset? LastErrorTime;
        public string? LastDevice;
    }

    private readonly Dictionary<EquipmentBus, BusState> _state = new()
    {
        [EquipmentBus.I2C] = new(),
        [EquipmentBus.OneWire] = new(),
        [EquipmentBus.Rs485] = new(),
    };

    private readonly object _lock = new();
    private readonly ILogger<EquipmentHealth> _logger;

    public EquipmentHealth(ILogger<EquipmentHealth> logger) => _logger = logger;

    // device = nom de l'équipement concerné (ex. "EZO-pH", "PCF8574",
    // "LCD1602", "DS18B20", "WK600-D"), utile pour savoir LEQUEL est en
    // cause quand plusieurs équipements partagent le même bus (I2C).
    public void ReportFailure(EquipmentBus bus, string device, Exception ex)
    {
        bool wasOk;
        lock (_lock)
        {
            var s = _state[bus];
            wasOk = s.Ok;
            s.Ok = false;
            s.LastDevice = device;
            s.LastError = ex.Message;
            s.LastErrorTime = DateTimeOffset.UtcNow;
        }
        if (wasOk)
            _logger.LogError(ex, "Équipement {Bus}/{Device}: communication en échec", bus, device);
    }

    public void ReportSuccess(EquipmentBus bus, string device)
    {
        bool wasOk;
        lock (_lock)
        {
            var s = _state[bus];
            wasOk = s.Ok;
            s.Ok = true;
        }
        if (!wasOk)
            _logger.LogInformation("Équipement {Bus}/{Device}: communication rétablie", bus, device);
    }

    public bool IsOk(EquipmentBus bus) { lock (_lock) return _state[bus].Ok; }

    public string LastErrorText(EquipmentBus bus)
    {
        lock (_lock)
        {
            var s = _state[bus];
            return s.LastDevice != null ? $"{s.LastDevice}: {s.LastError}" : "Aucune erreur";
        }
    }
}
