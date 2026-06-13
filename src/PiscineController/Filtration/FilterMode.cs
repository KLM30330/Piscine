namespace PiscineController.Filtration;

public enum FilterMode { Auto, Forced, Boost, Pause, Stop }

public enum WaterState
{
    Unknown, CriticalLow, Low, BorderLow, Optimal, BorderHigh, Overdose
}
