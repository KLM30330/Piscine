using PiscineController.Filtration;

namespace PiscineController;

public sealed class PoolState
{
    private double _waterTempC;
    private double _phValue;
    private double _orpMv;
    private double _pumpFreqHz;
    private int    _filterMode;
    private int    _pumpRunning;
    private int    _electrolyzerRunning;
    private int    _electrolyzerEnabled = 1;  // activé par défaut

    // Événements sur changement réel (pas à chaque écriture) pour
    // publication MQTT immédiate sans attendre le prochain cycle.
    public event Action<FilterMode>? FilterModeChanged;
    public event Action<bool>?       ElectrolyzerEnabledChanged;
    // Permet à ElectrolyzerService de couper le relais immédiatement
    // quand la pompe s'arrête, sans attendre son cycle de 5s.
    public event Action<bool>?       PumpRunningChanged;

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
        set
        {
            int newVal = value ? 1 : 0;
            int old    = Interlocked.Exchange(ref _pumpRunning, newVal);
            if (old != newVal) PumpRunningChanged?.Invoke(value);
        }
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
            int old    = Interlocked.Exchange(ref _electrolyzerEnabled, newVal);
            if (old != newVal) ElectrolyzerEnabledChanged?.Invoke(value);
        }
    }
}
