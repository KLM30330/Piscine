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

    // ── Registres de contrôle (FC06) ─────────────────────────────────────────
    private const ushort REG_FREQ_SETPOINT = 0x1000; // % fréquence max × 100 (-10000 à 10000)
    private const ushort REG_COMMAND       = 0x2000; // Mot de commande

    private const ushort CMD_FORWARD       = 0x0001; // Marche avant
    private const ushort CMD_STOP_RAMP     = 0x0006; // Arrêt sur rampe
    private const ushort CMD_FAULT_RESET   = 0x0007; // Reset défaut

    // ── Registres de lecture (FC03) — groupe U0 base 0x7000 ──────────────────
    // U0-00 0x7000 : Fréquence sortie     × 0.01 Hz
    // U0-01 0x7001 : Courant sortie       × 0.1 A
    // U0-02 0x7002 : Tension sortie       × 1 V
    // U0-03 0x7003 : Tension bus DC       × 1 V
    // U0-04 0x7004 : Vitesse moteur       × 1 RPM
    // U0-05 0x7005 : Puissance sortie     × 0.1 kW
    // U0-06 0x7006 : Couple sortie        × 0.1 %
    // U0-07 0x7007 : Température          × 1 °C
    // U0-08 0x7008 : Mot d'état
    //   bit 0 : en marche
    //   bit 1 : sens avant
    //   bit 2 : sens arrière
    //   bit 3 : défaut
    //   bit 4 : alarme
    // U0-09 0x7009 : Code défaut actuel

    public Wk600Drive(PoolConfig cfg, ILogger<Wk600Drive> logger)
    {
        _cfg = cfg;
        _logger = logger;
        _slaveId = (byte)cfg.ModbusSlaveId;
        _port = new SerialPort(cfg.ModbusPort, cfg.ModbusBaudrate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout  = 1000,
            WriteTimeout = 1000,
            RtsEnable    = true,
        };
        _port.Open();
        _logger.LogInformation("WK600-D ouvert sur {Port} à {Baud} baud", cfg.ModbusPort, cfg.ModbusBaudrate);
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
    // Le registre 0x1000 attend un pourcentage de la fréquence max × 100
    // Exemple : 25 Hz sur base 50 Hz → 50.00% → valeur 5000
    private void SetFreqRaw(double hz)
    {
        double clamped = Math.Clamp(hz, _cfg.FreqMinAbsolute, _cfg.FreqNominal);
        ushort value   = (ushort)(clamped / _cfg.FreqNominal * 10000);
        WriteRegister(REG_FREQ_SETPOINT, value);
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
        WriteRegister(REG_COMMAND, CMD_FORWARD);
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

        WriteRegister(REG_COMMAND, CMD_STOP_RAMP);
        _running     = false;
        _currentFreq = 0;
        _logger.LogInformation("WK600-D arrêté");
    }

    // ── Reset défaut ──────────────────────────────────────────────────────────
    public void FaultReset() => WriteRegister(REG_COMMAND, CMD_FAULT_RESET);

    // ── Lecture état complet (U0-00 à U0-09, 10 registres) ───────────────────
    public DriveStatusSnapshot ReadStatus()
    {
        var m = ReadHoldingRegisters(0x7000, 10);
        if (m == null)
        {
            _logger.LogWarning("WK600-D ReadStatus : pas de réponse");
            return new DriveStatusSnapshot { IsRunning = _running, SetpointHz = _currentFreq };
        }

        ushort sw       = m[8];
        bool isRunning  = (sw & (1 << 0)) != 0;
        bool isFault    = (sw & (1 << 3)) != 0;
        bool atSetpoint = (sw & (1 << 7)) != 0;
        int faultCode   = m[9];
        _running = isRunning;

        return new DriveStatusSnapshot
        {
            OutFreqHz   = m[0] * 0.01,   // U0-00 : fréquence sortie  (0.01 Hz)
            DcBusV      = m[2],          // U0-02 : tension bus DC    (1 V)
            OutVoltageV = m[3],          // U0-03 : tension sortie    (1 V)
            OutCurrentA = m[4] * 0.1,    // U0-04 : courant sortie    (0.1 A)
            OutPowerKw  = m[5] * 10,    // U0-05 : puissance sortie  (0.1 kW)
            //MotorRpm    = m[4],           // U0-0? : vitesse moteur    (1 RPM)
            //OutTorquePct= m[6] * 0.1,    // U0-0? : couple sortie     (0.1 %)
            //DriveTempC  = m[7],           // U0-0? : température       (1 °C)
            IsRunning   = isRunning,
            IsFault     = isFault,
            AtSetpoint  = atSetpoint,
            FaultCode   = faultCode,      // U0-09 : code défaut
            FaultLabel  = FaultLabels.TryGetValue(faultCode, out var lbl) ? lbl : $"Code {faultCode}",
            SetpointHz  = _currentFreq,
        };
    }

    public void Dispose()
    {
        try { _port.Close(); _port.Dispose(); } catch { /* best-effort */ }
    }
}

public sealed class DriveStatusSnapshot
{
    public double OutFreqHz    { get; init; }
    public double OutCurrentA  { get; init; }
    public double OutVoltageV  { get; init; }
    public double DcBusV       { get; init; }
    public int    MotorRpm     { get; init; }
    public double OutPowerKw   { get; init; }
    public double OutTorquePct { get; init; }
    public double DriveTempC   { get; init; }
    public bool   IsRunning    { get; init; }
    public bool   IsFault      { get; init; }
    public bool   AtSetpoint   { get; init; }
    public int    FaultCode    { get; init; }
    public string FaultLabel   { get; init; } = "Aucun";
    public double SetpointHz   { get; init; }
    // Compatibilité DriveService
    public int    RunTimeH     { get; init; }
}
