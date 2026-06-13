using Xunit;
using PiscineController.Config;
using PiscineController.Ph;

namespace PiscineController.Tests.Ph;

public class PhPidControllerTests
{
    private static PoolConfig Cfg() => new();

    [Fact]
    public void Dose_ZeroWhenWithinDeadband()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(7.2, pumpRunning: true);
        Assert.Equal(0.0, dose);
    }

    [Fact]
    public void Dose_ZeroWhenPumpNotRunning()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(7.5, pumpRunning: false);
        Assert.Equal(0.0, dose);
    }

    [Fact]
    public void Dose_ClampedToMax()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(8.5, pumpRunning: true);
        Assert.True(dose <= Cfg().PhDoseMaxMl);
    }

    [Fact]
    public void Dose_BlockedByMinDelay()
    {
        var pid = new PhPidController(Cfg());
        pid.Compute(7.5, pumpRunning: true);
        double dose2 = pid.Compute(7.5, pumpRunning: true);
        Assert.Equal(0.0, dose2);
    }

    [Fact]
    public void TotalMl_Accumulates()
    {
        var pid = new PhPidController(Cfg());
        double d1 = pid.Compute(7.5, pumpRunning: true);
        Assert.Equal(d1, pid.TotalMl);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var pid = new PhPidController(Cfg());
        pid.Compute(7.5, pumpRunning: true);
        pid.Reset();
        Assert.Equal(0.0, pid.TotalMl);
    }

    [Fact]
    public void Dose_ZeroWhenPhBelowTarget()
    {
        var pid = new PhPidController(Cfg());
        double dose = pid.Compute(6.8, pumpRunning: true);
        Assert.Equal(0.0, dose);
    }
}
