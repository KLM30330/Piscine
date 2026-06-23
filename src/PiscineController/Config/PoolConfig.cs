namespace PiscineController.Config;

public sealed class PoolConfig
{
    // MQTT
    public string MqttBroker { get; set; } = "192.168.1.100";
    public int MqttPort { get; set; } = 1883;
    public string MqttUser { get; set; } = "pool_user";
    public string MqttPassword { get; set; } = "";
    public string MqttClientId { get; set; } = "pool_controller";
    public string MqttPrefix { get; set; } = "pool";
    public string MqttHaDisc { get; set; } = "homeassistant";

    // Hardware I2C
    public int I2cBus { get; set; } = 1;
    public int AtlasPhAddr { get; set; } = 0x63;
    public int AtlasOrpAddr { get; set; } = 0x62;
    public int AtlasRtdAddr { get; set; } = 0x66;
    public int AtlasPmpAddr { get; set; } = 0x67;
    public int Pcf8574Addr { get; set; } = 0x20;
    public int LcdI2cAddr { get; set; } = 0x27;

    // 1-Wire DS18B20
    public string? OnewirePumpSensorId { get; set; } = "28-0103804a321b";
    public double PumpTempAlertC { get; set; } = 60.0;
    public double PumpTempCriticalC { get; set; } = 70.0;

    // GPIO boutons (BCM)
    public int BtnLcdDisplay { get; set; } = 5;
    public int BtnPrimePump { get; set; } = 6;
    public int BtnPauseFilter { get; set; } = 13;
    public int BtnResumeFilter { get; set; } = 19;

    // Modbus WK600-D
    public string ModbusPort { get; set; } = "/dev/ttyUSB0";
    public int ModbusSlaveId { get; set; } = 1;
    public int ModbusBaudrate { get; set; } = 9600;
    public string ModbusParity { get; set; } = "N";
    public int ModbusStopbits { get; set; } = 1;

    // Fréquences moteur
    public double FreqMinAbsolute { get; set; } = 30.0;
    public double FreqMinFiltration { get; set; } = 35.0;
    public double FreqStartMin { get; set; } = 40.0;
    public double FreqNominal { get; set; } = 50.0;
    public double FreqRampStep { get; set; } = 2.0;
    public double FreqRampDelay { get; set; } = 0.5;

    // Hydraulique
    public double PoolVolumeM3 { get; set; } = 38.0;
    public double PumpFlowM3H { get; set; } = 17.0;
    public int[][] FiltrationSlots { get; set; } = [[8, 13], [14, 21]];

    // ORP
    public double OrpCriticalLow { get; set; } = 550.0;
    public double OrpLow { get; set; } = 620.0;
    public double OrpTargetLow { get; set; } = 650.0;
    public double OrpTargetHigh { get; set; } = 750.0;
    public double OrpHigh { get; set; } = 800.0;
    public int OrpStabilityN { get; set; } = 3;
    public double OrpMinFreqChange { get; set; } = 5.0;
    public double OrpMinChangeInterval { get; set; } = 120.0;

    // pH PID
    public double PhTarget { get; set; } = 7.2;
    public double PhDeadband { get; set; } = 0.05;
    // Recalibré pour retrouver l'intensité de dosage de l'ancien flux Node-RED
    // (ml = (pH-7.2) × 1000, sans plafond) tout en gardant un plafond de
    // sécurité (PhDoseMaxMl) que l'ancien flux n'avait pas. Avec Kp=1000,
    // un écart de 0.1 pH (le plus petit pas significatif observé côté capteur)
    // donne ≈100 mL au premier cycle, comme avant — mais ne dépasse jamais
    // PhDoseMaxMl, même pour un écart de pH beaucoup plus grand.
    public double PhKp { get; set; } = 1000.0;
    public double PhKi { get; set; } = 1.0;
    public double PhKd { get; set; } = 10.0;
    public double PhDoseMinMl { get; set; } = 1.0;
    public double PhDoseMaxMl { get; set; } = 100.0;
    public double PhMinDelayS { get; set; } = 600.0;
    public double PhCalMidValue { get; set; } = 7.0;
    public double PrimeVolumeMl { get; set; } = 20.0;

    // LCD
    public int LcdDisplayDuration { get; set; } = 30;
    public int LcdBacklightTimeout { get; set; } = 60;
}
