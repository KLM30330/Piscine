# vfd_drive.py — Variateur WK600-D Modbus RTU via USB/RS485
"""
Pilotage du variateur WK600-D via Modbus RTU.
Registres basés sur la documentation standard WK600.
⚠ Vérifiez les adresses dans votre manuel avant mise en service.
"""

import time, logging
from dataclasses import dataclass, field
from typing import Optional

try:
    from pymodbus.client import ModbusSerialClient
    from pymodbus.exceptions import ModbusException
except ImportError:
    raise ImportError("pip install pymodbus")

logger = logging.getLogger("vfd")


# ─────────────────────────────────────────────────────────────────────────────
# Registres Modbus WK600-D
# ─────────────────────────────────────────────────────────────────────────────
class Reg:
    # Commandes (écriture)
    CONTROL       = 0x2000   # 0x0001=RUN FWD, 0x0002=RUN REV, 0x0005=STOP
    FREQ_SETPOINT = 0x2001   # consigne Hz × 100

    # Paramètres (lecture/écriture)
    ACCEL_TIME    = 0x1001   # temps accélération × 10 (s)
    DECEL_TIME    = 0x1002   # temps décélération × 10 (s)
    FREQ_MIN      = 0x1003   # fréquence mini × 100
    FREQ_MAX      = 0x1004   # fréquence maxi × 100

    # Statut (lecture seule) — bloc 0x3000..0x300B
    OUT_FREQ      = 0x3000   # Hz × 100
    OUT_CURRENT   = 0x3001   # A × 10
    OUT_VOLTAGE   = 0x3002   # V
    DC_BUS        = 0x3003   # V bus DC
    OUT_POWER     = 0x3004   # kW × 10
    DRIVE_TEMP    = 0x3005   # °C radiateur
    RUN_TIME      = 0x3006   # heures cumulées
    FAULT_CODE    = 0x3007   # code défaut
    STATUS_WORD   = 0x3008   # bits d'état
    MOTOR_RPM     = 0x3009   # tr/min estimé
    IN_FREQ       = 0x300A   # Hz réseau × 100
    IN_VOLTAGE    = 0x300B   # V réseau
    STATUS_COUNT  = 12       # nb registres dans le bloc statut

STATUS_BITS = {
    0: "running", 1: "forward", 2: "reverse",
    3: "fault",   4: "ready",   7: "at_setpoint",
}
FAULT_LABELS = {
    0:"Aucun", 1:"Surintensité accel", 2:"Surintensité décel",
    3:"Surintensité const", 4:"Surtension accel", 5:"Surtension décel",
    6:"Surtension const", 7:"Sous-tension DC", 8:"Surchauffe variateur",
    9:"Surchauffe moteur", 10:"Surcharge variateur", 11:"Surcharge moteur",
    12:"Entrée externe", 13:"Communication", 14:"Perte phase entrée",
    15:"Perte phase sortie", 16:"EEPROM", 17:"CPU", 18:"Court-circuit sortie",
}


@dataclass
class DriveStatus:
    out_freq_hz    : float = 0.0
    out_current_a  : float = 0.0
    out_voltage_v  : float = 0.0
    out_power_kw   : float = 0.0
    dc_bus_v       : float = 0.0
    drive_temp_c   : float = 0.0
    run_time_h     : int   = 0
    motor_rpm      : int   = 0
    in_freq_hz     : float = 0.0
    in_voltage_v   : float = 0.0
    fault_code     : int   = 0
    fault_label    : str   = "Aucun"
    is_running     : bool  = False
    is_fault       : bool  = False
    at_setpoint    : bool  = False
    setpoint_hz    : float = 0.0
    status_flags   : dict  = field(default_factory=dict)

    def to_mqtt(self) -> dict:
        return {
            "out_freq_hz"   : self.out_freq_hz,
            "out_current_a" : self.out_current_a,
            "out_voltage_v" : self.out_voltage_v,
            "out_power_kw"  : self.out_power_kw,
            "dc_bus_v"      : self.dc_bus_v,
            "drive_temp_c"  : self.drive_temp_c,
            "run_time_h"    : self.run_time_h,
            "motor_rpm"     : self.motor_rpm,
            "in_freq_hz"    : self.in_freq_hz,
            "in_voltage_v"  : self.in_voltage_v,
            "fault_code"    : self.fault_code,
            "fault_label"   : self.fault_label,
            "is_running"    : self.is_running,
            "is_fault"      : self.is_fault,
            "at_setpoint"   : self.at_setpoint,
            "setpoint_hz"   : self.setpoint_hz,
        }


class VFDError(Exception):
    pass


