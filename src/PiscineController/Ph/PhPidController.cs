using PiscineController.Config;

namespace PiscineController.Ph;

public sealed class PhPidController
{
    private readonly PoolConfig _cfg;
    private double _integral;
    private double _prevError;
    private double _lastDoseTime = double.MinValue;
    private double _totalMl;

    public PhPidController(PoolConfig cfg) => _cfg = cfg;

    public double TotalMl => _totalMl;

    public double Compute(double phValue, bool pumpRunning)
    {
        if (!pumpRunning) return 0.0;

        double error = phValue - _cfg.PhTarget;

        if (Math.Abs(error) <= _cfg.PhDeadband) return 0.0;

        if (error <= 0) return 0.0;

        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - _lastDoseTime < _cfg.PhMinDelayS) return 0.0;

        _integral += error;
        double derivative = error - _prevError;
        _prevError = error;

        double dose = _cfg.PhKp * error
                    + _cfg.PhKi * _integral
                    + _cfg.PhKd * derivative;

        dose = Math.Clamp(dose, _cfg.PhDoseMinMl, _cfg.PhDoseMaxMl);
        _lastDoseTime = now;
        _totalMl += dose;
        return dose;
    }

    public void Reset()
    {
        _integral = 0;
        _prevError = 0;
        _lastDoseTime = double.MinValue;
        _totalMl = 0;
    }
}
