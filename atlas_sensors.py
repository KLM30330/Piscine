# atlas_sensors.py — Drivers Atlas Scientific EZO (I2C)
"""
Pilote les modules EZO Atlas Scientific via bus I2C (Tentacle Shield).
  - EZO-pH   : mesure pH compensée en température
  - EZO-ORP  : mesure redox (mV)
  - EZO-RTD  : sonde température
  - EZO-PMP  : pompe doseuse péristaltique
"""

import io, fcntl, time, logging

logger = logging.getLogger("atlas")

I2C_SLAVE = 0x0703
RESPONSE_OK      = 1
RESPONSE_PENDING = 254
RESPONSE_ERROR   = 2
RESPONSE_NO_DATA = 255


class AtlasSensorError(Exception):
    pass


class AtlasSensor:
    def __init__(self, address: int, bus: int = 1, name: str = ""):
        self.address = address
        self.bus     = bus
        self.name    = name or f"0x{address:02X}"
        self._open()

    def _open(self):
        try:
            self.fr = io.open(f"/dev/i2c-{self.bus}", "rb", buffering=0)
            self.fw = io.open(f"/dev/i2c-{self.bus}", "wb", buffering=0)
            fcntl.ioctl(self.fr, I2C_SLAVE, self.address)
            fcntl.ioctl(self.fw, I2C_SLAVE, self.address)
        except Exception as e:
            raise AtlasSensorError(f"[{self.name}] Ouverture I2C impossible : {e}")

    def query(self, cmd: str, delay: float = 1.5) -> str:
        try:
            self.fw.write(cmd.encode())
            time.sleep(delay)
            raw = self.fr.read(31)
        except Exception as e:
            raise AtlasSensorError(f"[{self.name}] I2C '{cmd}' : {e}")
        if not raw:
            raise AtlasSensorError(f"[{self.name}] Pas de réponse")
        code = raw[0]
        if code == RESPONSE_OK:
            return raw[1:].decode("ascii", errors="ignore").strip("\x00").strip()
        if code == RESPONSE_PENDING:
            raise AtlasSensorError(f"[{self.name}] En attente (254)")
        if code == RESPONSE_ERROR:
            raise AtlasSensorError(f"[{self.name}] Erreur commande (2)")
        raise AtlasSensorError(f"[{self.name}] Code inconnu {code}")

    def close(self):
        try: self.fr.close(); self.fw.close()
        except Exception: pass


class PHSensor(AtlasSensor):
    def __init__(self, bus=1, address=0x63):
        super().__init__(address, bus, "EZO-pH")

    def read(self, temp_c: float = 25.0) -> float:
        return float(self.query(f"RT,{temp_c:.2f}", delay=1.5))

    def calibrate_mid(self, val=7.00):
        self.query(f"Cal,mid,{val:.2f}", delay=2.0)
        logger.info(f"[pH] Cal milieu pH={val}")

    def calibrate_low(self, val=4.00):
        self.query(f"Cal,low,{val:.2f}", delay=2.0)
        logger.info(f"[pH] Cal bas pH={val}")

    def calibrate_high(self, val=10.00):
        self.query(f"Cal,high,{val:.2f}", delay=2.0)
        logger.info(f"[pH] Cal haut pH={val}")

    def get_cal_points(self) -> int:
        try:
            r = self.query("Cal,?", delay=0.3)
            return int(r.split(",")[-1])
        except Exception:
            return 0

    def clear_cal(self):
        self.query("Cal,clear", delay=0.3)
        logger.info("[pH] Calibration effacée")


class ORPSensor(AtlasSensor):
    def __init__(self, bus=1, address=0x62):
        super().__init__(address, bus, "EZO-ORP")

    def read(self) -> float:
        return float(self.query("R", delay=1.5))


class TempSensor(AtlasSensor):
    def __init__(self, bus=1, address=0x66):
        super().__init__(address, bus, "EZO-RTD")

    def read(self) -> float:
        return float(self.query("R", delay=0.6))


class PeristalticPump(AtlasSensor):
    def __init__(self, bus=1, address=0x67):
        super().__init__(address, bus, "EZO-PMP")
        self._total_ml  = 0.0
        self._dispensing= False

    def dose(self, ml: float) -> bool:
        if ml <= 0:
            return False
        try:
            self.query(f"D,{ml:.2f}", delay=0.5)
            self._total_ml  += ml
            self._dispensing = True
            logger.info(f"[PMP] Dosage {ml:.2f} mL | total={self._total_ml:.2f} mL")
            return True
        except AtlasSensorError as e:
            logger.error(f"[PMP] Échec dosage : {e}")
            return False

    def stop(self):
        try:
            self.query("X", delay=0.3)
            self._dispensing = False
        except AtlasSensorError:
            pass

    def is_dispensing(self) -> bool:
        try:
            r = self.query("D,?", delay=0.3)
            self._dispensing = "," in r
            return self._dispensing
        except AtlasSensorError:
            return False

    @property
    def total_ml(self) -> float:
        return self._total_ml

    def reset_total(self):
        self._total_ml = 0.0
