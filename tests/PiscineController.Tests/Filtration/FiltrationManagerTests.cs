using Xunit;
using PiscineController.Config;
using PiscineController.Filtration;

namespace PiscineController.Tests.Filtration;

public class FiltrationManagerTests
{
    private static PoolConfig Cfg() => new();

    [Theory]
    [InlineData(8.0, 2.24)]
    [InlineData(10.0, 3.0)]
    [InlineData(20.0, 10.0)]
    [InlineData(30.0, 24.0)]
    public void RequiredHours_FollowsRule(double temp, double expectedH)
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(expectedH, mgr.RequiredHours(temp), 1);
    }

    [Theory]
    [InlineData(500.0, WaterState.CriticalLow)]
    [InlineData(580.0, WaterState.Low)]
    [InlineData(630.0, WaterState.BorderLow)]
    [InlineData(700.0, WaterState.Optimal)]
    [InlineData(760.0, WaterState.BorderHigh)]
    [InlineData(820.0, WaterState.Overdose)]
    public void ClassifyOrp_CorrectState(double orp, WaterState expected)
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(expected, mgr.ClassifyOrp(orp));
    }

    [Fact]
    public void UpdateOrp_ConfirmsAfter3ConsistentReadings()
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.UpdateOrp(700.0);
        Assert.Equal(WaterState.Unknown, mgr.WaterState);
        mgr.UpdateOrp(700.0);
        Assert.Equal(WaterState.Unknown, mgr.WaterState);
        mgr.UpdateOrp(700.0);
        Assert.Equal(WaterState.Optimal, mgr.WaterState);
    }

    [Theory]
    [InlineData(500.0, 50.0)]
    [InlineData(630.0, 45.0)]
    [InlineData(820.0, 30.0)]
    public void TargetFreq_MatchesOrpState(double orp, double expectedHz)
    {
        var mgr = new FiltrationManager(Cfg());
        double freq = mgr.OrpTargetFreq(mgr.ClassifyOrp(orp), orp);
        Assert.Equal(expectedHz, freq, 0);
    }

    [Fact]
    public void ModeTransition_AutoPauseResume()
    {
        var mgr = new FiltrationManager(Cfg());
        Assert.Equal(FilterMode.Auto, mgr.Mode);
        mgr.SetMode("pause");
        Assert.Equal(FilterMode.Pause, mgr.Mode);
        mgr.ResumeAuto();
        Assert.Equal(FilterMode.Auto, mgr.Mode);
    }

    [Theory]
    [InlineData("forced", true)]
    [InlineData("boost",  true)]
    [InlineData("pause",  false)]
    [InlineData("stop",   false)]
    public void ShouldPumpRun_ByMode(string mode, bool expected)
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.SetMode(mode);
        Assert.Equal(expected, mgr.ShouldPumpRun());
    }

    [Fact]
    public void BoostExit_WhenOrpReachesTarget()
    {
        var mgr = new FiltrationManager(Cfg());
        mgr.SetMode("boost");
        mgr.CheckBoostExit(699.0);
        Assert.Equal(FilterMode.Boost, mgr.Mode);
        mgr.CheckBoostExit(700.0);
        Assert.Equal(FilterMode.Auto, mgr.Mode);
    }

    [Fact]
    public void BuildSchedule_DistributesAcrossSlots()
    {
        var mgr = new FiltrationManager(Cfg());
        var slots = mgr.BuildSchedule(20.0, 50.0);
        double total = slots.Sum(s => s.End - s.Start);
        Assert.Equal(10.0, total, 1);
    }
}
