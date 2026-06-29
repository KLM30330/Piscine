using Microsoft.Extensions.Logging;

namespace PiscineController;

public enum EquipmentBus { I2C, OneWire, Rs485 }

// Suivi centralisé de l'état de communication par bus matériel.
//
// Comportement : un bus n'est déclaré en PROBLÈME que si FailureThreshold
// défauts CONSÉCUTIFS sont enregistrés — une erreur isolée (transitoire au
// démarrage, glitch I2C ponctuel, timeout Modbus unique) ne génère plus
// d'alerte. Le compteur est remis à zéro dès le premier succès.
//
// Analogie avec Wk600Drive.MaxConsecutiveFailuresBeforeReset : même logique,
// mais appliquée aux alertes MQTT/logs plutôt qu'au reset du port série.
public sealed class EquipmentHealth
{
    private sealed class BusState
    {
        public bool Ok = true;
        public int ConsecutiveFailures;
        public string? LastError;
        public DateTimeOffset? LastErrorTime;
        public string? LastDevice;
    }

    private readonly Dictionary<EquipmentBus, BusState> _state = new()
    {
        [EquipmentBus.I2C]     = new(),
        [EquipmentBus.OneWire] = new(),
        [EquipmentBus.Rs485]   = new(),
    };

    private readonly object _lock = new();
    private readonly ILogger<EquipmentHealth> _logger;
    private readonly int _failureThreshold;

    // failureThreshold : nombre de défauts consécutifs requis avant de
    // déclarer un bus en panne. Valeur par défaut = 3 :
    //   • I2C RTD prend 600ms, ORP/pH 900ms → un Pi chargé peut rater
    //     une fenêtre de réponse sans que le bus soit réellement mort.
    //   • One-wire : le module noyau n'est pas toujours initialisé avant
    //     le premier cycle du service.
    //   • RS485 : le variateur peut ne pas répondre au démarrage.
    // Avec 3, un vrai problème matériel déclenche l'alerte en ~15s
    // (3 cycles × 5s), ce qui reste très réactif.
    public EquipmentHealth(ILogger<EquipmentHealth> logger, int failureThreshold = 3)
    {
        _logger = logger;
        _failureThreshold = Math.Max(1, failureThreshold);
    }

    // device = nom de l'équipement (ex. "EZO-pH", "DS18B20", "WK600-D").
    public void ReportFailure(EquipmentBus bus, string device, Exception ex)
    {
        bool becomesBad;
        int count;
        lock (_lock)
        {
            var s = _state[bus];
            s.ConsecutiveFailures++;
            s.LastDevice    = device;
            s.LastError     = ex.Message;
            s.LastErrorTime = DateTimeOffset.UtcNow;
            count      = s.ConsecutiveFailures;
            becomesBad = s.Ok && count >= _failureThreshold;
            if (becomesBad) s.Ok = false;
        }

        // Log intermédiaire (debug) sur les défauts qui n'atteignent pas encore
        // le seuil — visible en mode Debug sans polluer le niveau Info/Warning.
        if (count < _failureThreshold)
            _logger.LogDebug("Équipement {Bus}/{Device}: défaut transitoire ({N}/{Threshold})",
                bus, device, count, _failureThreshold);
        else if (becomesBad)
            _logger.LogError(ex, "Équipement {Bus}/{Device}: {N} défauts consécutifs — bus déclaré en panne",
                bus, device, count);
    }

    public void ReportSuccess(EquipmentBus bus, string device)
    {
        bool wasOk;
        lock (_lock)
        {
            var s = _state[bus];
            wasOk = s.Ok;
            s.Ok = true;
            if (s.ConsecutiveFailures > 0)
                s.ConsecutiveFailures = 0;
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
