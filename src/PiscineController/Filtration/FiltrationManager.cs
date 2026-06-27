using PiscineController.Config;

namespace PiscineController.Filtration;

public sealed record FiltrationSlot(double Start, double End);

public sealed class FiltrationManager
{
    private readonly PoolConfig _cfg;
    private FilterMode _mode = FilterMode.Auto;
    private WaterState _waterState = WaterState.Unknown;
    private readonly List<WaterState> _orpHistory = [];
    private double _targetFreqHz;
    private double _forcedFreqHz;
    private List<FiltrationSlot> _schedule = [];
    private int _scheduleDay = -1;

    public FiltrationManager(PoolConfig cfg)
    {
        _cfg = cfg;
        _targetFreqHz = cfg.FreqNominal;
        _forcedFreqHz = cfg.FreqNominal;
    }

    public FilterMode Mode => _mode;
    public WaterState WaterState => _waterState;
    public double TargetFreqHz => _targetFreqHz;
    public double ForcedFreqHz => _forcedFreqHz;

    // Accès lecture seule au planning courant et au temps calculé,
    // pour permettre à FiltrationService de les publier en MQTT.
    public IReadOnlyList<FiltrationSlot> CurrentSchedule => _schedule;
    public double CurrentRequiredHours => _schedule.Count > 0
        ? _schedule.Sum(s => s.End - s.Start)
        : 0.0;

    public double RequiredHours(double tempC)
    {
        double minH = _cfg.PoolVolumeM3 / _cfg.PumpFlowM3H;
        double raw = tempC switch
        {
            < 10 => 2.0,
            < 12 => 3.0,
            < 16 => 4.0 + (tempC - 12) * 0.5,
            <= 28 => tempC / 2.0,
            _ => 24.0
        };
        return Math.Max(raw, minH);
    }

    public double CorrectedHours(double tempC, double freqHz)
    {
        double freq = Math.Max(freqHz, _cfg.FreqMinAbsolute);
        return Math.Min(RequiredHours(tempC) * (_cfg.FreqNominal / freq), 24.0);
    }

    public WaterState ClassifyOrp(double orp) => orp switch
    {
        _ when orp < _cfg.OrpCriticalLow => WaterState.CriticalLow,
        _ when orp < _cfg.OrpLow         => WaterState.Low,
        _ when orp < _cfg.OrpTargetLow   => WaterState.BorderLow,
        _ when orp <= _cfg.OrpTargetHigh => WaterState.Optimal,
        _ when orp <= _cfg.OrpHigh       => WaterState.BorderHigh,
        _ => WaterState.Overdose
    };

    public double OrpTargetFreq(WaterState state, double orp)
    {
        if (state == WaterState.Optimal)
        {
            double ratio = Math.Clamp(
                (orp - _cfg.OrpTargetLow) / (_cfg.OrpTargetHigh - _cfg.OrpTargetLow), 0, 1);
            return Math.Round(_cfg.FreqMinFiltration - ratio * (_cfg.FreqMinFiltration - _cfg.FreqMinAbsolute), 1);
        }
        return state switch
        {
            WaterState.CriticalLow or WaterState.Low or WaterState.Unknown => _cfg.FreqNominal,
            WaterState.BorderLow => 45.0,
            WaterState.BorderHigh or WaterState.Overdose => _cfg.FreqMinAbsolute,
            _ => _cfg.FreqNominal
        };
    }

    public (WaterState State, double TargetHz, bool Alarm) UpdateOrp(double orp)
    {
        var newState = ClassifyOrp(orp);
        _orpHistory.Add(newState);
        if (_orpHistory.Count > _cfg.OrpStabilityN)
            _orpHistory.RemoveAt(0);

        bool confirmed = _orpHistory.Count >= _cfg.OrpStabilityN
                      && _orpHistory.All(s => s == newState);
        if (confirmed && newState != _waterState)
            _waterState = newState;

        double freq = OrpTargetFreq(newState, orp);
        bool alarm = newState is WaterState.CriticalLow or WaterState.Overdose;
        return (newState, freq, alarm);
    }

    public void SetTargetFreq(double hz) =>
        _targetFreqHz = Math.Max(hz, _cfg.FreqMinAbsolute);

    public void SetMode(string modeStr, double? freqHz = null)
    {
        _mode = modeStr.ToLowerInvariant() switch
        {
            "auto"    => FilterMode.Auto,
            "forced"  => FilterMode.Forced,
            "rescue"  => FilterMode.Rescue,
            "pause"   => FilterMode.Pause,
            "stop"    => FilterMode.Stop,
            _ => _mode
        };
        if (freqHz.HasValue)
            _forcedFreqHz = Math.Max(freqHz.Value, _cfg.FreqMinAbsolute);
    }

    public void ResumeAuto() => _mode = FilterMode.Auto;

    public bool ShouldPumpRun() => _mode switch
    {
        FilterMode.Auto    => InSchedule(),
        FilterMode.Forced  => true,
        // En mode secours, la pompe est alimentée directement par relais PCF
        // (sans variateur) : le variateur doit rester ARRÊTÉ.
        FilterMode.Rescue  => false,
        FilterMode.Pause   => false,
        FilterMode.Stop    => false,
        _ => false
    };

    public double GetRunFreq() => _mode switch
    {
        FilterMode.Forced => _forcedFreqHz,
        _ => _targetFreqHz
    };

    public List<FiltrationSlot> BuildSchedule(double tempC, double freqHz = 50.0)
    {
        double total = CorrectedHours(tempC, freqHz);
        if (total >= 23.5)
        {
            _schedule = [new(0.0, 24.0)];
        }
        else
        {
            double remaining = total;
            var result = new List<FiltrationSlot>();
            foreach (var slot in _cfg.FiltrationSlots)
            {
                if (remaining <= 0) break;
                double slotLen = slot[1] - slot[0];
                double alloc = Math.Min(remaining, slotLen);
                result.Add(new(slot[0], slot[0] + alloc));
                remaining -= alloc;
            }
            if (remaining > 0)
                result.Add(new(22.0, Math.Min(22.0 + remaining, 30.0)));
            _schedule = result;
        }
        _scheduleDay = DateTime.Now.DayOfYear;
        return _schedule;
    }

    public bool NeedsRebuild() =>
        _schedule.Count == 0 || DateTime.Now.DayOfYear != _scheduleDay;

    private bool InSchedule()
    {
        double cur = DateTime.Now.Hour + DateTime.Now.Minute / 60.0;
        foreach (var s in _schedule)
        {
            if (s.End > 24.0)
            { if (cur >= s.Start || cur < s.End - 24.0) return true; }
            else if (cur >= s.Start && cur < s.End) return true;
        }
        return false;
    }
}
