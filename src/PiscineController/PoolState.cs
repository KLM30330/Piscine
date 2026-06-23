using PiscineController.Config;
using PiscineController.Filtration;

namespace PiscineController;

public sealed class PoolState
{
    private double _waterTempC;
    private double _phValue;
    private double _orpMv;
    private double _pumpFreqHz;
    private int _filterMode;
    private int _pumpRunning;
    private int _electrolyzerRunning;
    private int _electrolyzerEnabled = 1;   // activé par défaut (suit la pompe)

    // Déclenchés uniquement sur changement réel (pas à chaque écriture), pour
    // permettre une publication MQTT immédiate au lieu d'attendre le prochain
    // cycle périodique (jusqu'à 60s pour FilterMode via SensorService, 5s pour
    // l'électrolyseur) — la latence perçue côté switch HA vient de là.
    public event Action<FilterMode>? FilterModeChanged;
    public event Action<bool>? ElectrolyzerEnabledChanged;

    public double WaterTempC
    {
        get => Volatile.Read(ref _waterTempC);
        set => Volatile.Write(ref _waterTempC, value);
    }
    public double PhValue
    {
        get => Volatile.Read(ref _phValue);
        set => Volatile.Write(ref _phValue, value);
    }
    public double OrpMv
    {
        get => Volatile.Read(ref _orpMv);
        set => Volatile.Write(ref _orpMv, value);
    }
    public double PumpFreqHz
    {
        get => Volatile.Read(ref _pumpFreqHz);
        set => Volatile.Write(ref _pumpFreqHz, value);
    }
    public FilterMode FilterMode
    {
        get => (FilterMode)Volatile.Read(ref _filterMode);
        set
        {
            int old = Interlocked.Exchange(ref _filterMode, (int)value);
            if (old != (int)value) FilterModeChanged?.Invoke(value);
        }
    }
    public bool PumpRunning
    {
        get => Volatile.Read(ref _pumpRunning) == 1;
        set => Volatile.Write(ref _pumpRunning, value ? 1 : 0);
    }
    public bool ElectrolyzerRunning
    {
        get => Volatile.Read(ref _electrolyzerRunning) == 1;
        set => Volatile.Write(ref _electrolyzerRunning, value ? 1 : 0);
    }
    public bool ElectrolyzerEnabled
    {
        get => Volatile.Read(ref _electrolyzerEnabled) == 1;
        set
        {
            int newVal = value ? 1 : 0;
            int old = Interlocked.Exchange(ref _electrolyzerEnabled, newVal);
            if (old != newVal) ElectrolyzerEnabledChanged?.Invoke(value);
        }
    }

    private readonly object _driveLock = new();
    private DriveStatusPayload? _driveStatus;
    public DriveStatusPayload? DriveStatus
    {
        get { lock (_driveLock) return _driveStatus; }
        set { lock (_driveLock) _driveStatus = value; }
    }
}
