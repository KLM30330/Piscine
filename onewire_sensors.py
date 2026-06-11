# onewire_sensors.py — DS18B20 1-Wire (GPIO4)
"""
Lecture sonde DS18B20 via sysfs kernel (/sys/bus/w1/devices/).
Aucune dépendance Python externe.
Prérequis : dtoverlay=w1-gpio,gpiopin=4 dans /boot/config.txt + reboot.
"""

import os, glob, time, logging
from dataclasses import dataclass
from enum import Enum
from typing import Optional

logger = logging.getLogger("onewire")

W1_BASE  = "/sys/bus/w1/devices"
W1_FILE  = "w1_slave"
DS_PFX   = "28-"


class ThermalState(Enum):
    UNKNOWN  = "unknown"
    NORMAL   = "normal"
    ALERT    = "alert"
    CRITICAL = "critical"


@dataclass
class PumpTempReading:
    temp_c       : float
    state        : ThermalState
    sensor_id    : str
    trend        : str = "stable"
    alarm_alert  : bool = False
    alarm_crit   : bool = False

    def to_mqtt(self) -> dict:
        return {
            "pump_temp_c"     : round(self.temp_c, 1),
            "pump_temp_state" : self.state.value,
            "pump_temp_trend" : self.trend,
            "pump_temp_alert" : self.alarm_alert,
            "pump_temp_crit"  : self.alarm_crit,
            "pump_temp_sensor": self.sensor_id,
        }


class DS18B20Error(Exception):
    pass


def list_sensors() -> list:
    try:
        return [os.path.basename(d) for d in glob.glob(os.path.join(W1_BASE, DS_PFX + "*"))]
    except Exception:
        return []


class PumpTempMonitor:
    def __init__(self, sensor_id: str = None,
                 alert_c: float = 60.0, critical_c: float = 70.0):
        self.alert_c    = alert_c
        self.critical_c = critical_c
        self._history   = []
        self._alert     = False
        self._crit      = False
        self._path      = self._find(sensor_id)
        logger.info(f"[1-Wire] Sonde pompe : {self.sensor_id}")

    def _find(self, sensor_id: Optional[str]) -> str:
        if not os.path.isdir(W1_BASE):
            raise DS18B20Error(
                f"{W1_BASE} absent. Ajoutez dtoverlay=w1-gpio,gpiopin=4 "
                "dans /boot/config.txt et redémarrez."
            )
        if sensor_id:
            p = os.path.join(W1_BASE, sensor_id, W1_FILE)
            if not os.path.isfile(p):
                raise DS18B20Error(f"Sonde {sensor_id} introuvable")
            self.sensor_id = sensor_id
            return p
        devices = glob.glob(os.path.join(W1_BASE, DS_PFX + "*"))
        if not devices:
            raise DS18B20Error(
                "Aucun DS18B20 détecté. Vérifiez câblage + pull-up 4.7 kΩ."
            )
        self.sensor_id = os.path.basename(devices[0])
        return os.path.join(devices[0], W1_FILE)

    def _read_raw(self) -> float:
        for attempt in range(3):
            try:
                with open(self._path) as f:
                    lines = f.read().strip().splitlines()
                if len(lines) < 2:
                    raise DS18B20Error("Réponse incomplète")
                if "YES" not in lines[0]:
                    if attempt < 2:
                        time.sleep(0.2); continue
                    raise DS18B20Error(f"CRC invalide : {lines[0]}")
                t = int(lines[1].split("t=")[1])
                if t == 85000:
                    raise DS18B20Error("Valeur reset DS18B20 (85°C)")
                return t / 1000.0
            except DS18B20Error:
                raise
            except Exception as e:
                raise DS18B20Error(f"Lecture sysfs : {e}")
        raise DS18B20Error("Lecture échouée après 3 tentatives")

    def _trend(self) -> str:
        if len(self._history) < 3: return "stable"
        d = self._history[-1] - self._history[-3]
        return "rising" if d > 1.0 else ("falling" if d < -1.0 else "stable")

    def read(self) -> PumpTempReading:
        temp = self._read_raw()
        if   temp >= self.critical_c: state = ThermalState.CRITICAL
        elif temp >= self.alert_c:    state = ThermalState.ALERT
        else:                         state = ThermalState.NORMAL

        alert = state in (ThermalState.ALERT, ThermalState.CRITICAL)
        crit  = state == ThermalState.CRITICAL

        if crit  and not self._crit:  logger.error(f"[1-Wire] CRIT pompe {temp:.1f}°C")
        elif alert and not self._alert: logger.warning(f"[1-Wire] Alerte pompe {temp:.1f}°C")
        elif not alert and self._alert: logger.info(f"[1-Wire] Pompe OK {temp:.1f}°C")

        self._alert = alert
        self._crit  = crit
        self._history.append(temp)
        if len(self._history) > 10: self._history.pop(0)

        return PumpTempReading(
            temp_c=temp, state=state, sensor_id=self.sensor_id,
            trend=self._trend(), alarm_alert=alert, alarm_crit=crit,
        )

    @property
    def alert_active(self) -> bool: return self._alert
    @property
    def critical_active(self) -> bool: return self._crit
