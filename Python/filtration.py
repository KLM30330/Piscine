# filtration.py — Gestion filtration : planning, ORP, modes
"""
Calcule le temps de filtration journalier, gère les modes de fonctionnement
et ajuste la fréquence de la pompe selon l'ORP.

Hydraulique piscine :
  Volume = 38 m³  |  Débit pompe = 17 m³/h @ 50 Hz
  Renouvellement complet = 38/17 ≈ 2.24 h

Règle de filtration : T°eau (°C) / 2  → heures de filtration
  Corrigé si f < 50 Hz : t_corr = t_nominal × (50 / f)  [loi d'affinité Q ∝ f]
"""

import time, logging
from enum import Enum
from typing import List, Tuple

import config as C

logger = logging.getLogger("filtration")


class FilterMode(Enum):
    AUTO   = "auto"     # planning calculé selon T° + ORP
    FORCED = "forced"   # pompe ON en permanence à freq fixée
    BOOST  = "boost"    # pleine vitesse jusqu'à ORP cible
    PAUSE  = "pause"    # pompe OFF temporairement
    STOP   = "stop"     # pompe OFF indéfiniment


class WaterState(Enum):
    UNKNOWN      = "unknown"
    CRITICAL_LOW = "critical_low"   # ORP < 550
    LOW          = "low"            # 550–620
    BORDER_LOW   = "border_low"     # 620–650
    OPTIMAL      = "optimal"        # 650–750 ✅
    BORDER_HIGH  = "border_high"    # 750–800
    OVERDOSE     = "overdose"       # > 800


# Fréquence cible selon état ORP
ORP_FREQ_MAP = {
    WaterState.CRITICAL_LOW : 50.0,
    WaterState.LOW          : 50.0,
    WaterState.BORDER_LOW   : 45.0,
    WaterState.OPTIMAL      : 35.0,
    WaterState.BORDER_HIGH  : 30.0,
    WaterState.OVERDOSE     : 30.0,
    WaterState.UNKNOWN      : 50.0,
}


