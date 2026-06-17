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

    // ── Registres de lecture (FC03) — mapping confirmé via manuel constructeur ──
    // Bloc A : 0x7000-0x7006 (7 registres)
    //   U0-00 0x7000 : Running frequency      × 0.01 Hz
    //   U0-01 0x7001 : Set frequency           × 0.01 Hz   (consigne, PAS le courant)
    //   U0-02 0x7002 : Bus voltage             × 0.1 V     (PAS la tension de sortie)
    //   U0-03 0x7003 : Output voltage          × 1 V
    //   U0-04 0x7004 : Output current          × 0.01 A    (PAS × 0.1 !)
    //   U0-05 0x7005 : Output power             × 0.1 kW
    //   U0-06 0x7006 : Output torque            × 0.1 %
    //
    // Bloc B : 0x703D-0x703E (2 registres)
    //   U0-61 0x703D : AC drive running state   0=arrêté, 1=en marche (valeur simple, pas des bits)
    //   U0-62 0x703E : Current fault code        valeur directe
    //
    // Registre isolé : 0x7022
    //   U0-34 0x7022 : Motor temperature         × 1 °C
    //
    // ⚠️ 0x7007/0x7008 ne sont PAS des températures ni un mot d'état marche/arrêt :
    // ce sont respectivement l'état des entrées (X state) et des sorties (DO state)
    // numériques du variateur. L'ancien code les utilisait par erreur.

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
            try
            {
                while (read < expected)
                    read += _port.Read(resp, read, expected - read);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("WK600-D ReadHoldingRegisters timeout addr=0x{Addr:X4} " +
                                   "({Read}/{Expected} octets reçus)", addr, read, expected);
                _port.DiscardInBuffer();   // évite de polluer la prochaine requête
                return null;
            }

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

    // ── Lecture état complet ────────────────────────────────────────────────
    // Délai entre deux requêtes FC03 successives : certains variateurs ont besoin
    // d'un court répit pour traiter une nouvelle requête après avoir répondu.
    // 50ms s'est révélé insuffisant en pratique (timeouts intermittents sur 0x703D,
    // alors que le registre répond correctement via un autre client Modbus) —
    // augmenté à 150ms et complété par un retry pour absorber les cas résiduels.
    private const int InterRequestDelayMs = 150;
    private const int MaxRetries = 2;

    private ushort[]? ReadWithRetry(ushort addr, ushort count, string label)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var regs = ReadHoldingRegisters(addr, count);
            if (regs != null) return regs;

            if (attempt < MaxRetries)
            {
                _logger.LogDebug("WK600-D: {Label} (0x{Addr:X4}) timeout, " +
                                  "tentative {Attempt}/{Max}", label, addr, attempt, MaxRetries);
                Thread.Sleep(InterRequestDelayMs * 2);   // pause plus longue avant retry
            }
        }
        return null;
    }

    public DriveStatusSnapshot ReadStatus()
    {
        // Bloc A : fréquence / tensions / courant / puissance / couple
        var a = ReadWithRetry(0x7000, 7, "bloc A");
        Thread.Sleep(InterRequestDelayMs);

        // Bloc B : état marche/arrêt + code défaut (registre confirmé valide via
        // test Python indépendant — un éventuel échec ici est transitoire, pas
        // une absence du registre sur ce firmware)
        var b = ReadWithRetry(0x703D, 2, "bloc B (U0-61/U0-62)");
        if (b == null)
            _logger.LogWarning("WK600-D : lecture U0-61/U0-62 (0x703D) échouée " +
                               "après {Max} tentatives — état marche conservé", MaxRetries);
        Thread.Sleep(InterRequestDelayMs);

        // Registre isolé : température moteur
        var t = ReadWithRetry(0x7022, 1, "température moteur");

        if (a == null)
        {
            _logger.LogWarning("WK600-D ReadStatus : bloc principal (0x7000) sans réponse");
            return new DriveStatusSnapshot { IsRunning = _running, SetpointHz = _currentFreq };
        }

        // Bloc B optionnel : si absent, on garde le dernier état connu plutôt que
        // de perdre toutes les autres mesures qui ont, elles, été lues avec succès.
        bool isRunning = b != null ? b[0] == 1 : _running;
        int faultCode  = b != null ? b[1] : 0;
        bool isFault   = faultCode != 0;
        double tempC   = t != null ? t[0] : 0.0;  // U0-34, absente si la lecture a échoué

        _running = isRunning;

        return new DriveStatusSnapshot
        {
            OutFreqHz    = a[0] * 0.01,   // U0-00 : fréquence sortie   (0.01 Hz)
            SetpointFreqHz = a[1] * 0.01, // U0-01 : fréquence consigne (0.01 Hz)
            DcBusV       = a[2] * 0.1,    // U0-02 : tension bus DC     (0.1 V)
            OutVoltageV  = a[3],           // U0-03 : tension sortie     (1 V)
            OutCurrentA  = a[4] * 0.1,   // U0-04 : courant sortie     (0.01 A)
            OutPowerKw   = a[5] * 10,    // U0-05 : puissance sortie   (0.1 kW)
            OutTorquePct = a[6] * 0.1,    // U0-06 : couple sortie      (0.1 %)
            DriveTempC   = tempC,          // U0-34 : température moteur (1 °C)
            IsRunning    = isRunning,       // U0-61

            IsFault      = isFault,
            AtSetpoint   = Math.Abs(a[0] * 0.01 - _currentFreq) < 0.5,
            FaultCode    = faultCode,       // U0-62
            FaultLabel   = FaultLabels.TryGetValue(faultCode, out var lbl) ? lbl : $"Code {faultCode}",
            SetpointHz   = _currentFreq,
        };
    }

    public void Dispose()
    {
        try { _port.Close(); _port.Dispose(); } catch { /* best-effort */ }
    }
}

public sealed class DriveStatusSnapshot
{
    public double OutFreqHz       { get; init; }
    public double SetpointFreqHz  { get; init; }
    public double OutCurrentA     { get; init; }
    public double OutVoltageV     { get; init; }
    public double DcBusV          { get; init; }
    public int    MotorRpm        { get; init; }
    public double OutPowerKw      { get; init; }
    public double OutTorquePct    { get; init; }
    public double DriveTempC      { get; init; }
    public bool   IsRunning       { get; init; }
    public bool   IsFault         { get; init; }
    public bool   AtSetpoint      { get; init; }
    public int    FaultCode       { get; init; }
    public string FaultLabel      { get; init; } = "Aucun";
    public double SetpointHz      { get; init; }
    public int    RunTimeH        { get; init; }
}
