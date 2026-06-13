# gpio_buttons.py — Boutons poussoir GPIO avec antirebond
"""
Gestion des 4 boutons poussoir en face avant.
Utilise RPi.GPIO avec détection de front descendant (bouton actif bas avec pull-up).
Thread-safe : les callbacks sont exécutés dans un thread dédié.
"""

import time, threading, logging

logger = logging.getLogger("gpio")

DEBOUNCE_MS = 200   # ms antirebond


class ButtonError(Exception):
    pass


class ButtonManager:
    """
    Gère les 4 boutons GPIO avec antirebond logiciel.
    Les callbacks sont appelés dans un thread secondaire.
    """
    def __init__(self, pins: dict):
        """
        pins : dict {nom: gpio_bcm}
          ex. {"lcd": 5, "prime": 6, "pause": 13, "resume": 19}
        """
        try:
            import RPi.GPIO as GPIO
            self._GPIO = GPIO
        except ImportError:
            raise ButtonError("pip install RPi.GPIO")

        GPIO = self._GPIO
        GPIO.setmode(GPIO.BCM)
        GPIO.setwarnings(False)

        self._pins     = pins
        self._cbs      = {}
        self._last     = {name: 0.0 for name in pins}
        self._lock     = threading.Lock()

        for name, pin in pins.items():
            GPIO.setup(pin, GPIO.IN, pull_up_down=GPIO.PUD_UP)
            # Callback générique avec closure sur `name`
            GPIO.add_event_detect(
                pin,
                GPIO.FALLING,
                callback=self._make_cb(name),
                bouncetime=DEBOUNCE_MS,
            )
            logger.info(f"[GPIO] Bouton '{name}' sur GPIO{pin}")

    def _make_cb(self, name: str):
        def cb(channel):
            now = time.time()
            with self._lock:
                if now - self._last[name] < DEBOUNCE_MS / 1000.0:
                    return
                self._last[name] = now
            logger.info(f"[GPIO] Bouton '{name}' appuyé")
            fn = self._cbs.get(name)
            if fn:
                threading.Thread(target=fn, daemon=True).start()
        return cb

    def on(self, name: str, callback):
        """Enregistre le callback pour un bouton."""
        if name not in self._pins:
            raise ButtonError(f"Bouton inconnu : {name}")
        self._cbs[name] = callback

    def close(self):
        try:
            self._GPIO.cleanup(list(self._pins.values()))
        except Exception:
            pass
