# pcf8574.py — Driver relais PCF8574 I2C + gestion électrolyseur
import time, logging

logger = logging.getLogger("pcf8574")


class PCF8574Error(Exception):
    pass


class PCF8574Relay:
    """
    Driver PCF8574 — 8 sorties quasi-bidirectionnelles open-drain.
    Logique ACTIF HAUT : écrire 1 sur un bit → relais ON.
    """
    def __init__(self, address: int = 0x20, bus: int = 1):
        try:
            import smbus2
            self._bus = smbus2.SMBus(bus)
        except ImportError:
            raise PCF8574Error("pip install smbus2")
        except Exception as e:
            raise PCF8574Error(f"I2C bus {bus} : {e}")
        self.address = address
        self._state  = 0x00          # tous relais OFF
        self._write()
        logger.info(f"[PCF8574] @ 0x{address:02X} — tous relais OFF")

    def _write(self):
        try:
            self._bus.write_byte(self.address, self._state)
        except Exception as e:
            raise PCF8574Error(f"Écriture I2C : {e}")

    def set(self, pin: int, on: bool):
        if not 0 <= pin <= 7:
            raise PCF8574Error(f"Pin {pin} invalide (0–7)")
        if on:
            self._state |=  (1 << pin)
        else:
            self._state &= ~(1 << pin)
        self._write()
        logger.info(f"[PCF8574] P{pin} → {'ON' if on else 'OFF'}")

    def get(self, pin: int) -> bool:
        return bool(self._state & (1 << pin))

    def all_off(self):
        self._state = 0x00
        self._write()
        logger.info("[PCF8574] Tous relais OFF")

    def close(self):
        try:
            self.all_off()
            self._bus.close()
        except Exception:
            pass


class ElectrolyzerController:
    """
    Pilote le relais électrolyseur via PCF8574.
    Verrouillages de sécurité :
      - pompe filtration arrêtée → OFF
      - alarme ORP → OFF
      - désactivé manuellement → OFF
    """
    def __init__(self, pcf: PCF8574Relay, pin: int = 0):
        self.pcf         = pcf
        self.pin         = pin
        self._enabled    = False
        self._on         = False
        self._start_time = 0.0
        self._total_on_s = 0.0
        self._lock       = ""

    @property
    def is_on(self):      return self._on
    @property
    def is_enabled(self): return self._enabled
    @property
    def lock_reason(self):return self._lock

    def enable(self):
        self._enabled = True
        logger.info("[ELEC] Autorisé")

    def disable(self):
        self._enabled = False
        self._off("désactivé manuellement")

    def _on_relay(self):
        if self._on: return
        self.pcf.set(self.pin, True)
        self._on         = True
        self._start_time = time.time()
        logger.info("[ELEC] ON")

    def _off(self, reason=""):
        if not self._on: return
        self.pcf.set(self.pin, False)
        self._total_on_s += time.time() - self._start_time
        self._on          = False
        logger.info(f"[ELEC] OFF — {reason}")

    def update(self, pump_running: bool, orp_alarm: bool):
        if not self._enabled:
            self._lock = "désactivé"
            self._off(self._lock)
        elif not pump_running:
            self._lock = "pompe arrêtée"
            self._off(self._lock)
        elif orp_alarm:
            self._lock = "alarme ORP"
            self._off(self._lock)
        else:
            self._lock = ""
            self._on_relay()

    def runtime_hours(self) -> float:
        total = self._total_on_s
        if self._on and self._start_time:
            total += time.time() - self._start_time
        return round(total / 3600, 2)

    def status(self) -> dict:
        return {
            "enabled"       : self._enabled,
            "is_on"         : self._on,
            "lock_reason"   : self._lock,
            "runtime_hours" : self.runtime_hours(),
        }
