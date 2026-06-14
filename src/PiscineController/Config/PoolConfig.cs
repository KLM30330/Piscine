namespace PiscineController.Config;

public sealed class PoolConfig
{
    // MQTT
    public string MqttBroker { get; init; } = "192.168.1.100";
    public int MqttPort { get; init; } = 1883;
    public string MqttUser { get; init; } = "pool_user";
    public string MqttPassword { get; init; } = "";
    public string MqttClientId { get; init; } = "pool_controller";
    public string MqttPrefix { get; init; } = "pool";
    public string MqttHaDisc { get; init; } = "homeassistant";

    // Hardware I2C
    public int I2cBus { get; set; } = 1;
    public int AtlasPhAddr { get; init; } = 0x63;
    public int AtlasOrpAddr { get; init; } = 0x62;
    public int AtlasRtdAddr { get; init; } = 0x66;
    public int AtlasPmpAddr { get; init; } = 0x67;
    public int Pcf8574Addr { get; init; } = 0x20;
    public int LcdI2cAddr { get; init; } = 0x27;

    // 1-Wire DS18B20
    public string? OnewirePumpSensorId { get; set; } = "a1-00a029cc9225";
    public double PumpTempAlertC { get; init; } = 60.0;
    public double PumpTempCriticalC { get; init; } = 70.0;

    // GPIO boutons (BCM)
    public int BtnLcdDisplay { get; init; } = 5;
    public int BtnPrimePump { get; init; } = 6;
    public int BtnPauseFilter { get; init; } = 13;
    public int BtnResumeFilter { get; init; } = 19;

    // Modbus WK600-D
    public string ModbusPort { get; set; } = "/dev/ttyUSB0";
    public int ModbusSlaveId { get; init; } = 1;
    public int ModbusBaudrate { get; init; } = 9600;
    public string ModbusParity { get; init; } = "N";
    public int ModbusStopbits { get; init; } = 1;

    // Fréquences moteur
    public double FreqMinAbsolute { get; init; } = 30.0;
    public double FreqMinFiltration { get; init; } = 35.0;
    public double FreqStartMin { get; init; } = 40.0;
    public double FreqNominal { get; init; } = 50.0;
    public double FreqRampStep { get; init; } = 2.0;
    public double FreqRampDelay { get; init; } = 0.5;

    // Hydraulique
    public double PoolVolumeM3 { get; init; } = 38.0;
    public double PumpFlowM3H { get; init; } = 17.0;
    public int[][] FiltrationSlots { get; init; } = [[8, 13], [14, 21]];

    // ORP
    public double OrpCriticalLow { get; init; } = 550.0;
    public double OrpLow { get; init; } = 620.0;
    public double OrpTargetLow { get; init; } = 650.0;
    public double OrpTargetHigh { get; init; } = 750.0;
    public double OrpHigh { get; init; } = 800.0;
    public int OrpStabilityN { get; init; } = 3;
    public double OrpMinFreqChange { get; init; } = 5.0;
    public double OrpMinChangeInterval { get; init; } = 120.0;

    // pH PID
    public double PhTarget { get; init; } = 7.2;
    public double PhDeadband { get; init; } = 0.05;
    public double PhKp { get; init; } = 10.0;
    public double PhKi { get; init; } = 0.1;
    public double PhKd { get; init; } = 1.0;
    public double PhDoseMinMl { get; init; } = 1.0;
    public double PhDoseMaxMl { get; init; } = 50.0;
    public double PhMinDelayS { get; init; } = 600.0;
    public double PrimeVolumeMl { get; init; } = 20.0;

    // Boost
    public double BoostFreq { get; init; } = 50.0;
    public double BoostOrpTarget { get; init; } = 700.0;

    // LCD
    public int LcdDisplayDuration { get; init; } = 30;
    public int LcdBacklightTimeout { get; init; } = 60;
}
