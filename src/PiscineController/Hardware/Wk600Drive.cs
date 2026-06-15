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
        _port = new SerialPort(cfg.ModbusPort, cfg.ModbusBaudrate, Parity.None, 8, StopBits.Two)
        {
            ReadTimeout  = 1000,
            WriteTimeout = 1000,
            RtsEnable    = true,
        };
        _port.Open();
        _logger.LogInformation("WK600-D ouvert sur {Port}", cfg.ModbusPort);
    }

    public bool IsRunning     => _running;
    public double CurrentFreq => _currentFreq;

    // ── CRC-16 Modbus ────────────────────────────────────────────────────────
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

    // ── FC06 : écriture d'un registre ────────────────────────────────────────
    private bool WriteRegister(ushort addr, ushort value)
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();

            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x06;
            req[2] = (byte)(addr  >> 8); req[3] = (byte)(addr  & 0xFF);
            req[4] = (byte)(value >> 8); req[5] = (byte)(value & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            Thread.Sleep(20);

            byte[] resp = new byte[8];
            int read = 0;
            while (read < 8)
                read += _port.Read(resp, read, 8 - read);

            ushort respCrc = Crc16(resp.AsSpan(0, 6));
            ushort gotCrc  = (ushort)(resp[6] | (resp[7] << 8));
            if (respCrc != gotCrc)
            {
                _logger.LogWarning("WK600-D WriteRegister CRC invalide addr=0x{Addr:X4}", addr);
                return false;
            }

            return resp[0] == _slaveId && resp[1] == 0x06;
        }
    }

    // ── FC03 : lecture de registres ──────────────────────────────────────────
    private ushort[]? ReadHoldingRegisters(ushort addr, ushort count)
    {
        lock (_lock)
        {
            _port.DiscardInBuffer();

            Span<byte> req = stackalloc byte[8];
            req[0] = _slaveId; req[1] = 0x03;
            req[2] = (byte)(addr  >> 8); req[3] = (byte)(addr  & 0xFF);
            req[4] = (byte)(count >> 8); req[5] = (byte)(count & 0xFF);
            ushort crc = Crc16(req[..6]);
            req[6] = (byte)(crc & 0xFF); req[7] = (byte)(crc >> 8);
            _port.Write(req.ToArray(), 0, 8);

            Thread.Sleep(20);

            int expected = 5 + count * 2;
            byte[] resp = new byte[expected];
            int read = 0;
            while (read < expected)
                read += _port.Read(resp, read, expected - read);

            if (resp[0] != _slaveId || resp[1] != 0x03)
                return null;

            ushort respCrc = Crc16(resp.AsSpan(0, expected - 2));
            ushort gotCrc  = (ushort)(resp[expected - 2] | (resp[expected - 1] << 8));
            if (respCrc != gotCrc)
            {
                _logger.LogWarning("WK600-D ReadHoldingRegisters CRC invalide addr=0x{Addr:X4}", addr);
                return null;
            }

            var regs = new ushort[count];
            for (int i = 0; i < count; i++)
                regs[i] = (ushort)((resp[3 + i * 2] << 8) | resp[4 + i * 2]);
            return regs;
        }
    }

    // ── Consigne fréquence ───────────────────────────────────────────────────
    private void SetFreqRaw(double hz)
    {
        double clamped = Math.Clamp(hz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        WriteRegister(0x1000, (ushort)(clamped * 100)); // 0.01 Hz
        _currentFreq = clamped;
    }

    // ── Démarrage avec rampe logicielle ──────────────────────────────────────
    public async Task RampStartAsync(double targetHz, CancellationToken ct)
    {
        if (_running) return;

        double target = Math.Max(
            Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal),
            _cfg.FreqStartMin);

        SetFreqRaw(_cfg.FreqStartMin);
        WriteRegister(0x2000, 0x0001); // RUN avant
        _running     = true;
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

    // ── Changement de fréquence avec rampe ───────────────────────────────────
    public async Task RampToAsync(double targetHz, CancellationToken ct)
    {
        if (!_running) return;

        double target = Math.Clamp(targetHz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        if (Math.Abs(target - _currentFreq) < 0.5) return;

        double freq = _currentFreq;
        double dir  = target > freq ? 1 : -1;

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

    // ── Arrêt avec rampe logicielle ───────────────────────────────────────────
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
        _running     = false;
        _currentFreq = 0;
        _logger.LogInformation("WK600-D arrêté");
    }

    // ── Reset défaut ──────────────────────────────────────────────────────────
    public void FaultReset() => WriteRegister(0x2000, 0x0007);

    // ── Lecture mesures ───────────────────────────────────────────────────────
    // Bloc principal : U0-00 à U0-05 (0x7000, 6 registres)
    // État variateur : U0-61 (0x703D)
    // Code défaut    : U0-62 (0x703E)
    public DriveStatusSnapshot ReadStatus()
    {
        var m = ReadHoldingRegisters(0x7000, 6);
        if (m == null)
        {
            _logger.LogWarning("WK600-D ReadStatus : pas de réponse (mesures)");
            return new DriveStatusSnapshot
            {
                IsRunning  = _running,
                SetpointHz = _currentFreq,
            };
        }

        // Lecture état + défaut (2 registres contigus 0x703D–0x703E)
        var s = ReadHoldingRegisters(0x703D, 2);
        ushort sw     = s != null && s.Length >= 1 ? s[0] : (ushort)0;
        int faultCode = s != null && s.Length >= 2 ? s[1] : 0;

        // U0-61 bits : 0=Running, 3=Fault, 7=AtSetpoint (à confirmer sur votre manuel)
        bool isRunning  = (sw & (1 << 0)) != 0;
        bool isFault    = (sw & (1 << 3)) != 0;
        bool atSetpoint = (sw & (1 << 7)) != 0;
        _running = isRunning;

        return new DriveStatusSnapshot
        {
            OutFreqHz   = m[0] / 100.0,  // U0-00 : fréquence sortie  (0.01 Hz)
            // m[1] = U0-01 : fréquence consigne — non exposé
            DcBusV      = m[2] / 10.0,   // U0-02 : tension bus DC    (0.1 V)
            OutVoltageV = m[3],           // U0-03 : tension sortie    (1 V)
            OutCurrentA = m[4] / 100.0,  // U0-04 : courant sortie    (0.01 A)
            OutPowerKw  = m[5] / 10.0,   // U0-05 : puissance sortie  (0.1 kW)
            IsRunning   = isRunning,
            IsFault     = isFault,
            AtSetpoint  = atSetpoint,
            FaultCode   = faultCode,
            FaultLabel  = FaultLabels.TryGetValue(faultCode, out var lbl) ? lbl : $"Code {faultCode}",
            SetpointHz  = _currentFreq,
            // DriveTempC et RunTimeH non disponibles dans ce bloc — valeur neutre
            DriveTempC  = 0,
            RunTimeH    = 0,
        };
    }

    public void Dispose()
    {
        try { _port.Close(); _port.Dispose(); } catch { /* best-effort */ }
    }
}

public sealed class DriveStatusSnapshot
{
    public double OutFreqHz   { get; init; }
    public double OutCurrentA { get; init; }
    public double OutVoltageV { get; init; }
    public double DcBusV      { get; init; }
    public double OutPowerKw  { get; init; }
    public double SetpointHz  { get; init; }
    public bool   IsRunning   { get; init; }
    public bool   IsFault     { get; init; }
    public bool   AtSetpoint  { get; init; }
    public int    FaultCode   { get; init; }
    public string FaultLabel  { get; init; } = "Aucun";
    public double DriveTempC  { get; init; }
    public int    RunTimeH    { get; init; }
}