class WK600Drive:
    """
    Pilotage variateur WK600-D via Modbus RTU.
    Inclut rampes de démarrage/arrêt pour moteur monophasé + condensateur.
    """

    def __init__(self, port="/dev/ttyUSB0", slave_id=1,
                 baudrate=9600, parity="N", stopbits=1,
                 freq_min_abs=30.0, freq_start=40.0,
                 freq_nominal=50.0, ramp_step=2.0, ramp_delay=0.5):

        self.slave       = slave_id
        self.freq_min    = freq_min_abs
        self.freq_start  = freq_start
        self.freq_nom    = freq_nominal
        self.ramp_step   = ramp_step
        self.ramp_delay  = ramp_delay
        self._cur_freq   = 0.0
        self._setpoint   = 0.0
        self._running    = False

        self.client = ModbusSerialClient(
            port=port, baudrate=baudrate, bytesize=8,
            parity=parity, stopbits=stopbits, timeout=1,
        )
        if not self.client.connect():
            raise VFDError(f"Connexion Modbus impossible sur {port}")
        logger.info(f"[VFD] Connecté {port} @ {baudrate}bd esclave #{slave_id}")

    # ── Primitives Modbus ─────────────────────────────────────

    def _rd(self, addr: int, count: int = 1) -> Optional[list]:
        try:
            r = self.client.read_holding_registers(addr, count, slave=self.slave)
            if r.isError(): return None
            return r.registers
        except ModbusException as e:
            logger.error(f"[VFD] Lecture 0x{addr:04X} : {e}"); return None

    def _wr(self, addr: int, val: int) -> bool:
        try:
            r = self.client.write_register(addr, val, slave=self.slave)
            return not r.isError()
        except ModbusException as e:
            logger.error(f"[VFD] Écriture 0x{addr:04X}={val} : {e}"); return False

    # ── Validation fréquence ──────────────────────────────────

    def _clamp(self, hz: float) -> float:
        if hz < self.freq_min:
            logger.warning(f"[VFD] {hz} Hz < min {self.freq_min} Hz → forcé")
            hz = self.freq_min
        return min(hz, self.freq_nom)

    # ── Commandes basses ──────────────────────────────────────

    def _set_freq_raw(self, hz: float):
        hz = self._clamp(hz)
        if self._wr(Reg.FREQ_SETPOINT, int(hz * 100)):
            self._setpoint = hz

    def _start_fwd(self):
        if self._wr(Reg.CONTROL, 0x0001):
            self._running = True

    def _stop_cmd(self):
        if self._wr(Reg.CONTROL, 0x0005):
            self._running = False

    def fault_reset(self):
        self._wr(Reg.CONTROL, 0x0080)

    # ── Rampes ───────────────────────────────────────────────

    def ramp_start(self, target_hz: float = 50.0):
        if self._running: return
        target_hz = max(self._clamp(target_hz), self.freq_start)
        self._set_freq_raw(self.freq_start)
        self._start_fwd()
        self._cur_freq = self.freq_start
        freq = self.freq_start
        while freq < target_hz:
            freq = min(freq + self.ramp_step, target_hz)
            self._set_freq_raw(freq)
            self._cur_freq = freq
            time.sleep(self.ramp_delay)
        logger.info(f"[VFD] Démarré @ {self._cur_freq:.0f} Hz")

    def ramp_to(self, target_hz: float):
        if not self._running: return
        target_hz = self._clamp(target_hz)
        if abs(target_hz - self._cur_freq) < 0.5: return
        step = self.ramp_step
        freq = self._cur_freq
        direction = 1 if target_hz > freq else -1
        while (direction == 1 and freq < target_hz) or \
              (direction == -1 and freq > target_hz):
            freq = freq + direction * step
            freq = target_hz if direction == 1 and freq > target_hz else freq
            freq = target_hz if direction == -1 and freq < target_hz else freq
            self._set_freq_raw(freq)
            self._cur_freq = freq
            time.sleep(self.ramp_delay)
        logger.info(f"[VFD] Fréquence → {self._cur_freq:.0f} Hz")

    def ramp_stop(self):
        if not self._running: return
        freq = self._cur_freq
        while freq > self.freq_start:
            freq = max(freq - self.ramp_step, self.freq_start)
            self._set_freq_raw(freq)
            self._cur_freq = freq
            time.sleep(self.ramp_delay)
        self._stop_cmd()
        self._cur_freq = 0.0
        logger.info("[VFD] Arrêté")

    # ── Lecture statut complet ─────────────────────────────────

    def read_status(self) -> DriveStatus:
        s = DriveStatus(setpoint_hz=self._setpoint)
        regs = self._rd(Reg.OUT_FREQ, Reg.STATUS_COUNT)
        if not regs or len(regs) < Reg.STATUS_COUNT:
            return s
        s.out_freq_hz   = regs[0]  / 100.0
        s.out_current_a = regs[1]  / 10.0
        s.out_voltage_v = float(regs[2])
        s.dc_bus_v      = float(regs[3])
        s.out_power_kw  = regs[4]  / 10.0
        s.drive_temp_c  = float(regs[5])
        s.run_time_h    = regs[6]
        s.fault_code    = regs[7]
        s.fault_label   = FAULT_LABELS.get(regs[7], f"Code {regs[7]}")
        sw              = regs[8]
        s.motor_rpm     = regs[9]
        s.in_freq_hz    = regs[10] / 100.0
        s.in_voltage_v  = float(regs[11])
        s.is_running    = bool(sw & (1 << 0))
        s.is_fault      = bool(sw & (1 << 3))
        s.at_setpoint   = bool(sw & (1 << 7))
        s.status_flags  = {n: bool(sw & (1 << b)) for b, n in STATUS_BITS.items()}
        self._running   = s.is_running
        if s.is_fault:
            logger.warning(f"[VFD] Défaut : {s.fault_label}")
        return s

    @property
    def is_running(self) -> bool: return self._running
    @property
    def current_freq(self) -> float: return self._cur_freq

    def close(self):
        try: self.client.close()
        except Exception: pass
