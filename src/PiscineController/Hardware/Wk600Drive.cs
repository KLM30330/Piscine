using System.IO.Ports;
using Microsoft.Extensions.Logging;
using PiscineController.Config;

namespace PiscineController.Hardware;

public sealed class Wk600Drive : IDisposable
{
    private readonly SerialPort _port;
    private readonly byte _slaveId;
    private readonly PoolConfig _cfg;
    private readonly ILogger<Wk600Drive> _logger;
    private double _currentFreq;
    private bool _running;
    private readonly object _lock = new();

    private static readonly Dictionary<int, string> FaultLabels = new()
    {
        [0]="Aucun", [1]="Surintensité accel", [2]="Surintensité décel",
        [3]="Surintensité const", [4]="Surtension accel", [5]="Surtension décel",
        [6]="Surtension const", [7]="Sous-tension DC", [8]="Surchauffe variateur",
        [9]="Surchauffe moteur", [10]="Surcharge variateur", [11]="Surcharge moteur",
        [12]="Entrée externe", [13]="Communication", [14]="Perte phase entrée",
        [15]="Perte phase sortie", [16]="EEPROM", [17]="CPU", [18]="Court-circuit sortie",
    };

    public Wk600Drive(PoolConfig cfg, ILogger<Wk600Drive> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _slaveId = (byte)cfg.ModbusSlaveId;
        _port = new SerialPort(cfg.ModbusPort, cfg.ModbusBaudrate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        _port.Open();
        _logger.LogInformation("WK600-D ouvert sur {Port}", cfg.ModbusPort);
    }

    public bool IsRunning => _running;
    public double CurrentFreq => _currentFreq;

    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) == 1 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    private bool WriteRegister(ushort addr, ushort value)
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();
            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x06;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(value >> 8); req[5] = (byte)(value & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            byte[] resp = new byte[8];
            int read = 0;
            while (read < 8) read += _port.Read(resp, read, 8 - read);
            return resp[0] == _slaveId && resp[1] == 0x06;
        }
    }

    private ushort[]? ReadHoldingRegisters(ushort addr, ushort count)
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();
            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x03;
            req[2] = (byte)(addr >> 8); req[3] = (byte)(addr & 0xFF);
            req[4] = (byte)(count >> 8); req[5] = (byte)(count & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            int expected = 5 + count * 2;
            byte[] resp = new byte[expected];
            int read = 0;
            while (read < expected) read += _port.Read(resp, read, expected - read);
            if (resp[0] != _slaveId || resp[1] != 0x03) return null;

            var regs = new ushort[count];
            for (int i = 0; i < count; i++)
                regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[4 + i * 2]);
            return regs;
        }
    }

private void SetFreqRaw(double hz)
{
    double clamped = Math.Clamp(hz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
    WriteRegister(0x1000, (ushort)(clamped * 100)); // consigne fréquence
    _currentFreq = clamped;
}

public async Task RampStartAsync(double targetHz, CancellationToken ct)
{
    if (_running) return;
    double target = Math.Max(Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal), _cfg.FreqStartMin);
    SetFreqRaw(_cfg.FreqStartMin);
    WriteRegister(0x2000, 0x0001); // RUN avant
    _running = true;
    _currentFreq = _cfg.FreqStartMin;
    double freq = _cfg.FreqStartMin;
    while (freq < target && !ct.IsCancellationRequested)
    {
        freq = Math.Min(freq + _cfg.FreqRampStep, target);
        SetFreqRaw(freq);
        await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
    }
    _logger.LogInformation("WK600-D démarré @ {Freq} Hz", _currentFreq);
}

