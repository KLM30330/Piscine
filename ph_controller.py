# ph_controller.py — Régulateur PID dosage pH-
import time, logging

logger = logging.getLogger("ph_pid")


class PHController:
    def __init__(self, target=7.2, deadband=0.05, kp=10.0, ki=0.1, kd=1.0,
                 dose_min=1.0, dose_max=50.0, min_delay_s=600.0):
        self.target   = target
        self.deadband = deadband
        self.kp, self.ki, self.kd = kp, ki, kd
        self.dose_min = dose_min
        self.dose_max = dose_max
        self.min_delay= min_delay_s

        self._integral     = 0.0
        self._last_error   = 0.0
        self._last_time    = time.time()
        self._last_dose_t  = 0.0
        self._total_ml     = 0.0
        self._dose_count   = 0

    def compute_dose(self, ph: float) -> float:
        now = time.time()
        dt  = now - self._last_time
        if dt <= 0: return 0.0

        error = self.target - ph
        if abs(error) < self.deadband:
            self._integral = 0.0
            self._last_time = now
            return 0.0
        if error >= 0:           # pH en dessous de cible → pas de dosage pH-
            self._integral = 0.0
            self._last_time = now
            return 0.0
        if now - self._last_dose_t < self.min_delay:
            return 0.0

        self._integral += error * dt
        max_i = self.dose_max / (self.ki + 1e-9)
        self._integral = max(-max_i, min(self._integral, max_i))
        deriv = (error - self._last_error) / dt
        out   = self.kp * error + self.ki * self._integral + self.kd * deriv
        self._last_error = error
        self._last_time  = now

        return max(self.dose_min, min(abs(out), self.dose_max))

    def record_dose(self, ml: float):
        self._last_dose_t = time.time()
        self._total_ml   += ml
        self._dose_count += 1
        logger.info(f"[pH PID] Dosé {ml:.2f} mL | total={self._total_ml:.2f} mL")

    def reset(self):
        self._integral = self._last_error = 0.0
        self._last_time = time.time()

    @property
    def total_ml(self) -> float: return self._total_ml
    @property
    def dose_count(self) -> int: return self._dose_count
    @property
    def last_dose_age_s(self) -> float: return time.time() - self._last_dose_t

    def status(self) -> dict:
        return {
            "target"         : self.target,
            "integral"       : round(self._integral, 4),
            "total_ml"       : round(self._total_ml, 2),
            "dose_count"     : self._dose_count,
            "last_dose_age_s": round(self.last_dose_age_s, 0),
        }