class FiltrationManager:
    """
    Orchestre le fonctionnement de la pompe de filtration.
    """

    def __init__(self, drive):
        self.drive          = drive
        self.mode           = FilterMode.AUTO
        self._schedule      : List[Tuple[float, float]] = []
        self._schedule_day  = -1
        self._orp_state     = WaterState.UNKNOWN
        self._orp_history   = []
        self._target_freq   = C.FREQ_NOMINAL
        self._forced_freq   = C.FREQ_NOMINAL
        self._last_freq_chg = 0.0
        self._pause_until   = 0.0   # timestamp fin de pause

    # ═══════════════════════════════════════════════════════════
    # Calcul temps de filtration
    # ═══════════════════════════════════════════════════════════

    def required_hours(self, water_temp_c: float) -> float:
        """Temps de filtration nominal à 50 Hz (règle T/2)."""
        # Temps minimum = 1 renouvellement complet
        min_h = C.POOL_VOLUME_M3 / C.PUMP_FLOW_M3H   # 38/17 ≈ 2.24 h
        if   water_temp_c < 10: raw = 2.0
        elif water_temp_c < 12: raw = 3.0
        elif water_temp_c < 16: raw = 4.0 + (water_temp_c - 12) * 0.5
        elif water_temp_c <= 28: raw = water_temp_c / 2.0
        else:                    raw = 24.0
        return max(raw, min_h)

    def corrected_hours(self, water_temp_c: float, freq_hz: float) -> float:
        """Corrige le temps si fréquence réduite (loi d'affinité)."""
        freq  = max(freq_hz, C.FREQ_MIN_ABSOLUTE)
        t_nom = self.required_hours(water_temp_c)
        return min(t_nom * (C.FREQ_NOMINAL / freq), 24.0)

    # ═══════════════════════════════════════════════════════════
    # Planning journalier
    # ═══════════════════════════════════════════════════════════

    def build_schedule(self, water_temp_c: float, freq_hz: float = 50.0):
        total   = self.corrected_hours(water_temp_c, freq_hz)
        if total >= 23.5:
            self._schedule = [(0.0, 24.0)]
        else:
            remaining = total
            result    = []
            for (h_s, h_e) in C.FILTRATION_SLOTS:
                if remaining <= 0: break
                alloc = min(remaining, h_e - h_s)
                result.append((float(h_s), float(h_s + alloc)))
                remaining -= alloc
            if remaining > 0:
                result.append((22.0, min(22.0 + remaining, 30.0)))
            self._schedule = result

        self._schedule_day = time.localtime().tm_yday
        t_nom  = self.required_hours(water_temp_c)
        t_corr = self.corrected_hours(water_temp_c, freq_hz)
        logger.info(
            f"[FILT] Planning T={water_temp_c:.1f}°C f={freq_hz}Hz "
            f"t_nom={t_nom:.1f}h t_corr={t_corr:.1f}h"
        )
        for i, (s, e) in enumerate(self._schedule):
            hs, ms = int(s), int((s % 1) * 60)
            he, me = int(e % 24), int((e % 1) * 60)
            logger.info(f"[FILT]   Créneau {i+1}: {hs:02d}h{ms:02d}–{he:02d}h{me:02d}")
        return self._schedule

    def _in_schedule(self) -> bool:
        if not self._schedule: return False
        now = time.localtime()
        cur = now.tm_hour + now.tm_min / 60.0
        for (s, e) in self._schedule:
            if e > 24.0:
                if cur >= s or cur < (e - 24.0): return True
            elif s <= cur < e: return True
        return False

    def schedule_info(self, water_temp_c: float) -> dict:
        slots_str = []
        total = 0.0
        for (s, e) in self._schedule:
            d = min(e, 24.0) - s
            if e > 24.0: d = (e - 24.0) + (24.0 - s)
            total += d
            hs, ms = int(s), int((s % 1) * 60)
            he, me = int(e % 24), int((e % 1) * 60)
            slots_str.append(f"{hs:02d}h{ms:02d}-{he:02d}h{me:02d}")
        return {
            "slots"           : " | ".join(slots_str) if slots_str else "—",
            "total_hours"     : round(total, 2),
            "required_hours"  : round(self.required_hours(water_temp_c), 2),
        }

    # ═══════════════════════════════════════════════════════════
    # Régulation ORP → fréquence
    # ═══════════════════════════════════════════════════════════

    def _classify_orp(self, orp: float) -> WaterState:
        if   orp < C.ORP_CRITICAL_LOW : return WaterState.CRITICAL_LOW
        elif orp < C.ORP_LOW          : return WaterState.LOW
        elif orp < C.ORP_TARGET_LOW   : return WaterState.BORDER_LOW
        elif orp <= C.ORP_TARGET_HIGH  : return WaterState.OPTIMAL
        elif orp <= C.ORP_HIGH        : return WaterState.BORDER_HIGH
        else                          : return WaterState.OVERDOSE

    def _orp_target_freq(self, state: WaterState, orp: float) -> float:
        if state == WaterState.OPTIMAL:
            ratio = (orp - C.ORP_TARGET_LOW) / (C.ORP_TARGET_HIGH - C.ORP_TARGET_LOW)
            ratio = max(0.0, min(1.0, ratio))
            return round(C.FREQ_MIN_FILTRATION - ratio * (C.FREQ_MIN_FILTRATION - C.FREQ_MIN_ABSOLUTE), 1)
        return ORP_FREQ_MAP.get(state, C.FREQ_NOMINAL)

    def update_orp(self, orp: float) -> dict:
        """Met à jour l'état ORP et recalcule la fréquence cible."""
        new_state = self._classify_orp(orp)
        self._orp_history.append(new_state)
        self._orp_history = self._orp_history[-C.ORP_STABILITY_N:]
        confirmed = (len(self._orp_history) >= C.ORP_STABILITY_N and
                     all(s == new_state for s in self._orp_history))

        if confirmed and new_state != self._orp_state:
            logger.info(f"[ORP] {self._orp_state.value} → {new_state.value} ({orp:.0f} mV)")
            self._orp_state = new_state

        freq = self._orp_target_freq(new_state, orp)

        # Appliquer si variation suffisante et délai respecté
        if (self.drive.is_running and confirmed and
                abs(freq - self._target_freq) >= C.ORP_MIN_FREQ_CHANGE and
                time.time() - self._last_freq_chg >= C.ORP_MIN_CHANGE_INTERVAL):
            if self.mode in (FilterMode.AUTO,):
                logger.info(f"[ORP] Fréquence {self._target_freq}→{freq} Hz")
                self.drive.ramp_to(freq)
                self._target_freq  = freq
                self._last_freq_chg = time.time()

        alarms = {}
        if new_state == WaterState.CRITICAL_LOW:
            alarms["orp"] = f"ORP critique {orp:.0f} mV — eau dangereuse !"
        elif new_state == WaterState.OVERDOSE:
            alarms["orp"] = f"ORP surdosage {orp:.0f} mV — vérifier électrolyseur"

        return {
            "water_state"  : new_state.value,
            "target_freq"  : self._target_freq,
            "orp_alarm"    : bool(alarms),
            "alarms"       : alarms,
        }

    # ═══════════════════════════════════════════════════════════
    # Modes de fonctionnement
    # ═══════════════════════════════════════════════════════════

    def set_mode(self, mode_str: str, freq_hz: float = None):
        try:
            new_mode = FilterMode(mode_str)
        except ValueError:
            logger.warning(f"[FILT] Mode inconnu : {mode_str}")
            return
        old = self.mode
        self.mode = new_mode
        if freq_hz:
            self._forced_freq = max(freq_hz, C.FREQ_MIN_ABSOLUTE)
        logger.info(f"[FILT] Mode {old.value} → {new_mode.value}"
                    + (f" @ {self._forced_freq} Hz" if freq_hz else ""))

    def pause(self, duration_s: float = None):
        self.mode = FilterMode.PAUSE
        if duration_s:
            self._pause_until = time.time() + duration_s
        if self.drive.is_running:
            self.drive.ramp_stop()

    def resume_auto(self):
        self.mode = FilterMode.AUTO
        self._pause_until = 0.0

    # ═══════════════════════════════════════════════════════════
    # Mise à jour principale
    # ═══════════════════════════════════════════════════════════

    def update(self, water_temp_c: float):
        """À appeler chaque cycle. Démarre/arrête la pompe selon le mode."""
        today = time.localtime().tm_yday

        # Fin de pause automatique
        if self.mode == FilterMode.PAUSE and self._pause_until > 0:
            if time.time() >= self._pause_until:
                self.resume_auto()

        # Rebuild planning quotidien
        if self.mode == FilterMode.AUTO:
            if not self._schedule or today != self._schedule_day:
                self.build_schedule(water_temp_c, self._target_freq)

        should_run = self._should_pump_run()

        if should_run and not self.drive.is_running:
            freq = self._get_run_freq()
            self.drive.ramp_start(freq)
        elif not should_run and self.drive.is_running:
            self.drive.ramp_stop()
        elif should_run and self.drive.is_running:
            # Ajustement fréquence si mode forcé
            if self.mode == FilterMode.FORCED:
                if abs(self._forced_freq - self.drive.current_freq) > 2:
                    self.drive.ramp_to(self._forced_freq)

    def _should_pump_run(self) -> bool:
        if   self.mode == FilterMode.AUTO   : return self._in_schedule()
        elif self.mode == FilterMode.FORCED : return True
        elif self.mode == FilterMode.BOOST  : return True
        elif self.mode == FilterMode.PAUSE  : return False
        elif self.mode == FilterMode.STOP   : return False
        return False

    def _get_run_freq(self) -> float:
        if self.mode == FilterMode.FORCED : return self._forced_freq
        if self.mode == FilterMode.BOOST  : return C.BOOST_FREQ
        return self._target_freq

    # ── Boost ─────────────────────────────────────────────────

    def check_boost_exit(self, orp: float):
        """Quitte le mode boost si ORP cible atteint."""
        if self.mode == FilterMode.BOOST and orp >= C.BOOST_ORP_TARGET:
            logger.info(f"[FILT] Boost terminé — ORP={orp:.0f} mV ≥ {C.BOOST_ORP_TARGET}")
            self.set_mode("auto")

    # ── Accesseurs ────────────────────────────────────────────

    @property
    def target_freq(self) -> float: return self._target_freq
    @property
    def water_state(self) -> WaterState: return self._orp_state
    @property
    def orp_alarm(self) -> bool:
        return self._orp_state in (WaterState.CRITICAL_LOW, WaterState.OVERDOSE)
