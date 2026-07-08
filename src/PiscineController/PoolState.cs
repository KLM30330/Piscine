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
    public event Action<bool>?       PumpRunningChanged;
    // Permet à ElectrolyzerService de réagir immédiatement à un ORP qui
    // dépasse le seuil, sans attendre son prochain cycle de 5s.
    public event Action<double>?     OrpMvChanged;

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
        set
        {
            double old = Volatile.Read(ref _orpMv);
            Volatile.Write(ref _orpMv, value);
            // Notifie uniquement si la variation dépasse 5 mV pour éviter
            // de déclencher Apply() à chaque micro-fluctuation du capteur.
            if (Math.Abs(value - old) >= 5.0) OrpMvChanged?.Invoke(value);
        }
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
