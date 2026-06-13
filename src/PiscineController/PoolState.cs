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
        set => Volatile.Write(ref _filterMode, (int)value);
    }
    public bool PumpRunning
    {
        get => Volatile.Read(ref _pumpRunning) == 1;
        set => Volatile.Write(ref _pumpRunning, value ? 1 : 0);
    }

    private readonly object _driveLock = new();
    private DriveStatusPayload? _driveStatus;
    public DriveStatusPayload? DriveStatus
    {
        get { lock (_driveLock) return _driveStatus; }
        set { lock (_driveLock) _driveStatus = value; }
    }
}