public async Task RampToAsync(double targetHz, CancellationToken ct)
{
    if (!_running) return;
    double target = Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
    if (Math.Abs(target - _currentFreq) < 0.5) return;
    double freq = _currentFreq;
    double dir = target > freq ? 1 : -1;
    while (dir > 0 ? freq < target : freq > target)
    {
        freq = Math.Clamp(freq + dir * _cfg.FreqRampStep, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        if (dir > 0 && freq > target) freq = target;
        if (dir < 0 && freq < target) freq = target;
        SetFreqRaw(freq);
        await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
    }
    _logger.LogInformation("WK600-D fréquence → {Freq} Hz", _currentFreq);
}

public async Task RampStopAsync(CancellationToken ct)
{
    if (!_running) return;
    double freq = _currentFreq;
    while (freq > _cfg.FreqStartMin && !ct.IsCancellationRequested)
    {
        freq = Math.Max(freq - _cfg.FreqRampStep, _cfg.FreqStartMin);
        SetFreqRaw(freq);
        await Task.Delay((int)(_cfg.FreqRampDelay * 1000), ct).ConfigureAwait(false);
    }
    WriteRegister(0x2000, 0x0006); // STOP
    _running = false;
    _currentFreq = 0;
    _logger.LogInformation("WK600-D arrêté");
}

public void FaultReset() => WriteRegister(0x2000, 0x0007); // Reset défaut

public DriveStatusSnapshot ReadStatus()
{
    // Bloc 1 : mesures continues 0x7000–0x7005 (U0-00 à U0-05)
    var measures = ReadHoldingRegisters(0x7000, 6);
    // Bloc 2 : température moteur 0x7022 (U0-34)
    // var tempRegs = ReadHoldingRegisters(0x7022, 1);
    // Bloc 3 : état variateur 0x703D + code défaut 0x703E (U0-61, U0-62)
   // var statusRegs = ReadHoldingRegisters(0x703D, 2);

    if (measures == null || measures.Length < 6)
        return new DriveStatusSnapshot { SetpointHz = _currentFreq };

    // U0-61 : bits d'état du variateur
    // bit 0 = Running, bit 2 = Fault (à confirmer selon manuel)
   // ushort sw       = statusRegs != null && statusRegs.Length >= 1 ? statusRegs[0] : (ushort)0;
    // int faultCode   = statusRegs != null && statusRegs.Length >= 2 ? statusRegs[1] : 0;
    bool isRunning  = (sw & (1 << 0)) != 0; // bit 0 : en marche
    bool isFault    = (sw & (1 << 3)) != 0; // bit 3 : défaut actif
    bool atSetpoint = (sw & (1 << 7)) != 0; // bit 7 : à la consigne
    _running = isRunning;

    return new DriveStatusSnapshot
    {
        OutFreqHz   = measures[0] / 100.0,           // U0-00 : 0.01 Hz
        // measures[1] = U0-01 fréquence consigne (non mappé dans le snapshot)
        DcBusV      = measures[2] / 10.0,            // U0-02 : 0.1 V
        OutVoltageV = measures[3],                    // U0-03 : 1 V
        OutCurrentA = measures[4] / 100.0,           // U0-04 : 0.01 A
        OutPowerKw  = measures[5] / 10.0,            // U0-05 : 0.1 kW
        // DriveTempC  = tempRegs != null && tempRegs.Length >= 1 ? tempRegs[0] : 0, // U0-34 : 1°C
        FaultCode   = faultCode,                     // U0-62
        FaultLabel  = FaultLabels.TryGetValue(faultCode, out var lbl) ? lbl : $"Code {faultCode}",
        IsRunning   = isRunning,
        IsFault     = isFault,
        AtSetpoint  = atSetpoint,
        SetpointHz  = _currentFreq,
        // RunTimeH et MotorRpm non disponibles directement — voir U0-26 (0x701A) et U0-14 (0x700E)
    };
}

    public void Dispose()
    {
        try { _port.Close(); _port.Dispose(); } catch { }
    }
}

public sealed class DriveStatusSnapshot
{
    public double OutFreqHz { get; init; }
    public double OutCurrentA { get; init; }
    public double OutVoltageV { get; init; }
    public double DcBusV { get; init; }
    public double OutPowerKw { get; init; }
    public double DriveTempC { get; init; }
    public int RunTimeH { get; init; }
    public int FaultCode { get; init; }
    public string FaultLabel { get; init; } = "Aucun";
    public int MotorRpm { get; init; }
    public double InFreqHz { get; init; }
    public double InVoltageV { get; init; }
    public bool IsRunning { get; init; }
    public bool IsFault { get; init; }
    public bool AtSetpoint { get; init; }
    public double SetpointHz { get; init; }
}
